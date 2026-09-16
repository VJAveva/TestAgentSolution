using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Maintenance;

/// <summary>How a baseline replacement ended, from the operator's point of view.</summary>
public enum BaselineReplacementStatus
{
    Succeeded,

    /// <summary>Nothing was destroyed; the previous baseline is intact.</summary>
    FailedBaselineIntact,

    /// <summary>
    /// The old snapshot was deleted and the new one did not get created. The VM currently has NO baseline.
    /// Only reachable on a platform that cannot hold two snapshots.
    /// </summary>
    FailedBaselineMissing,
}

public sealed record BaselineReplacementResult(
    string VmName, BaselineReplacementStatus Status, string? Error = null, SnapshotInfo? NewBaseline = null)
{
    public bool Ok => Status == BaselineReplacementStatus.Succeeded;
}

public sealed record BaselineRetentionOptions
{
    /// <summary>
    /// How many baselines to keep where the platform allows a tree. Default 1: Hyper-V and vSphere can hold
    /// several, but the fleet is operated with a single golden image.
    /// </summary>
    public int Keep { get; init; } = 1;

    /// <summary>
    /// Delete the old snapshot BEFORE creating the new one. Reclaims disk immediately, but leaves a window in
    /// which the VM has NO snapshot at all — a crash there costs the rollback point. Off by default: on a
    /// platform that replaces atomically the space saving is not worth that exposure.
    /// </summary>
    public bool ReclaimSpaceBeforeReplace { get; init; }
}

/// <summary>
/// Replaces a VM's golden-image baseline. This is the only irreversible step in a refresh, so the default on
/// every platform is an ordering in which the VM is never left without a snapshot:
/// <list type="bullet">
/// <item>Atomic-replace platforms (vCloud): one create call; it supersedes the existing snapshot.</item>
/// <item>Tree platforms (vSphere, Hyper-V): create first, then prune down to the retention limit.</item>
/// <item>Delete-then-create is used ONLY when <see cref="BaselineRetentionOptions.ReclaimSpaceBeforeReplace"/>
/// is set, because it is the one ordering that can leave a VM with no baseline.</item>
/// </list>
/// The caller must have verified the VM before calling this.
/// </summary>
public sealed class BaselineReplacer
{
    private readonly IVirtualizationProvider _provider;
    private readonly BaselineRetentionOptions _retention;
    private readonly IAppLogger _logger;

    public BaselineReplacer(
        IVirtualizationProvider provider, BaselineRetentionOptions retention, IAppLogger logger)
    {
        _provider = provider;
        _retention = retention;
        _logger = logger;
    }

    public async Task<BaselineReplacementResult> ReplaceAsync(
        string vmName, string newSnapshotName, string description, CancellationToken ct)
    {
        // Opt-in only: the caller has accepted the exposure window in order to reclaim disk immediately.
        if (_retention.ReclaimSpaceBeforeReplace && !_provider.Capabilities.CanRetainPreviousBaseline)
            return await DeleteThenCreateAsync(vmName, newSnapshotName, description, ct).ConfigureAwait(false);

        if (_provider.Capabilities.SupportsAtomicSnapshotReplace)
            return await AtomicReplaceAsync(vmName, newSnapshotName, description, ct).ConfigureAwait(false);

        return await CreateThenPruneAsync(vmName, newSnapshotName, description, ct).ConfigureAwait(false);
    }

    // vCloud: one call supersedes the existing snapshot, so there is never a moment without a baseline. The
    // superseded snapshot's space is not reclaimed until the platform collapses it.
    private async Task<BaselineReplacementResult> AtomicReplaceAsync(
        string vmName, string newSnapshotName, string description, CancellationToken ct)
    {
        var create = await _provider.CreateSnapshotAsync([vmName], newSnapshotName, description, ct)
            .ConfigureAwait(false);
        var created = create.For(vmName);

        return created is { Ok: true }
            ? new BaselineReplacementResult(vmName, BaselineReplacementStatus.Succeeded, null, created.Snapshot)
            : new BaselineReplacementResult(
                vmName, BaselineReplacementStatus.FailedBaselineIntact,
                created?.Error ?? "Snapshot creation reported no result.");
    }

