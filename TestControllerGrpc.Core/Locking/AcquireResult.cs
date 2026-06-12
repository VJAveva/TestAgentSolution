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

    /// <summary>Same owner re-acquired (TTL extended, no conflict).</summary>
    public sealed record ReAcquired(PipelineLock Lock) : AcquireResult;

    /// <summary>Different owner holds the lock — caller cannot proceed.</summary>
    public sealed record Conflict(PipelineLock ExistingLock) : AcquireResult;
}
