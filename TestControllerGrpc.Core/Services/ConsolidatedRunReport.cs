using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>Per-agent outcome within one pipeline phase (an outermost ActionGroup).</summary>
public enum PhaseOutcome { NotRun, Passed, Failed, Skipped }

/// <summary>One action row in an agent's detail table.</summary>
public sealed record ConsolidatedActionRow(
    int Index, string Name, string Phase, ActionOutcome Outcome, TimeSpan Duration, string? ErrorMessage)
{
    public bool IsFailure => Outcome is ActionOutcome.Failed or ActionOutcome.Terminated or ActionOutcome.TimedOut;
}

/// <summary>One row of the Agent Summary table, plus that agent's action detail.</summary>
public sealed record ConsolidatedAgentRow(
    string AgentName,
    string Pool,
    IReadOnlyDictionary<string, PhaseOutcome> PhaseResults,
    bool Passed,
    int Succeeded,
    int Failed,
    int Skipped,
    int Total,
    TimeSpan WallTime,
    IReadOnlyList<ConsolidatedActionRow> Actions);

/// <summary>
/// Everything the consolidated run email renders, derived from a completed <see cref="ExecutionSession"/>.
/// Pure data: no I/O, no SMTP, no HTML — so the counting and timing rules are unit-testable on their own.
/// </summary>
public sealed record ConsolidatedRunReport(
    string PipelineTag,
    string SessionId,
    string EventType,
    string BuildNumber,
    string DropLocation,
    DateTime StartedUtc,
    DateTime CompletedUtc,
    TimeSpan WallTime,
    bool Passed,
    int AgentsPassed,
    int AgentsTotal,
    int ActionsSucceeded,
    int ActionsFailed,
    int ActionsSkipped,
    int ActionsTotal,
    string TriggeredBy,
    string Source,
    IReadOnlyList<string> Phases,
    IReadOnlyList<ConsolidatedAgentRow> Agents)
{
    public string Verdict => Passed ? "PASSED" : "FAILED";

    /// <summary>Subject-line summary; leads with the failure count because that is what gets acted on.</summary>
    public string Headline => Passed
        ? $"all {AgentsTotal} agent(s) succeeded"
        : $"{AgentsTotal - AgentsPassed} of {AgentsTotal} agent(s) failed";
}
