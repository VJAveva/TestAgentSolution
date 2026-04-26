using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels.Execution;

/// <summary>
/// Master ViewModel for the execution dashboard.
///
/// PHASE 1 ADAPTER: The codebase currently does not publish
/// per-action <c>NodeProgressEvent</c> / <c>AgentOutputEvent</c>
/// events. Instead this VM:
///   - Subscribes to the existing <see cref="ExecutionStartedEvent"/> and
///     <see cref="ExecutionCompletedEvent"/> for card lifecycle.
///   - Polls each active <see cref="ExecutionSession"/> every second via a
///     <see cref="DispatcherTimer"/>, reconciling agent rows / pills from
///     <see cref="ExecutionSession.GetAgentSummaries"/> and harvesting new
///     log entries from <see cref="ExecutionSession.GetRecentLogs"/>.
///
/// This keeps the UI decoupled and forward-compatible: when proper
/// per-action events are added later, swap the timer reconciliation
/// for direct event handlers.
/// </summary>
public partial class ExecutionDashboardVM : ObservableObject
{
    private readonly ExecutionSessionManager _sessionManager;
    private readonly AgentLockManager _lockManager;
    private readonly IEventAggregator _eventAggregator;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _refreshTimer;

    /// <summary>Per-session count of log entries already mirrored into <see cref="LogEntries"/>.</summary>
    private readonly Dictionary<string, int> _logCursors = new(StringComparer.Ordinal);

    public ExecutionDashboardVM(
        ExecutionSessionManager sessionManager,
        AgentLockManager lockManager,
        IEventAggregator eventAggregator,
        Dispatcher dispatcher)
    {
        _sessionManager = sessionManager;
        _lockManager = lockManager;
        _eventAggregator = eventAggregator;
        _dispatcher = dispatcher;

        _eventAggregator.Subscribe<ExecutionStartedEvent>(OnExecutionStarted);
        _eventAggregator.Subscribe<ExecutionCompletedEvent>(OnExecutionCompleted);

        _refreshTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            OnRefreshTick,
            _dispatcher);
        _refreshTimer.Start();

