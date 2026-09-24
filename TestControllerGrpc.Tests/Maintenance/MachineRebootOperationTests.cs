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

    [Theory]
    [InlineData(MaintenanceState.Draining)]
    [InlineData(MaintenanceState.Updating)]
    public async Task ExecuteAsync_Should_Proceed_When_NodeAwaitsRebootFromUpdatePosture(MaintenanceState posture)
    {
        var h = new Harness();
        h.StateStore.Set(Node, posture);

        var result = await h.Build().ExecuteAsync(Shell(), Request(), new Progress<MaintenanceProgress>(), CancellationToken.None);

        Assert.Equal(MaintenanceOperationState.Succeeded, result.State);
        Assert.Equal(MaintenanceState.None, h.StateStore.Get(Node));
        h.Dispatcher.Verify(
            d => d.ExecuteRemoteCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData(MaintenanceState.Reverting)]
    [InlineData(MaintenanceState.Rebooting)]
    [InlineData(MaintenanceState.Quarantined)]
    public async Task ExecuteAsync_Should_RejectAtPrecheck_When_NodeHeldByAnotherOperation(MaintenanceState held)
    {
        var h = new Harness();
        h.StateStore.Set(Node, held);

        var result = await h.Build().ExecuteAsync(Shell(), Request(), new Progress<MaintenanceProgress>(), CancellationToken.None);

        Assert.Equal(MaintenanceOperationState.Failed, result.State);
        Assert.Equal(RevertPhase.Precheck, result.FailurePhase);
        Assert.Equal(held, h.StateStore.Get(Node));
        h.Dispatcher.Verify(
            d => d.ExecuteRemoteCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ---- Wave 0: failure and cancel branches -------------------------------

    [Fact]
    public async Task ExecuteAsync_Should_RejectAtPrecheck_When_NodeIsNotRegistered()
    {
        var h = new Harness();
        h.Dispatcher.Setup(d => d.RegisteredAgents).Returns(Array.Empty<string>());

        var result = await h.Build().ExecuteAsync(Shell(), Request(), new Progress<MaintenanceProgress>(), CancellationToken.None);

        Assert.Equal(MaintenanceOperationState.Failed, result.State);
        Assert.Equal(RevertPhase.Precheck, result.FailurePhase);
        // Precheck must not leave a state behind - that is the F-2 class of stranding.
        Assert.Equal(MaintenanceState.None, h.StateStore.Get(Node));
    }

    [Fact]
    public async Task ExecuteAsync_Should_AbortTheRunningSession_When_ForcedWhileBusy()
    {
        var h = new Harness();
        h.LockManager.TryLockAgents([Node], "sess1", "Nightly Regression", "user1", "WPF");

        var result = await h.Build().ExecuteAsync(Shell(), Request(force: true), new Progress<MaintenanceProgress>(), CancellationToken.None);

        Assert.Equal(MaintenanceOperationState.Succeeded, result.State);
        // The lock must be gone, or the node returns to rotation still owned by a dead session.
        Assert.Null(h.LockManager.GetLock(Node));
    }

    [Fact]
    public async Task ExecuteAsync_Should_QuarantineAtPowerOn_When_TheRebootCommandThrows()
    {
        var h = new Harness();
        h.Dispatcher
            .Setup(d => d.ExecuteRemoteCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("channel died"));

        var result = await h.Build().ExecuteAsync(Shell(), Request(), new Progress<MaintenanceProgress>(), CancellationToken.None);

        Assert.Equal(MaintenanceOperationState.Failed, result.State);
        Assert.Equal(RevertPhase.PowerOn, result.FailurePhase);
        Assert.Equal(MaintenanceState.Quarantined, h.StateStore.Get(Node));
    }

    [Fact]
    public async Task ExecuteAsync_Should_CancelAndQuarantine_When_CancelledBeforeTheAgentWait()
    {
        // CancelAsync had no coverage at all. A cancelled reboot must still quarantine: the machine was told
        // to restart, so leaving it in rotation would hand work to a node that is about to disappear.
        var h = new Harness();
        using var cts = new CancellationTokenSource();
        h.Dispatcher
            .Setup(d => d.ExecuteRemoteCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns(() => { cts.Cancel(); return Task.FromResult(new ActionResult(true, 0, "")); });

        var result = await h.Build().ExecuteAsync(Shell(), Request(), new Progress<MaintenanceProgress>(), cts.Token);

        Assert.Equal(MaintenanceOperationState.Cancelled, result.State);
        Assert.Equal(RevertPhase.AgentWait, result.FailurePhase);
        Assert.Equal(MaintenanceState.Quarantined, h.StateStore.Get(Node));
    }

    [Fact]
    public async Task ExecuteAsync_Should_CancelAndQuarantine_When_CancelledDuringTheAgentWait()
    {
        var h = new Harness();
        using var cts = new CancellationTokenSource();
        h.Probe.Setup(p => p.WaitForAgentAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(() => { cts.Cancel(); return Task.FromResult(new ReadinessResult(true, TimeSpan.Zero, null)); });

        var result = await h.Build().ExecuteAsync(Shell(), Request(), new Progress<MaintenanceProgress>(), cts.Token);

        Assert.Equal(MaintenanceOperationState.Cancelled, result.State);
        Assert.Equal(MaintenanceState.Quarantined, h.StateStore.Get(Node));
    }

    [Fact]
    public async Task ExecuteAsync_Should_QuarantineAndFail_When_AnUnexpectedExceptionEscapes()
    {
        // The outer catch is the last line of defence: whatever throws, the node must not be left mid-flight.
        var h = new Harness();
        h.Probe.Setup(p => p.WaitForAgentAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("probe exploded"));

        var result = await h.Build().ExecuteAsync(Shell(), Request(), new Progress<MaintenanceProgress>(), CancellationToken.None);

        Assert.Equal(MaintenanceOperationState.Failed, result.State);
        Assert.Equal(MaintenanceState.Quarantined, h.StateStore.Get(Node));
    }

    // ---- F-1: quarantine, then the agent comes back healthy ----------------

    [Fact]
    public async Task Quarantine_Should_Persist_When_TheAgentLaterReturnsHealthy()
    {
        // F-1. A node quarantined by a timeout must NOT silently return to rotation just because the agent
        // reconnected - the reason it was quarantined was never established. Quarantine is sticky by design
        // and ClearQuarantineAsync is its only exit; an operator must see it.
        var h = new Harness();
        h.Probe.Setup(p => p.WaitForAgentAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReadinessResult(false, TimeSpan.FromMinutes(15), "Agent did not reconnect."));

        var first = await h.Build().ExecuteAsync(Shell(), Request(), new Progress<MaintenanceProgress>(), CancellationToken.None);
        Assert.Equal(MaintenanceState.Quarantined, h.StateStore.Get(Node));

        // The agent is now healthy again, and a fresh reboot is attempted.
        h.Probe.Setup(p => p.WaitForAgentAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReadinessResult(true, TimeSpan.Zero, null));

        var second = await h.Build().ExecuteAsync(Shell(), Request(), new Progress<MaintenanceProgress>(), CancellationToken.None);

        Assert.Equal(RevertPhase.AgentWait, first.FailurePhase);
        Assert.Equal(MaintenanceOperationState.Failed, second.State);
        Assert.Equal(RevertPhase.Precheck, second.FailurePhase);
        Assert.Equal(MaintenanceState.Quarantined, h.StateStore.Get(Node));
    }
}
