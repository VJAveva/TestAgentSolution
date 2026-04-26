using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TestControllerGrpc.ViewModels.Execution;

/// <summary>
/// ViewModel for one session card in the dashboard.
/// </summary>
public partial class SessionCardVM : ObservableObject
{
    [ObservableProperty] private string _sessionId = "";
    [ObservableProperty] private string _watchItemTag = "";
    [ObservableProperty] private string _userId = "";
    [ObservableProperty] private string _source = "";
    [ObservableProperty] private string _status = "Running";
    [ObservableProperty] private string _elapsed = "00:00";
    [ObservableProperty] private string _buildNumber = "";
    [ObservableProperty] private int _totalActions;
    [ObservableProperty] private int _completedActions;
    [ObservableProperty] private int _passedActions;
    [ObservableProperty] private int _failedActions;
    [ObservableProperty] private int _progressPercent;
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _lockedAgentsList = "";

    public ObservableCollection<AgentRowVM> Agents { get; } = new();

    public string StatusBadge => Status switch
    {
        "Running"   => "Running",
        "Success"   => "Completed",
        "Failed"    => "Failed",
        "Cancelled" => "Cancelled",
        _           => Status,
    };

    public string Summary =>
        $"{Agents.Count} agents | " +
        $"{PassedActions} passed, {FailedActions} failed | " +
        $"{ProgressPercent}%";

    /// <summary>
    /// Finds or creates an AgentRowVM. Must be called on UI thread.
    /// </summary>
    public AgentRowVM GetOrCreateAgent(string agentName)
    {
        var existing = Agents.FirstOrDefault(a =>
            string.Equals(a.AgentName, agentName,
                StringComparison.OrdinalIgnoreCase));

        if (existing != null) return existing;

        var newAgent = new AgentRowVM { AgentName = agentName };
        Agents.Add(newAgent);
        return newAgent;
    }

    public void RecalculateCounters()
    {
        CompletedActions = Agents.Sum(a => a.CompletedCount);
        PassedActions = Agents.Sum(a =>
            a.Actions.Count(act => act.Status == "Success"));
        FailedActions = Agents.Sum(a =>
            a.Actions.Count(act => act.Status == "Failed"));
        TotalActions = Math.Max(TotalActions,
            Agents.Sum(a => a.TotalCount));
        ProgressPercent = TotalActions > 0
            ? (int)(CompletedActions * 100.0 / TotalActions) : 0;

        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(StatusBadge));
    }

    partial void OnStatusChanged(string value)
    {
        OnPropertyChanged(nameof(StatusBadge));
        OnPropertyChanged(nameof(Summary));
    }

    partial void OnPassedActionsChanged(int value)
        => OnPropertyChanged(nameof(Summary));

    partial void OnFailedActionsChanged(int value)
        => OnPropertyChanged(nameof(Summary));

    partial void OnProgressPercentChanged(int value)
        => OnPropertyChanged(nameof(Summary));
}
