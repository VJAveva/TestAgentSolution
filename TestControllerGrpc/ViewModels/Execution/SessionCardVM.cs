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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OwnerLabel))]
    private string _userId = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OwnerLabel))]
    private string _userDisplayName = "";
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

    /// <summary>Friendly "by &lt;user&gt;" attribution: prefers the display name, falls back to the
    /// raw user id, then to an em dash when nothing was captured.</summary>
    public string OwnerLabel =>
        !string.IsNullOrWhiteSpace(UserDisplayName) ? UserDisplayName
        : !string.IsNullOrWhiteSpace(UserId) ? UserId
        : "—";

    public string StatusBadge => Status switch
    {
        "Running"   => "Running",
        "Success"   => "Completed",
        "Failed"    => "Failed",
        "Cancelled" => "Cancelled",
        _           => Status,
    };

    /// <summary>
    /// Severity rank for triage-first sorting.
    /// Higher rank = more urgent (sorts first in descending order).
    /// </summary>
    public int SeverityRank => Status switch
    {
        "Failed"    => 100,
        "Running" when FailedActions > 0 => 90,  // Running but has failures
        "Running"   => 10,
        "Cancelled" => 5,
        "Success"   => 0,
        _           => 5
    };

    /// <summary>Group key for triage display: "NeedsAttention" or "Normal".</summary>
    public string AttentionGroup => SeverityRank >= 90 ? "NeedsAttention" : "Normal";

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

        // Raise derived-property notifications only once at the end
        // instead of per-counter setter (reduces cascading INPC storms).
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(StatusBadge));
        OnPropertyChanged(nameof(SeverityRank));
        OnPropertyChanged(nameof(AttentionGroup));
    }

    // ── Dirty-guarded cascades ──────────────────────────────────────
    private string _lastStatusBadge = "";
    private string _lastSummary = "";

    private void RaiseDerivedIfChanged()
    {
        var badge = StatusBadge;
        if (badge != _lastStatusBadge) { _lastStatusBadge = badge; OnPropertyChanged(nameof(StatusBadge)); }
        var sum = Summary;
        if (sum != _lastSummary) { _lastSummary = sum; OnPropertyChanged(nameof(Summary)); }
    }

    partial void OnStatusChanged(string value) => RaiseDerivedIfChanged();

    partial void OnPassedActionsChanged(int value) => RaiseDerivedIfChanged();

    partial void OnFailedActionsChanged(int value) => RaiseDerivedIfChanged();

    partial void OnProgressPercentChanged(int value) => RaiseDerivedIfChanged();
}
