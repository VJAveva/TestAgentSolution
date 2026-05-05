using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using TestControllerGrpc.Views;
using TestControllerGrpc.ViewModels;

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
public partial class ExecutionDashboardVM : ObservableObject, IDisposable
{
    private readonly ExecutionSessionManager _sessionManager;
    private readonly AgentLockManager _lockManager;
    private readonly IEventAggregator _eventAggregator;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _refreshTimer;
    private readonly List<IDisposable> _subscriptions = new();
    private bool _disposed;

    /// <summary>Maximum number of session cards retained (running + completed).</summary>
    private const int MaxSessionCards = 50;

    /// <summary>Soft cap for log entries; <see cref="AddLogEntry"/> evicts in batches once exceeded.</summary>
    private const int MaxLogEntries = 5000;
    private const int LogEvictBatch = 500;

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

        // Capture subscription tokens so Dispose() can release them and we
        // don't leak event handlers (B1).
        _subscriptions.Add(_eventAggregator.Subscribe<ExecutionStartedEvent>(OnExecutionStarted));
        _subscriptions.Add(_eventAggregator.Subscribe<ExecutionCompletedEvent>(OnExecutionCompleted));
        // Phase 1.13: real-time push events from the executors.
        _subscriptions.Add(_eventAggregator.Subscribe<NodeProgressEvent>(OnNodeProgress));
        _subscriptions.Add(_eventAggregator.Subscribe<AgentOutputEvent>(OnAgentOutput));

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

    /// <summary>
    /// Private feed-only constructor used by <see cref="CreateForFeed"/>.
    /// Skips event-aggregator subscriptions and the polling timer because
    /// the data feed (e.g. <c>SignalRExecutionFeed</c>) drives mutations
    /// directly via the public collection members.
    /// </summary>
    private ExecutionDashboardVM(Dispatcher dispatcher)
    {
        _sessionManager = null!;
        _lockManager    = null!;
        _eventAggregator = null!;
        _dispatcher = dispatcher;
        // No timer, no subscriptions.
        _refreshTimer = null!;

        // CancelSession requires the in-process session manager. In feed-only
        // mode the command is a no-op (a remote dashboard cannot directly
        // cancel a session running in another process); a future iteration
        // can route this through a hub method.
        CancelSessionCommand = new RelayCommand<string>(_ => { /* no-op in remote mode */ });
        ClearSelectionCommand = new RelayCommand(() =>
        {
            SelectedSessionId = null;
            SelectedAgentName = null;
        });
        ClearLogsCommand = new RelayCommand(() => LogEntries.Clear());
    }

    /// <summary>
    /// Factory for the standalone Dashboard process (no in-process services).
    /// The VM exposes <see cref="Sessions"/>, <see cref="LogEntries"/>, and
    /// <see cref="ConnectionStatus"/> for an external feed adapter to mutate.
    /// </summary>
    public static ExecutionDashboardVM CreateForFeed(Dispatcher dispatcher)
        => new(dispatcher);

    // ?? Collections ?????????????????????????????????????????????????

    public ObservableCollection<SessionCardVM> Sessions { get; } = new();
    public RangeObservableCollection<LogEntryVM> LogEntries { get; } = new();

    /// <summary>Timeline VM created by the dashboard window; set externally after construction.</summary>
    private TimelineVM? _timelineVm;
    public TimelineVM? Timeline
    {
        get => _timelineVm;
        set => SetProperty(ref _timelineVm, value);
    }

    // ?? Selection state (drives log filtering) ??????????????????????

    [ObservableProperty] private string? _selectedSessionId;
    [ObservableProperty] private string? _selectedAgentName;

    // ?? Summary stats ???????????????????????????????????????????????

    [ObservableProperty] private int _activeSessionCount;
    [ObservableProperty] private int _lockedAgentCount;
    [ObservableProperty] private int _totalPassedActions;
    [ObservableProperty] private int _totalFailedActions;
    [ObservableProperty] private int _overallProgressPercent;

    // ?? Connection / filter state (Dashboard v2 spec) ?????????????????

    /// <summary>
    /// Status of the dashboard's data feed. For the in-process WPF dashboard
    /// this is "Connected" whenever the controller services are alive; if
    /// background event subscriptions throw it flips to "Disconnected". The
    /// UI footer binds to this and shows a colored dot.
    /// Values: "Connected" | "Connecting" | "Disconnected".
    /// </summary>
    [ObservableProperty] private string _connectionStatus = "Connected";

