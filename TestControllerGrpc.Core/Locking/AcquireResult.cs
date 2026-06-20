namespace TestControllerGrpc.Locking;

/// <summary>
/// Result of attempting to acquire a pipeline lock.
/// Per Pipeline_Lock_Coordination_Spec.md §4.3.
/// </summary>
public abstract record AcquireResult
{
    private AcquireResult() { }

    /// <summary>Lock successfully acquired (new lock created).</summary>
    public sealed record Success(PipelineLock Lock) : AcquireResult;

    /// <summary>
    /// An active lock already exists for this pipeline — the caller cannot proceed.
    /// Single-run rule: this is returned for EVERY existing active lock, including the
    /// caller's own. Privilege does not grant a second concurrent run; the path is Cancel.
    /// </summary>
    public sealed record Conflict(PipelineLock ExistingLock) : AcquireResult;
}
