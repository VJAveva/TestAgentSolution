using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Identity;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels.Execution;
using TestControllerGrpc.ViewModels.AgentWorkspace;
using System.Windows;

namespace TestControllerGrpc.ViewModels;

/// <summary>
/// Main view model for the TestController application.
/// Split into partial files for readability:
///   MainViewModel.cs              — Fields, constructor, dispose
///   MainViewModel.File.cs         — Open, Save, SaveAs, Close commands
///   MainViewModel.WatchListCrud.cs— Add/Delete/Move WatchItem/Event/Action/Group/Ref/Init
///   MainViewModel.TemplateCrud.cs — Template CRUD + edit XML
///   MainViewModel.Agents.cs       — Register, Test, Diagnose, Health Check, Self-registration
///   MainViewModel.Execution.cs    — Trigger, Execute, Cancel, Reset, Retry
///   MainViewModel.XmlEditor.cs    — Inline XML editor, WatchList editor
///   MainViewModel.ImportExport.cs — Import/Export WatchItems + Templates
///   MainViewModel.Log.cs          — Log entries, filtering, copy, clear
///   MainViewModel.InitParameters.cs — Initialize parameter file editor
///   MainViewModel.Helpers.cs      — BuildTree, RebuildTreeFromConfig, utility methods, Help
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IVocabularyMonitor _vocabMonitor;
    private readonly IFileWatcherManager _watcherManager;
    private readonly IActionPipelineExecutor _executor;
    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly ExecutionSessionManager _sessionManager;
    private readonly ILogger<MainViewModel> _logger;
    private readonly IAppLogger _appLogger;
    private readonly IEventAggregator _events;
    private readonly AgentLockManager _lockManager;
    private readonly TestControllerGrpc.Core.Maintenance.IMaintenanceStateStore? _maintenanceState;
    private readonly TestControllerGrpc.Core.Maintenance.IFleetMaintenanceService? _fleetMaintenance;
    private readonly HealthThresholdSettings _healthThresholds;
    private readonly TestController.Api.Services.PipelineAuthorizationGuard _pipelineGuard;
    private readonly Services.AuthClient _authClient;
    private readonly Services.CapabilityChecker _capabilityChecker;
    private readonly Services.CurrentUserHolder _currentUserHolder;
    private readonly Services.LockStateService _lockStateService;
    private readonly TestControllerGrpc.Locking.ILockRegistry _lockRegistry;
    private readonly System.Windows.Threading.DispatcherTimer _sessionElapsedTimer;
    private readonly System.Windows.Threading.DispatcherTimer _assignmentRefreshTimer;
    private readonly List<IDisposable> _subscriptions = [];

    [ObservableProperty] private TreeNodeViewModel? _selectedNode;
    [ObservableProperty] private TreeNodeViewModel? _selectedTemplateNode;
    [ObservableProperty] private TreeNodeViewModel? _activeEditNode;
    [ObservableProperty] private string _statusMessage = "Ready";
    [ObservableProperty] private string _vocabFilePath = "";
    [ObservableProperty] private int _activeWatchers;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _newAgentName = "";
    [ObservableProperty] private string _newAgentAddress = "http://localhost:5200";

    // ── Watermark visibility ────────────────────────────────────────
    /// <summary>True when no meaningful node is selected (show watermark).</summary>
    [ObservableProperty] private bool _showWatermark = true;

    // ── Simplified layout toggle ────────────────────────────────────
    /// <summary>When true, ribbon collapses to a single compact toolbar row.</summary>
    [ObservableProperty] private bool _isSimplifiedLayout;

    // ── Users tab visibility (Phase 1b) ─────────────────────────────
    /// <summary>True when the current user has User_Create capability (Admin in Secured mode).</summary>
    [ObservableProperty] private bool _isUsersTabVisible;

    // ── Inline XML Editor state ─────────────────────────────────────
    [ObservableProperty] private bool _isXmlEditorOpen;
    [ObservableProperty] private string _xmlEditorText = "";
    [ObservableProperty] private string _xmlEditorStatus = "";

    // ── Inline AvalonEdit panel state (Feature 1) ───────────────────
    [ObservableProperty] private bool _isInlineXmlEditorVisible;
    [ObservableProperty] private string _inlineXmlEditorText = "";
    [ObservableProperty] private string _inlineXmlEditorStatus = "";
    /// <summary>"WatchList" or "WatchItem" — determines what's being edited inline.</summary>
    [ObservableProperty] private string _inlineXmlEditorScope = "";

    // ── Execution state ─────────────────────────────────────────────
    [ObservableProperty] private bool _isExecuting;
    private CancellationTokenSource? _executionCts = new();

    /// <summary>All currently running pipeline sessions.</summary>
    public ObservableCollection<PipelineSession> ActiveSessions { get; } = new();

    /// <summary>Count of active sessions for display.</summary>
    [ObservableProperty] private int _activeSessionCount;
    [ObservableProperty] private string _totalSessionsElapsed = "";

    // ── Agent Lock Display ──────────────────────────────────────────
    /// <summary>Current agent lock state for admin dashboard binding.</summary>
    public ObservableCollection<AgentLockDisplayItem> AgentLocks { get; } = new();

    /// <summary>Multi-session execution dashboard ViewModel.</summary>
    public ExecutionDashboardVM ExecutionDashboard { get; private set; } = null!;

    /// <summary>Controller health metrics strip ViewModel.</summary>
    public HealthMetricsVM HealthMetrics { get; private set; } = null!;

    partial void OnIsExecutingChanged(bool value)
    {
        NotifyExecutionCanExecuteChanged();
    }

    /// <summary>Notifies all execution-related commands to re-evaluate their CanExecute state.</summary>
    private void NotifyExecutionCanExecuteChanged()
    {
        TriggerEventCommand.NotifyCanExecuteChanged();
        TriggerWatchItemCommand.NotifyCanExecuteChanged();
        TriggerAllWatchItemsCommand.NotifyCanExecuteChanged();
        ExecuteGroupCommand.NotifyCanExecuteChanged();
        ExecuteSingleActionCommand.NotifyCanExecuteChanged();
        ExecuteTemplateCommand.NotifyCanExecuteChanged();
        CancelExecutionCommand.NotifyCanExecuteChanged();
        RetryFailedCommand.NotifyCanExecuteChanged();
    }

    // ── Active editing context (Feature 3) ──────────────────────────
    [ObservableProperty] private string _activeEditingContext = "WatchList";

    // ── Context-sensitive execute button visibility (Feature 4) ─────
    [ObservableProperty] private Visibility _showExecuteAll = Visibility.Visible;
    [ObservableProperty] private Visibility _showTriggerAllEvents = Visibility.Collapsed;
    [ObservableProperty] private Visibility _showTriggerEvent = Visibility.Collapsed;
    [ObservableProperty] private Visibility _showExecuteGroup = Visibility.Collapsed;
    [ObservableProperty] private Visibility _showExecuteAction = Visibility.Collapsed;

    // ── Import/Export/EditXML ribbon button visibility ────────────
    [ObservableProperty] private Visibility _showImportButton = Visibility.Visible;
    [ObservableProperty] private Visibility _showExportButton = Visibility.Visible;
    [ObservableProperty] private Visibility _showEditXmlButton = Visibility.Visible;

    // ── Initialize parameter file editor state ──────────────────────
    public ObservableCollection<ParameterEntryViewModel> ParameterFileEntries { get; } = new();
    [ObservableProperty] private string _parameterFileStatus = "";

    // ── Theme switching ─────────────────────────────────────────────
    public string[] AvailableThemes => ThemeService.AvailableThemes;

    private string _selectedTheme = "Dark";
    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (SetProperty(ref _selectedTheme, value))
                ThemeService.ApplyTheme(value);
        }
    }

    // ── Agent status summary ────────────────────────────────────────
    [ObservableProperty] private string _agentStatusSummary = "No agents";

    // ── Execution log filtering ─────────────────────────────────────
    private string _logFilterTag = "";
    public string LogFilterTag
    {
        get => _logFilterTag;
        set
        {
            if (SetProperty(ref _logFilterTag, value))
            {
                FilterAgentNamesByPipeline(value);
                ApplyLogFilter();
            }
        }
    }

    private string _logFilterAgent = "";
    public string LogFilterAgent
    {
        get => _logFilterAgent;
        set { if (SetProperty(ref _logFilterAgent, value)) ApplyLogFilter(); }
    }

    private string _logLevelFilter = "All";
    public string LogLevelFilter
    {
        get => _logLevelFilter;
        set { if (SetProperty(ref _logLevelFilter, value)) ApplyLogFilter(); }
    }

    private string _logSearchText = "";
    public string LogSearchText
    {
        get => _logSearchText;
        set { if (SetProperty(ref _logSearchText, value)) ApplyLogFilter(); }
    }

    [ObservableProperty] private bool _isLogPaused;
    [ObservableProperty] private bool _isAutoScrollEnabled = true;
    [ObservableProperty] private bool _isLogCollapsed;

    // ── Enhanced log filter properties ───────────────────────────────
    /// <summary>Filter by session ID. Empty = show all sessions.</summary>
    private string _logFilterSession = "";
    public string LogFilterSession
    {
        get => _logFilterSession;
        set
        {
            if (SetProperty(ref _logFilterSession, value))
            {
                // Smart session filter: selecting a specific session shows every
                // action for that run (executed and in-progress) by clearing the
                // narrowing filters so nothing is hidden.
                if (!string.IsNullOrWhiteSpace(value) &&
                    !string.Equals(value, "All", StringComparison.OrdinalIgnoreCase))
                {
                    _logFilterAgent = "";
                    OnPropertyChanged(nameof(LogFilterAgent));
                    _logFilterTag = "";
                    OnPropertyChanged(nameof(LogFilterTag));
                    FilterAgentNamesByPipeline("");
                    _logLevelFilter = "All";
                    OnPropertyChanged(nameof(LogLevelFilter));
                    ShowErrorsOnly = false;
                }
                ApplyLogFilter();
            }
        }
    }

    /// <summary>When true, search uses regex. When false, plain text.</summary>
    [ObservableProperty] private bool _isRegexSearch;
    partial void OnIsRegexSearchChanged(bool value) => RebuildSearchRegex();

    /// <summary>Quick toggle: show only errors.</summary>
    [ObservableProperty] private bool _showErrorsOnly;
    partial void OnShowErrorsOnlyChanged(bool value) => ApplyLogFilter();

    /// <summary>Error count for display badge.</summary>
    [ObservableProperty] private int _logErrorCount;

    /// <summary>Warning count for display badge.</summary>
    [ObservableProperty] private int _logWarningCount;

    /// <summary>Search match count for display.</summary>
    [ObservableProperty] private int _searchMatchCount;

    /// <summary>Available session IDs for the filter dropdown. "All" = no filter.</summary>
    public ObservableCollection<string> AvailableSessionIds { get; } = new() { "All" };

    /// <summary>Pre-compiled regex for search (null if plain text mode).</summary>
    private System.Text.RegularExpressions.Regex? _searchRegex;

    // ── Dockable pane state ────────────────────────────────────────
    /// <summary>Tree pane is pinned (docked) vs auto-hidden (collapsed to vertical tab).</summary>
    [ObservableProperty] private bool _isTreePanePinned = true;

    /// <summary>Log pane is pinned (docked) vs auto-hidden (collapsed to tab).</summary>
    [ObservableProperty] private bool _isLogPanePinned = true;

    /// <summary>Agent pane is pinned (docked) vs auto-hidden.</summary>
    [ObservableProperty] private bool _isAgentPanePinned = true;

    // ── Tree search/filter ──────────────────────────────────────────
    private string _treeSearchText = "";
    public string TreeSearchText
    {
        get => _treeSearchText;
        set
        {
            if (SetProperty(ref _treeSearchText, value))
                ApplyTreeSearch();
        }
    }

    // ── Template search/filter ──────────────────────────────────────
    private string _templateSearchText = "";
    public string TemplateSearchText
    {
        get => _templateSearchText;
        set
        {
            if (SetProperty(ref _templateSearchText, value))
                ApplyTemplateSearch();
        }
    }

    public string[] LogLevelOptions { get; } = ["All", "Info", "Success", "Warning", "Error"];

    /// <summary>TreeRoots[0] is the single "WatchList" root node — always present.</summary>
    public ObservableCollection<TreeNodeViewModel> TreeRoots { get; } = new();
    public ObservableCollection<TreeNodeViewModel> TemplateRoots { get; } = new();

    // ── Pipeline filter (Phase 2b) ──────────────────────────────────
    /// <summary>
    /// "Showing N of M pipelines" for Engineers in Secured mode; empty otherwise.
    /// </summary>
    [ObservableProperty] private string _pipelineFilterLabel = "";

    // ── High-performance log collections (GAP 1 + 4 fix) ────────────
    // RangeObservableCollection supports batch Add/Remove with single
    // Reset notification, reducing WPF layout passes from O(n) to O(1).
    // LogBufferService decouples log producers from the UI thread via
    // a Channel<T>, draining in batches every 100ms.
    public RangeObservableCollection<LogEntryViewModel> LogEntries { get; } = new();
    public RangeObservableCollection<LogEntryViewModel> FilteredLogEntries { get; } = new();
    private LogBufferService? _logBuffer;

    /// <summary>
    /// Exposes the log buffer to the View for auto-scroll subscription.
    /// The View subscribes to <see cref="LogBufferService.BatchFlushed"/>
    /// instead of per-item CollectionChanged for efficient scrolling.
    /// </summary>
    public LogBufferService? LogBuffer => _logBuffer;

    public ObservableCollection<AgentInfoViewModel> RegisteredAgents { get; } = new();
    public ObservableCollection<string> AvailableTemplateIds { get; } = new();
    public ObservableCollection<string> AvailableWatchItemTags { get; } = new();
    public ObservableCollection<string> AvailableAgentNames { get; } = new();

    private WatchListConfig _config = new();

    private TreeNodeViewModel? WatchListRoot => TreeRoots.Count > 0 ? TreeRoots[0] : null;
    private TreeNodeViewModel? TemplateListRoot => TemplateRoots.Count > 0 ? TemplateRoots[0] : null;

    // ── Build Results ────────────────────────────────────────────────
    public BuildResultsViewModel BuildResultsVM { get; }

    // ── Agent Workspace ─────────────────────────────────────────────
    public AgentWorkspaceVM AgentWorkspace { get; }

    // ── Constructor ─────────────────────────────────────────────────

    public MainViewModel(IVocabularyMonitor vocabMonitor, IFileWatcherManager watcherManager,
        IActionPipelineExecutor executor, IAgentGrpcDispatcher dispatcher,
        ExecutionSessionManager sessionManager, ILogger<MainViewModel> logger,
        BuildResultsViewModel buildResultsVM, IAppLogger appLogger,
        IEventAggregator events, AgentLockManager lockManager,
        HealthThresholdSettings healthThresholds,
        TestController.Api.Services.PipelineAuthorizationGuard pipelineGuard,
        Services.AuthClient authClient,
        Services.CapabilityChecker capabilityChecker,
        Services.CurrentUserHolder currentUserHolder,
        Services.LockStateService lockStateService,
        TestControllerGrpc.Locking.ILockRegistry lockRegistry,
        TestControllerGrpc.Core.Maintenance.IMaintenanceStateStore? maintenanceState = null,
        TestControllerGrpc.Core.Maintenance.IFleetMaintenanceService? fleetMaintenance = null,
        TestControllerGrpc.Core.Maintenance.IMaintenanceOperationStore? maintenanceStore = null,
        TestControllerGrpc.Core.Maintenance.INodeUpdateStatusStore? updateStatus = null,
        TestControllerGrpc.Core.Maintenance.IFleetNotificationService? fleetNotifications = null,
        TestControllerGrpc.Core.Maintenance.UpdatePolicyStore? updatePolicy = null)
    {
        _vocabMonitor = vocabMonitor;
        _watcherManager = watcherManager;
        _executor = executor;
        _dispatcher = dispatcher;
        _sessionManager = sessionManager;
        _logger = logger;
        _appLogger = appLogger;
        _events = events;
        _lockManager = lockManager;
        _healthThresholds = healthThresholds;
        _pipelineGuard = pipelineGuard;
        _authClient = authClient;
        _capabilityChecker = capabilityChecker;
        _currentUserHolder = currentUserHolder;
        _lockStateService = lockStateService;
        _lockRegistry = lockRegistry;
        _maintenanceState = maintenanceState;
        _fleetMaintenance = fleetMaintenance;
        BuildResultsVM = buildResultsVM;
        AgentWorkspace = new AgentWorkspaceVM(_dispatcher, _lockManager, _sessionManager, _events,
            Application.Current.Dispatcher, fleetMaintenance, _maintenanceState, maintenanceStore,
            updateStatus, fleetNotifications, updatePolicy);

        // Mirror fleet-maintenance (revert / reboot) activity into the main execution log.
        if (_fleetMaintenance is not null)
        {
            _fleetMaintenance.ProgressChanged += OnMaintenanceProgressLog;
            _fleetMaintenance.OperationCompleted += OnMaintenanceCompletedLog;
        }

        // Phase 2b: subscribe to capability changes for CanExecute + filtering
        _capabilityChecker.CapabilitiesChanged += OnCapabilitiesChanged;
        _authClient.AuthStateChanged += OnAuthStateChanged;
        _currentUserHolder.UserChanged += OnCurrentUserChanged;

        // Phase 3b: subscribe to lock state changes for CanExecute + badge refresh
        _lockStateService.LocksChanged += OnLocksChanged;

        _vocabMonitor.ConfigReloaded += OnConfigReloaded;
        _executor.LogEntry += OnLogEntry;
        _executor.NodeProgress += OnNodeProgress;
        _executor.NodeFailed += OnNodeFailed;
        _dispatcher.OutputReceived += OnOutputReceived;
        _dispatcher.StatusChanged += OnStatusChanged;
        _watcherManager.TriggerFired += OnTriggerFired;
        _watcherManager.TriggerMetadataParsed += OnTriggerMetadataParsed;
        _watcherManager.TriggerParametersLoaded += OnTriggerParametersLoaded;

        // Subscribe to gRPC server events via event aggregator (auto-cleanup on Dispose)
        _subscriptions.Add(events.Subscribe<AgentRegisteredEvent>(
            e => OnAgentSelfRegistered(e.AgentName, e.Address)));
        _subscriptions.Add(events.Subscribe<AgentUnregisteredEvent>(
            e => OnAgentSelfUnregistered(e.AgentName)));
        _subscriptions.Add(events.Subscribe<AgentHeartbeatEvent>(
            e => OnAgentHeartbeat(e.AgentName, e.State, e.Metrics)));

        // Subscribe to lock changes for admin display refresh
        _subscriptions.Add(events.Subscribe<AgentLocksChangedEvent>(_ =>
            Application.Current?.Dispatcher.InvokeAsync(RefreshLockDisplay)));
        _subscriptions.Add(events.Subscribe<ExecutionCompletedEvent>(_ =>
            Application.Current?.Dispatcher.InvokeAsync(RefreshLockDisplay)));

        // Issue #3: Immediately add new sessions to the filter dropdown
        // so users can filter by session as soon as execution starts.
        _subscriptions.Add(events.Subscribe<ExecutionStartedEvent>(e =>
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (!string.IsNullOrEmpty(e.SessionId) && !AvailableSessionIds.Contains(e.SessionId))
                    AvailableSessionIds.Add(e.SessionId);
            })));

        // Always start with a single empty WatchList root
        InitializeEmptyWatchList();

        // Initialize high-performance log buffer (GAP 1 + 4 fix)
        // Decouples log producers from the UI thread via Channel<T>.
        _logBuffer = new LogBufferService(LogEntries, FilteredLogEntries);
        _logBuffer.BatchProcessed += OnLogBatchProcessed;

        // Track active sessions for concurrent execution
        ActiveSessions.CollectionChanged += (_, _) =>
        {
            ActiveSessionCount = ActiveSessions.Count;
            IsExecuting = ActiveSessions.Count > 0;
        };

        // ── Multi-session execution dashboard ──────────────────────
        ExecutionDashboard = new ExecutionDashboardVM(
            sessionManager, lockManager, events,
            Application.Current?.Dispatcher
                ?? System.Windows.Threading.Dispatcher.CurrentDispatcher);

        // ── Controller health metrics strip ─────────────────────────
        HealthMetrics = new HealthMetricsVM(dispatcher, sessionManager, _healthThresholds);

        // ── Session elapsed timer (updates Elapsed on active sessions) ──
        _sessionElapsedTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _sessionElapsedTimer.Tick += (_, _) =>
        {
            var totalElapsed = TimeSpan.Zero;
            foreach (var s in ActiveSessions.Where(s => s.Status == "Running"))
            {
                var elapsed = DateTime.UtcNow - s.StartedUtc;
                s.Elapsed = elapsed.TotalHours >= 1
                    ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
                    : $"{elapsed.Minutes}:{elapsed.Seconds:D2}";
                totalElapsed += elapsed;
            }
            TotalSessionsElapsed = totalElapsed.TotalHours >= 1
                ? $"{(int)totalElapsed.TotalHours}:{totalElapsed.Minutes:D2}:{totalElapsed.Seconds:D2}"
                : $"{totalElapsed.Minutes}:{totalElapsed.Seconds:D2}";
        };
        _sessionElapsedTimer.Start();

        // Cancel requests raised from the dashboard cancel the matching
        // PipelineSession's CTS via the existing ActiveSessions tracking.
        _subscriptions.Add(events.Subscribe<CancelSessionRequestEvent>(req =>
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                var session = ActiveSessions.FirstOrDefault(s =>
                    s.SessionId == req.SessionId);
                session?.Cancel();
                if (session is not null)
                    AddLog($"[{session.SessionId}] Cancellation requested from dashboard for {session.WatchItemTag}");
            })));

        // Phase 2b: Assignment refresh timer (60s) — re-pulls /api/auth/me
        _assignmentRefreshTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(60)
        };
        _assignmentRefreshTimer.Tick += async (_, _) =>
        {
            if (_currentUserHolder.IsSecuredMode && _authClient.IsAuthenticated)
                await RefreshUserAssignmentsAsync();
        };
        _assignmentRefreshTimer.Start();

        // Initialize Default-mode banner state
        RefreshDefaultModeState();

        // Seed identity badge from whatever CurrentUserHolder has at construction time
        // (LoginPage already called SetUser before MainWindow opens).
        RefreshUserBadge();
    }

    /// <summary>Creates the single WatchList + TemplateList root nodes on startup.</summary>
    private void InitializeEmptyWatchList()
    {
        _config = new WatchListConfig();
        TreeRoots.Clear();
        TreeRoots.Add(TreeNodeViewModel.FromWatchList(_config));
        TemplateRoots.Clear();
        TemplateRoots.Add(TreeNodeViewModel.FromTemplateList(_config.Templates));
    }

    // ── Phase 2b: Pipeline permission state refresh ───────────────

    /// <summary>
    /// Refreshes PipelinePermission on each WatchItem node based on the current
    /// user's role and assignments. All pipelines remain visible (reads are open);
    /// only the trigger action is permission-gated.
    /// </summary>
    private void RefreshPipelinePermissions()
    {
        if (WatchListRoot is null) return;

        var children = WatchListRoot.Children;

        if (!_currentUserHolder.IsSecuredMode)
        {
            // Default mode: everyone can trigger everything, no indicator needed
            foreach (var child in children)
            {
                child.PipelinePermission = PipelinePermissionState.Triggerable;
                child.ShowPermissionIndicator = false;
            }
            PipelineFilterLabel = "";
            return;
        }

        var role = _currentUserHolder.User.Roles.FirstOrDefault();

        // Admin / SeniorManager → all triggerable
        if (string.Equals(role, Identity.Role.Administrator.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, Identity.Role.SeniorManager.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            foreach (var child in children)
            {
                child.PipelinePermission = PipelinePermissionState.Triggerable;
                child.ShowPermissionIndicator = false;
            }
            PipelineFilterLabel = "";
            return;
        }

        // Guest → all view-only
        if (string.Equals(role, Identity.Role.Guest.ToString(), StringComparison.OrdinalIgnoreCase)
            || role is null)
        {
            foreach (var child in children)
            {
                child.PipelinePermission = PipelinePermissionState.ViewOnly;
                child.ShowPermissionIndicator = true;
            }
            PipelineFilterLabel = "";
            return;
        }

        // Engineer in Secured mode: assigned = Triggerable, rest = ViewOnly
        var assigned = _currentUserHolder.User.AssignedPipelineIds;
        foreach (var child in children)
        {
            var tag = (child.ModelObject as WatchItemConfig)?.Tag ?? "";
            child.PipelinePermission = assigned.Contains(tag)
                ? PipelinePermissionState.Triggerable
                : PipelinePermissionState.ViewOnly;
            child.ShowPermissionIndicator = true;
        }
        PipelineFilterLabel = "";
    }

    /// <summary>Handles CapabilitiesChanged from background thread.</summary>
    private void OnCapabilitiesChanged()
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            if (dispatcher.CheckAccess())
            {
                RefreshDefaultModeState();
                RefreshPipelinePermissions();
                NotifyExecutionCanExecuteChanged();
                RefreshUserBadge();
                RefreshUsersTabVisibility();
                RefreshToolsPermissions();
            }
            else
            {
                dispatcher.Invoke(() =>
                {
                    RefreshDefaultModeState();
                    RefreshPipelinePermissions();
                    NotifyExecutionCanExecuteChanged();
                    RefreshUserBadge();
                    RefreshUsersTabVisibility();
                    RefreshToolsPermissions();
                });
            }
        }
    }

    /// <summary>Phase 3b: lock state changed — refresh CanExecute on trigger commands.</summary>
    private void OnLocksChanged()
    {
        // LocksChanged is already marshaled to Dispatcher by LockStateService
        NotifyExecutionCanExecuteChanged();
    }

    /// <summary>Live assignment update: PermissionsChanged signal received — refetch immediately.</summary>
    private async void OnAssignmentsChanged()
    {
        // AssignmentsChanged is already marshaled to Dispatcher by LockStateService
        await RefreshUserAssignmentsAsync();
    }

    /// <summary>Handles AuthClient.AuthStateChanged — updates CurrentUserHolder.</summary>
    private void OnAuthStateChanged()
    {
        var info = _authClient.CurrentUser;
        if (info is not null)
            _currentUserHolder.SetUser(info);
        else
            _currentUserHolder.Clear();
    }

    /// <summary>Re-pulls /api/auth/me to refresh assignments.</summary>
    private async Task RefreshUserAssignmentsAsync()
    {
        try
        {
            // FetchMeAsync refreshes CurrentUser which fires AuthStateChanged
            await _authClient.FetchMeAsync();
        }
        catch
        {
            // Best-effort; next timer tick will retry
        }
    }

    /// <summary>Called once after window layout completes to ensure WatchList root is active.</summary>
    public void EnsureWatchListSelected()
    {
        if (WatchListRoot is not null)
            ActiveEditNode = WatchListRoot;
    }

    public void SyncRegisteredAgents()
    {
        foreach (var name in _dispatcher.RegisteredAgents)
        {
            if (FindAgent(name) is null)
            {
                var addr = _dispatcher.GetAgentAddress(name) ?? "";
                var vm = new AgentInfoViewModel
                {
                    Name = name, Address = addr, ConnectionStatus = "Unknown"
                };
                IndexAgent(vm);
            }
        }
        if (_vocabMonitor.CurrentConfig is { } cfg && !string.IsNullOrEmpty(cfg.FilePath))
        {
            VocabFilePath = cfg.FilePath;
            ApplyConfig(cfg);
            AddLog($"Loaded from startup config: {cfg.FilePath}");
        }
        // Auto-test all agents on startup
        if (RegisteredAgents.Count > 0)
            _ = TestAllAgentsAsync();

        // Start background health check (every 30s)
        StartPeriodicHealthCheck(30);
    }

    // ── Dispose ─────────────────────────────────────────────────────

    /// <summary>
    /// Cancels all running pipelines. Called during app shutdown.
    /// </summary>
    public void CancelAllPipelines()
    {
        _executionCts?.Cancel();

        foreach (var session in ActiveSessions.ToList())
            session.Cts.Cancel();

        AddLog("All pipelines cancelled (app shutting down)", LogSeverity.Warning);
    }

    public void Dispose()
    {
        _vocabMonitor.ConfigReloaded -= OnConfigReloaded;
        _executor.LogEntry -= OnLogEntry;
        _executor.NodeProgress -= OnNodeProgress;
        _executor.NodeFailed -= OnNodeFailed;
        _dispatcher.OutputReceived -= OnOutputReceived;
        _dispatcher.StatusChanged -= OnStatusChanged;
        _watcherManager.TriggerFired -= OnTriggerFired;
        _watcherManager.TriggerMetadataParsed -= OnTriggerMetadataParsed;
        _watcherManager.TriggerParametersLoaded -= OnTriggerParametersLoaded;
        _capabilityChecker.CapabilitiesChanged -= OnCapabilitiesChanged;
        _authClient.AuthStateChanged -= OnAuthStateChanged;
        _currentUserHolder.UserChanged -= OnCurrentUserChanged;
        if (_fleetMaintenance is not null)
        {
            _fleetMaintenance.ProgressChanged -= OnMaintenanceProgressLog;
            _fleetMaintenance.OperationCompleted -= OnMaintenanceCompletedLog;
        }

        // Dispose event aggregator subscriptions (replaces static event unsubscription)
        foreach (var sub in _subscriptions) sub.Dispose();
        _subscriptions.Clear();

        StopPeriodicHealthCheck();
        _logBuffer?.Dispose();
        _executionCts?.Dispose();

        // Stop the multi-session dashboard's refresh timer + event subscriptions.
        _sessionElapsedTimer.Stop();
        _assignmentRefreshTimer.Stop();
        ExecutionDashboard?.Dispose();
        HealthMetrics?.Dispose();
        AgentWorkspace?.Dispose();

        // Cancel all active sessions
        foreach (var session in ActiveSessions.ToList())
            session.Cts.Dispose();
    }

    // ── Concurrent session helpers ──────────────────────────────────

    /// <summary>
    /// Checks if a specific WatchItem is already executing.
    /// Different WatchItems can run concurrently.
    /// </summary>
    private bool IsWatchItemRunning(string watchItemTag)
    {
        return ActiveSessions.Any(s =>
            string.Equals(s.WatchItemTag, watchItemTag, StringComparison.OrdinalIgnoreCase)
            && s.Status == "Running");
    }

    /// <summary>Creates a new session and adds it to the active list.</summary>
    private PipelineSession CreateSession(string watchItemTag)
    {
        var session = new PipelineSession { WatchItemTag = watchItemTag };
        Application.Current?.Dispatcher.InvokeAsync(() => ActiveSessions.Add(session));
        return session;
    }

    /// <summary>Completes a session and removes it from the active list after a delay.</summary>
    private void CompleteSession(PipelineSession session)
    {
        session.Complete();
        // Keep in list for 30 seconds for visibility, then remove
        _ = Task.Delay(30_000).ContinueWith(_ =>
        {
            Application.Current?.Dispatcher.InvokeAsync(() => ActiveSessions.Remove(session));
        });
    }

    // ── Agent Lock Admin ────────────────────────────────────────────

    /// <summary>Refreshes the lock display from AgentLockManager.</summary>
    public void RefreshLockDisplay()
    {
        AgentLocks.Clear();
        foreach (var l in _lockManager.GetAllLocks())
        {
            var elapsed = DateTime.UtcNow - l.LockedAtUtc;
            AgentLocks.Add(new AgentLockDisplayItem
            {
                AgentName = l.AgentName,
                SessionId = l.SessionId,
                WatchItemTag = l.WatchItemTag,
                UserId = l.UserId,
                Source = l.Source,
                Duration = elapsed.TotalHours >= 1
                    ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m"
                    : elapsed.TotalMinutes >= 1
                        ? $"{elapsed.Minutes}m {elapsed.Seconds}s"
                        : $"{elapsed.Seconds}s",
            });
        }
    }

    /// <summary>Admin force-release a single agent's lock.</summary>
    [RelayCommand]
    private void AdminForceRelease(string? agentName)
    {
        if (string.IsNullOrEmpty(agentName)) return;

        var currentLock = _lockManager.GetLock(agentName);
        if (currentLock == null)
        {
            AddLog($"Agent '{agentName}' is not locked");
            return;
        }

        _lockManager.ForceRelease(agentName);
        AddLog($"Admin force-released agent {agentName} (was: {currentLock.WatchItemTag}/{currentLock.UserId})", LogSeverity.Warning);
        RefreshLockDisplay();

        _events.Publish(new AgentLocksChangedEvent
        {
            Locks = _lockManager.GetAllLocks()
                .Select(l => new AgentLockInfo
                {
                    AgentName = l.AgentName,
                    SessionId = l.SessionId,
                    WatchItemTag = l.WatchItemTag,
                    UserId = l.UserId,
                    Source = l.Source,
                    LockedAtUtc = l.LockedAtUtc,
                }).ToList(),
            Reason = $"Admin force-released {agentName}",
        });
    }

    /// <summary>Admin emergency: release all agent locks.</summary>
    [RelayCommand]
    private void AdminForceReleaseAll()
    {
        var count = _lockManager.GetAllLocks().Count;
        if (count == 0) { AddLog("No agents are currently locked"); return; }

        _lockManager.ForceReleaseAll();
        AddLog($"Admin force-released ALL {count} agent locks", LogSeverity.Warning);
        RefreshLockDisplay();

        _events.Publish(new AgentLocksChangedEvent
        {
            Locks = [],
            Reason = "All locks force-released",
        });
    }

    /// <summary>Finds the WatchItem tag for an arbitrary tree node by walking up the tree.</summary>
    private static string? FindWatchItemTag(TreeNodeViewModel? node)
    {
        var current = node;
        while (current is not null)
        {
            if (current.NodeKind == NodeKinds.WatchItem)
                return current.Tag;
            current = current.Parent;
        }
        return null;
    }

    /// <summary>Finds the owning Template's pipeline tag ("Template:{ID}") for a node in the Templates tree.</summary>
    private static string? FindTemplateTag(TreeNodeViewModel? node)
    {
        var current = node;
        while (current is not null)
        {
            if (current.NodeKind == NodeKinds.Template)
                return $"Template:{current.Tag}";
            current = current.Parent;
        }
        return null;
    }

    /// <summary>Auto-populate gRPC address from agent name for convenience.</summary>
    partial void OnNewAgentNameChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            NewAgentAddress = $"http://{value.Trim()}:5200";
        else
            NewAgentAddress = "http://localhost:5200";
    }

    partial void OnSelectedNodeChanged(TreeNodeViewModel? value)
    {
        if (value is null) return;

        ActiveEditNode = value;
        if (value.NodeKind == NodeKinds.Action)
            value.EnsureDefaultActionTag();
        ActiveEditingContext = value.NodeKind is NodeKinds.Template or NodeKinds.TemplateList ? "Templates" : "WatchList";

        // Context-sensitive execute button visibility
        ShowExecuteAll = value.NodeKind == NodeKinds.WatchList ? Visibility.Visible : Visibility.Collapsed;
        ShowTriggerAllEvents = value.NodeKind == NodeKinds.WatchItem ? Visibility.Visible : Visibility.Collapsed;
        ShowTriggerEvent = value.NodeKind == NodeKinds.Event ? Visibility.Visible : Visibility.Collapsed;
        ShowExecuteGroup = value.NodeKind == NodeKinds.ActionGroup ? Visibility.Visible : Visibility.Collapsed;
        ShowExecuteAction = value.NodeKind == NodeKinds.Action ? Visibility.Visible : Visibility.Collapsed;

        // Import/Export/EditXML visibility — only for WatchList-level operations
        ShowImportButton = value.NodeKind is NodeKinds.WatchList
            ? Visibility.Visible : Visibility.Collapsed;
        ShowEditXmlButton = value.NodeKind is NodeKinds.WatchList
            ? Visibility.Visible : Visibility.Collapsed;
        ShowExportButton = value.NodeKind is NodeKinds.WatchList or NodeKinds.WatchItem
            ? Visibility.Visible : Visibility.Collapsed;

        // Load Initialize parameter file entries
        if (value.NodeKind == NodeKinds.Initialize && !string.IsNullOrWhiteSpace(value.ParameterFile))
            LoadParameterFileEntries(value.ParameterFile);
        else
            ParameterFileEntries.Clear();

        // Re-evaluate CanExecute for all execution commands since they depend on SelectedNode
        NotifyExecutionCanExecuteChanged();

        // Update watermark visibility
        ShowWatermark = false;
    }

    partial void OnSelectedTemplateNodeChanged(TreeNodeViewModel? value)
    {
        NotifyExecutionCanExecuteChanged();
        if (value is null) return;

        ActiveEditNode = value;
        ActiveEditingContext = "Templates";

        // Update watermark visibility
        ShowWatermark = false;
    }
}
