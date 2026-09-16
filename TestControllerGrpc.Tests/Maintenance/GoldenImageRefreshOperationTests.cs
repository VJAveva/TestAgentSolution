using Moq;
using TestControllerGrpc.Core.Maintenance;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Maintenance;

/// <summary>
/// The refresh engine's safety contract: a precheck failure changes nothing, any failure past quarantine holds
/// the node out of rotation, and — the reason this type exists — an unverified node NEVER reaches the
/// irreversible baseline replacement.
/// </summary>
public class GoldenImageRefreshOperationTests
{
    private const string Node = "JVKPRI";

    private sealed class Harness
    {
        public Mock<IVirtualizationProvider> Provider { get; } = new();
        public Mock<INodeUpdateInstaller> Installer { get; } = new();
        public Mock<INodeReadinessProbe> Readiness { get; } = new();
        public MaintenanceStateStore StateStore { get; } = new();
        public AgentLockManager Locks { get; } = new();
        public List<string> ProviderCalls { get; } = [];
        public int CreateSnapshotCalls { get; private set; }

        public Harness(
            VmPlatformCapabilities? caps = null,
            bool installSupported = true,
            int availableUpdates = 3,
            bool hasBaseline = true,
            bool verifyOk = true)
        {
            Provider.SetupGet(p => p.Capabilities).Returns(caps ?? VmPlatformCapabilities.VSphere);
            Provider.SetupGet(p => p.PlatformId).Returns("fake");

            Provider.Setup(p => p.ListSnapshotsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(hasBaseline
                    ? [new SnapshotInfo("base-1", "baseline", DateTimeOffset.UtcNow.AddDays(-30), 1024)]
                    : []);

            Provider.Setup(p => p.RevertSnapshotAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok).Callback(() => ProviderCalls.Add("Revert"));

            Provider.Setup(p => p.PowerOnAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok).Callback(() => ProviderCalls.Add("PowerOn"));

            Provider.Setup(p => p.PowerOffAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok).Callback(() => ProviderCalls.Add("PowerOff"));

            Provider.Setup(p => p.CreateSnapshotAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new VmOpResult([new VmResult(Node, true)
                {
                    Snapshot = new SnapshotInfo("base-2", "new", DateTimeOffset.UtcNow, 2048),
                }]))
                .Callback(() => { ProviderCalls.Add("Create"); CreateSnapshotCalls++; });

            Provider.Setup(p => p.DeleteSnapshotAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok).Callback(() => ProviderCalls.Add("Delete"));

            Installer.Setup(i => i.IsInstallSupportedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(installSupported);
            Installer.Setup(i => i.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new UpdateSearchResult(true, availableUpdates, ["KB5000001"]));
            Installer.Setup(i => i.InstallAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new UpdateInstallResult(true, availableUpdates, 0, RebootRequired: true));

            Readiness.Setup(r => r.WaitForAgentAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(verifyOk
                    ? new ReadinessResult(true, TimeSpan.Zero, null)
                    : new ReadinessResult(false, TimeSpan.Zero, "agent never came back"));
        }

        private static VmOpResult Ok => new([new VmResult(Node, true)]);

        public GoldenImageRefreshOperation Build() => new(
            Provider.Object, Installer.Object, Readiness.Object,
            new BaselineReplacer(Provider.Object, new BaselineRetentionOptions { Keep = 2 }, new Mock<IAppLogger>().Object),
            Locks, new ExecutionSessionManager(), StateStore,
            new Mock<IMaintenanceOperationStore>().Object,
            new MaintenanceOptions { LogDirectory = Path.Combine(Path.GetTempPath(), "refresh-tests") },
            new Mock<IAppLogger>().Object);
    }

    private static MaintenanceOperation NewOp() => new()
    {
        Id = Guid.NewGuid(),
        NodeId = Node,
        Kind = MaintenanceKind.GoldenImageRefresh,
        TriggerSource = MaintenanceTriggerSource.FleetPanel,
    };

    private static GoldenImageRefreshRequest Request(bool refreshWhenNoUpdates = false) => new()
    {
        NodeId = Node,
        TriggeredBy = "test",
        RefreshWhenNoUpdates = refreshWhenNoUpdates,
        AgentWaitTimeout = TimeSpan.FromSeconds(1),
    };

    private static Task<MaintenanceOperation> Run(Harness h, GoldenImageRefreshRequest? request = null) =>
        h.Build().ExecuteAsync(NewOp(), request ?? Request(),
            new Progress<MaintenanceProgress>(), CancellationToken.None);

    [Fact]
    public async Task ExecuteAsync_Should_ReplaceBaselineAndReturnToRotation_When_EverythingSucceeds()
    {
        var h = new Harness();

        var result = await Run(h);

        Assert.Equal(MaintenanceOperationState.Succeeded, result.State);
        Assert.Equal(MaintenanceState.None, h.StateStore.Get(Node));
        Assert.Equal(1, h.CreateSnapshotCalls);
    }

    [Fact]
    public async Task ExecuteAsync_Should_RevertBeforeInstalling_When_RefreshStarts()
    {
        var h = new Harness();

        await Run(h);

        // Reverting first is what stops drift compounding across refreshes.
        Assert.Equal("Revert", h.ProviderCalls[0]);
    }

    [Fact]
    public async Task ExecuteAsync_Should_NotReplaceBaseline_When_VerificationFails()
    {
        var h = new Harness(verifyOk: false);

        var result = await Run(h);

        // The whole point of the gate: an unverified node must never reach the irreversible step.
        Assert.Equal(0, h.CreateSnapshotCalls);
        Assert.Equal(MaintenanceOperationState.Failed, result.State);
        Assert.Equal(MaintenanceState.Quarantined, h.StateStore.Get(Node));
    }

    [Fact]
    public async Task ExecuteAsync_Should_NotQuarantine_When_PrecheckRejectsUnsupportedNode()
    {
        var h = new Harness(installSupported: false);

        var result = await Run(h);

        Assert.Equal(RevertPhase.Precheck, result.FailurePhase);
        // Nothing was touched, so the node stays dispatchable.
        Assert.Equal(MaintenanceState.None, h.StateStore.Get(Node));
        Assert.DoesNotContain(h.ProviderCalls, c => c is "Revert" or "Delete" or "Create");
    }

    [Fact]
    public async Task ExecuteAsync_Should_FailPrecheck_When_NodeHasNoBaselineSnapshot()
    {
        var h = new Harness(hasBaseline: false);

        var result = await Run(h);

        Assert.Equal(RevertPhase.Precheck, result.FailurePhase);
        Assert.Equal(MaintenanceState.None, h.StateStore.Get(Node));
    }

    [Fact]
    public async Task ExecuteAsync_Should_FailPrecheck_When_NodeIsAlreadyQuarantined()
    {
        var h = new Harness();
        h.StateStore.Set(Node, MaintenanceState.Quarantined);

        var result = await Run(h);

        Assert.Equal(RevertPhase.Precheck, result.FailurePhase);
        Assert.Empty(h.ProviderCalls);
    }

    [Fact]
    public async Task ExecuteAsync_Should_SkipRefresh_When_NoUpdatesAvailable()
    {
        var h = new Harness(availableUpdates: 0);

        var result = await Run(h);

        // Burning the rollback point for zero updates is pure loss.
        Assert.Equal(MaintenanceOperationState.Succeeded, result.State);
        Assert.Equal(0, h.CreateSnapshotCalls);
        Assert.Equal(MaintenanceState.None, h.StateStore.Get(Node));
    }

    [Fact]
    public async Task ExecuteAsync_Should_StillRefresh_When_NoUpdatesButCallerForcedIt()
    {
        var h = new Harness(availableUpdates: 0);

        var result = await Run(h, Request(refreshWhenNoUpdates: true));

        Assert.Equal(MaintenanceOperationState.Succeeded, result.State);
        Assert.Equal(1, h.CreateSnapshotCalls);
    }

    [Fact]
    public async Task ExecuteAsync_Should_QuarantineWithoutReplacing_When_InstallFails()
    {
        var h = new Harness();
        h.Installer.Setup(i => i.InstallAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UpdateInstallResult.Failed("0x80240034"));

        var result = await Run(h);

        Assert.Equal(RevertPhase.InstallUpdates, result.FailurePhase);
        Assert.Equal(0, h.CreateSnapshotCalls);
        Assert.Equal(MaintenanceState.Quarantined, h.StateStore.Get(Node));
    }

    [Fact]
    public async Task ExecuteAsync_Should_QuarantineWithoutReplacing_When_SearchFails()
    {
        var h = new Harness();
        h.Installer.Setup(i => i.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UpdateSearchResult.Failed("WU service unavailable"));

        var result = await Run(h);

        Assert.Equal(RevertPhase.SearchUpdates, result.FailurePhase);
        Assert.Equal(0, h.CreateSnapshotCalls);
    }

    [Fact]
    public async Task ExecuteAsync_Should_PowerOffBeforeReplacingBaseline_When_Verified()
    {
        var h = new Harness();

        await Run(h);

        // The snapshot must be captured from a quiescent disk.
        var powerOffIndex = h.ProviderCalls.LastIndexOf("PowerOff");
        var createIndex = h.ProviderCalls.IndexOf("Create");
        Assert.True(powerOffIndex < createIndex, "baseline was captured before the node was powered off");
    }

    [Fact]
    public async Task ExecuteAsync_Should_QuarantineAtReplace_When_BaselineReplacementFails()
    {
        var h = new Harness();
        h.Provider.Setup(p => p.CreateSnapshotAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmOpResult([new VmResult(Node, false, "datastore full")]));

        var result = await Run(h);

        Assert.Equal(RevertPhase.ReplaceBaseline, result.FailurePhase);
        Assert.Equal(MaintenanceState.Quarantined, h.StateStore.Get(Node));
    }

    [Fact]
    public async Task ExecuteAsync_Should_QuarantineAndRollBack_When_AgentNeverReturnsAfterRevert()
    {
        var h = new Harness(verifyOk: false);

        var result = await Run(h);

        // The first readiness wait is after the revert, so this fails before any patching happens.
        Assert.Equal(RevertPhase.AgentWait, result.FailurePhase);
        Assert.Equal(0, h.CreateSnapshotCalls);
    }
}
