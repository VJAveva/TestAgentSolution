using Moq;
using TestControllerGrpc.Core.Maintenance;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Maintenance;

/// <summary>
/// The baseline replacement is the only irreversible step in a golden-image refresh, and the correct order of
/// operations differs by platform. These pin that: create-then-prune where a previous baseline can be kept,
/// delete-then-create only where the platform cannot hold two — and the exposed window reported honestly.
/// </summary>
public class BaselineReplacerTests
{
    private const string Vm = "JVKPRI";

    /// <summary>Records call order so the test can assert ordering, which is the whole safety property.</summary>
    private sealed class RecordingProvider : IVirtualizationProvider
    {
        private readonly List<SnapshotInfo> _snapshots;

        public RecordingProvider(VmPlatformCapabilities caps, params SnapshotInfo[] existing)
        {
            Capabilities = caps;
            _snapshots = [.. existing];
        }

        public string PlatformId => "fake";
        public VmPlatformCapabilities Capabilities { get; }
        public List<string> Calls { get; } = [];
        public bool CreateFails { get; set; }
        public bool DeleteFails { get; set; }
        public List<string> Deleted { get; } = [];

        public Task<VmOpResult> GetPowerStateAsync(IReadOnlyList<string> v, CancellationToken ct) =>
            Task.FromResult(new VmOpResult([.. v.Select(x => new VmResult(x, true))]));

        public Task<VmOpResult> PowerOnAsync(IReadOnlyList<string> v, CancellationToken ct) =>
            Task.FromResult(new VmOpResult([.. v.Select(x => new VmResult(x, true))]));

        public Task<VmOpResult> PowerOffAsync(IReadOnlyList<string> v, bool g, CancellationToken ct) =>
            Task.FromResult(new VmOpResult([.. v.Select(x => new VmResult(x, true))]));

        public Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(string vm, CancellationToken ct)
        {
            Calls.Add("List");
            return Task.FromResult<IReadOnlyList<SnapshotInfo>>(_snapshots.ToList());
        }

        public Task<VmOpResult> CreateSnapshotAsync(
            IReadOnlyList<string> vms, string name, string desc, CancellationToken ct)
        {
            Calls.Add("Create");
            if (CreateFails)
                return Task.FromResult(new VmOpResult([.. vms.Select(x => new VmResult(x, false, "create failed"))]));

            var snap = new SnapshotInfo($"id-{name}", name, DateTimeOffset.UtcNow, 1024);
            _snapshots.Add(snap);
            return Task.FromResult(new VmOpResult(
                [.. vms.Select(x => new VmResult(x, true) { Snapshot = snap })]));
        }

        public Task<VmOpResult> RevertSnapshotAsync(IReadOnlyList<string> v, string? n, CancellationToken ct) =>
            Task.FromResult(new VmOpResult([.. v.Select(x => new VmResult(x, true))]));

        public Task<VmOpResult> DeleteSnapshotAsync(string vm, string snapshotId, CancellationToken ct)
        {
            Calls.Add("Delete");
            if (DeleteFails)
                return Task.FromResult(new VmOpResult([new VmResult(vm, false, "delete failed")]));

            Deleted.Add(snapshotId);
            _snapshots.RemoveAll(s => s.Id == snapshotId);
            return Task.FromResult(new VmOpResult([new VmResult(vm, true)]));
        }
    }

    private static SnapshotInfo Snap(string id, int daysOld) =>
        new(id, id, DateTimeOffset.UtcNow.AddDays(-daysOld), 1024);

    private static BaselineReplacer Build(RecordingProvider provider, int keep = 1, bool reclaimSpace = false) =>
        new(provider,
            new BaselineRetentionOptions { Keep = keep, ReclaimSpaceBeforeReplace = reclaimSpace },
            new Mock<IAppLogger>().Object);

    [Fact]
    public async Task ReplaceAsync_Should_CreateOnly_When_PlatformReplacesAtomically()
    {
        var provider = new RecordingProvider(VmPlatformCapabilities.VCloud, Snap("only", 10));

        var result = await Build(provider).ReplaceAsync(Vm, "new", "desc", CancellationToken.None);

        // vCloud supersedes the existing snapshot in one call, so the VM is never left without a baseline.
        Assert.True(result.Ok);
        Assert.DoesNotContain("Delete", provider.Calls);
        Assert.Empty(provider.Deleted);
    }

    [Fact]
    public async Task ReplaceAsync_Should_KeepBaselineIntact_When_AtomicCreateFails()
    {
        var provider = new RecordingProvider(VmPlatformCapabilities.VCloud, Snap("only", 10)) { CreateFails = true };

        var result = await Build(provider).ReplaceAsync(Vm, "new", "desc", CancellationToken.None);

        // Nothing was deleted, so the old baseline still protects the node.
        Assert.Equal(BaselineReplacementStatus.FailedBaselineIntact, result.Status);
        Assert.Empty(provider.Deleted);
    }

