using TestControllerGrpc.Core.Maintenance;

namespace TestControllerGrpc.Tests.Maintenance;

public class UpdatePostureCoordinatorTests
{
    private const string Node = "JVKPRI";

    private sealed record Harness(
        NodeUpdateStatusStore Statuses,
        MaintenanceStateStore Maintenance,
        FleetNotificationService Notifications,
        UpdatePolicyStore Policy,
        UpdatePostureCoordinator Coordinator);

    private static Harness Build(UpdatePolicy? policy = null)
    {
        var statuses = new NodeUpdateStatusStore();
        var maintenance = new MaintenanceStateStore();
        var notifications = new FleetNotificationService();
        var policyStore = new UpdatePolicyStore(policy ?? new UpdatePolicy());
        var coordinator = new UpdatePostureCoordinator(
            statuses, new UpdatePolicyEvaluator(), maintenance, notifications, policyStore);
        coordinator.Start();
        return new Harness(statuses, maintenance, notifications, policyStore, coordinator);
    }

    private static NodeMaintenanceEventDto Event(
        MaintenanceEventKind kind, bool rebootRequired = false, int pending = 0, DateTimeOffset? at = null)
        => new()
        {
            NodeId = Node,
            Kind = kind,
            Source = MaintenanceEventSource.RegistryPoll,
            Status = new WindowsUpdateStatusDto { RebootRequired = rebootRequired, PendingCount = pending },
            DetectedUtc = at ?? DateTimeOffset.UtcNow,
        };

    [Fact]
    public void Apply_Should_SetDraining_When_RebootRequiredReported()
    {
        var h = Build();

        h.Statuses.Apply(Event(MaintenanceEventKind.RebootRequired, rebootRequired: true));

        Assert.Equal(MaintenanceState.Draining, h.Maintenance.Get(Node));
    }

    [Fact]
    public void Apply_Should_LeaveNodeDispatchable_When_UpdatesOnlyPending()
    {
        var h = Build();

        h.Statuses.Apply(Event(MaintenanceEventKind.UpdatePending, pending: 4));

        Assert.Equal(MaintenanceState.None, h.Maintenance.Get(Node));
    }

    [Fact]
    public void Apply_Should_NotOverrideState_When_NodeIsReverting()
    {
        var h = Build();
        h.Maintenance.Set(Node, MaintenanceState.Reverting);

        h.Statuses.Apply(Event(MaintenanceEventKind.RebootRequired, rebootRequired: true));

        Assert.Equal(MaintenanceState.Reverting, h.Maintenance.Get(Node));
    }

    [Fact]
    public void Apply_Should_NotDowngradeToDraining_When_AlreadyUpdating()
    {
        var h = Build();
        h.Statuses.Apply(Event(MaintenanceEventKind.RebootRequired, rebootRequired: true));
        h.Coordinator.OnNodeIdle(Node);
        Assert.Equal(MaintenanceState.Updating, h.Maintenance.Get(Node));

        h.Statuses.Apply(Event(MaintenanceEventKind.RebootRequired, rebootRequired: true,
            at: DateTimeOffset.UtcNow.AddMinutes(5)));

        Assert.Equal(MaintenanceState.Updating, h.Maintenance.Get(Node));
    }

    [Fact]
    public void Apply_Should_ReturnNodeToRotation_When_PostureClears()
    {
        var h = Build();
        h.Statuses.Apply(Event(MaintenanceEventKind.RebootRequired, rebootRequired: true));

        h.Statuses.Apply(Event(MaintenanceEventKind.RebootCleared, at: DateTimeOffset.UtcNow.AddMinutes(5)));

        Assert.Equal(MaintenanceState.None, h.Maintenance.Get(Node));
    }

    [Fact]
    public void OnNodeIdle_Should_PromoteDrainingToUpdating_And_Notify_When_RebootRequired()
    {
        var h = Build();
        h.Statuses.Apply(Event(MaintenanceEventKind.RebootRequired, rebootRequired: true));

        h.Coordinator.OnNodeIdle(Node);

        Assert.Equal(MaintenanceState.Updating, h.Maintenance.Get(Node));
        Assert.Contains(h.Notifications.Notifications, n => n.Title.Contains("ready to reboot"));
    }

    [Fact]
    public void OnNodeIdle_Should_DoNothing_When_NodeNotDraining()
    {
        var h = Build();
        h.Maintenance.Set(Node, MaintenanceState.Quarantined);

        h.Coordinator.OnNodeIdle(Node);

        Assert.Equal(MaintenanceState.Quarantined, h.Maintenance.Get(Node));
    }

    [Fact]
    public void Apply_Should_UseConfiguredEffect_When_PolicyBlocksOnPending()
    {
        var h = Build(new UpdatePolicy { PendingEffect = MaintenanceState.Updating });

        h.Statuses.Apply(Event(MaintenanceEventKind.UpdatePending, pending: 1));

        Assert.Equal(MaintenanceState.Updating, h.Maintenance.Get(Node));
    }

    [Fact]
    public void OnMaintenanceCompleted_Should_SuppressNode_When_OperationFinishes()
    {
        var h = Build();

        h.Coordinator.OnMaintenanceCompleted(Node);

        Assert.Equal(WindowsUpdateState.Suppressed, h.Statuses.Get(Node)!.State);
    }

    [Fact]
    public void Apply_Should_NotBlockDispatch_When_NodeSuppressedAfterMaintenance()
    {
        var h = Build();
        h.Coordinator.OnMaintenanceCompleted(Node);

        h.Statuses.Apply(Event(MaintenanceEventKind.RebootRequired, rebootRequired: true));

        Assert.Equal(MaintenanceState.None, h.Maintenance.Get(Node));
        Assert.Contains(h.Notifications.Notifications, n => n.IsSuppressed);
    }

    [Fact]
    public void Notify_Should_RaiseNotification_When_RebootRequiredDetected()
    {
        var h = Build();

        h.Statuses.Apply(Event(MaintenanceEventKind.RebootRequired, rebootRequired: true));

        var note = Assert.Single(h.Notifications.Notifications);
        Assert.Equal(MaintenanceEventKind.RebootRequired, note.Kind);
        Assert.Equal(Node, note.NodeId);
    }
}
