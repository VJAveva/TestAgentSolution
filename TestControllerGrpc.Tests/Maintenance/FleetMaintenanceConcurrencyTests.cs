using Moq;
using TestControllerGrpc.Core.Maintenance;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Maintenance;

/// <summary>
/// Phase 0c item 4: the reboot queue has only ever been exercised at a cap of 1 on a 9-node fleet. These pin
/// the behaviour a 50-node patch window depends on — the cap is never exceeded, and every queued node is
/// eventually started as slots free.
/// </summary>
public class FleetMaintenanceConcurrencyTests
{
    /// <summary>Tracks concurrent executions so the test can assert a ceiling, not just a final count.</summary>
    private sealed class StartTracker
    {
        private readonly object _gate = new();
        private int _inFlight;
        private int _target = int.MaxValue;
        private TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Started { get; private set; }
        public int Peak { get; private set; }

        public void Enter()
        {
            lock (_gate)
            {
                Started++;
                _inFlight++;
                if (_inFlight > Peak) Peak = _inFlight;
                if (Started >= _target) _reached.TrySetResult();
            }
        }

        public void Exit()
        {
            lock (_gate) { _inFlight--; }
        }

        public Task WaitForStarts(int target)
        {
            lock (_gate)
            {
                _target = target;
                if (Started >= target) return Task.CompletedTask;
                _reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return _reached.Task;
            }
        }
    }

    private static RebootRequest Reboot(string node) => new()
    {
        NodeId = node,
        TriggerSource = MaintenanceTriggerSource.FleetPanel,
        TriggeredBy = "test",
    };

    private static MaintenanceOperation Completed(string node) => new()
    {
        Id = Guid.NewGuid(),
        NodeId = node,
        Kind = MaintenanceKind.Reboot,
        State = MaintenanceOperationState.Succeeded,
        TriggerSource = MaintenanceTriggerSource.FleetPanel,
    };

