using TestControllerGrpc.Core.Maintenance;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Maintenance;

/// <summary>
/// A node being reverted, rebooted or updated is expected to be unreachable. Alerting on that pages an
/// operator for every planned maintenance operation, which trains them to ignore the alert that matters.
/// <see cref="AgentLivenessMonitor"/> already applies this rule to dispatch; these guard the alert path.
/// </summary>
public class FleetAlertMaintenanceSuppressionTests
{
    private const string Node = "JVKPRI";

    [Theory]
    [InlineData(MaintenanceState.Reverting)]
    [InlineData(MaintenanceState.Rebooting)]
    [InlineData(MaintenanceState.Updating)]
    [InlineData(MaintenanceState.Draining)]
    [InlineData(MaintenanceState.Quarantined)]
    public void IsUnderMaintenance_Should_SuppressAlert_When_NodeNotIdle(MaintenanceState state)
    {
        var store = new MaintenanceStateStore();
        store.Set(Node, state);

        Assert.True(FleetAlertDispatcher.IsUnderMaintenance(store, Node));
    }

    [Fact]
    public void IsUnderMaintenance_Should_AllowAlert_When_NodeIdle()
    {
        var store = new MaintenanceStateStore();
        store.Set(Node, MaintenanceState.None);

        // A genuinely dead agent must still page someone.
        Assert.False(FleetAlertDispatcher.IsUnderMaintenance(store, Node));
    }

    [Fact]
    public void IsUnderMaintenance_Should_AllowAlert_When_NodeUnknownToStore()
    {
        Assert.False(FleetAlertDispatcher.IsUnderMaintenance(new MaintenanceStateStore(), "NEVER-SEEN"));
    }

    [Fact]
    public void IsUnderMaintenance_Should_AllowAlert_When_StoreUnavailable()
    {
        // The WebApi host has no maintenance store; alerting must not silently switch off there.
        Assert.False(FleetAlertDispatcher.IsUnderMaintenance(null, Node));
    }
}
