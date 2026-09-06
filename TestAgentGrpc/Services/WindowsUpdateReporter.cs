using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Core.Maintenance;

namespace TestAgentGrpc.Services;

/// <summary>
/// Sits between <see cref="WindowsUpdateDetector"/> and the agent event firehose (spec Prompt 15). Buffers a burst of
/// detections for a quiet window, then emits ONE event carrying the merged item list and the highest-severity kind.
/// Every message carries the complete level state, never a delta, so the controller can rebuild a node's posture from
/// whichever single message it happens to receive.
/// </summary>
public sealed class WindowsUpdateReporter : IDisposable
{
    // Highest wins when a burst is merged. RebootRequired is the operationally significant one.
    private static readonly MaintenanceEventKind[] SeverityOrder =
    [
        MaintenanceEventKind.RebootRequired,
        MaintenanceEventKind.UpdateFailed,
        MaintenanceEventKind.UpdateInstalled,
        MaintenanceEventKind.UpdateInstalling,
        MaintenanceEventKind.UpdatePending,
        MaintenanceEventKind.RebootCleared,
        MaintenanceEventKind.Unspecified,
    ];

    private readonly EventBroadcaster _broadcaster;
    private readonly AgentSettings _agent;
    private readonly WindowsUpdateSettings _settings;
    private readonly ILogger<WindowsUpdateReporter> _logger;

    private readonly object _gate = new();
    private readonly Dictionary<string, UpdateItemDto> _pendingItems = new(StringComparer.OrdinalIgnoreCase);
    // Fully qualified: the agent enables WinForms, which also defines a Timer.
    private readonly System.Threading.Timer _flushTimer;

    private MaintenanceEventKind _pendingKind = MaintenanceEventKind.Unspecified;
    private MaintenanceEventSource _pendingSource;
    private WindowsUpdateStatusDto? _pendingStatus;
    private DateTimeOffset _firstBufferedUtc;
    private bool _hasBuffered;

    public WindowsUpdateReporter(
        EventBroadcaster broadcaster,
        IOptions<AgentSettings> agent,
        IOptions<WindowsUpdateSettings> settings,
        ILogger<WindowsUpdateReporter> logger)
    {
        _broadcaster = broadcaster;
        _agent = agent.Value;
        _settings = settings.Value;
        _logger = logger;
        _flushTimer = new System.Threading.Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Buffer one detection. <paramref name="levelState"/> is always the node's complete current posture.
    /// <see cref="MaintenanceEventKind.RebootCleared"/> and startup snapshots bypass coalescing.
    /// </summary>
    public void Report(
        MaintenanceEventKind kind,
        MaintenanceEventSource source,
        WindowsUpdateStatusDto levelState,
        IReadOnlyList<UpdateItemDto>? eventItems = null)
    {
        var immediate = kind == MaintenanceEventKind.RebootCleared
                     || source == MaintenanceEventSource.StartupSnapshot;

        lock (_gate)
        {
            _pendingStatus = levelState;
            _pendingSource = source;
            _pendingKind = Escalate(_pendingKind, kind);

            foreach (var item in eventItems ?? [])
                _pendingItems[Key(item)] = item;

            if (!_hasBuffered)
            {
                _firstBufferedUtc = DateTimeOffset.UtcNow;
                _hasBuffered = true;
            }

            if (immediate)
            {
                FlushLocked();
                return;
            }

            // Reset the quiet window on each new detection, but never defer past the hard cap.
            var elapsed = DateTimeOffset.UtcNow - _firstBufferedUtc;
            var cap = TimeSpan.FromSeconds(_settings.MaxCoalescingSeconds);
            if (elapsed >= cap)
            {
                FlushLocked();
                return;
            }

            var window = TimeSpan.FromSeconds(_settings.CoalescingWindowSeconds);
            var remaining = cap - elapsed;
            _flushTimer.Change(window < remaining ? window : remaining, Timeout.InfiniteTimeSpan);
        }
    }

    public void Flush()
    {
        lock (_gate) FlushLocked();
    }

    private void FlushLocked()
    {
        if (!_hasBuffered || _pendingStatus is null) return;

        var items = _pendingItems.Values
            .Take(Math.Max(0, _settings.MaxReportedItems))
            .ToList();

        var payload = new WindowsUpdatePayload
        {
            Kind = _pendingKind,
            Source = _pendingSource,
            RebootRequired = _pendingStatus.RebootRequired,
            PendingCount = _pendingStatus.PendingCount,
            Items = items.Count > 0 ? items : _pendingStatus.Items,
            LastInstallUtc = _pendingStatus.LastInstallUtc,
            DetectedUtc = DateTimeOffset.UtcNow,
        };

        _pendingItems.Clear();
        _pendingKind = MaintenanceEventKind.Unspecified;
        _hasBuffered = false;
        _flushTimer.Change(Timeout.Infinite, Timeout.Infinite);

        Publish(payload);
    }

    private void Publish(WindowsUpdatePayload payload)
    {
        try
        {
            _broadcaster.Publish(new ExecutionEvent
            {
                AgentName = _agent.AgentName,
                EventType = ExecutionEventType.EventWindowsUpdate,
                Detail = JsonSerializer.Serialize(payload, WindowsUpdateEventMapper.JsonOptions),
                Timestamp = Timestamp.FromDateTimeOffset(payload.DetectedUtc),
            });
            _logger.LogInformation(
                "Windows Update posture reported: kind={Kind} source={Source} pending={Pending} rebootRequired={Reboot}",
                payload.Kind, payload.Source, payload.PendingCount, payload.RebootRequired);
        }
        catch (Exception ex)
        {
            // Reporting must never take the agent down; the next detection or snapshot carries the same level state.
            _logger.LogWarning(ex, "Failed to publish Windows Update posture.");
        }
    }

    private static MaintenanceEventKind Escalate(MaintenanceEventKind current, MaintenanceEventKind incoming)
    {
        var currentRank = Array.IndexOf(SeverityOrder, current);
        var incomingRank = Array.IndexOf(SeverityOrder, incoming);
        if (currentRank < 0) return incoming;
        if (incomingRank < 0) return current;
        return incomingRank < currentRank ? incoming : current;
    }

    private static string Key(UpdateItemDto item)
        => !string.IsNullOrEmpty(item.KbId) ? item.KbId : item.Title;

    public void Dispose() => _flushTimer.Dispose();
}