    // Safe ordering: the old baseline is only pruned once the new one exists.
    private async Task<BaselineReplacementResult> CreateThenPruneAsync(
        string vmName, string newSnapshotName, string description, CancellationToken ct)
    {
        var existing = await _provider.ListSnapshotsAsync(vmName, ct).ConfigureAwait(false);

        var create = await _provider.CreateSnapshotAsync([vmName], newSnapshotName, description, ct)
            .ConfigureAwait(false);
        var created = create.For(vmName);
        if (created is null || !created.Ok)
        {
            return new BaselineReplacementResult(
                vmName, BaselineReplacementStatus.FailedBaselineIntact,
                created?.Error ?? "Snapshot creation reported no result.");
        }

        // Prune oldest-first, never touching the snapshot just created.
        var newId = created.Snapshot?.Id;
        var prunable = existing
            .Where(s => newId is null || !string.Equals(s.Id, newId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.CreatedUtc ?? DateTimeOffset.MinValue)
            .Skip(Math.Max(0, _retention.Keep - 1))
            .ToList();

        foreach (var stale in prunable)
        {
            ct.ThrowIfCancellationRequested();
            var delete = await _provider.DeleteSnapshotAsync(vmName, stale.Id, ct).ConfigureAwait(false);
            if (delete.For(vmName) is { Ok: false } failed)
            {
                // A stale snapshot left behind costs storage; it does not cost correctness, so do not fail here.
                _logger.Warn("Baseline",
                    $"{vmName}: could not prune snapshot '{stale.Name}' ({stale.Id}): {failed.Error}");
            }
        }

        return new BaselineReplacementResult(
            vmName, BaselineReplacementStatus.Succeeded, null, created.Snapshot);
    }

    // Single-snapshot platform with space reclamation requested: unavoidable gap between delete and create.
    // Nothing else goes between them, and the create is not cancellable.
    private async Task<BaselineReplacementResult> DeleteThenCreateAsync(
        string vmName, string newSnapshotName, string description, CancellationToken ct)
    {
        var existing = await _provider.ListSnapshotsAsync(vmName, ct).ConfigureAwait(false);
        var current = existing.FirstOrDefault();

        if (current is not null)
        {
            var delete = await _provider.DeleteSnapshotAsync(vmName, current.Id, ct).ConfigureAwait(false);
            if (delete.For(vmName) is { Ok: false } failed)
            {
                return new BaselineReplacementResult(
                    vmName, BaselineReplacementStatus.FailedBaselineIntact, failed.Error);
            }

            _logger.Warn("Baseline",
                $"{vmName}: baseline deleted, no snapshot exists until the replacement is created.");
        }

        // Deliberately not cancellable: abandoning here is what leaves a VM with no baseline at all.
        var create = await _provider.CreateSnapshotAsync([vmName], newSnapshotName, description, CancellationToken.None)
            .ConfigureAwait(false);
        var created = create.For(vmName);

        if (created is null || !created.Ok)
        {
            var status = current is null
                ? BaselineReplacementStatus.FailedBaselineIntact   // there was nothing to lose
                : BaselineReplacementStatus.FailedBaselineMissing;

            if (status == BaselineReplacementStatus.FailedBaselineMissing)
            {
                _logger.Error("Baseline",
                    $"{vmName}: BASELINE MISSING — the previous snapshot was deleted and the replacement failed. "
                    + "This VM cannot be reverted until a new snapshot is taken.",
                    null);
            }

            return new BaselineReplacementResult(vmName, status, created?.Error ?? "Snapshot creation reported no result.");
        }

        return new BaselineReplacementResult(
            vmName, BaselineReplacementStatus.Succeeded, null, created.Snapshot);
    }
}
