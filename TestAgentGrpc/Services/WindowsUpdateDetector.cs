using System.Diagnostics.Eventing.Reader;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using TestControllerGrpc.Core.Maintenance;

namespace TestAgentGrpc.Services;

/// <summary>
/// Detects this node's Windows Update posture and hands it to <see cref="WindowsUpdateReporter"/> (spec Prompt 14).
/// Two detection paths feed one merge point: an event-log subscription for install activity, and a registry poller
/// for the authoritative reboot-required level state. Neither may ever take the agent down — if the event-log
/// subscription is unavailable (access denied, channel missing) the poller alone still reports posture.
/// </summary>
public sealed class WindowsUpdateDetector : BackgroundService
{
    private const string UpdateChannel = "Microsoft-Windows-WindowsUpdateClient/Operational";

    private const string UpdateQuery =
        "*[System[Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] " +
        "and (EventID=19 or EventID=20 or EventID=43 or EventID=44)]]";

    private readonly WindowsUpdateReporter _reporter;
    private readonly WindowsUpdateSettings _settings;
    private readonly ILogger<WindowsUpdateDetector> _logger;

    private readonly object _stateGate = new();
    private EventLogWatcher? _watcher;
    private bool? _lastRebootRequired;
    private DateTimeOffset? _lastInstallUtc;
    private WindowsUpdateStatusDto _current = new();

