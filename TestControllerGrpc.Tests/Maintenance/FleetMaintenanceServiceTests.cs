using Moq;
using TestControllerGrpc.Core.Maintenance;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Maintenance;

public class FleetMaintenanceServiceTests
{
    private const string Node = "JVKPRI";

    private static FleetMaintenanceService Build(
        IMachineRevertOperation revertOperation,
        IAgentGrpcDispatcher dispatcher,
        AgentLockManager lockManager,
        IMaintenanceStateStore stateStore,
        IMachineRebootOperation? rebootOperation = null,
        UpdatePolicy? policy = null)
        => new(revertOperation, rebootOperation ?? new Mock<IMachineRebootOperation>().Object, dispatcher, lockManager,
            stateStore, new Mock<IMaintenanceOperationStore>().Object, new UpdatePolicyStore(policy),
            new Mock<IAppLogger>().Object);

    [Fact]
    public async Task StartRevertAsync_Should_ThrowMaintenanceInProgress_When_SecondRevertOnSameNode()
    {
        var gate = new TaskCompletionSource<MaintenanceOperation>();
        var revertOp = new Mock<IMachineRevertOperation>();
        revertOp
            .Setup(o => o.ExecuteAsync(It.IsAny<MaintenanceOperation>(), It.IsAny<RevertRequest>(),
                It.IsAny<IProgress<MaintenanceProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(gate.Task);  // keeps the first operation active

        var sut = Build(revertOp.Object, new Mock<IAgentGrpcDispatcher>().Object, new AgentLockManager(), new MaintenanceStateStore());
        var request = new RevertRequest { NodeId = Node, SnapshotName = "snap", TriggerSource = MaintenanceTriggerSource.WebApi };

        await sut.StartRevertAsync(request, CancellationToken.None);

        await Assert.ThrowsAsync<MaintenanceInProgressException>(() => sut.StartRevertAsync(request, CancellationToken.None));

        gate.SetResult(new MaintenanceOperation
        {
            Id = Guid.NewGuid(),
            NodeId = Node,
            Kind = MaintenanceKind.Revert,
            TriggerSource = MaintenanceTriggerSource.WebApi,
        });
    }

    [Fact]
    public async Task PrecheckAsync_Should_ReportBusyWatchItemAndNotDispatchable_When_NodeLocked()
    {
        var lockManager = new AgentLockManager();
        lockManager.TryLockAgents(new[] { Node }, "sess1", "Nightly Regression", "user1", "WPF");

        var dispatcher = new Mock<IAgentGrpcDispatcher>();
        dispatcher.Setup(d => d.RegisteredAgents).Returns(new[] { Node });

        var sut = Build(new Mock<IMachineRevertOperation>().Object, dispatcher.Object, lockManager, new MaintenanceStateStore());

        var result = await sut.PrecheckAsync(new[] { Node }, CancellationToken.None);

        var precheck = Assert.Single(result.Nodes);
        Assert.False(precheck.IsDispatchable);
        Assert.Equal("Nightly Regression", precheck.RunningWatchItem);
        Assert.Equal(DispatchEligibility.Blocked, precheck.Eligibility);
    }

    [Fact]
    public async Task PrecheckAsync_Should_ReportDraining_When_NodeIsDraining()
    {
        var stateStore = new MaintenanceStateStore();
        stateStore.Set(Node, MaintenanceState.Draining);

        var dispatcher = new Mock<IAgentGrpcDispatcher>();
        dispatcher.Setup(d => d.RegisteredAgents).Returns(new[] { Node });

        var sut = Build(new Mock<IMachineRevertOperation>().Object, dispatcher.Object, new AgentLockManager(), stateStore);

        var result = await sut.PrecheckAsync(new[] { Node }, CancellationToken.None);

        var precheck = Assert.Single(result.Nodes);
        Assert.Equal(DispatchEligibility.Draining, precheck.Eligibility);
        Assert.False(precheck.IsDispatchable);
    }

    [Fact]
    public async Task StartRebootAsync_Should_QueueSecondReboot_When_MaxConcurrentRebootsIsOne()
    {
        var gate = new TaskCompletionSource<MaintenanceOperation>();
        // The engine is invoked on a pool thread, so wait for it rather than racing the assertion.
        var firstStarted = new TaskCompletionSource();
        var rebootOp = new Mock<IMachineRebootOperation>();
        rebootOp
            .Setup(o => o.ExecuteAsync(It.IsAny<MaintenanceOperation>(), It.IsAny<RebootRequest>(),
                It.IsAny<IProgress<MaintenanceProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(() => { firstStarted.TrySetResult(); return gate.Task; });

        var sut = Build(new Mock<IMachineRevertOperation>().Object, new Mock<IAgentGrpcDispatcher>().Object,
            new AgentLockManager(), new MaintenanceStateStore(), rebootOp.Object,
            new UpdatePolicy { MaxConcurrentReboots = 1 });

        await sut.StartRebootAsync(Reboot("NodeA"), CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await sut.StartRebootAsync(Reboot("NodeB"), CancellationToken.None);

        // Both are tracked, but only the first has been handed to the engine.
        Assert.Equal(2, sut.ActiveOperations.Count);
        rebootOp.Verify(o => o.ExecuteAsync(It.IsAny<MaintenanceOperation>(), It.IsAny<RebootRequest>(),
            It.IsAny<IProgress<MaintenanceProgress>>(), It.IsAny<CancellationToken>()), Times.Once);

        gate.SetResult(new MaintenanceOperation
        {
            Id = Guid.NewGuid(),
            NodeId = "NodeA",
            Kind = MaintenanceKind.Reboot,
            TriggerSource = MaintenanceTriggerSource.WebApi,
        });
    }

    private static RebootRequest Reboot(string nodeId) => new()
    {
        NodeId = nodeId,
        TriggerSource = MaintenanceTriggerSource.WebApi,
    };

    [Fact]
    public async Task ClearQuarantineAsync_Should_ReturnNodeToRotation_When_Quarantined()
    {
        var stateStore = new MaintenanceStateStore();
        stateStore.Set(Node, MaintenanceState.Quarantined);

        var sut = Build(new Mock<IMachineRevertOperation>().Object, new Mock<IAgentGrpcDispatcher>().Object, new AgentLockManager(), stateStore);

        await sut.ClearQuarantineAsync(Node, "operator1");

        Assert.Equal(MaintenanceState.None, stateStore.Get(Node));
    }
}
