namespace TestControllerGrpc.Core.Maintenance;

/// <summary>What a hypervisor can actually do. Exposed rather than hidden, because the platforms differ in a
/// way that changes rollback safety: vCloud Director holds exactly ONE snapshot per VM, so replacing a
/// baseline there destroys the only rollback point, while vSphere and Hyper-V can retain the previous one.
/// An abstraction that papered over this would silently lose the ability to recover on vCloud.</summary>
public sealed record VmPlatformCapabilities
{
    public required bool SupportsNamedSnapshots { get; init; }

    /// <summary>1 on vCloud Director. Drives the baseline-replacement strategy.</summary>
    public required int MaxSnapshotsPerVm { get; init; }

    /// <summary>
    /// True when creating a snapshot REPLACES the existing one in a single call, so the VM is never left
    /// without a baseline. vCloud behaves this way; vSphere and Hyper-V add to a tree instead.
    /// </summary>
    public required bool SupportsAtomicSnapshotReplace { get; init; }

    public required bool RequiresPowerOffForSnapshot { get; init; }

    /// <summary>True when one invocation can act on many VMs — the fleet-scale win.</summary>
    public required bool SupportsBatchOperations { get; init; }

    /// <summary>A platform that can hold only one snapshot cannot keep a previous baseline as a safety net.</summary>
    public bool CanRetainPreviousBaseline => MaxSnapshotsPerVm > 1;

    public static VmPlatformCapabilities VCloud { get; } = new()
    {
        SupportsNamedSnapshots = false,
        MaxSnapshotsPerVm = 1,
        SupportsAtomicSnapshotReplace = true,
        RequiresPowerOffForSnapshot = false,
        SupportsBatchOperations = true,
    };

    public static VmPlatformCapabilities VSphere { get; } = new()
    {
        SupportsNamedSnapshots = true,
        MaxSnapshotsPerVm = int.MaxValue,
        SupportsAtomicSnapshotReplace = false,
        RequiresPowerOffForSnapshot = false,
        SupportsBatchOperations = true,
    };

    public static VmPlatformCapabilities HyperV { get; } = new()
    {
        SupportsNamedSnapshots = true,
        MaxSnapshotsPerVm = int.MaxValue,
        SupportsAtomicSnapshotReplace = false,
        RequiresPowerOffForSnapshot = false,
        SupportsBatchOperations = true,
    };
}

/// <summary>One snapshot as reported by the platform. <paramref name="Id"/> is opaque and platform-specific.</summary>
public sealed record SnapshotInfo(string Id, string Name, DateTimeOffset? CreatedUtc, long? SizeBytes);

public enum VmPower { Unknown, On, Off, Suspended }

/// <summary>Per-VM outcome. A batch routinely returns a mix, which callers must handle (see the partial-batch policy).</summary>
public sealed record VmResult(string VmName, bool Ok, string? Error = null, bool Retryable = false)
{
    public SnapshotInfo? Snapshot { get; init; }
    public VmPower Power { get; init; } = VmPower.Unknown;
}

/// <summary>Result of one provider call across one or more VMs.</summary>
public sealed record VmOpResult(IReadOnlyList<VmResult> Results)
{
    public bool AllSucceeded => Results.Count > 0 && Results.All(r => r.Ok);
    public IEnumerable<VmResult> Failures => Results.Where(r => !r.Ok);
    public IEnumerable<VmResult> Successes => Results.Where(r => r.Ok);

    public VmResult? For(string vmName) =>
        Results.FirstOrDefault(r => string.Equals(r.VmName, vmName, StringComparison.OrdinalIgnoreCase));

    public static VmOpResult Failed(IEnumerable<string> vmNames, string error) =>
        new([.. vmNames.Select(v => new VmResult(v, false, error))]);
}

/// <summary>
/// Hypervisor operations the maintenance engine needs. Every method is plural-capable: batching is where the
/// fleet-scale win lives, and a per-VM loop pays a fresh session cost on every call.
/// </summary>
public interface IVirtualizationProvider
{
    /// <summary>"vcloud" | "vsphere" | "hyperv".</summary>
    string PlatformId { get; }

    VmPlatformCapabilities Capabilities { get; }

    Task<VmOpResult> GetPowerStateAsync(IReadOnlyList<string> vmNames, CancellationToken ct);

    Task<VmOpResult> PowerOnAsync(IReadOnlyList<string> vmNames, CancellationToken ct);

    Task<VmOpResult> PowerOffAsync(IReadOnlyList<string> vmNames, bool graceful, CancellationToken ct);

    Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(string vmName, CancellationToken ct);

    /// <summary>
    /// Creates a snapshot. On a platform with <see cref="VmPlatformCapabilities.MaxSnapshotsPerVm"/> of 1 this
    /// REPLACES the existing snapshot — the caller must have verified the VM first.
    /// </summary>
    Task<VmOpResult> CreateSnapshotAsync(
        IReadOnlyList<string> vmNames, string snapshotName, string description, CancellationToken ct);

    /// <summary>Reverts to <paramref name="snapshotName"/>, or to the single current snapshot when the platform
    /// does not support names.</summary>
    Task<VmOpResult> RevertSnapshotAsync(
        IReadOnlyList<string> vmNames, string? snapshotName, CancellationToken ct);

    Task<VmOpResult> DeleteSnapshotAsync(string vmName, string snapshotId, CancellationToken ct);
}
