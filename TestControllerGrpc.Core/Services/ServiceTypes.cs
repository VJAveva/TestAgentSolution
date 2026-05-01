namespace TestControllerGrpc.Services;

/// <summary>Result of executing a single action (local or remote).</summary>
public sealed record ActionResult(bool Success, int ExitCode, string ErrorMessage);

/// <summary>Log entry emitted by the action pipeline executor.</summary>
/// <remarks>
/// <see cref="AgentName"/> and <see cref="SessionId"/> are optional attribution
/// added in P2-1 so the multi-session dashboard can filter logs per agent and
/// per session. Existing callers that pass only the first three arguments
/// remain source-compatible.
/// </remarks>
public sealed record PipelineLogEntry(
    DateTime Timestamp,
    string Category,
    string Message,
    string? AgentName = null,
    string? SessionId = null,
    string? Severity = null);

/// <summary>A single step in agent diagnostic results.</summary>
public sealed record DiagnosticStep(string Name, bool Passed, string Detail, bool IsFatal = true);

/// <summary>Per-agent health state tracked by the resilience layer.</summary>
public sealed record AgentHealthState
{
    /// <summary>Logical name of the agent this state applies to.</summary>
    public required string AgentName { get; init; }

    /// <summary>Indicates whether the agent is currently considered healthy by the circuit breaker.</summary>
    public bool IsHealthy { get; set; } = true;

    /// <summary>Number of consecutive failed operations since the last success.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>UTC timestamp of the most recent successful interaction with the agent, if any.</summary>
    public DateTime? LastSuccessUtc { get; set; }

    /// <summary>UTC timestamp at which the circuit breaker was last opened, if currently open.</summary>
    public DateTime? CircuitOpenedUtc { get; set; }
}