    /// <summary>Pipeline view status filter: "All" | "Running" | "Failed".</summary>
    [ObservableProperty] private string _pipelineStatusFilter = "All";

    /// <summary>Pipeline view free-text filter (matches session/agent/action).</summary>
    [ObservableProperty] private string _pipelineSearchText = "";

    /// <summary>Unified-log severity filter: "All" | "Error" | "Warning" | "Success" | "Info".</summary>
    [ObservableProperty] private string _logSeverityFilter = "All";

    /// <summary>Unified-log free-text filter.</summary>
    [ObservableProperty] private string _logSearchText = "";

    // ?? Commands ????????????????????????????????????????????????????

    public System.Windows.Input.ICommand CancelSessionCommand { get; }
    public System.Windows.Input.ICommand ClearSelectionCommand { get; }
    public System.Windows.Input.ICommand ClearLogsCommand { get; }

    /// <summary>
    /// Predicate used by CollectionViewSource.Filter in XAML.
    /// </summary>
    public bool FilterLogEntry(LogEntryVM entry)
    {
        if (!string.IsNullOrEmpty(SelectedSessionId) &&
            entry.SessionId != SelectedSessionId)
            return false;

        // P2-1: agent-attributed lines now carry entry.AgentName, so strict
        // equality filters them correctly. Session-scoped lines (executor
        // emissions, lifecycle messages) legitimately have no agent and must
        // remain visible under any agent selection � keep the empty-matches-all
        // rule.
        if (!string.IsNullOrEmpty(SelectedAgentName) &&
            !string.IsNullOrEmpty(entry.AgentName) &&
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

    /// <summary>
    /// Predicate used by the Pipeline view to filter session cards by status
    /// (All / Running / Failed) and free-text search across session name,
    /// owner, agent name, and action name.
    /// </summary>
    public bool FilterSessionCard(SessionCardVM card)
    {
        // Status filter
        if (PipelineStatusFilter == "Running" && card.Status != "Running") return false;
        if (PipelineStatusFilter == "Failed"  && card.Status != "Failed")  return false;

        // Free-text search
        if (!string.IsNullOrEmpty(PipelineSearchText))
        {
            var q = PipelineSearchText.Trim();
            if (q.Length == 0) return true;

            bool match = card.WatchItemTag.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (card.UserId?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || card.Agents.Any(a =>
                       a.AgentName.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || a.Actions.Any(act =>
                           act.DisplayLabel.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || act.Status.Contains(q, StringComparison.OrdinalIgnoreCase)));

            if (!match) return false;
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
                    session.ResolvedParameters.TryGetValue(WatchListConstants.BuildNumberKey, out var bn)
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

            // B6: Tolerate Completed-before-Started ordering. EventAggregator
            // dispatches handlers via ThreadPool, so the Completed handler
            // can hit the dispatcher before its matching Started. Materialize
            // the card in completed state instead of dropping the event.
            if (card == null)
            {
                var session = _sessionManager.GetSession(e.SessionId);
                card = new SessionCardVM
                {
                    SessionId = e.SessionId,
                    WatchItemTag = e.WatchItemTag,
                    UserId = session?.UserId ?? "",
                    Source = session?.Source ?? "",
                    BuildNumber = session != null &&
                        session.ResolvedParameters.TryGetValue(WatchListConstants.BuildNumberKey, out var bn)
                            ? bn : "",
                    LockedAgentsList = session != null
                        ? string.Join(", ", session.LockedAgents)
                        : "",
                    IsExpanded = false,
                };
                Sessions.Insert(0, card);
            }

            card.Status = e.State;
            if (e.Passed > 0) card.PassedActions = e.Passed;
            if (e.Failed > 0) card.FailedActions = e.Failed;
            card.ProgressPercent = 100;
            card.IsExpanded = false;

            // Final reconcile so all pills show terminal state.
            ReconcileCard(card);
            card.RecalculateCounters();
            EvictOldCompletedCards();
            RecalculateStats();
        });
    }

    private void OnNodeProgress(NodeProgressEvent e)
    {
        _dispatcher.InvokeAsync(() =>
        {
            var card = Sessions.FirstOrDefault(s => s.SessionId == e.SessionId);

            // B7: Tolerate NodeProgress arriving before ExecutionStarted
            // (race condition when both events publish via ThreadPool).
            // Create a placeholder card so action pills are not silently dropped.
            if (card == null)
            {
                var session = _sessionManager.GetSession(e.SessionId);
                card = new SessionCardVM
                {
                    SessionId = e.SessionId,
                    WatchItemTag = session?.WatchItemTag ?? e.SessionId,
                    UserId = session?.UserId ?? "",
                    Source = session?.Source ?? "",
                    Status = "Running",
                    BuildNumber = session != null &&
                        session.ResolvedParameters.TryGetValue(WatchListConstants.BuildNumberKey, out var bn)
                            ? bn : "",
                    LockedAgentsList = session != null
                        ? string.Join(", ", session.LockedAgents)
                        : "",
                    IsExpanded = true,
                };
                Sessions.Insert(0, card);
            }

            var agentName = string.IsNullOrEmpty(e.AgentName) ? "Controller" : e.AgentName;
            var row = card.GetOrCreateAgent(agentName);
            row.UpdateAction(
                tag: e.NodeTag,
                actionType: e.ActionType,
                command: e.Command,
                status: e.Status,
                exitCode: e.ExitCode ?? 0,
                errorMessage: e.ErrorMessage ?? "",
                duration: e.Duration ?? "",
                progressPercent: e.ProgressPercent ?? 0);
            card.RecalculateCounters();
        });
    }

    private void OnAgentOutput(AgentOutputEvent e)
    {
        _dispatcher.InvokeAsync(() =>
        {
            var sessionName = !string.IsNullOrEmpty(e.SessionId)
                ? Sessions.FirstOrDefault(s => s.SessionId == e.SessionId)?.WatchItemTag ?? ""
                : "";
            AddLogEntry(new LogEntryVM
            {
                Timestamp = e.Timestamp.ToString("HH:mm:ss"),
                SessionId = e.SessionId,
                SessionName = sessionName,
                AgentName = e.AgentName,
                Category = e.Kind,
                Message = e.Line,
                Severity = e.Kind == "stderr" ? "Error" : "Info",
            });
        });
    }

    private void OnRefreshTick(object? sender, EventArgs e)
    {
        // UI thread (DispatcherTimer). After the move to push events
        // (Phase 1.13) this loop only needs to refresh the elapsed-time
        // string for running cards. The pill state arrives via
        // OnNodeProgress and the log via OnAgentOutput.
        //
        // We still call ReconcileCard once per running session as a
        // defensive backfill: if the dashboard is opened mid-flight
        // (after some events have already been published) the in-memory
        // ExecutionSession snapshot lets us catch up.
        foreach (var card in Sessions.ToList())
        {
            if (card.Status != "Running") continue;

            var session = _sessionManager.GetSession(card.SessionId);
            if (session == null) continue;

            card.Elapsed = (DateTime.UtcNow - session.StartedUtc)
                .ToString(@"hh\:mm\:ss");

            // Cheap when no agents/actions; runs at most once per second.
            ReconcileCard(card, session);
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
            // B4: ConcurrentBag has no defined enumeration order. Sort by
            // Sequence so pills render in the order actions actually started.
            foreach (var action in summary.Actions.OrderBy(a => a.Sequence))
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

    // Wrapper used by OnExecutionCompleted, which already has the card but
    // not the session reference.
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

        // B9: Batch the per-tick harvest. AddLogEntry would fire one
        // CollectionChanged per item, which during a log burst defeats the
        // batching that RangeObservableCollection was added for. Build the
        // VMs locally, then publish + trim in a single Reset notification.
        var batch = new List<LogEntryVM>(all.Count - seen);
        for (var i = seen; i < all.Count; i++)
        {
            var entry = all[i];
            batch.Add(new LogEntryVM
            {
                Timestamp = entry.Timestamp.ToString("HH:mm:ss"),
                SessionId = card.SessionId,
                SessionName = card.WatchItemTag,
                // P2-1: real agent attribution (empty = session-scope line).
                AgentName = entry.AgentName ?? "",
                Category = entry.Category,
                Message = entry.Message,
                Severity = InferSeverity(entry.Category, entry.Message),
            });
        }
        _logCursors[card.SessionId] = all.Count;

        LogEntries.AddRange(batch);
        if (LogEntries.Count > MaxLogEntries + LogEvictBatch)
            LogEntries.TrimFromStart(LogEntries.Count - MaxLogEntries);
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
        // B7: Batch eviction. Removing one item at a time from an
        // ObservableCollection at index 0 is O(N) per remove and fires
        // a CollectionChanged event for every shift; doing it 5000 times
        // during a burst dwarfs the actual append work.
        if (LogEntries.Count > MaxLogEntries + LogEvictBatch)
            LogEntries.TrimFromStart(LogEvictBatch);
    }

    /// <summary>
    /// B2: cap the number of session cards. Keep all running sessions plus the
    /// most recent completed cards up to <see cref="MaxSessionCards"/>; evict
    /// the oldest completed cards and clean up their log cursors so the
    /// dictionary doesn't accumulate dead entries.
    /// </summary>
    private void EvictOldCompletedCards()
    {
        if (Sessions.Count <= MaxSessionCards) return;

        // Walk from the end of the list (oldest) and remove completed cards
        // until we're under the cap. Running cards are never evicted.
        for (var i = Sessions.Count - 1; i >= 0 && Sessions.Count > MaxSessionCards; i--)
        {
            var card = Sessions[i];
            if (card.Status == "Running") continue;
            _logCursors.Remove(card.SessionId);
            Sessions.RemoveAt(i);
        }
    }

    // ?? Helpers ?????????????????????????????????????????????????????

    internal void RecalculateStats()
    {
        ActiveSessionCount = Sessions.Count(s => s.Status == "Running");
        LockedAgentCount = _lockManager.GetAllLocks().Count;

        // P2-2: Passed/Failed totals span all retained cards (Running +
        // Completed within the MaxSessionCards window) so the KPI strip
        // doesn't snap to zero the moment the last session finishes.
        // OverallProgressPercent stays Running-only � averaging completed
        // cards (always 100%) would mask in-flight progress.
        TotalPassedActions = Sessions.Sum(s => s.PassedActions);
        TotalFailedActions = Sessions.Sum(s => s.FailedActions);

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

    // ── Window launch command ────────────────────────────────────────────

    [RelayCommand]
    private void OpenDashboardWindow()
    {
        var win = new ExecutionDashboardWindow { DataContext = this };
        win.Show();
    }

    // ── Demo Data (for testing the dashboard UI without a real pipeline run) ──

    /// <summary>
    /// Populates the dashboard with realistic fake sessions so you can verify
    /// the Pipeline View, Timeline, and Log tabs render correctly without
    /// actually triggering a pipeline. Call from code-behind or a debug menu.
    /// </summary>
    [RelayCommand]
    private void LoadDemoData()
    {
        Sessions.Clear();
        LogEntries.Clear();

        var now = DateTime.UtcNow;

        // Session 1: Running pipeline (3 agents, mixed status)
        var s1 = new SessionCardVM
        {
            SessionId = "demo01",
            WatchItemTag = "Deploy.WebApi",
            UserId = "developer1",
            Source = "WPF",
            Status = "Running",
            BuildNumber = "2026.05.04.1",
            Elapsed = "02:15",
            IsExpanded = true,
            LockedAgentsList = "Agent-01, Agent-02, Agent-03",
        };

        var a1r1 = new AgentRowVM { AgentName = "Agent-01", Status = "Success" };
        a1r1.Actions.Add(new ActionPillVM { Tag = "Install Build", ActionType = "RunRemoteCommand", Command = @"\\server\install.cmd", Status = "Success", Duration = "45s", StartedUtc = now.AddSeconds(-135), DurationSeconds = 45 });
        a1r1.Actions.Add(new ActionPillVM { Tag = "Run Smoke Tests", ActionType = "RunRemoteCommand", Command = @"\\server\smoke.cmd", Status = "Success", Duration = "30s", StartedUtc = now.AddSeconds(-90), DurationSeconds = 30 });
        a1r1.Actions.Add(new ActionPillVM { Tag = "Reboot", ActionType = "RunRemoteCommand", Command = "shutdown /r /t 0", Status = "Success", Duration = "60s", StartedUtc = now.AddSeconds(-60), DurationSeconds = 60 });
        a1r1.CompletedCount = 3; a1r1.TotalCount = 3; a1r1.ProgressPercent = 100;

        var a1r2 = new AgentRowVM { AgentName = "Agent-02", Status = "Executing" };
        a1r2.Actions.Add(new ActionPillVM { Tag = "Install Build", ActionType = "RunRemoteCommand", Command = @"\\server\install.cmd", Status = "Success", Duration = "48s", StartedUtc = now.AddSeconds(-130), DurationSeconds = 48 });
        a1r2.Actions.Add(new ActionPillVM { Tag = "Run Integration", ActionType = "RunRemoteCommand", Command = @"\\server\integration.cmd", Status = "Running", ProgressPercent = 65, StartedUtc = now.AddSeconds(-80), DurationSeconds = 0 });
        a1r2.Actions.Add(new ActionPillVM { Tag = "Email: Results", ActionType = "SendMail", Command = "qa-team@company.com", Status = "Pending" });
        a1r2.CompletedCount = 1; a1r2.TotalCount = 3; a1r2.ProgressPercent = 33;

        var a1r3 = new AgentRowVM { AgentName = "Agent-03", Status = "Executing" };
        a1r3.Actions.Add(new ActionPillVM { Tag = "Install Build", ActionType = "RunRemoteCommand", Command = @"\\server\install.cmd", Status = "Success", Duration = "52s", StartedUtc = now.AddSeconds(-125), DurationSeconds = 52 });
        a1r3.Actions.Add(new ActionPillVM { Tag = "Run Perf Suite", ActionType = "RunRemoteCommand", Command = @"\\server\perf.cmd", Status = "Running", ProgressPercent = 30, StartedUtc = now.AddSeconds(-70), DurationSeconds = 0 });
        a1r3.CompletedCount = 1; a1r3.TotalCount = 2; a1r3.ProgressPercent = 50;

        s1.Agents.Add(a1r1);
        s1.Agents.Add(a1r2);
        s1.Agents.Add(a1r3);
        s1.RecalculateCounters();

        // Session 2: Completed with failures (2 agents)
        var s2 = new SessionCardVM
        {
            SessionId = "demo02",
            WatchItemTag = "Nightly.FullSuite",
            UserId = "scheduler",
            Source = "WebClient",
            Status = "Failed",
            BuildNumber = "2026.05.03.7",
            Elapsed = "15:42",
            IsExpanded = true,
            LockedAgentsList = "Agent-04, Agent-05",
        };

        var a2r1 = new AgentRowVM { AgentName = "Agent-04", Status = "Success" };
        a2r1.Actions.Add(new ActionPillVM { Tag = "Install Build", ActionType = "RunRemoteCommand", Command = @"\\nightly\install.cmd", Status = "Success", Duration = "1m 10s", StartedUtc = now.AddMinutes(-16), DurationSeconds = 70 });
        a2r1.Actions.Add(new ActionPillVM { Tag = "Run Unit Tests", ActionType = "RunRemoteCommand", Command = @"dotnet test", Status = "Success", Duration = "8m 30s", StartedUtc = now.AddMinutes(-14), DurationSeconds = 510 });
        a2r1.Actions.Add(new ActionPillVM { Tag = "Collect Results", ActionType = "RunCommand", Command = @"copy *.trx \\results", Status = "Success", Duration = "5s", StartedUtc = now.AddMinutes(-6), DurationSeconds = 5 });
        a2r1.CompletedCount = 3; a2r1.TotalCount = 3; a2r1.ProgressPercent = 100;

        var a2r2 = new AgentRowVM { AgentName = "Agent-05", Status = "Failed" };
        a2r2.Actions.Add(new ActionPillVM { Tag = "Install Build", ActionType = "RunRemoteCommand", Command = @"\\nightly\install.cmd", Status = "Success", Duration = "1m 15s", StartedUtc = now.AddMinutes(-16), DurationSeconds = 75 });
        a2r2.Actions.Add(new ActionPillVM { Tag = "Run E2E Tests", ActionType = "RunRemoteCommand", Command = @"\\nightly\e2e.cmd", Status = "Failed", ExitCode = 1, ErrorMessage = "3 test cases failed: LoginTest, PaymentTest, CheckoutTest", Duration = "12m 5s", StartedUtc = now.AddMinutes(-14), DurationSeconds = 725 });
        a2r2.Actions.Add(new ActionPillVM { Tag = "Cleanup", ActionType = "RunRemoteCommand", Command = @"\\nightly\cleanup.cmd", Status = "Skipped" });
        a2r2.CompletedCount = 2; a2r2.TotalCount = 3; a2r2.ProgressPercent = 67;

        s2.Agents.Add(a2r1);
        s2.Agents.Add(a2r2);
        s2.RecalculateCounters();

        // Session 3: Completed successfully (1 agent)
        var s3 = new SessionCardVM
        {
            SessionId = "demo03",
            WatchItemTag = "Build.QuickVerify",
            UserId = "ci-bot",
            Source = "WebClient",
            Status = "Success",
            BuildNumber = "2026.05.04.3",
            Elapsed = "03:20",
            IsExpanded = false,
            LockedAgentsList = "Agent-01",
        };

        var a3r1 = new AgentRowVM { AgentName = "Agent-01", Status = "Success" };
        a3r1.Actions.Add(new ActionPillVM { Tag = "Install Build", ActionType = "RunRemoteCommand", Command = @"\\server\install.cmd", Status = "Success", Duration = "40s", StartedUtc = now.AddMinutes(-4), DurationSeconds = 40 });
        a3r1.Actions.Add(new ActionPillVM { Tag = "Quick BVT", ActionType = "RunRemoteCommand", Command = @"\\server\bvt.cmd", Status = "Success", Duration = "2m 30s", StartedUtc = now.AddMinutes(-3), DurationSeconds = 150 });
        a3r1.CompletedCount = 2; a3r1.TotalCount = 2; a3r1.ProgressPercent = 100;

        s3.Agents.Add(a3r1);
        s3.RecalculateCounters();

        Sessions.Add(s1);
        Sessions.Add(s2);
        Sessions.Add(s3);

        // Demo log entries
        var logs = new[]
        {
            new LogEntryVM { Timestamp = now.AddSeconds(-135).ToString("HH:mm:ss"), SessionId = "demo01", SessionName = "Deploy.WebApi", AgentName = "Agent-01", Category = "Action", Message = "Starting: Install Build", Severity = "Info" },
            new LogEntryVM { Timestamp = now.AddSeconds(-90).ToString("HH:mm:ss"), SessionId = "demo01", SessionName = "Deploy.WebApi", AgentName = "Agent-01", Category = "Action", Message = "Install completed (exit code 0)", Severity = "Success" },
            new LogEntryVM { Timestamp = now.AddSeconds(-80).ToString("HH:mm:ss"), SessionId = "demo01", SessionName = "Deploy.WebApi", AgentName = "Agent-02", Category = "Action", Message = "Starting: Run Integration Tests", Severity = "Info" },
            new LogEntryVM { Timestamp = now.AddSeconds(-70).ToString("HH:mm:ss"), SessionId = "demo01", SessionName = "Deploy.WebApi", AgentName = "Agent-03", Category = "Action", Message = "Starting: Run Perf Suite", Severity = "Info" },
            new LogEntryVM { Timestamp = now.AddSeconds(-60).ToString("HH:mm:ss"), SessionId = "demo01", SessionName = "Deploy.WebApi", AgentName = "Agent-02", Category = "stdout", Message = "Running test 42/65... TestPaymentFlow", Severity = "Info" },
            new LogEntryVM { Timestamp = now.AddMinutes(-14).ToString("HH:mm:ss"), SessionId = "demo02", SessionName = "Nightly.FullSuite", AgentName = "Agent-05", Category = "Action", Message = "Starting: Run E2E Tests", Severity = "Info" },
            new LogEntryVM { Timestamp = now.AddMinutes(-2).ToString("HH:mm:ss"), SessionId = "demo02", SessionName = "Nightly.FullSuite", AgentName = "Agent-05", Category = "stderr", Message = "FAIL: LoginTest - Element '#submit-btn' not found after 30s timeout", Severity = "Error" },
            new LogEntryVM { Timestamp = now.AddMinutes(-1).ToString("HH:mm:ss"), SessionId = "demo02", SessionName = "Nightly.FullSuite", AgentName = "Agent-05", Category = "Action", Message = "E2E Tests failed (exit code 1): 3 failures", Severity = "Error" },
        };
        LogEntries.AddRange(logs);

        RecalculateStats();
    }

    /// <summary>Stops the refresh timer and releases all event subscriptions.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _refreshTimer?.Stop();

        foreach (var sub in _subscriptions) sub.Dispose();
        _subscriptions.Clear();
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