        CancelSessionCommand = new RelayCommand<string>(CancelSession);
        ClearSelectionCommand = new RelayCommand(() =>
        {
            SelectedSessionId = null;
            SelectedAgentName = null;
        });
        ClearLogsCommand = new RelayCommand(() => LogEntries.Clear());
    }

    // ?? Collections ?????????????????????????????????????????????????

    public ObservableCollection<SessionCardVM> Sessions { get; } = new();
    public ObservableCollection<LogEntryVM> LogEntries { get; } = new();

    // ?? Selection state (drives log filtering) ??????????????????????

    [ObservableProperty] private string? _selectedSessionId;
    [ObservableProperty] private string? _selectedAgentName;

    // ?? Summary stats ???????????????????????????????????????????????

    [ObservableProperty] private int _activeSessionCount;
    [ObservableProperty] private int _lockedAgentCount;
    [ObservableProperty] private int _totalPassedActions;
    [ObservableProperty] private int _totalFailedActions;
    [ObservableProperty] private int _overallProgressPercent;

    // ?? Commands ????????????????????????????????????????????????????

    public System.Windows.Input.ICommand CancelSessionCommand { get; }
    public System.Windows.Input.ICommand ClearSelectionCommand { get; }
    public System.Windows.Input.ICommand ClearLogsCommand { get; }

    // ?? Filter properties for log panel ?????????????????????????????

    [ObservableProperty] private string _logSearchText = "";
    [ObservableProperty] private string _logSeverityFilter = "All";

    /// <summary>
    /// Predicate used by CollectionViewSource.Filter in XAML.
    /// </summary>
    public bool FilterLogEntry(LogEntryVM entry)
    {
        if (!string.IsNullOrEmpty(SelectedSessionId) &&
            entry.SessionId != SelectedSessionId)
            return false;

        if (!string.IsNullOrEmpty(SelectedAgentName) &&
            !string.Equals(entry.AgentName, SelectedAgentName,
                StringComparison.OrdinalIgnoreCase))
            return false;

        if (LogSeverityFilter != "All" &&
            entry.Severity != LogSeverityFilter)
            return false;

        if (!string.IsNullOrEmpty(LogSearchText))
        {
            var lower = LogSearchText.ToLowerInvariant();
            if (!entry.Message.ToLowerInvariant().Contains(lower) &&
                !entry.AgentName.ToLowerInvariant().Contains(lower))
                return false;
        }

        return true;
    }

    // ?? Event handlers ??????????????????????????????????????????????

    private void OnExecutionStarted(ExecutionStartedEvent e)
    {
        _dispatcher.InvokeAsync(() =>
        {
            if (Sessions.Any(s => s.SessionId == e.SessionId)) return;

            var session = _sessionManager.GetSession(e.SessionId);
            var card = new SessionCardVM
            {
                SessionId = e.SessionId,
                WatchItemTag = e.WatchItemTag,
                UserId = session?.UserId ?? "",
                Source = e.Source,
                Status = "Running",
                BuildNumber = session != null &&
                    session.ResolvedParameters.TryGetValue("_BuildNumber", out var bn)
                        ? bn : "",
                LockedAgentsList = session != null
                    ? string.Join(", ", session.LockedAgents)
                    : "",
                IsExpanded = true,
            };

            Sessions.Insert(0, card);
            RecalculateStats();
        });
    }

    private void OnExecutionCompleted(ExecutionCompletedEvent e)
    {
        _dispatcher.InvokeAsync(() =>
        {
            var card = Sessions.FirstOrDefault(s => s.SessionId == e.SessionId);
            if (card == null) return;

            card.Status = e.State;
            card.PassedActions = e.Passed;
            card.FailedActions = e.Failed;
            card.ProgressPercent = 100;
            card.IsExpanded = false;

            // Final reconcile so all pills show terminal state.
            ReconcileCard(card);
            card.RecalculateCounters();
            RecalculateStats();
        });
    }

    private void OnRefreshTick(object? sender, EventArgs e)
    {
        // UI thread (DispatcherTimer).
        foreach (var card in Sessions.ToList())
        {
            var session = _sessionManager.GetSession(card.SessionId);
            if (session == null) continue;

            if (card.Status == "Running")
            {
                card.Elapsed = (DateTime.UtcNow - session.StartedUtc)
                    .ToString(@"hh\:mm\:ss");
            }

            ReconcileCard(card);
            HarvestLogs(card, session);
        }

        RecalculateStats();
    }

    /// <summary>Reconciles agent rows + pills against the session's current snapshots.</summary>
    private void ReconcileCard(SessionCardVM card, ExecutionSession session)
    {
        var summaries = session.GetAgentSummaries();
        foreach (var summary in summaries)
        {
            var row = card.GetOrCreateAgent(summary.AgentName);
            foreach (var action in summary.Actions)
            {
                row.UpdateAction(
                    tag: action.ActionTag,
                    actionType: action.ActionType,
                    command: action.Command,
                    status: MapOutcome(action.Outcome),
                    exitCode: action.ExitCode ?? 0,
                    errorMessage: action.ErrorMessage ?? "",
                    duration: action.DurationText);
            }
        }
        card.RecalculateCounters();
    }

    // Wrapper: the real reconcile body needs the session — re-resolve here
    // so the timer can use a single call site.
    private void ReconcileCard(SessionCardVM card)
    {
        var s = _sessionManager.GetSession(card.SessionId);
        if (s != null) ReconcileCard(card, s);
    }

    private static string MapOutcome(ActionOutcome outcome) => outcome switch
    {
        ActionOutcome.Success    => "Success",
        ActionOutcome.Failed     => "Failed",
        ActionOutcome.Terminated => "Failed",
        ActionOutcome.TimedOut   => "Failed",
        _                        => "Running",
    };

    private void HarvestLogs(SessionCardVM card, ExecutionSession session)
    {
        var all = session.GetRecentLogs(0);
        var seen = _logCursors.GetValueOrDefault(card.SessionId, 0);
        if (all.Count <= seen) return;

        for (var i = seen; i < all.Count; i++)
        {
            var entry = all[i];
            AddLogEntry(new LogEntryVM
            {
                Timestamp = entry.Timestamp.ToString("HH:mm:ss"),
                SessionId = card.SessionId,
                SessionName = card.WatchItemTag,
                AgentName = "",
                Category = entry.Category,
                Message = entry.Message,
                Severity = InferSeverity(entry.Category, entry.Message),
            });
        }
        _logCursors[card.SessionId] = all.Count;
    }

    private static string InferSeverity(string category, string message)
    {
        if (category.Equals("Error", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("FAIL", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Exception", StringComparison.OrdinalIgnoreCase))
            return "Error";
        if (category.Equals("Warning", StringComparison.OrdinalIgnoreCase))
            return "Warning";
        if (message.Contains("Success", StringComparison.OrdinalIgnoreCase))
            return "Success";
        return "Info";
    }

    private void AddLogEntry(LogEntryVM entry)
    {
        LogEntries.Add(entry);
        while (LogEntries.Count > 5000)
            LogEntries.RemoveAt(0);
    }

    // ?? Helpers ?????????????????????????????????????????????????????

    private void RecalculateStats()
    {
        ActiveSessionCount = Sessions.Count(s => s.Status == "Running");
        LockedAgentCount = _lockManager.GetAllLocks().Count;
        TotalPassedActions = Sessions
            .Where(s => s.Status == "Running")
            .Sum(s => s.PassedActions);
        TotalFailedActions = Sessions
            .Where(s => s.Status == "Running")
            .Sum(s => s.FailedActions);

        var running = Sessions.Where(s => s.Status == "Running").ToList();
        OverallProgressPercent = running.Count > 0
            ? (int)running.Average(s => s.ProgressPercent)
            : 0;
    }

    private void CancelSession(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;

        var card = Sessions.FirstOrDefault(s => s.SessionId == sessionId);
        if (card == null) return;

        var result = System.Windows.MessageBox.Show(
            $"Cancel session '{card.WatchItemTag}'?\n\n" +
            $"User: {card.UserId}\n" +
            $"Agents: {card.LockedAgentsList}\n" +
            $"Progress: {card.ProgressPercent}%\n\n" +
            "This will cancel the running pipeline and " +
            "release all locked agents.",
            "Cancel Session",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (result != System.Windows.MessageBoxResult.Yes) return;

        _eventAggregator.Publish(new CancelSessionRequestEvent
        {
            SessionId = sessionId
        });
    }

    public void SelectAgent(string agentName)
    {
        SelectedAgentName =
            SelectedAgentName == agentName ? null : agentName;
    }

    public void SelectSession(string sessionId)
    {
        SelectedSessionId =
            SelectedSessionId == sessionId ? null : sessionId;
        SelectedAgentName = null;
    }
}

/// <summary>Log entry for display in the log panel.</summary>
public partial class LogEntryVM : ObservableObject
{
    [ObservableProperty] private string _timestamp = "";
    [ObservableProperty] private string _sessionId = "";
    [ObservableProperty] private string _sessionName = "";
    [ObservableProperty] private string _agentName = "";
    [ObservableProperty] private string _category = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _severity = "Info";
}

/// <summary>
/// Published when the dashboard requests cancellation of a session.
/// MainViewModel handles this by cancelling the corresponding CTS.
/// </summary>
public sealed record CancelSessionRequestEvent
{
    public string SessionId { get; init; } = "";
}
