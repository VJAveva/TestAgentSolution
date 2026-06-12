namespace TestControllerGrpc.Locking;

/// <summary>
/// In-memory registry of pipeline locks. Lives ONLY in the WPF controller process.
/// Per 03_Integration_With_Lock_Spec.md §3 and phase-3a-context.md.
/// </summary>
public interface ILockRegistry
{
    /// <summary>Attempt to acquire a lock on a pipeline.</summary>
    AcquireResult TryAcquire(string pipelineId, OwnerIdentity owner, LockKind kind);

    /// <summary>Release a lock held by the specified owner. Returns false if not found or different owner.</summary>
    bool TryRelease(string pipelineId, OwnerIdentity owner);

    /// <summary>Get the current lock for a pipeline, or null if unlocked.</summary>
    PipelineLock? Get(string pipelineId);

    /// <summary>Get all active locks.</summary>
    IReadOnlyList<PipelineLock> GetAll();

    /// <summary>Force-release a lock regardless of owner (for admin/sweeper).</summary>
    void ForceRelease(string pipelineId);

    /// <summary>Extend the TTL for a lock. Returns false if not found or different owner.</summary>
    bool Heartbeat(string pipelineId, OwnerIdentity owner);

    /// <summary>Rewrite all lock owners (used during mode transitions).</summary>
    void RewriteOwners(OwnerIdentity newOwner);

    /// <summary>Fired on every lock state transition.</summary>
    event Action<LockEvent>? OnLockEvent;
}
