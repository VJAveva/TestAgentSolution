using Moq;
using TestControllerGrpc.Core.Maintenance;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Maintenance;

/// <summary>
/// Slice 1 of the fleet update flow. The Fleet UI used to call <see cref="INodeUpdateInstaller"/> directly, so a
/// patch run set no maintenance state, took no slot and left no row — a node stayed dispatchable mid-install and a
/// controller crash left no trace. These pin the operation that closes that.
/// </summary>
public class MachineUpdateOperationTests
{
    private const string Node = "lpwin11test2";

    private sealed class Harness
    {
        public Mock<INodeUpdateInstaller> Installer { get; } = new();
        public Mock<IAgentGrpcDispatcher> Dispatcher { get; } = new();
        public Mock<INodeReadinessProbe> Probe { get; } = new();
        public Mock<INodeUpdateStatusStore> Status { get; } = new();
        public Mock<IMaintenanceOperationStore> Store { get; } = new();
        public AgentLockManager Locks { get; } = new();
        public MaintenanceStateStore State { get; } = new();
        public List<MaintenanceOperation> Saved { get; } = [];

        public Harness(
            bool supported = true,
            int available = 1,
            bool installOk = true,
            bool rebootRequired = false,
            bool agentReturns = true,
            UpdateScanStatus scan = UpdateScanStatus.Ok,
            int failedCount = 0)
        {
            Dispatcher.SetupGet(d => d.RegisteredAgents).Returns([Node]);
            Dispatcher
                .Setup(d => d.ExecuteRemoteCommandAsync(It.IsAny<TestControllerGrpc.Models.ActionConfig>(),
                    It.IsAny<TestControllerGrpc.Models.PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ActionResult(true, 0, ""));

            Installer.Setup(i => i.IsInstallSupportedAsync(Node, It.IsAny<CancellationToken>())).ReturnsAsync(supported);
            Installer.Setup(i => i.SearchAsync(Node, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new UpdateSearchResult(true, available, []));
            Installer.Setup(i => i.InstallAsync(Node, It.IsAny<CancellationToken>()))
                .ReturnsAsync(installOk
                    ? new UpdateInstallResult(true, 3, failedCount, rebootRequired)
                    : UpdateInstallResult.Failed("install blew up"));

            Probe.Setup(p => p.WaitForAgentAsync(Node, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(agentReturns
                    ? new ReadinessResult(true, TimeSpan.Zero, null)
                    : new ReadinessResult(false, TimeSpan.Zero, "agent never came back"));

            // Always "fresh": the operation only accepts a report stamped after Verify begins.
            Status.Setup(s => s.Get(Node)).Returns(() => new NodeUpdateStatus
            {
                NodeId = Node,
                State = WindowsUpdateState.UpToDate,
                LastSource = MaintenanceEventSource.StartupSnapshot,
                LastReportUtc = DateTimeOffset.UtcNow.AddMinutes(1),
                ScanStatus = scan,
            });

            Store.Setup(s => s.SaveAsync(It.IsAny<MaintenanceOperation>(), It.IsAny<CancellationToken>()))
                .Callback<MaintenanceOperation, CancellationToken>((op, _) => Saved.Add(op))
                .Returns(Task.CompletedTask);
        }

        public MachineUpdateOperation Build() => new(
            Installer.Object, Dispatcher.Object, Probe.Object, Status.Object,
            Locks, State, Store.Object, Mock.Of<IAppLogger>())
        {
            PosturePollInterval = TimeSpan.FromMilliseconds(1),
        };

        public static MaintenanceOperation NewOp() => new()
        {
            Id = Guid.NewGuid(),
            NodeId = Node,
            Kind = MaintenanceKind.InstallUpdates,
            TriggerSource = MaintenanceTriggerSource.FleetPanel,
        };

        public static InstallUpdatesRequest Request() => new()
        {
            NodeId = Node,
            TriggerSource = MaintenanceTriggerSource.FleetPanel,
            PostureTimeout = TimeSpan.FromMilliseconds(50),
        };
    }

    private static Task<MaintenanceOperation> RunAsync(Harness h) =>
        h.Build().ExecuteAsync(Harness.NewOp(), Harness.Request(),
            new Progress<MaintenanceProgress>(), CancellationToken.None);

    // ── The headline fix ─────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Should_SetUpdating_When_InstallIsRunning()
    {
        var h = new Harness();
        MaintenanceState? duringInstall = null;
        h.Installer.Setup(i => i.InstallAsync(Node, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                duringInstall = h.State.Get(Node);
                return new UpdateInstallResult(true, 3, 0, false);
            });

        await RunAsync(h);

        Assert.Equal(MaintenanceState.Updating, duringInstall);
    }

    [Fact]
    public async Task ExecuteAsync_Should_ReturnNodeToRotation_When_EverythingSucceeds()
    {
        var h = new Harness();

        var result = await RunAsync(h);

        Assert.Equal(MaintenanceOperationState.Succeeded, result.State);
        Assert.Equal(MaintenanceState.None, h.State.Get(Node));
    }

    // ── Precheck refuses rather than overrides ───────────────────────

    [Fact]
    public async Task ExecuteAsync_Should_FailPrecheckWithoutQuarantine_When_NodeIsBusy()
    {
        var h = new Harness();
        h.Locks.TryLockAgents([Node], "session-1", "SomeWatchItem", "tester", "test");

        var result = await RunAsync(h);

        Assert.Equal(MaintenanceOperationState.Failed, result.State);
        Assert.Equal(RevertPhase.Precheck, result.FailurePhase);
        // A refused precheck changed nothing, so the node must NOT be quarantined.
        Assert.Equal(MaintenanceState.None, h.State.Get(Node));
        h.Installer.Verify(i => i.InstallAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_Should_FailPrecheck_When_NodeIsReservedByAnotherOperation()
    {
        var h = new Harness();
        h.State.Set(Node, MaintenanceState.Reverting);

        var result = await RunAsync(h);

        Assert.Equal(RevertPhase.Precheck, result.FailurePhase);
        h.Installer.Verify(i => i.InstallAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_Should_FailPrecheck_When_AgentCannotInstall()
    {
        var result = await RunAsync(new Harness(supported: false));

        Assert.Equal(RevertPhase.Precheck, result.FailurePhase);
    }

    // ── Failure policy ───────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Should_Quarantine_When_InstallFails()
    {
        var h = new Harness(installOk: false);

        var result = await RunAsync(h);

        Assert.Equal(RevertPhase.InstallUpdates, result.FailurePhase);
        Assert.Equal(MaintenanceState.Quarantined, h.State.Get(Node));
    }

    [Fact]
    public async Task ExecuteAsync_Should_Quarantine_When_AgentDoesNotReturnAfterReboot()
    {
        var h = new Harness(rebootRequired: true, agentReturns: false);

        var result = await RunAsync(h);

        Assert.Equal(RevertPhase.RebootWait, result.FailurePhase);
        Assert.Equal(MaintenanceState.Quarantined, h.State.Get(Node));
    }

    [Fact]
    public async Task ExecuteAsync_Should_Quarantine_When_PostureScanIsNotOk()
    {
        var h = new Harness(scan: UpdateScanStatus.Failed);

        var result = await RunAsync(h);

        Assert.Equal(RevertPhase.Verify, result.FailurePhase);
        Assert.Equal(MaintenanceState.Quarantined, h.State.Get(Node));
    }

    // ── Reboot only when asked ───────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Should_SkipReboot_When_InstallerReportsNoRebootRequired()
    {
        var h = new Harness(rebootRequired: false);

        await RunAsync(h);

        h.Dispatcher.Verify(d => d.ExecuteRemoteCommandAsync(
            It.IsAny<TestControllerGrpc.Models.ActionConfig>(),
            It.IsAny<TestControllerGrpc.Models.PipelineExecutionContext>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_Should_Reboot_When_InstallerReportsRebootRequired()
    {
        var h = new Harness(rebootRequired: true);

        await RunAsync(h);

        h.Dispatcher.Verify(d => d.ExecuteRemoteCommandAsync(
            It.Is<TestControllerGrpc.Models.ActionConfig>(a => a.Command == "shutdown" && a.IsReboot),
            It.IsAny<TestControllerGrpc.Models.PipelineExecutionContext>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Nothing to do, and partial success ───────────────────────────

    [Fact]
    public async Task ExecuteAsync_Should_SucceedWithoutInstalling_When_NoUpdatesAreAvailable()
    {
        var h = new Harness(available: 0);

        var result = await RunAsync(h);

        Assert.Equal(MaintenanceOperationState.Succeeded, result.State);
        Assert.Equal(MaintenanceState.None, h.State.Get(Node));
        h.Installer.Verify(i => i.InstallAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_Should_Succeed_When_SomeUpdatesFailed()
    {
        // D8: a partial install is normal and retryable — report it, do not quarantine.
        var h = new Harness(failedCount: 2);

        var result = await RunAsync(h);

        Assert.Equal(MaintenanceOperationState.Succeeded, result.State);
        Assert.Equal(MaintenanceState.None, h.State.Get(Node));
    }

    // ── Crash recovery can see the run ───────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Should_PersistAnInstallUpdatesRow_When_OperationRuns()
    {
        var h = new Harness();

        await RunAsync(h);

        // MaintenanceRecoveryService reads unfinished rows, so the row must exist from the first phase on.
        Assert.Contains(h.Saved, o => o.Kind == MaintenanceKind.InstallUpdates);
        Assert.Contains(h.Saved, o => o.State == MaintenanceOperationState.Running);
    }
}
