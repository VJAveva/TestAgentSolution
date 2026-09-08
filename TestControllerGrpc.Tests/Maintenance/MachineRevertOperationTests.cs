using Moq;
using TestControllerGrpc.Core.Maintenance;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Maintenance;

public class MachineRevertOperationTests
{
    private const string Node = "JVKPRI";

    private sealed class Harness
    {
        public Mock<IAgentGrpcDispatcher> Dispatcher { get; } = new();
        public Mock<IPowerShellScriptRunner> ScriptRunner { get; } = new();
        public Mock<INodeReadinessProbe> Probe { get; } = new();
        public AgentLockManager LockManager { get; } = new();
        public ExecutionSessionManager SessionManager { get; } = new();
        public MaintenanceStateStore StateStore { get; } = new();
        public Mock<IMaintenanceOperationStore> OperationStore { get; } = new();
        public MaintenanceOptions Options { get; }
        public List<ScriptInvocation> Invocations { get; } = new();

        public Harness(string? revertScriptPath = null)
        {
            var dir = Path.Combine(Path.GetTempPath(), "maint-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            // Default: a real stub script on disk so the File.Exists precheck passes and tests exercise the full
            // pipeline. Precheck tests pass an explicit "" or a non-existent path to assert the guard rejects them.
            string scriptPath;
            if (revertScriptPath is null)
            {
                scriptPath = Path.Combine(dir, "revert.ps1");
                File.WriteAllText(scriptPath, "# test stub");
            }
            else
            {
                scriptPath = revertScriptPath;
            }

            Options = new MaintenanceOptions
            {
                RevertScriptPath = scriptPath,
                LogDirectory = dir,
            };

            Dispatcher.Setup(d => d.RegisteredAgents).Returns(new[] { Node });
            Dispatcher.Setup(d => d.GetAgentAddress(Node)).Returns($"https://{Node}:5001");

            ScriptRunner
                .Setup(r => r.RunAsync(It.IsAny<ScriptInvocation>(), It.IsAny<IProgress<ScriptOutputLine>>(), It.IsAny<CancellationToken>()))
                .Callback<ScriptInvocation, IProgress<ScriptOutputLine>, CancellationToken>((inv, _, _) => Invocations.Add(inv))
                .ReturnsAsync(new ScriptResult(0, TimeSpan.Zero, false));

            Probe.Setup(p => p.WaitForPingAsync(It.IsAny<string>(), It.IsAny<PingOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ReadinessResult(true, TimeSpan.Zero, null));
            Probe.Setup(p => p.WaitForAgentAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ReadinessResult(true, TimeSpan.Zero, null));
        }

        public MachineRevertOperation Build() => new(
            Dispatcher.Object, ScriptRunner.Object, Probe.Object, LockManager, SessionManager,
            StateStore, OperationStore.Object, Options, new Mock<IAppLogger>().Object);
    }

    private static MaintenanceOperation Shell() => new()
    {
        Id = Guid.NewGuid(),
        NodeId = Node,
        Kind = MaintenanceKind.Revert,
        TriggerSource = MaintenanceTriggerSource.FleetPanel,
        State = MaintenanceOperationState.Queued,
        StartedUtc = DateTimeOffset.UtcNow,
    };

    private static RevertRequest Request(bool force = false, bool waitForAgent = true, string snapshot = "snap 1") => new()
    {
        NodeId = Node,
        SnapshotName = snapshot,
        TriggerSource = MaintenanceTriggerSource.FleetPanel,
        ForceIfBusy = force,
        WaitForAgent = waitForAgent,
    };

    [Fact]
    public async Task ExecuteAsync_Should_CompleteAllPhasesAndReturnToRotation_When_NodeIdle()
    {
        var h = new Harness();

        var result = await h.Build().ExecuteAsync(Shell(), Request(), new Progress<MaintenanceProgress>(), CancellationToken.None);

        Assert.Equal(MaintenanceOperationState.Succeeded, result.State);
        Assert.Equal(RevertPhase.Verify, result.Phase);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(MaintenanceState.None, h.StateStore.Get(Node));
    }

    [Fact]
    public async Task ExecuteAsync_Should_ForwardSnapshotNameVerbatim_When_NameContainsSpaces()
    {
        var h = new Harness();

        await h.Build().ExecuteAsync(Shell(), Request(snapshot: "snap 1"), new Progress<MaintenanceProgress>(), CancellationToken.None);

        Assert.Contains(h.Invocations, i => i.Arguments.Contains("snap 1"));
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
        h.ScriptRunner.Verify(
            r => r.RunAsync(It.IsAny<ScriptInvocation>(), It.IsAny<IProgress<ScriptOutputLine>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_Should_RejectAtPrecheckAndLeaveNodeUntouched_When_BusyAndNotForced()
    {
        var h = new Harness();
        h.LockManager.TryLockAgents(new[] { Node }, "sess1", "Nightly Regression", "user1", "WPF");

        var result = await h.Build().ExecuteAsync(Shell(), Request(force: false), new Progress<MaintenanceProgress>(), CancellationToken.None);

        Assert.Equal(MaintenanceOperationState.Failed, result.State);
        Assert.Equal(RevertPhase.Precheck, result.FailurePhase);
        Assert.Equal(MaintenanceState.None, h.StateStore.Get(Node));
        h.ScriptRunner.Verify(
            r => r.RunAsync(It.IsAny<ScriptInvocation>(), It.IsAny<IProgress<ScriptOutputLine>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_Should_RejectAtPrecheckWithoutQuarantine_When_RevertScriptNotConfigured()
    {
        var h = new Harness(revertScriptPath: "");

        var result = await h.Build().ExecuteAsync(Shell(), Request(), new Progress<MaintenanceProgress>(), CancellationToken.None);

        Assert.Equal(MaintenanceOperationState.Failed, result.State);
        Assert.Equal(RevertPhase.Precheck, result.FailurePhase);
        Assert.Equal(MaintenanceState.None, h.StateStore.Get(Node));
        h.ScriptRunner.Verify(
            r => r.RunAsync(It.IsAny<ScriptInvocation>(), It.IsAny<IProgress<ScriptOutputLine>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_Should_RejectAtPrecheckWithoutQuarantine_When_RevertScriptFileMissing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "maint-missing", Guid.NewGuid().ToString("N") + ".ps1");
        var h = new Harness(revertScriptPath: missing);

        var result = await h.Build().ExecuteAsync(Shell(), Request(), new Progress<MaintenanceProgress>(), CancellationToken.None);

        Assert.Equal(MaintenanceOperationState.Failed, result.State);
        Assert.Equal(RevertPhase.Precheck, result.FailurePhase);
        Assert.Equal(MaintenanceState.None, h.StateStore.Get(Node));
        h.ScriptRunner.Verify(
            r => r.RunAsync(It.IsAny<ScriptInvocation>(), It.IsAny<IProgress<ScriptOutputLine>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_Should_AbortSessionAndProceed_When_BusyAndForced()
    {
        var h = new Harness();
        h.LockManager.TryLockAgents(new[] { Node }, "sess1", "Nightly Regression", "user1", "WPF");

        var result = await h.Build().ExecuteAsync(Shell(), Request(force: true), new Progress<MaintenanceProgress>(), CancellationToken.None);

        Assert.Equal(MaintenanceOperationState.Succeeded, result.State);
        Assert.Null(h.LockManager.GetLock(Node));
        Assert.Equal(MaintenanceState.None, h.StateStore.Get(Node));
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

    [Fact]
    public async Task ExecuteAsync_Should_CancelAndQuarantine_When_CancelledAtPhaseBoundary()
    {
        var h = new Harness();
        using var cts = new CancellationTokenSource();
        cts.Cancel();  // observed at the first boundary (PingWait); SnapshotRevert already ran uncancellably

        var result = await h.Build().ExecuteAsync(Shell(), Request(), new Progress<MaintenanceProgress>(), cts.Token);

        Assert.Equal(MaintenanceOperationState.Cancelled, result.State);
        Assert.Equal(RevertPhase.PingWait, result.FailurePhase);
        Assert.Equal(MaintenanceState.Quarantined, h.StateStore.Get(Node));
        h.Probe.Verify(
            p => p.WaitForPingAsync(It.IsAny<string>(), It.IsAny<PingOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
