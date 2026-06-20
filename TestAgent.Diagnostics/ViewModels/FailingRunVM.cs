using CommunityToolkit.Mvvm.ComponentModel;
using TestAgent.Diagnostics.Models;

namespace TestAgent.Diagnostics.ViewModels;

public enum StepStatus { Passed, Failed, NotRun }

/// <summary>One step in a run's action sequence (passed / failed / not-run).</summary>
public sealed partial class ActionStepVM : ObservableObject
{
    public string Name { get; init; } = "";
    public StepStatus Status { get; init; }
    public string Component { get; init; } = "";
    public string Agent { get; init; } = "";
    public DateTime? Timestamp { get; init; }

    /// <summary>The error record for a failed step (drives the failed-action detail panel).</summary>
    public LogRecord? ErrorRecord { get; init; }

    public string Glyph => Status switch
    {
        StepStatus.Passed => "\u2713",   // check
        StepStatus.Failed => "\u2717",   // cross
        _ => "\u25CB",                    // hollow circle
    };

    public string StatusText => Status.ToString();
    public string TimeText => Timestamp?.ToString("HH:mm:ss") ?? "";
}

/// <summary>One failing pipeline run (a run that has at least one error record).</summary>
public sealed partial class FailingRunVM : ObservableObject
{
    public string RunId { get; init; } = "";
    public string Pipeline { get; init; } = "";
    public string FailedAction { get; init; } = "";
    public string Component { get; init; } = "";
    public string Agent { get; init; } = "";
    public DateTime FirstErrorTime { get; init; }

    /// <summary>All records belonging to this run, in time order.</summary>
    public IReadOnlyList<LogRecord> Records { get; init; } = Array.Empty<LogRecord>();

    /// <summary>The ordered action sequence built from this run's records.</summary>
    public IReadOnlyList<ActionStepVM> Steps { get; init; } = Array.Empty<ActionStepVM>();

    public string FailedSummary => $"failed at {FailedAction}";
    public string TimeText => FirstErrorTime.ToString("yyyy-MM-dd HH:mm:ss");
}
