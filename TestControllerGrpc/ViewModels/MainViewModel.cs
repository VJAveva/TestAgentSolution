using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
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
    private CancellationTokenSource? _executionCts;

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
        CancelExecutionCommand.NotifyCanExecuteChanged();
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
        set { if (SetProperty(ref _logFilterTag, value)) ApplyLogFilter(); }
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
    public ObservableCollection<LogEntryViewModel> LogEntries { get; } = new();
    public ObservableCollection<LogEntryViewModel> FilteredLogEntries { get; } = new();
    public ObservableCollection<AgentInfoViewModel> RegisteredAgents { get; } = new();
    public ObservableCollection<string> AvailableTemplateIds { get; } = new();
    public ObservableCollection<string> AvailableWatchItemTags { get; } = new();
    public ObservableCollection<string> AvailableAgentNames { get; } = new();

    private WatchListConfig _config = new();

    private TreeNodeViewModel? WatchListRoot => TreeRoots.Count > 0 ? TreeRoots[0] : null;
    private TreeNodeViewModel? TemplateListRoot => TemplateRoots.Count > 0 ? TemplateRoots[0] : null;

    // ── Build Results ────────────────────────────────────────────────
    public BuildResultsViewModel BuildResultsVM { get; }

    // ── Constructor ─────────────────────────────────────────────────

    public MainViewModel(IVocabularyMonitor vocabMonitor, IFileWatcherManager watcherManager,
        IActionPipelineExecutor executor, IAgentGrpcDispatcher dispatcher,
        ExecutionSessionManager sessionManager, ILogger<MainViewModel> logger,
        BuildResultsViewModel buildResultsVM, IAppLogger appLogger,
        IEventAggregator events)
    {
        _vocabMonitor = vocabMonitor;
        _watcherManager = watcherManager;
        _executor = executor;
        _dispatcher = dispatcher;
        _sessionManager = sessionManager;
        _logger = logger;
        _appLogger = appLogger;
        BuildResultsVM = buildResultsVM;
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

        // Always start with a single empty WatchList root
        InitializeEmptyWatchList();
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
            if (RegisteredAgents.All(a => !string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                var addr = _dispatcher.GetAgentAddress(name) ?? "";
                RegisteredAgents.Add(new AgentInfoViewModel
                {
                    Name = name, Address = addr, ConnectionStatus = "Unknown"
                });
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

    public void Dispose()
    {
        _vocabMonitor.ConfigReloaded -= OnConfigReloaded;
        _executor.LogEntry -= OnLogEntry;
        _executor.NodeProgress -= OnNodeProgress;
        _dispatcher.OutputReceived -= OnOutputReceived;
        _dispatcher.StatusChanged -= OnStatusChanged;
        _watcherManager.TriggerFired -= OnTriggerFired;
        _watcherManager.TriggerMetadataParsed -= OnTriggerMetadataParsed;
        _watcherManager.TriggerParametersLoaded -= OnTriggerParametersLoaded;

        // Dispose event aggregator subscriptions (replaces static event unsubscription)
        foreach (var sub in _subscriptions) sub.Dispose();
        _subscriptions.Clear();

        StopPeriodicHealthCheck();
        _executionCts?.Dispose();
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
        ActiveEditingContext = value.NodeKind is "Template" or "TemplateList" ? "Templates" : "WatchList";

        // Context-sensitive execute button visibility
        ShowExecuteAll = value.NodeKind == "WatchList" ? Visibility.Visible : Visibility.Collapsed;
        ShowTriggerAllEvents = value.NodeKind == "WatchItem" ? Visibility.Visible : Visibility.Collapsed;
        ShowTriggerEvent = value.NodeKind == "Event" ? Visibility.Visible : Visibility.Collapsed;
        ShowExecuteGroup = value.NodeKind == "ActionGroup" ? Visibility.Visible : Visibility.Collapsed;
        ShowExecuteAction = value.NodeKind == "Action" ? Visibility.Visible : Visibility.Collapsed;

        // Import/Export/EditXML visibility — only for WatchList-level operations
        ShowImportButton = value.NodeKind is "WatchList"
            ? Visibility.Visible : Visibility.Collapsed;
        ShowEditXmlButton = value.NodeKind is "WatchList"
            ? Visibility.Visible : Visibility.Collapsed;
        ShowExportButton = value.NodeKind is "WatchList" or "WatchItem"
            ? Visibility.Visible : Visibility.Collapsed;

        // Load Initialize parameter file entries
        if (value.NodeKind == "Initialize" && !string.IsNullOrWhiteSpace(value.ParameterFile))
            LoadParameterFileEntries(value.ParameterFile);
        else
            ParameterFileEntries.Clear();

        // Re-evaluate CanExecute for all execution commands since they depend on SelectedNode
        NotifyExecutionCanExecuteChanged();
    }

    partial void OnSelectedTemplateNodeChanged(TreeNodeViewModel? value)
    {
        if (value is null) return;

        ActiveEditNode = value;
        ActiveEditingContext = "Templates";
    }
}