    [Fact]
    public async Task ReplaceAsync_Should_CreateBeforeDeleting_When_PlatformCanRetainBaselines()
    {
        var provider = new RecordingProvider(VmPlatformCapabilities.VSphere, Snap("old", 10));

        var result = await Build(provider, keep: 1).ReplaceAsync(Vm, "new", "desc", CancellationToken.None);

        Assert.True(result.Ok);
        // Create must precede Delete: a failure then leaves the old baseline intact.
        Assert.True(provider.Calls.IndexOf("Create") < provider.Calls.IndexOf("Delete"));
    }

    [Fact]
    public async Task ReplaceAsync_Should_EndWithSingleSnapshot_When_HyperVKeepsOne()
    {
        var provider = new RecordingProvider(VmPlatformCapabilities.HyperV, Snap("old", 10));

        var result = await Build(provider, keep: 1).ReplaceAsync(Vm, "new", "desc", CancellationToken.None);

        // Hyper-V can hold a tree, but the fleet policy is one golden image.
        Assert.True(result.Ok);
        Assert.Contains("old", provider.Deleted);
    }

    [Fact]
    public async Task ReplaceAsync_Should_DeleteBeforeCreating_When_SpaceReclamationRequested()
    {
        var provider = new RecordingProvider(VmPlatformCapabilities.VCloud, Snap("only", 10));

        var result = await Build(provider, reclaimSpace: true).ReplaceAsync(Vm, "new", "desc", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.True(provider.Calls.IndexOf("Delete") < provider.Calls.IndexOf("Create"));
    }

    [Fact]
    public async Task ReplaceAsync_Should_KeepOldBaseline_When_CreateFailsOnMultiSnapshotPlatform()
    {
        var provider = new RecordingProvider(VmPlatformCapabilities.VSphere, Snap("old", 10)) { CreateFails = true };

        var result = await Build(provider).ReplaceAsync(Vm, "new", "desc", CancellationToken.None);

        Assert.Equal(BaselineReplacementStatus.FailedBaselineIntact, result.Status);
        Assert.Empty(provider.Deleted);
    }

    [Fact]
    public async Task ReplaceAsync_Should_ReportBaselineMissing_When_CreateFailsAfterDeleteOnVCloud()
    {
        var provider = new RecordingProvider(VmPlatformCapabilities.VCloud, Snap("only", 10)) { CreateFails = true };

        var result = await Build(provider, reclaimSpace: true).ReplaceAsync(Vm, "new", "desc", CancellationToken.None);

        // The VM now has no snapshot at all; this must be stated, not softened to a generic failure.
        Assert.Equal(BaselineReplacementStatus.FailedBaselineMissing, result.Status);
        Assert.Contains("only", provider.Deleted);
    }

    [Fact]
    public async Task ReplaceAsync_Should_ReportIntact_When_VCloudCreateFailsAndThereWasNoPriorSnapshot()
    {
        var provider = new RecordingProvider(VmPlatformCapabilities.VCloud) { CreateFails = true };

        var result = await Build(provider, reclaimSpace: true).ReplaceAsync(Vm, "new", "desc", CancellationToken.None);

        // Nothing was destroyed because there was nothing there — not a "baseline missing" incident.
        Assert.Equal(BaselineReplacementStatus.FailedBaselineIntact, result.Status);
    }

    [Fact]
    public async Task ReplaceAsync_Should_AbortWithoutCreating_When_VCloudDeleteFails()
    {
        var provider = new RecordingProvider(VmPlatformCapabilities.VCloud, Snap("only", 10)) { DeleteFails = true };

        var result = await Build(provider, reclaimSpace: true).ReplaceAsync(Vm, "new", "desc", CancellationToken.None);

        Assert.Equal(BaselineReplacementStatus.FailedBaselineIntact, result.Status);
        Assert.DoesNotContain("Create", provider.Calls);
    }

    [Fact]
    public async Task ReplaceAsync_Should_PruneOldestFirst_When_RetentionExceeded()
    {
        var provider = new RecordingProvider(
            VmPlatformCapabilities.VSphere, Snap("newest", 1), Snap("middle", 5), Snap("oldest", 30));

        await Build(provider, keep: 2).ReplaceAsync(Vm, "new", "desc", CancellationToken.None);

        // Keep=2 means the new one plus one previous; the two older baselines go.
        Assert.Contains("oldest", provider.Deleted);
        Assert.Contains("middle", provider.Deleted);
        Assert.DoesNotContain("newest", provider.Deleted);
    }
    [Fact]
    public async Task ReplaceAsync_Should_NeverPruneTheSnapshotItJustCreated()
    {
        var provider = new RecordingProvider(VmPlatformCapabilities.VSphere, Snap("old", 10));

        var result = await Build(provider, keep: 1).ReplaceAsync(Vm, "new", "desc", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.DoesNotContain("id-new", provider.Deleted);
    }

    [Fact]
    public async Task ReplaceAsync_Should_StillSucceed_When_PruneFails()
    {
        var provider = new RecordingProvider(VmPlatformCapabilities.VSphere, Snap("old", 10)) { DeleteFails = true };

        var result = await Build(provider, keep: 1).ReplaceAsync(Vm, "new", "desc", CancellationToken.None);

        // A stale snapshot costs storage, not correctness — the new baseline exists, so the refresh succeeded.
        Assert.True(result.Ok);
    }
}
