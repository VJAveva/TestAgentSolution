namespace TestControllerGrpc.Core.Maintenance;

// Windows Update controller-side seams (Part 2, spec §19). Contracts only.

/// <summary>
/// Live, in-memory store of each node's Windows Update posture. <see cref="Apply"/> is idempotent (last-write-wins,
/// dedup on node+kind+timestamp) so a startup snapshot arriving twice after a reconnect does not double-notify.
/// </summary>
public interface INodeUpdateStatusStore
{
    NodeUpdateStatus? Get(string nodeId);
    IReadOnlyCollection<NodeUpdateStatus> GetAll();

    /// <summary>Apply an agent event, overwriting the level fields and raising <see cref="Changed"/> on a transition.</summary>
    void Apply(NodeMaintenanceEventDto evt);

    /// <summary>Open the post-revert suppression window on a node (spec §15).</summary>
    void Suppress(string nodeId, DateTimeOffset until);

    event EventHandler<NodeUpdateStatusChanged>? Changed;
}

/// <summary>Maps a node's update posture to a scheduling decision, per the configured <see cref="UpdatePolicy"/>.</summary>
public interface IUpdatePolicyEvaluator
{
    MaintenanceState Evaluate(NodeUpdateStatus status, UpdatePolicy policy);
}

/// <summary>The operator notification feed (bell + toasts) for update events.</summary>
public interface IFleetNotificationService
{
    IReadOnlyList<FleetNotification> Notifications { get; }
    void Raise(FleetNotification notification);
    void Acknowledge(Guid id);
    void Snooze(string nodeId, TimeSpan duration);
    void MarkAllRead();
    event EventHandler? NotificationsChanged;
}
