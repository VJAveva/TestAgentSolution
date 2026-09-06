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

        // The WUApi COM search blocks; run it off the loop thread with a hard timeout so a hung
        // Windows Update service can never stall the agent.
        var (pendingCount, items) = _settings.ScanPendingUpdates
            ? await Task.Run(SearchPending, ct)
                .WaitAsync(TimeSpan.FromSeconds(_settings.ScanTimeoutSeconds), ct)
                .ConfigureAwait(false)
            : (0, (IReadOnlyList<UpdateItemDto>)[]);

        WindowsUpdateStatusDto status;
        bool? previousReboot;
        lock (_stateGate)
        {
            status = new WindowsUpdateStatusDto
            {
                RebootRequired = rebootRequired,
                PendingCount = pendingCount,
                Items = items,
                LastInstallUtc = _lastInstallUtc,
            };
            _current = status;
            previousReboot = _lastRebootRequired;
            _lastRebootRequired = rebootRequired;
        }

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
    // Any one of the three signals means a reboot is owed. Read-only, explicit 64-bit view.
    private bool IsRebootRequired()
        => KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")
        || KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")
        || HasPendingFileRenames();

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

    private bool HasPendingFileRenames()
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            return key?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PendingFileRenameOperations probe failed.");
            return false;
        }
    }

    // ── WUApi path ───────────────────────────────────────────────────
    // Late-bound search of the LOCAL cache (Online=false: no WU-server round-trip) for applicable, not-yet-installed
    // software updates. Late binding avoids a COM interop assembly reference.
    private (int Count, IReadOnlyList<UpdateItemDto> Items) SearchPending()
    {
        var items = new List<UpdateItemDto>();
        try
        {
            // Fully qualify: Google.Protobuf.WellKnownTypes (in scope agent-wide) also defines a Type.
            System.Type? sessionType = System.Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (sessionType is null) return (0, items);

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
            return (count, items);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WUApi pending-update search unavailable; reporting reboot-required only.");
            return (items.Count, items);
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