    public WindowsUpdateDetector(
        WindowsUpdateReporter reporter,
        IOptions<WindowsUpdateSettings> settings,
        ILogger<WindowsUpdateDetector> logger)
    {
        _reporter = reporter;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>The node's complete current posture — what a startup snapshot sends, changed or not.</summary>
    public WindowsUpdateStatusDto CurrentStatus
    {
        get { lock (_stateGate) return _current; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("Windows Update detection disabled by configuration.");
            return;
        }

        try { await Task.Delay(TimeSpan.FromSeconds(_settings.StartupDelaySeconds), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        if (_settings.WatchEventLog)
            TryStartEventLogWatcher();

        var lastSnapshot = DateTimeOffset.MinValue;
        var pollInterval = TimeSpan.FromSeconds(Math.Max(30, _settings.PollIntervalSeconds));
        var snapshotInterval = TimeSpan.FromSeconds(Math.Max(60, _settings.SnapshotIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var dueForSnapshot = DateTimeOffset.UtcNow - lastSnapshot >= snapshotInterval;
                await PollAsync(dueForSnapshot, stoppingToken).ConfigureAwait(false);
                if (dueForSnapshot) lastSnapshot = DateTimeOffset.UtcNow;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "Windows Update scan failed; will retry next interval."); }

            try { await Task.Delay(pollInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public override void Dispose()
    {
        try { _watcher?.Dispose(); } catch { /* shutting down */ }
        base.Dispose();
    }

    private async Task PollAsync(bool forceSnapshot, CancellationToken ct)
    {
        var rebootRequired = IsRebootRequired();

        // Visible without being actionable: these are almost always an installer replacing a locked file,
        // which is why they no longer flag the node.
        if (!_settings.TreatPendingFileRenamesAsRebootRequired)
        {
            var pendingRenames = PendingFileRenameCount();
            if (pendingRenames > 0)
                _logger.LogDebug(
                    "{Count} pending file rename(s) queued for next boot; not treated as reboot-required.",
                    pendingRenames);
        }

        // The WUApi COM search blocks; run it off the loop thread with a hard timeout so a hung
        // Windows Update service can never stall the agent.
        ScanOutcome scan = _settings.ScanPendingUpdates
            ? await Task.Run(SearchPending, ct)
                .WaitAsync(TimeSpan.FromSeconds(_settings.ScanTimeoutSeconds), ct)
                .ConfigureAwait(false)
            : new ScanOutcome(null, [], UpdateScanStatus.Unknown, null, null);

        WindowsUpdateStatusDto status;
        bool? previousReboot;
        lock (_stateGate)
        {
            status = new WindowsUpdateStatusDto
            {
                RebootRequired = rebootRequired,
                PendingCount = scan.Count,
                Items = scan.Items,
                LastInstallUtc = _lastInstallUtc,
                ScanStatus = scan.Status,
                LastScanUtc = scan.Status == UpdateScanStatus.Failed ? null : DateTimeOffset.UtcNow,
                WindowsLastSearchUtc = scan.WindowsLastSearchUtc,
                ScanError = scan.Error,
            };
            _current = status;
            previousReboot = _lastRebootRequired;
            _lastRebootRequired = rebootRequired;
        }

        var items = scan.Items;

        if (forceSnapshot)
        {
            _reporter.Report(DeriveKind(status), MaintenanceEventSource.StartupSnapshot, status, items);
            return;
        }

        // Only a transition is news; the periodic snapshot above covers everything else.
        if (previousReboot == rebootRequired) return;

        var kind = rebootRequired ? MaintenanceEventKind.RebootRequired : MaintenanceEventKind.RebootCleared;
        _reporter.Report(kind, MaintenanceEventSource.RegistryPoll, status, items);
    }

    private static MaintenanceEventKind DeriveKind(WindowsUpdateStatusDto s) =>
        s.RebootRequired ? MaintenanceEventKind.RebootRequired
        : s.PendingCount > 0 ? MaintenanceEventKind.UpdatePending
        : MaintenanceEventKind.RebootCleared;

    // ── Registry path ────────────────────────────────────────────────
    // Only the two servicing-owned keys are authoritative. PendingFileRenameOperations is reported but does
    // not gate dispatch unless explicitly enabled - see WindowsUpdateSettings.
    private bool IsRebootRequired()
        => KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")
        || KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")
        || (_settings.TreatPendingFileRenamesAsRebootRequired && PendingFileRenameCount() > 0);

    private bool KeyExists(string path)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(path);
            return key is not null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Registry probe failed for {Path}.", path);
            return false;
        }
    }

    /// <summary>Informational: how many file replacements are queued for next boot, whatever scheduled them.</summary>
    private int PendingFileRenameCount()
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            return key?.GetValue("PendingFileRenameOperations") is string[] entries
                ? entries.Count(e => !string.IsNullOrWhiteSpace(e))
                : 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PendingFileRenameOperations probe failed.");
            return 0;
        }
    }

    // ── WUApi path ───────────────────────────────────────────────────
    /// <summary>What one cache read produced. A null <paramref name="Count"/> means "not known".</summary>
    internal sealed record ScanOutcome(
        int? Count,
        IReadOnlyList<UpdateItemDto> Items,
        UpdateScanStatus Status,
        DateTimeOffset? WindowsLastSearchUtc,
        string? Error);

    /// <summary>
    /// Windows' own last successful search. Null when the COM API does not expose it — which must read as
    /// "age unknown", never as "old", or every node without the property would be reported Stale.
    /// </summary>
    internal static DateTimeOffset? ReadWindowsLastSearchUtc()
    {
        try
        {
            System.Type? autoUpdateType = System.Type.GetTypeFromProgID("Microsoft.Update.AutoUpdate");
            if (autoUpdateType is null) return null;

            dynamic autoUpdate = Activator.CreateInstance(autoUpdateType)!;
            dynamic results = autoUpdate.Results;
            var last = (DateTime)results.LastSearchSuccessDate;
            return last == default ? null : new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Utc));
        }
        catch
        {
            return null;   // property absent, never searched, or access denied
        }
    }

    /// <summary>Decides Ok vs Stale from Windows' own last search; unknown age is not old age.</summary>
    internal static UpdateScanStatus ClassifyFreshness(DateTimeOffset? windowsLastSearchUtc, DateTimeOffset now, int staleAfterDays)
        => windowsLastSearchUtc is { } last && (now - last).TotalDays > staleAfterDays
            ? UpdateScanStatus.Stale
            : UpdateScanStatus.Ok;

    /// <summary>
    /// What a failed scan reports. Debug level made a broken scan invisible, and returning 0 made it
    /// indistinguishable from a clean node, so this warns and reports NO count rather than a fabricated zero.
    /// </summary>
    internal static ScanOutcome ScanFailed(ILogger logger, Exception ex, DateTimeOffset? windowsLastSearchUtc)
    {
        logger.LogWarning(ex, "WUApi pending-update search failed; reporting ScanStatus=Failed.");
        return new ScanOutcome(null, [], UpdateScanStatus.Failed, windowsLastSearchUtc, ex.Message);
    }