    private static FleetMaintenanceService Build(IMachineRebootOperation rebootOp, int maxConcurrent)
        => new(new Mock<IMachineRevertOperation>().Object, rebootOp,
            new Mock<IAgentGrpcDispatcher>().Object, new AgentLockManager(), new MaintenanceStateStore(),
            new Mock<IMaintenanceOperationStore>().Object,
            new UpdatePolicyStore(new UpdatePolicy { MaxConcurrentReboots = maxConcurrent }),
            new Mock<IAppLogger>().Object);

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(10)]
    public async Task StartRebootAsync_Should_NeverExceedCap_When_FiftyNodesQueuedAtOnce(int cap)
    {
        const int fleet = 50;
        var tracker = new StartTracker();
        var gates = new Dictionary<string, TaskCompletionSource<MaintenanceOperation>>();
        for (var i = 0; i < fleet; i++)
            gates[$"NODE{i:00}"] = new TaskCompletionSource<MaintenanceOperation>(TaskCreationOptions.RunContinuationsAsynchronously);

        var rebootOp = new Mock<IMachineRebootOperation>();
        rebootOp
            .Setup(o => o.ExecuteAsync(It.IsAny<MaintenanceOperation>(), It.IsAny<RebootRequest>(),
                It.IsAny<IProgress<MaintenanceProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((MaintenanceOperation op, RebootRequest req, IProgress<MaintenanceProgress> _, CancellationToken __) =>
            {
                tracker.Enter();
                return gates[req.NodeId].Task.ContinueWith(t => { tracker.Exit(); return t.Result; },
                    TaskScheduler.Default);
            });

        var sut = Build(rebootOp.Object, cap);

        foreach (var node in gates.Keys)
            await sut.StartRebootAsync(Reboot(node), CancellationToken.None);

        // Exactly the cap should be in flight; the remainder are tracked but not handed to the engine.
        await tracker.WaitForStarts(cap).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(fleet, sut.ActiveOperations.Count);
        Assert.Equal(cap, tracker.Peak);

        // Release everything; the pump must drain the queue without ever overshooting the cap.
        foreach (var (node, gate) in gates)
            gate.TrySetResult(Completed(node));

        await tracker.WaitForStarts(fleet).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(fleet, tracker.Started);
        Assert.True(tracker.Peak <= cap, $"peak concurrency {tracker.Peak} exceeded the cap of {cap}");
    }

    [Fact]
    public async Task PumpQueues_Should_StartQueuedNode_When_RunningOperationCompletes()
    {
        var tracker = new StartTracker();
        var first = new TaskCompletionSource<MaintenanceOperation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<MaintenanceOperation>(TaskCreationOptions.RunContinuationsAsynchronously);

        var rebootOp = new Mock<IMachineRebootOperation>();
        rebootOp
            .Setup(o => o.ExecuteAsync(It.IsAny<MaintenanceOperation>(), It.IsAny<RebootRequest>(),
                It.IsAny<IProgress<MaintenanceProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((MaintenanceOperation op, RebootRequest req, IProgress<MaintenanceProgress> _, CancellationToken __) =>
            {
                tracker.Enter();
                var gate = req.NodeId == "NodeA" ? first : second;
                return gate.Task.ContinueWith(t => { tracker.Exit(); return t.Result; }, TaskScheduler.Default);
            });

        var sut = Build(rebootOp.Object, maxConcurrent: 1);

        await sut.StartRebootAsync(Reboot("NodeA"), CancellationToken.None);
        await tracker.WaitForStarts(1).WaitAsync(TimeSpan.FromSeconds(10));

        await sut.StartRebootAsync(Reboot("NodeB"), CancellationToken.None);
        Assert.Equal(1, tracker.Started); // NodeB is queued, not started

        first.TrySetResult(Completed("NodeA"));

        // Completing NodeA must free the slot and start NodeB without any further caller action.
        await tracker.WaitForStarts(2).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, tracker.Started);
        Assert.Equal(1, tracker.Peak);

        second.TrySetResult(Completed("NodeB"));
    }

    [Fact]
    public async Task StartRebootAsync_Should_TreatZeroCapAsOne_When_PolicyMisconfigured()
    {
        var tracker = new StartTracker();
        var gate = new TaskCompletionSource<MaintenanceOperation>(TaskCreationOptions.RunContinuationsAsynchronously);

        var rebootOp = new Mock<IMachineRebootOperation>();
        rebootOp
            .Setup(o => o.ExecuteAsync(It.IsAny<MaintenanceOperation>(), It.IsAny<RebootRequest>(),
                It.IsAny<IProgress<MaintenanceProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((MaintenanceOperation op, RebootRequest req, IProgress<MaintenanceProgress> _, CancellationToken __) =>
            {
                tracker.Enter();
                return gate.Task.ContinueWith(t => { tracker.Exit(); return t.Result; }, TaskScheduler.Default);
            });

        // A cap of 0 must not deadlock the fleet: the engine clamps it to 1.
        var sut = Build(rebootOp.Object, maxConcurrent: 0);

        await sut.StartRebootAsync(Reboot("NodeA"), CancellationToken.None);
        await tracker.WaitForStarts(1).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, tracker.Started);
        gate.TrySetResult(Completed("NodeA"));
    }

    [Fact]
    public async Task StartRebootAsync_Should_HoldCap_When_CallersStartSimultaneously()
    {
        const int fleet = 32;
        const int cap = 4;
        var tracker = new StartTracker();
        var gates = new Dictionary<string, TaskCompletionSource<MaintenanceOperation>>();
        for (var i = 0; i < fleet; i++)
            gates[$"NODE{i:00}"] = new TaskCompletionSource<MaintenanceOperation>(TaskCreationOptions.RunContinuationsAsynchronously);

        var rebootOp = new Mock<IMachineRebootOperation>();
        rebootOp
            .Setup(o => o.ExecuteAsync(It.IsAny<MaintenanceOperation>(), It.IsAny<RebootRequest>(),
                It.IsAny<IProgress<MaintenanceProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((MaintenanceOperation op, RebootRequest req, IProgress<MaintenanceProgress> _, CancellationToken __) =>
            {
                tracker.Enter();
                return gates[req.NodeId].Task.ContinueWith(t => { tracker.Exit(); return t.Result; },
                    TaskScheduler.Default);
            });

        var sut = Build(rebootOp.Object, cap);

        // The sequential tests never exercise the count-then-start race. Releasing every caller at once does:
        // without a lock two callers both read "under cap" and both start, overshooting it.
        using var release = new SemaphoreSlim(0, fleet);
        var callers = gates.Keys
            .Select(node => Task.Run(async () =>
            {
                await release.WaitAsync();
                await sut.StartRebootAsync(Reboot(node), CancellationToken.None);
            }))
            .ToArray();

        release.Release(fleet);
        await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(30));

        await tracker.WaitForStarts(cap).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(tracker.Peak <= cap, $"peak concurrency {tracker.Peak} exceeded the cap of {cap}");

        foreach (var (node, gate) in gates)
            gate.TrySetResult(Completed(node));

        await tracker.WaitForStarts(fleet).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(fleet, tracker.Started);
        Assert.True(tracker.Peak <= cap, $"peak concurrency {tracker.Peak} exceeded the cap of {cap}");
    }

    [Fact]
    public async Task StartRevertAsync_Should_StayUncapped_When_CapIsKindAware()
    {
        const int fleet = 6;
        var tracker = new StartTracker();
        var gates = new Dictionary<string, TaskCompletionSource<MaintenanceOperation>>();
        for (var i = 0; i < fleet; i++)
            gates[$"NODE{i:00}"] = new TaskCompletionSource<MaintenanceOperation>(TaskCreationOptions.RunContinuationsAsynchronously);

        var revertOp = new Mock<IMachineRevertOperation>();
        revertOp
            .Setup(o => o.ExecuteAsync(It.IsAny<MaintenanceOperation>(), It.IsAny<RevertRequest>(),
                It.IsAny<IProgress<MaintenanceProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((MaintenanceOperation op, RevertRequest req, IProgress<MaintenanceProgress> _, CancellationToken __) =>
            {
                tracker.Enter();
                return gates[req.NodeId].Task.ContinueWith(t => { tracker.Exit(); return t.Result; },
                    TaskScheduler.Default);
            });

        // Revert is deliberately absent from CapFor, so routing it through the shared throttle must not
        // start capping it — that would silently change behaviour already in production.
        var sut = new FleetMaintenanceService(revertOp.Object, new Mock<IMachineRebootOperation>().Object,
            new Mock<IAgentGrpcDispatcher>().Object, new AgentLockManager(), new MaintenanceStateStore(),
            new Mock<IMaintenanceOperationStore>().Object,
            new UpdatePolicyStore(new UpdatePolicy { MaxConcurrentReboots = 1, MaxConcurrentRefreshes = 1 }),
            new Mock<IAppLogger>().Object);

        foreach (var node in gates.Keys)
            await sut.StartRevertAsync(new RevertRequest
            {
                NodeId = node,
                SnapshotName = "baseline",
                TriggerSource = MaintenanceTriggerSource.FleetPanel,
                TriggeredBy = "test",
            }, CancellationToken.None);

        await tracker.WaitForStarts(fleet).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(fleet, tracker.Peak);

        foreach (var (node, gate) in gates)
            gate.TrySetResult(Completed(node));
    }
}
