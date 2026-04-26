using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TestControllerGrpc.ViewModels.Execution;

/// <summary>
/// ViewModel for one agent row inside a session card.
/// </summary>
public partial class AgentRowVM : ObservableObject
{
    [ObservableProperty] private string _agentName = "";
    [ObservableProperty] private string _status = "Idle";
    [ObservableProperty] private int _progressPercent;
    [ObservableProperty] private int _completedCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private string _currentAction = "";
    [ObservableProperty] private string _lastOutput = "";

    public ObservableCollection<ActionPillVM> Actions { get; } = new();

    public string StatusText => Status switch
    {
        "Executing" => "Executing",
        "Rebooting" => "Rebooting...",
        "Success"   => "Done",
        "Failed"    => "Failed",
        _           => "Idle",
    };

    /// <summary>
    /// Updates or adds an action pill. Must be called on the UI thread.
    /// </summary>
    public void UpdateAction(
        string tag, string actionType, string command,
        string status, int exitCode = 0,
        string errorMessage = "", string duration = "",
        int progressPercent = 0)
    {
        var key = string.IsNullOrEmpty(tag)
            ? $"{actionType}|{command}"
            : tag;

        var existing = Actions.FirstOrDefault(a => a.Key == key);

        if (existing != null)
        {
            // B5: Never downgrade a terminal status. Once a pill reaches
            // Success/Failed/Skipped, a stale "Running"/"Pending" record
            // (e.g. an out-of-order reconcile from a ConcurrentBag) must
            // not flip it back to in-progress.
            var isTerminal = existing.Status is "Success" or "Failed" or "Skipped";
            var demotion = status is "Running" or "Pending";
            if (isTerminal && demotion)
                return;

            existing.ActionType = actionType;
            existing.Command = command;
            existing.Status = status;
            existing.ExitCode = exitCode;
            existing.ErrorMessage = errorMessage;
            existing.Duration = duration;
            existing.ProgressPercent = progressPercent;
        }
        else
        {
            Actions.Add(new ActionPillVM
            {
                Tag = tag,
                ActionType = actionType,
                Command = command,
                AgentName = AgentName,
                Status = status,
                ExitCode = exitCode,
                ErrorMessage = errorMessage,
                Duration = duration,
                ProgressPercent = progressPercent,
            });
            TotalCount = Actions.Count;
        }

        CompletedCount = Actions.Count(a =>
            a.Status is "Success" or "Failed" or "Skipped");
        ProgressPercent = TotalCount > 0
            ? (int)(CompletedCount * 100.0 / TotalCount) : 0;

        if (Actions.Any(a => a.Status == "Failed"))
            Status = "Failed";
        else if (Actions.Any(a => a.Status == "Running"))
            Status = "Executing";
        else if (CompletedCount > 0 && CompletedCount == TotalCount)
            Status = "Success";

        CurrentAction = Actions
            .FirstOrDefault(a => a.Status == "Running")?.Command ?? "";

        OnPropertyChanged(nameof(StatusText));
    }

    partial void OnStatusChanged(string value)
        => OnPropertyChanged(nameof(StatusText));
}