    // Late-bound search of the LOCAL cache (Online=false: no WU-server round-trip) for applicable, not-yet-installed
    // software updates. Late binding avoids a COM interop assembly reference.
    private ScanOutcome SearchPending()
    {
        var items = new List<UpdateItemDto>();
        DateTimeOffset? windowsLastSearchUtc = ReadWindowsLastSearchUtc();
        try
        {
            // Fully qualify: Google.Protobuf.WellKnownTypes (in scope agent-wide) also defines a Type.
            System.Type? sessionType = System.Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (sessionType is null)
                return new ScanOutcome(null, [], UpdateScanStatus.Failed, windowsLastSearchUtc, "Microsoft.Update.Session is not registered.");

            dynamic session = Activator.CreateInstance(sessionType)!;
            dynamic searcher = session.CreateUpdateSearcher();
            searcher.Online = false; // local update cache only — fast, no network call
            dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software'");
            dynamic updates = result.Updates;

            int count = (int)updates.Count;
            for (int i = 0; i < count && items.Count < _settings.MaxReportedItems; i++)
            {
                try
                {
                    string title = (string)updates.Item(i).Title;
                    items.Add(new UpdateItemDto
                    {
                        KbId = WindowsUpdateEventMapper.ExtractKb(title),
                        Title = title,
                        Result = "Pending",
                    });
                }
                catch { /* skip malformed entry */ }
            }

            var status = ClassifyFreshness(windowsLastSearchUtc, DateTimeOffset.UtcNow, _settings.StaleAfterDays);
            return new ScanOutcome(count, items, status, windowsLastSearchUtc, null);
        }
        catch (Exception ex)
        {
            return ScanFailed(_logger, ex, windowsLastSearchUtc);
        }
    }

    // ── Event-log path ───────────────────────────────────────────────
    private void TryStartEventLogWatcher()
    {
        try
        {
            _watcher = new EventLogWatcher(new EventLogQuery(UpdateChannel, PathType.LogName, UpdateQuery));
            _watcher.EventRecordWritten += OnUpdateEvent;
            _watcher.Enabled = true;
            _logger.LogInformation("Subscribed to {Channel} for Windows Update activity.", UpdateChannel);
        }
        catch (Exception ex)
        {
            // Logged once, then never retried — registry polling alone is a complete reboot-required signal.
            _logger.LogWarning(ex,
                "Windows Update event-log subscription unavailable; continuing with registry polling only.");
            _watcher = null;
        }
    }

    private void OnUpdateEvent(object? sender, EventRecordWrittenEventArgs e)
    {
        if (e.EventRecord is null) return;
        try
        {
            using var record = e.EventRecord;
            var kind = MapEventId(record.Id);
            if (kind == MaintenanceEventKind.Unspecified) return;

            var item = BuildItem(record, kind);

            WindowsUpdateStatusDto status;
            lock (_stateGate)
            {
                if (kind == MaintenanceEventKind.UpdateInstalled)
                    _lastInstallUtc = record.TimeCreated ?? DateTime.UtcNow;

                status = _current with { LastInstallUtc = _lastInstallUtc };
                _current = status;
            }

            _reporter.Report(kind, MaintenanceEventSource.EventLog, status, item is null ? null : [item]);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to process a Windows Update event-log record.");
        }
    }

    private static MaintenanceEventKind MapEventId(int eventId) => eventId switch
    {
        44 => MaintenanceEventKind.UpdatePending,
        43 => MaintenanceEventKind.UpdateInstalling,
        19 => MaintenanceEventKind.UpdateInstalled,
        20 => MaintenanceEventKind.UpdateFailed,
        _ => MaintenanceEventKind.Unspecified,
    };

    // WindowsUpdateClient events carry the update title and (for 19/20) an HRESULT in their properties.
    private static UpdateItemDto? BuildItem(EventRecord record, MaintenanceEventKind kind)
    {
        try
        {
            var props = record.Properties;
            if (props.Count == 0) return null;

            var title = props.Select(p => p.Value as string)
                             .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
            if (title.Length == 0) return null;

            var code = kind == MaintenanceEventKind.UpdateFailed
                ? props.Select(p => p.Value).OfType<int>().Select(v => $"0x{v:X8}").FirstOrDefault() ?? ""
                : "";

            return new UpdateItemDto
            {
                KbId = WindowsUpdateEventMapper.ExtractKb(title),
                Title = title,
                Result = kind.ToString(),
                ResultCode = code,
            };
        }
        catch { return null; }
    }
}
