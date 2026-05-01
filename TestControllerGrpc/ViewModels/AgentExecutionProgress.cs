using CommunityToolkit.Mvvm.ComponentModel;

namespace TestControllerGrpc.ViewModels;

/// <summary>
/// Tracks real-time execution progress for a single agent during pipeline run.
/// Displayed as a row in the Execution Dashboard.
/// </summary>
public sealed partial class AgentExecutionProgress : ObservableObject
{
    [ObservableProperty] private string _agentName = "";
    [ObservableProperty] private string _status = "Queued";        // Queued, Running, Success, Failed
    [ObservableProperty] private string _currentAction = "Pending";
    [ObservableProperty] private int _totalActions;
    [ObservableProperty] private int _completedActions;
    [ObservableProperty] private int _passedActions;
    [ObservableProperty] private int _failedActions;
    [ObservableProperty] private double _progressPercent;          // 0-100
    [ObservableProperty] private string _etaText = "";
    [ObservableProperty] private string _elapsedText = "";
    [ObservableProperty] private DateTime _startedAt;
    [ObservableProperty] private string _statusColor = "#94A3B8";  // Gray=queued

    partial void OnStatusChanged(string value)
    {
        StatusColor = value switch
        {
            "Running" => "#3B82F6",    // Blue
            "Success" => "#10B981",    // Green
            "Failed"  => "#EF4444",    // Red
            _         => "#94A3B8"     // Gray
        };
    }

    partial void OnCompletedActionsChanged(int value)
    {
        if (TotalActions > 0)
        {
            ProgressPercent = (double)value / TotalActions * 100.0;

            if (value > 0 && StartedAt != default)
            {
                var elapsed = DateTime.Now - StartedAt;
                var perAction = elapsed.TotalSeconds / value;
                var remaining = (TotalActions - value) * perAction;
                var eta = TimeSpan.FromSeconds(remaining);
                EtaText = remaining > 60
                    ? $"ETA: {eta.Minutes}m {eta.Seconds}s"
                    : $"ETA: {eta.Seconds}s";
                ElapsedText = elapsed.ToString(@"mm\:ss");
            }
        }
    }

    public void RecordPass()
    {
        PassedActions++;
        CompletedActions++;
    }

    public void RecordFail()
    {
        FailedActions++;
        CompletedActions++;
    }
}
