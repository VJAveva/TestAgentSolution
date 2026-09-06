using System.Collections.Concurrent;

namespace TestControllerGrpc.Core.Maintenance;

/// <summary>
/// In-memory, idempotent store of per-node Windows Update posture (spec §16/§19). Level fields (reboot-required,
/// pending count, items) are always overwritten from the incoming status — the agent sends the complete state, never
/// a delta. Duplicate or stale events (same node+kind, not newer) are ignored so reconnect snapshots don't re-alarm.
/// </summary>
public sealed class NodeUpdateStatusStore : INodeUpdateStatusStore
{
    private readonly ConcurrentDictionary<string, NodeUpdateStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string Node, MaintenanceEventKind Kind), DateTimeOffset> _lastSeen = new();

    public event EventHandler<NodeUpdateStatusChanged>? Changed;

    public NodeUpdateStatus? Get(string nodeId) => _statuses.TryGetValue(nodeId, out var s) ? s : null;

    public IReadOnlyCollection<NodeUpdateStatus> GetAll() => _statuses.Values.ToList();

    public void Apply(NodeMaintenanceEventDto evt)
    {
        var dedupKey = (evt.NodeId.ToLowerInvariant(), evt.Kind);
        if (_lastSeen.TryGetValue(dedupKey, out var seenAt) && evt.DetectedUtc <= seenAt)
            return;  // duplicate or older than what we already have for this node+kind
        _lastSeen[dedupKey] = evt.DetectedUtc;

        var existing = Get(evt.NodeId);
        var previous = existing?.State ?? WindowsUpdateState.Unknown;

        var suppressed = existing?.SuppressedUntilUtc is { } until && until > evt.DetectedUtc;
        var newState = suppressed ? WindowsUpdateState.Suppressed : DeriveState(evt.Status, evt.Kind);

        _statuses[evt.NodeId] = new NodeUpdateStatus
        {
            NodeId = evt.NodeId,
            State = newState,
            LastSource = evt.Source,
            LastEventUtc = evt.DetectedUtc,
            LastReportUtc = DateTimeOffset.UtcNow,
            LastInstallUtc = evt.Status.LastInstallUtc ?? existing?.LastInstallUtc,
            PendingCount = evt.Status.PendingCount,
            Items = evt.Status.Items,
            SuppressedUntilUtc = existing?.SuppressedUntilUtc,
            SnoozedUntilUtc = existing?.SnoozedUntilUtc,
            Acknowledged = existing?.Acknowledged ?? false,
        };

        if (previous != newState)
            Changed?.Invoke(this, new NodeUpdateStatusChanged(evt.NodeId, previous, newState));
    }

    public void Suppress(string nodeId, DateTimeOffset until)
    {
        // A node reverted before it ever reported still needs the window open, so seed an entry when absent.
        var existing = Get(nodeId) ?? new NodeUpdateStatus
        {
            NodeId = nodeId,
            State = WindowsUpdateState.Unknown,
            LastSource = MaintenanceEventSource.Unspecified,
            LastReportUtc = DateTimeOffset.UtcNow,
        };

        var previous = existing.State;
        _statuses[nodeId] = existing with { SuppressedUntilUtc = until, State = WindowsUpdateState.Suppressed };
        if (previous != WindowsUpdateState.Suppressed)
            Changed?.Invoke(this, new NodeUpdateStatusChanged(nodeId, previous, WindowsUpdateState.Suppressed));
    }

    // Installing is transient (comes from the event); otherwise the registry level is the source of truth.
    private static WindowsUpdateState DeriveState(WindowsUpdateStatusDto status, MaintenanceEventKind kind)
    {
        if (kind == MaintenanceEventKind.UpdateInstalling)
            return WindowsUpdateState.UpdateInstalling;
        if (status.RebootRequired)
            return WindowsUpdateState.RebootRequired;
        if (status.PendingCount > 0)
            return WindowsUpdateState.UpdatePending;
        return WindowsUpdateState.UpToDate;
    }
}
