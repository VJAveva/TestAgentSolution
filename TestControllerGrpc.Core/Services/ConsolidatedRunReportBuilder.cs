using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Projects a completed <see cref="ExecutionSession"/> into a <see cref="ConsolidatedRunReport"/>.
/// Concentrates the rules that are easy to get plausibly wrong: wall time is a span (never a sum of
/// durations, which double-counts parallel agents), action order comes from Sequence (the per-agent bag has
/// none), Skipped is neither a pass nor a failure, and only whitelisted parameters are copied out because
/// the resolved set carries credentials.
/// </summary>
public static class ConsolidatedRunReportBuilder
{
    private const string ControllerAgent = "Controller";

    public static ConsolidatedRunReport Build(ExecutionSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        IReadOnlyList<string> phases = session.GroupPhases;
        var agents = session.GetAgentSummaries()
            .OrderBy(a => a.AgentName, StringComparer.OrdinalIgnoreCase)
            .Select(a => BuildAgent(a, phases))
            .ToList();

        var agentsPassed = agents.Count(a => a.Passed);
        var completedUtc = session.CompletedUtc ?? DateTime.UtcNow;

        return new ConsolidatedRunReport(
            PipelineTag: session.WatchItemTag,
            SessionId: session.SessionId,
            EventType: session.EventType,
            BuildNumber: Param(session, "_BuildNumber"),
            DropLocation: Param(session, "_DropLocation"),
            StartedUtc: session.StartedUtc,
            CompletedUtc: completedUtc,
            WallTime: session.WallTime,
            Passed: agents.Count > 0 && agentsPassed == agents.Count,
            AgentsPassed: agentsPassed,
            AgentsTotal: agents.Count,
            ActionsSucceeded: session.SucceededCount,
            ActionsFailed: session.FailedCount,
            ActionsSkipped: session.SkippedCount,
            ActionsTotal: session.TotalActions,
            TriggeredBy: string.IsNullOrWhiteSpace(session.UserDisplayName) ? session.UserId : session.UserDisplayName,
            Source: session.Source,
            Phases: phases,
            Agents: agents);
    }

    private static ConsolidatedAgentRow BuildAgent(AgentSessionSummary summary, IReadOnlyList<string> phases)
    {
        IReadOnlyList<ActionExecutionResult> ordered = summary.OrderedActions;

        var actions = ordered
            .Select((r, i) => new ConsolidatedActionRow(
                Index: i + 1,
                Name: string.IsNullOrWhiteSpace(r.ActionTag) ? r.Command : r.ActionTag,
                Phase: r.TopGroup,
                Outcome: r.Outcome,
                Duration: r.Duration,
                ErrorMessage: r.ErrorMessage))
            .ToList();

        var phaseResults = new Dictionary<string, PhaseOutcome>(StringComparer.OrdinalIgnoreCase);
        foreach (var phase in phases)
            phaseResults[phase] = PhaseFor(ordered, phase);

        return new ConsolidatedAgentRow(
            AgentName: summary.AgentName,
            Pool: PoolOf(ordered),
            PhaseResults: phaseResults,
            Passed: summary.Status == "Success",
            Succeeded: summary.SucceededCount,
            Failed: summary.FailedCount,
            Skipped: summary.SkippedCount,
            Total: summary.TotalCount,
            WallTime: summary.WallTime,
            Actions: actions);
    }

    // A failure anywhere in the phase dominates; a phase the agent never entered stays NotRun so the email
    // shows a blank cell rather than claiming a pass the agent never earned.
    private static PhaseOutcome PhaseFor(IReadOnlyList<ActionExecutionResult> actions, string phase)
    {
        var inPhase = actions.Where(a => string.Equals(a.TopGroup, phase, StringComparison.OrdinalIgnoreCase)).ToList();
        if (inPhase.Count == 0) return PhaseOutcome.NotRun;
        if (inPhase.Any(a => a.IsRetryable)) return PhaseOutcome.Failed;
        if (inPhase.All(a => a.Outcome == ActionOutcome.Skipped)) return PhaseOutcome.Skipped;
        return PhaseOutcome.Passed;
    }

    /// <summary>
    /// The second-level ActionGroup of the agent's first action — the "pool" grouping in the WatchList tree.
    /// Taken from the first action rather than merged across phases, so it stays short and deterministic.
    /// </summary>
    private static string PoolOf(IReadOnlyList<ActionExecutionResult> actions)
    {
        foreach (var action in actions)
        {
            var path = action.GroupPath;
            if (path.Length == 0) continue;
            var i = path.IndexOf(ActionExecutionResult.GroupSeparator, StringComparison.Ordinal);
            if (i < 0) continue;
            var rest = path[(i + ActionExecutionResult.GroupSeparator.Length)..];
            var j = rest.IndexOf(ActionExecutionResult.GroupSeparator, StringComparison.Ordinal);
            return j < 0 ? rest : rest[..j];
        }
        return "";
    }

    // The resolved parameter set holds credentials (_VCloudPassword and friends), so the report copies out
    // only the keys it renders. Never enumerate the dictionary into an email.
    private static string Param(ExecutionSession session, string key) =>
        session.ResolvedParameters.TryGetValue(key, out var value) ? value : "";
}
