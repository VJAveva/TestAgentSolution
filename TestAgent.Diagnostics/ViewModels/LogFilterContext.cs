using TestAgent.Diagnostics.Models;

namespace TestAgent.Diagnostics.ViewModels;

/// <summary>
/// The filter context View A hands to View B via "View logs" — so Logs lands on the
/// relevant lines (run + action + agent + time window) instead of the whole log.
/// </summary>
public sealed class LogFilterContext
{
    public string RunId { get; init; } = "";
    public string Action { get; init; } = "";
    public string Agent { get; init; } = "";
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }

    /// <summary>Human-readable summary for the "filtered by" chip.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(RunId)) parts.Add($"run {RunId}");
        if (!string.IsNullOrEmpty(Action)) parts.Add($"action {Action}");
        if (!string.IsNullOrEmpty(Agent)) parts.Add($"agent {Agent}");
        return parts.Count == 0 ? "" : string.Join(" \u00B7 ", parts);
    }

    public static LogFilterContext ForRun(FailingRunVM run, ActionStepVM? step)
    {
        DateTime? from = step?.Timestamp ?? run.Records.FirstOrDefault()?.Timestamp;
        DateTime? to = run.Records.Count > 0 ? run.Records[^1].Timestamp.AddSeconds(1) : null;
        return new LogFilterContext
        {
            RunId = run.RunId,
            Action = step?.Name ?? run.FailedAction,
            Agent = step?.Agent ?? run.Agent,
            From = from,
            To = to,
        };
    }
}
