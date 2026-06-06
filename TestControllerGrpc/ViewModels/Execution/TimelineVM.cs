using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TestControllerGrpc.ViewModels.Execution;

/// <summary>
/// ViewModel for the Timeline (Gantt chart) window.
/// Derives horizontal bar positions from each agent's actions.
/// </summary>
public partial class TimelineVM : ObservableObject, IDisposable
{
    private readonly ExecutionDashboardVM _dashboard;
    private readonly DispatcherTimer _refreshTimer;
    private bool _disposed;

    public TimelineVM(ExecutionDashboardVM dashboard, Dispatcher dispatcher)
    {
        _dashboard = dashboard;
        _refreshTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(2),
            DispatcherPriority.Background,
            OnRefreshTick,
            dispatcher);
        _refreshTimer.Start();
        Rebuild();
    }

    /// <summary>Flat list of agent lanes for the Gantt Y-axis.</summary>
    public ObservableCollection<TimelineAgentLane> Lanes { get; } = new();

    /// <summary>Total timeline width in seconds (auto-calculated).</summary>
    [ObservableProperty] private double _totalSeconds = 90;

    /// <summary>Current time cursor position in seconds from the earliest session start.</summary>
    [ObservableProperty] private double _cursorSeconds;

    /// <summary>Canvas pixel width used for layout calculations.</summary>
    [ObservableProperty] private double _canvasWidth = 1200;

    private DateTime _timeOrigin = DateTime.UtcNow;

    private void OnRefreshTick(object? sender, EventArgs e)
    {
        CursorSeconds = (DateTime.UtcNow - _timeOrigin).TotalSeconds;
        if (CursorSeconds > TotalSeconds)
            TotalSeconds = CursorSeconds + 30;
        Rebuild();
    }

    /// <summary>Rebuilds all lanes and bars from the dashboard's current sessions.</summary>
    public void Rebuild()
    {
        // Determine earliest session start as time origin
        var allSessions = _dashboard.Sessions.ToList();
        if (allSessions.Count == 0) return;

        // Collect all agent rows across sessions
        var agentActions = new Dictionary<string, List<(ActionPillVM pill, string sessionTag)>>(
            StringComparer.OrdinalIgnoreCase);

        DateTime earliest = DateTime.UtcNow;
        foreach (var session in allSessions)
        {
            foreach (var agent in session.Agents)
            {
                if (!agentActions.ContainsKey(agent.AgentName))
                    agentActions[agent.AgentName] = new();

                foreach (var pill in agent.Actions)
                {
                    if (pill.StartedUtc < earliest) earliest = pill.StartedUtc;
                    agentActions[agent.AgentName].Add((pill, session.WatchItemTag));
                }
            }
        }

        _timeOrigin = earliest;

        // Update or create lanes
        var existingLanes = Lanes.ToDictionary(l => l.AgentName, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (agentName, actions) in agentActions.OrderBy(kv => kv.Key))
        {
            seen.Add(agentName);
            if (!existingLanes.TryGetValue(agentName, out var lane))
            {
                lane = new TimelineAgentLane { AgentName = agentName };
                Lanes.Add(lane);
            }

            // Rebuild bars
            lane.Bars.Clear();
            foreach (var (pill, sessionTag) in actions.OrderBy(a => a.pill.StartedUtc))
            {
                var offsetSec = (pill.StartedUtc - _timeOrigin).TotalSeconds;
                var durSec = pill.DurationSeconds > 0
                    ? pill.DurationSeconds
                    : pill.Status == "Running"
                        ? (DateTime.UtcNow - pill.StartedUtc).TotalSeconds
                        : 5; // default minimum width

                lane.Bars.Add(new TimelineBar
                {
                    ActionTag = pill.Tag,
                    SessionTag = sessionTag,
                    Status = pill.Status,
                    OffsetSeconds = offsetSec,
                    DurationSeconds = durSec,
                    Tooltip = pill.Tooltip,
                });
            }
        }

        // Remove lanes for agents no longer present
        for (int i = Lanes.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Lanes[i].AgentName))
                Lanes.RemoveAt(i);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer.Stop();
    }
}

/// <summary>One horizontal lane (row) in the Gantt chart, representing an agent.</summary>
public partial class TimelineAgentLane : ObservableObject
{
    [ObservableProperty] private string _agentName = "";
    public ObservableCollection<TimelineBar> Bars { get; } = new();
}

/// <summary>One horizontal bar in the Gantt chart, representing an action's time span.</summary>
public partial class TimelineBar : ObservableObject
{
    [ObservableProperty] private string _actionTag = "";
    [ObservableProperty] private string _sessionTag = "";
    [ObservableProperty] private string _status = "Pending";
    [ObservableProperty] private double _offsetSeconds;
    [ObservableProperty] private double _durationSeconds;
    [ObservableProperty] private double _baselineDurationSeconds;
    [ObservableProperty] private string _tooltip = "";

    /// <summary>
    /// Burnout level: "Normal" (under baseline), "Approaching" (100-120% of baseline),
    /// "Exceeded" (over 120% of baseline), or "Unknown" (no baseline data).
    /// </summary>
    public string BurnoutLevel
    {
        get
        {
            if (BaselineDurationSeconds <= 0) return "Unknown";
            var ratio = DurationSeconds / BaselineDurationSeconds;
            if (ratio > 1.2) return "Exceeded";
            if (ratio > 1.0) return "Approaching";
            return "Normal";
        }
    }

    partial void OnDurationSecondsChanged(double value) => OnPropertyChanged(nameof(BurnoutLevel));
    partial void OnBaselineDurationSecondsChanged(double value) => OnPropertyChanged(nameof(BurnoutLevel));
}
