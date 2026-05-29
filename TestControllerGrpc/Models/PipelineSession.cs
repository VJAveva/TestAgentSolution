using CommunityToolkit.Mvvm.ComponentModel;

namespace TestControllerGrpc.Models;

/// <summary>
/// Represents one active or recently-completed pipeline execution.
/// Each WatchItem trigger creates a new PipelineSession.
/// Multiple sessions can run concurrently.
/// </summary>
public sealed partial class PipelineSession : ObservableObject
{
    public string SessionId { get; } = Guid.NewGuid().ToString("N")[..6];
    public string WatchItemTag { get; init; } = "";
    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;

    [ObservableProperty] private string _status = "Running";
    [ObservableProperty] private string _statusDetail = "";
    [ObservableProperty] private int _totalActions;
    [ObservableProperty] private int _completedActions;
    [ObservableProperty] private int _passedActions;
    [ObservableProperty] private int _failedActions;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private int _agentCount = 1;
    [ObservableProperty] private string _elapsed = "0:00:00";

    public CancellationTokenSource Cts { get; } = new();

    /// <summary>Formatted action progress string (e.g. "3/12 actions").</summary>
    public string ActionProgress => $"{CompletedActions}/{TotalActions} actions";

    partial void OnCompletedActionsChanged(int value)
        => OnPropertyChanged(nameof(ActionProgress));

    partial void OnTotalActionsChanged(int value)
        => OnPropertyChanged(nameof(ActionProgress));

    /// <summary>The underlying ExecutionSession from the session manager.</summary>
    public ExecutionSession? ExecutionSession { get; set; }

    public string DisplayLabel => $"[{SessionId}] {WatchItemTag}";

    public void RecordPass()
    {
        PassedActions++;
        CompletedActions++;
        if (TotalActions > 0)
            ProgressPercent = (double)CompletedActions / TotalActions * 100;
    }

    public void RecordFail()
    {
        FailedActions++;
        CompletedActions++;
        if (TotalActions > 0)
            ProgressPercent = (double)CompletedActions / TotalActions * 100;
    }

    public void Complete()
    {
        Status = FailedActions > 0 ? "Failed" : "Success";
        ProgressPercent = 100;
    }

    public void Cancel()
    {
        Cts.Cancel();
        Status = "Cancelled";
    }
}
