namespace TestControllerGrpc.Services;

/// <summary>Result of executing a single action (local or remote).</summary>
public sealed record ActionResult(bool Success, int ExitCode, string ErrorMessage);

/// <summary>Log entry emitted by the action pipeline executor.</summary>
public sealed record PipelineLogEntry(DateTime Timestamp, string Category, string Message);

/// <summary>A single step in agent diagnostic results.</summary>
public sealed record DiagnosticStep(string Name, bool Passed, string Detail, bool IsFatal = true);

/// <summary>Per-agent health state tracked by the resilience layer.</summary>
public sealed record AgentHealthState
{
    public required string AgentName { get; init; }
    public bool IsHealthy { get; set; } = true;
    public int ConsecutiveFailures { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public DateTime? CircuitOpenedUtc { get; set; }
}
