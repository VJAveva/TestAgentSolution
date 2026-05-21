namespace TestAgentGrpc.Services;

/// <summary>
/// Immutable record capturing the full lifecycle of a single execution.
/// Replaces scattered fields in <see cref="CommandExecutor"/> with an explicit
/// owner-aware state object that tracks lock acquisition and termination reasons.
/// </summary>
public sealed class ExecutionLifecycleState
{
    public string ExecutionId { get; }
    public string Command { get; }
    public string RedactedCommand { get; }
    public bool LockAcquired { get; private set; }
    public DateTime StartedUtc { get; }
    public bool TerminationRequested { get; private set; }
    public string? ResetReason { get; private set; }

    public ExecutionLifecycleState(string executionId, string command, string arguments)
    {
        ExecutionId = executionId;
        Command = command;
        RedactedCommand = SecurityRedactor.RedactCommandLine(command, arguments);
        StartedUtc = DateTime.UtcNow;
    }

    public void MarkLockAcquired() => LockAcquired = true;

    public void RequestTermination(string reason)
    {
        TerminationRequested = true;
        ResetReason = reason;
    }

    public void MarkReset(string reason) => ResetReason = reason;
}
