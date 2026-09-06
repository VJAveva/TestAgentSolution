using Moq;
using TestControllerGrpc.Core.Maintenance;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Maintenance;

public class MachineRebootOperationTests
{
    private const string Node = "JVKPRI";

    private sealed class Harness
    {
        public Mock<IAgentGrpcDispatcher> Dispatcher { get; } = new();
        public Mock<INodeReadinessProbe> Probe { get; } = new();
        public AgentLockManager LockManager { get; } = new();
        public ExecutionSessionManager SessionManager { get; } = new();
        public MaintenanceStateStore StateStore { get; } = new();
        public Mock<IMaintenanceOperationStore> OperationStore { get; } = new();

        public Harness()
        {
            Dispatcher.Setup(d => d.RegisteredAgents).Returns(new[] { Node });
            Dispatcher
                .Setup(d => d.ExecuteRemoteCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ActionResult(true, 0, ""));
            Probe.Setup(p => p.WaitForAgentAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ReadinessResult(true, TimeSpan.Zero, null));
        }

        public MachineRebootOperation Build() => new(
            Dispatcher.Object, Probe.Object, LockManager, SessionManager, StateStore, OperationStore.Object, new Mock<IAppLogger>().Object);
    }

    private static MaintenanceOperation Shell() => new()
    {
        Id = Guid.NewGuid(),
        NodeId = Node,
        Kind = MaintenanceKind.Reboot,
        TriggerSource = MaintenanceTriggerSource.FleetPanel,
        State = MaintenanceOperationState.Queued,
        StartedUtc = DateTimeOffset.UtcNow,
    };

    private static RebootRequest Request(bool force = false) => new()
    {
        NodeId = Node,
        TriggerSource = MaintenanceTriggerSource.FleetPanel,
        ForceIfBusy = force,
    };

    [Fact]
    public async Task ExecuteAsync_Should_CompleteAndReturnToRotation_When_NodeIdle()
    {
        var h = new Harness();

        var result = await h.Build().ExecuteAsync(Shell(), Request(), new Progress<MaintenanceProgress>(), CancellationToken.None);

        Assert.Equal(MaintenanceOperationState.Succeeded, result.State);
        Assert.Equal(RevertPhase.Verify, result.Phase);
        Assert.Equal(MaintenanceState.None, h.StateStore.Get(Node));
    }

    [Fact]
    public async Task ExecuteAsync_Should_RejectAtPrecheck_When_BusyAndNotForced()
    {
        var h = new Harness();
        h.LockManager.TryLockAgents(new[] { Node }, "sess1", "Nightly Regression", "user1", "WPF");

        var result = await h.Build().ExecuteAsync(Shell(), Request(force: false), new Progress<MaintenanceProgress>(), CancellationToken.None);

        Assert.Equal(MaintenanceOperationState.Failed, result.State);
        Assert.Equal(RevertPhase.Precheck, result.FailurePhase);
        Assert.Equal(MaintenanceState.None, h.StateStore.Get(Node));
        h.Dispatcher.Verify(
            d => d.ExecuteRemoteCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_Should_QuarantineAtPowerOn_When_RebootCommandFails()
    {
        var h = new Harness();
        h.Dispatcher
            .Setup(d => d.ExecuteRemoteCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActionResult(false, 1, "reboot rejected"));

        var result = await h.Build().ExecuteAsync(Shell(), Request(), new Progress<MaintenanceProgress>(), CancellationToken.None);

        Assert.Equal(MaintenanceOperationState.Failed, result.State);
        Assert.Equal(RevertPhase.PowerOn, result.FailurePhase);
        Assert.Equal(MaintenanceState.Quarantined, h.StateStore.Get(Node));
    }

    [Fact]
    public async Task ExecuteAsync_Should_QuarantineAtAgentWait_When_AgentNeverReconnects()
    {
        var h = new Harness();
        h.Probe.Setup(p => p.WaitForAgentAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReadinessResult(false, TimeSpan.FromMinutes(15), "Agent did not reconnect."));

        var result = await h.Build().ExecuteAsync(Shell(), Request(), new Progress<MaintenanceProgress>(), CancellationToken.None);

        Assert.Equal(MaintenanceOperationState.Failed, result.State);
        Assert.Equal(RevertPhase.AgentWait, result.FailurePhase);
        Assert.Equal(MaintenanceState.Quarantined, h.StateStore.Get(Node));
    }
}
