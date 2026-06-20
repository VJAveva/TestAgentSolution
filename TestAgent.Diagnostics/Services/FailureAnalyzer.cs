using TestAgent.Diagnostics.Models;
using TestAgent.Diagnostics.ViewModels;

namespace TestAgent.Diagnostics.Services;

/// <summary>
/// Builds the failure-first view: groups records by run, surfaces runs that contain an error,
/// and derives each run's ordered action sequence (passed / failed steps).
/// </summary>
public sealed class FailureAnalyzer
{
    public IReadOnlyList<FailingRunVM> BuildFailingRuns(IReadOnlyList<LogRecord> records)
    {
        var groups = new Dictionary<string, List<LogRecord>>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var r in records)
        {
            var key = !string.IsNullOrEmpty(r.RunId) ? r.RunId
                    : !string.IsNullOrEmpty(r.Pipeline) ? "pipe:" + r.Pipeline
                    : "(uncorrelated)";
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<LogRecord>();
                groups[key] = list;
                order.Add(key);
            }
            list.Add(r);
        }

        var result = new List<FailingRunVM>();
        foreach (var key in order)
        {
            var list = groups[key];
            var firstError = list.FirstOrDefault(static x => x.IsError);
            if (firstError is null) continue; // failure-first: only runs that actually failed

            var pipeline = list.FirstOrDefault(static x => !string.IsNullOrEmpty(x.Pipeline))?.Pipeline
                           ?? "(unknown pipeline)";
            var runId = list.FirstOrDefault(static x => !string.IsNullOrEmpty(x.RunId))?.RunId
                        ?? key;

            result.Add(new FailingRunVM
            {
                RunId = runId,
                Pipeline = pipeline,
                FailedAction = string.IsNullOrEmpty(firstError.Action) ? "(unknown)" : firstError.Action,
                Component = firstError.Component,
                Agent = firstError.Agent,
                FirstErrorTime = firstError.Timestamp,
                Records = list,
                Steps = BuildSteps(list),
            });
        }

        // Most recent failures first.
        result.Sort(static (a, b) => b.FirstErrorTime.CompareTo(a.FirstErrorTime));
        return result;
    }

    /// <summary>Segment a run's records into contiguous action blocks, each passed or failed.</summary>
    private static IReadOnlyList<ActionStepVM> BuildSteps(List<LogRecord> records)
    {
        var steps = new List<ActionStepVM>();
        string? currentAction = null;
        List<LogRecord>? block = null;

        void Flush()
        {
            if (block is null || block.Count == 0) return;
            var error = block.FirstOrDefault(static x => x.IsError);
            var head = block[0];
            steps.Add(new ActionStepVM
            {
                Name = string.IsNullOrEmpty(currentAction) ? head.Component : currentAction!,
                Status = error is not null ? StepStatus.Failed : StepStatus.Passed,
                Component = head.Component,
                Agent = (error ?? head).Agent,
                Timestamp = head.Timestamp,
                ErrorRecord = error,
            });
        }

        foreach (var r in records)
        {
            var action = string.IsNullOrEmpty(r.Action) ? r.Component : r.Action;
            if (!string.Equals(action, currentAction, StringComparison.Ordinal))
            {
                Flush();
                currentAction = action;
                block = new List<LogRecord>();
            }
            block!.Add(r);
        }
        Flush();

        return steps;
    }
}
