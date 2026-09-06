using TestControllerGrpc.Core.Maintenance;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Maintenance;

public class DispatchGateTests
{
    private const string Node = "JVKPRI";

    [Fact]
    public void IsDispatchable_Should_BeTrue_When_ConnectedIdleAndNone()
    {
        var state = new MaintenanceStateStore();
        var locks = new AgentLockManager();

        Assert.True(DispatchGate.IsDispatchable(state, locks, Node, isConnected: true));
    }

    [Fact]
    public void IsDispatchable_Should_BeFalse_When_Disconnected()
    {
        var state = new MaintenanceStateStore();
        var locks = new AgentLockManager();

        Assert.False(DispatchGate.IsDispatchable(state, locks, Node, isConnected: false));
    }

    [Fact]
    public void IsDispatchable_Should_BeFalse_When_Reverting()
    {
        var state = new MaintenanceStateStore();
        state.Set(Node, MaintenanceState.Reverting);
        var locks = new AgentLockManager();

        Assert.False(DispatchGate.IsDispatchable(state, locks, Node, isConnected: true));
    }

    [Fact]
    public void IsDispatchable_Should_BeFalse_When_Busy()
    {
        var state = new MaintenanceStateStore();
        var locks = new AgentLockManager();
        locks.TryLockAgents(new[] { Node }, "sess1", "WatchItemX", "user1", "WPF");

        Assert.False(DispatchGate.IsDispatchable(state, locks, Node, isConnected: true));
    }

    [Fact]
    public void GetMaintenanceBlockers_Should_NameRevertingNode()
    {
        var state = new MaintenanceStateStore();
        state.Set(Node, MaintenanceState.Reverting);

        var result = DispatchGate.GetMaintenanceBlockers(state, new[] { Node, "OTHER" });

        Assert.NotNull(result);
        var block = Assert.Single(result!.BlockedNodes);
        Assert.Equal(Node, block.NodeId);
        Assert.Equal("Reverting", block.Reason);
    }

    [Fact]
    public void GetMaintenanceBlockers_Should_ReturnNull_When_AllDispatchable()
    {
        var state = new MaintenanceStateStore();

        var result = DispatchGate.GetMaintenanceBlockers(state, new[] { Node, "OTHER" });

        Assert.Null(result);
    }
}
