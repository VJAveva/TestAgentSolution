using TestControllerGrpc.Core.Maintenance;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Maintenance;

public class WindowsUpdateTests
{
    private const string Node = "JVKPRI";

    private static NodeMaintenanceEventDto Event(
        MaintenanceEventKind kind, bool rebootRequired = false, int pending = 0,
        DateTimeOffset? at = null, MaintenanceEventSource source = MaintenanceEventSource.RegistryPoll)
        => new()
        {
            NodeId = Node,
            Kind = kind,
            Source = source,
            Status = new WindowsUpdateStatusDto { RebootRequired = rebootRequired, PendingCount = pending },
            DetectedUtc = at ?? DateTimeOffset.UtcNow,
        };

    [Fact]
    public void Apply_Should_SetRebootRequiredAndRaiseChanged_When_RebootRequiredEvent()
    {
        var store = new NodeUpdateStatusStore();
        NodeUpdateStatusChanged? change = null;
        store.Changed += (_, e) => change = e;

        store.Apply(Event(MaintenanceEventKind.RebootRequired, rebootRequired: true));

        Assert.Equal(WindowsUpdateState.RebootRequired, store.Get(Node)!.State);
        Assert.Equal(WindowsUpdateState.RebootRequired, change!.Current);
    }

    [Fact]
    public void Apply_Should_BeIdempotent_When_SameEventDeliveredTwice()
    {
        var store = new NodeUpdateStatusStore();
        var count = 0;
        store.Changed += (_, _) => count++;
        var evt = Event(MaintenanceEventKind.RebootRequired, rebootRequired: true, at: DateTimeOffset.UtcNow);

        store.Apply(evt);
        store.Apply(evt);

        Assert.Equal(1, count);
    }

    [Fact]
    public void Apply_Should_IgnoreOlderEvent_When_TimestampNotNewer()
    {
        var store = new NodeUpdateStatusStore();
        var now = DateTimeOffset.UtcNow;
        store.Apply(Event(MaintenanceEventKind.RebootRequired, rebootRequired: true, at: now));
        store.Apply(Event(MaintenanceEventKind.RebootRequired, rebootRequired: false, at: now.AddMinutes(-5)));

        Assert.Equal(WindowsUpdateState.RebootRequired, store.Get(Node)!.State);
    }

    [Fact]
    public void Suppress_Should_HoldStateSuppressed_When_EventArrivesInWindow()
    {
        var store = new NodeUpdateStatusStore();
        store.Apply(Event(MaintenanceEventKind.UpdatePending, pending: 1));
        store.Suppress(Node, DateTimeOffset.UtcNow.AddMinutes(60));

        Assert.Equal(WindowsUpdateState.Suppressed, store.Get(Node)!.State);

        store.Apply(Event(MaintenanceEventKind.RebootRequired, rebootRequired: true, at: DateTimeOffset.UtcNow.AddSeconds(1)));

        Assert.Equal(WindowsUpdateState.Suppressed, store.Get(Node)!.State);
    }

    [Theory]
    [InlineData(WindowsUpdateState.RebootRequired, MaintenanceState.Draining)]
    [InlineData(WindowsUpdateState.UpdateInstalling, MaintenanceState.Updating)]
    [InlineData(WindowsUpdateState.UpdatePending, MaintenanceState.None)]
    [InlineData(WindowsUpdateState.UpToDate, MaintenanceState.None)]
    public void Evaluate_Should_MapPerDefaultPolicy(WindowsUpdateState state, MaintenanceState expected)
    {
        var evaluator = new UpdatePolicyEvaluator();
        var status = new NodeUpdateStatus { NodeId = Node, State = state, LastSource = MaintenanceEventSource.RegistryPoll };

        Assert.Equal(expected, evaluator.Evaluate(status, new UpdatePolicy()));
    }

    [Fact]
    public void DispatchGate_Should_ReturnDraining_And_NotDispatchable_When_NodeDraining()
    {
        var stateStore = new MaintenanceStateStore();
        stateStore.Set(Node, MaintenanceState.Draining);
        var locks = new AgentLockManager();

        Assert.Equal(DispatchEligibility.Draining, DispatchGate.Evaluate(stateStore, locks, Node, isConnected: true));
        Assert.False(DispatchGate.IsDispatchable(stateStore, locks, Node, isConnected: true));
    }
}
