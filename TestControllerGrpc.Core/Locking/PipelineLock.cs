namespace TestControllerGrpc.Locking;

/// <summary>
/// Immutable snapshot of a pipeline lock. Key = PipelineId (WatchItem Tag).
/// Per Pipeline_Lock_Coordination_Spec.md §4.3.
/// </summary>
public sealed record PipelineLock
{
    public required string PipelineId { get; init; }
    public required OwnerIdentity Owner { get; init; }
    public required LockKind Kind { get; init; }
    public required LockStatus Status { get; init; }
    public required DateTime AcquiredUtc { get; init; }
    public required DateTime ExpiresUtc { get; init; }
    public required DateTime LastHeartbeatUtc { get; init; }

    /// <summary>Create a copy with a new owner (for RewriteOwners).</summary>
    public PipelineLock WithOwner(OwnerIdentity newOwner) => this with { Owner = newOwner };

    /// <summary>Create a copy with extended expiry (for Heartbeat).</summary>
    public PipelineLock WithExtendedExpiry(DateTime newExpiry) =>
        this with { ExpiresUtc = newExpiry, LastHeartbeatUtc = DateTime.UtcNow };
}
