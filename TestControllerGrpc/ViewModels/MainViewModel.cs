using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TestAgentGrpc;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly VocabularyMonitor _vocabMonitor;
    private readonly FileWatcherManager _watcherManager;
    private readonly ActionPipelineExecutor _executor;
    private readonly AgentGrpcDispatcher _dispatcher;
    private readonly ILogger<MainViewModel> _logger;

    [ObservableProperty] private TreeNodeViewModel? _selectedNode;
    [ObservableProperty] private TreeNodeViewModel? _selectedTemplateNode;
    [ObservableProperty] private TreeNodeViewModel? _activeEditNode;
    [ObservableProperty] private string _statusMessage = "Ready";
    [ObservableProperty] private string _vocabFilePath = "";
    [ObservableProperty] private int _activeWatchers;
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

    // ── Active editing context (Feature 3) ──────────────────────────
    [ObservableProperty] private string _activeEditingContext = "WatchList";

    // ── Context-sensitive execute button visibility (Feature 4) ─────
    [ObservableProperty] private Visibility _showExecuteAll = Visibility.Visible;
    [ObservableProperty] private Visibility _showTriggerAllEvents = Visibility.Collapsed;
    [ObservableProperty] private Visibility _showTriggerEvent = Visibility.Collapsed;
    [ObservableProperty] private Visibility _showExecuteGroup = Visibility.Collapsed;
    [ObservableProperty] private Visibility _showExecuteAction = Visibility.Collapsed;

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

    partial void OnSelectedNodeChanged(TreeNodeViewModel? value)
    {
        if (value is not null)
        {
            ActiveEditNode = value;
            ActiveEditingContext = "WatchList";
            // Auto-close XML editor when selection changes
            IsXmlEditorOpen = false;
            XmlEditorStatus = "";

            // Auto-load parameter file entries when Initialize node is selected
            if (value.NodeKind == "Initialize" && !string.IsNullOrWhiteSpace(value.ParameterFile))
                LoadParameterFileEntries(value.ParameterFile);
            else
                ParameterFileEntries.Clear();
        }
        RefreshExecuteButtonVisibility();
    }

    // Note: TemplateTree ActiveEditNode is now set from code-behind
    // after _initialLayoutComplete guard, to prevent startup auto-selection override.
    partial void OnSelectedTemplateNodeChanged(TreeNodeViewModel? value)
    {
        if (value is not null)
            ActiveEditingContext = "Templates";
        RefreshExecuteButtonVisibility();
    }

    /// <summary>Refreshes context-sensitive execute button visibility based on the active selection.</summary>
    private void RefreshExecuteButtonVisibility()
    {
        var kind = ActiveEditingContext == "Templates" ? null : SelectedNode?.NodeKind;

        ShowExecuteAll = kind is null or "WatchList" ? Visibility.Visible : Visibility.Collapsed;
        ShowTriggerAllEvents = kind is "WatchItem" ? Visibility.Visible : Visibility.Collapsed;
        ShowTriggerEvent = kind is "Event" ? Visibility.Visible : Visibility.Collapsed;
        ShowExecuteGroup = kind is "ActionGroup" ? Visibility.Visible : Visibility.Collapsed;
        ShowExecuteAction = kind is "Action" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Called once after window layout completes to ensure WatchList root is active.</summary>
    public void EnsureWatchListSelected()
    {
        if (WatchListRoot is not null)
            ActiveEditNode = WatchListRoot;
    }

    public MainViewModel(VocabularyMonitor vocabMonitor, FileWatcherManager watcherManager,
        ActionPipelineExecutor executor, AgentGrpcDispatcher dispatcher, ILogger<MainViewModel> logger)
    {
        _vocabMonitor = vocabMonitor;
        _watcherManager = watcherManager;
        _executor = executor;
        _dispatcher = dispatcher;
        _logger = logger;
        _vocabMonitor.ConfigReloaded += OnConfigReloaded;
        _executor.LogEntry += OnLogEntry;
        _executor.NodeProgress += OnNodeProgress;
        _dispatcher.OutputReceived += OnOutputReceived;
        _dispatcher.StatusChanged += OnStatusChanged;
        _watcherManager.TriggerFired += OnTriggerFired;

        // Subscribe to gRPC server events (agents calling in)
        Services.TestControllerGrpcService.AgentRegistered += OnAgentSelfRegistered;
        Services.TestControllerGrpcService.AgentUnregistered += OnAgentSelfUnregistered;
        Services.TestControllerGrpcService.HeartbeatReceived += OnAgentHeartbeat;

        // F1: Always start with a single empty WatchList root
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

    // ── File (single WatchList — Open replaces, never adds) ─────────

    [RelayCommand]
    private void LoadVocabulary()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        { Filter = "XML Files|*.xml;*.txt|All|*.*", Title = "Open WatchList" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var config = _vocabMonitor.StartMonitoring(dlg.FileName);
            VocabFilePath = dlg.FileName;
            ApplyConfig(config);
            AddLog($"Loaded: {dlg.FileName}");
        }
        catch (Exception ex) { AddLog($"Error: {ex.Message}"); }
    }

    [RelayCommand]
    private void SaveVocabulary()
    {
        if (string.IsNullOrEmpty(VocabFilePath)) { SaveVocabularyAs(); return; }
        try
        {
            WriteBackAll();
            WatchListXmlParser.Save(_config, VocabFilePath);
            AddLog($"Saved: {VocabFilePath}");
        }
        catch (Exception ex) { AddLog($"Save error: {ex.Message}"); }
    }

    [RelayCommand]
    private void SaveVocabularyAs()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        { Filter = "XML|*.xml|All|*.*", Title = "Save WatchList" };
        if (dlg.ShowDialog() != true) return;
        VocabFilePath = dlg.FileName;
        SaveVocabulary();
    }

    // ── Agent Registration with Connectivity Check ────────────────

    [ObservableProperty] private AgentInfoViewModel? _selectedAgent;

    [RelayCommand]
    private async Task RegisterAgent()
    {
        if (string.IsNullOrWhiteSpace(NewAgentName) || string.IsNullOrWhiteSpace(NewAgentAddress)) return;

        var name = NewAgentName.Trim();
        var addr = NewAgentAddress.Trim();

        // Remove existing if re-registering
        var existing = RegisteredAgents.FirstOrDefault(a =>
            string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) RegisteredAgents.Remove(existing);

        // Register in dispatcher
        _dispatcher.RegisterAgent(name, addr);

        // Create view model and add to list
        var agentVm = new AgentInfoViewModel
        {
            Name = name, Address = addr, ConnectionStatus = "Testing"
        };
        agentVm.UpdateDetailLine();
        RegisteredAgents.Add(agentVm);
        AddLog($"Registering agent: {name} → {addr}...");
        NewAgentName = "";

        // Test connectivity
        await TestSingleAgentAsync(agentVm);
    }

    [RelayCommand]
    private async Task TestConnection()
    {
        if (SelectedAgent is null) return;
        await TestSingleAgentAsync(SelectedAgent);
    }

    [RelayCommand]
    private async Task TestAllAgents()
    {
        await TestAllAgentsAsync();
    }

    [RelayCommand]
    private async Task DiagnoseAgent()
    {
        if (SelectedAgent is null) return;
        var agent = SelectedAgent;
        agent.IsDiagnosing = true;
        agent.ConnectionStatus = "Testing";
        agent.UpdateDetailLine();

        AddLog($"── Diagnosing {agent.Name} ({agent.Address}) ──");

        try
        {
            var steps = await _dispatcher.DiagnoseAgentAsync(agent.Name);
            foreach (var step in steps)
            {
                var icon = step.Passed ? "✔" : "✖";
                AddLog($"  {icon} {step.Name}: {step.Detail}");
            }

            var allPassed = steps.All(s => s.Passed);
            var lastFailed = steps.LastOrDefault(s => !s.Passed);

            if (allPassed)
            {
                agent.ConnectionStatus = "Online";
                agent.ErrorDetail = "";
                // Also update metrics from the last step (Snapshot)
                await TestSingleAgentAsync(agent);
            }
            else
            {
                agent.ConnectionStatus = "Offline";
                agent.ErrorDetail = lastFailed?.Detail ?? "Unknown failure";
                agent.UpdateDetailLine();
            }

            AddLog($"── Diagnosis complete: {(allPassed ? "ALL PASSED" : $"FAILED at {lastFailed?.Name}")} ──");
        }
        catch (Exception ex)
        {
            agent.ConnectionStatus = "Error";
            agent.ErrorDetail = ex.Message;
            agent.UpdateDetailLine();
            AddLog($"  ✖ Diagnosis error: {ex.Message}");
        }
        finally
        {
            agent.IsDiagnosing = false;
        }
    }

    [RelayCommand]
    private void UnregisterAgent()
    {
        if (SelectedAgent is null) return;
        var name = SelectedAgent.Name;
        _dispatcher.UnregisterAgent(name);
        RegisteredAgents.Remove(SelectedAgent);
        SelectedAgent = null;
        AddLog($"Unregistered agent: {name}");
        RefreshAgentStatusSummary();
    }

    private async Task TestSingleAgentAsync(AgentInfoViewModel agentVm)
    {
        agentVm.ConnectionStatus = "Testing";
        agentVm.UpdateDetailLine();

        var (snapshot, error) = await _dispatcher.TestConnectionAsync(agentVm.Name);

        if (snapshot is not null)
        {
            var stateLabel = snapshot.State switch
            {
                TestAgentGrpc.AgentState.Ready => "Ready",
                TestAgentGrpc.AgentState.Running => "Running",
                _ => "Inactive"
            };
            agentVm.AgentState = stateLabel;

            if (snapshot.Metrics is not null)
            {
                agentVm.CpuUsage = $"{snapshot.Metrics.CpuUsagePct:F0}%";
                agentVm.MemoryUsage = $"{snapshot.Metrics.MemoryUsedMb:F0}MB";
                agentVm.DiskFree = $"{snapshot.Metrics.DiskFreeGb:F1}GB";
            }
            else
            {
                agentVm.CpuUsage = "—";
                agentVm.MemoryUsage = "—";
                agentVm.DiskFree = "—";
            }

            agentVm.ConnectionStatus = "Online";
            agentVm.ErrorDetail = "";
            agentVm.UpdateDetailLine();
            AddLog($"✔ Agent {agentVm.Name}: {stateLabel} | CPU: {agentVm.CpuUsage} Mem: {agentVm.MemoryUsage} Disk: {agentVm.DiskFree}");
        }
        else
        {
            agentVm.ConnectionStatus = "Offline";
            agentVm.AgentState = "—";
            agentVm.CpuUsage = "—";
            agentVm.MemoryUsage = "—";
            agentVm.DiskFree = "—";
            agentVm.ErrorDetail = error ?? "Unknown error";
            agentVm.UpdateDetailLine();
            AddLog($"✖ Agent {agentVm.Name}: {error}");
        }
        RefreshAgentStatusSummary();
    }

    private async Task TestAllAgentsAsync()
    {
        AddLog($"Testing {RegisteredAgents.Count} agent(s)...");
        var tasks = RegisteredAgents.Select(TestSingleAgentAsync).ToArray();
        await Task.WhenAll(tasks);
        var online = RegisteredAgents.Count(a => a.ConnectionStatus == "Online");
        AddLog($"Agent check complete: {online}/{RegisteredAgents.Count} online");
        RefreshAgentStatusSummary();
    }

    private void RefreshAgentStatusSummary()
    {
        if (RegisteredAgents.Count == 0)
        {
            AgentStatusSummary = "No agents";
            return;
        }
        var online = RegisteredAgents.Count(a => a.ConnectionStatus == "Online");
        AgentStatusSummary = $"{online}/{RegisteredAgents.Count} online";
    }

    // ── Periodic agent health check (runs every 30s) ──────────────────

    private System.Windows.Threading.DispatcherTimer? _healthCheckTimer;

    /// <summary>Starts background health-check polling for all registered agents.</summary>
    public void StartPeriodicHealthCheck(int intervalSeconds = 30)
    {
        _healthCheckTimer?.Stop();
        _healthCheckTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(intervalSeconds)
        };
        _healthCheckTimer.Tick += async (_, _) =>
        {
            if (RegisteredAgents.Count == 0) return;
            foreach (var agent in RegisteredAgents.ToList())
            {
                try
                {
                    var (snapshot, _) = await _dispatcher.TestConnectionAsync(agent.Name);
                    if (snapshot is not null)
                    {
                        agent.AgentState = snapshot.State switch
                        {
                            TestAgentGrpc.AgentState.Ready => "Ready",
                            TestAgentGrpc.AgentState.Running => "Running",
                            _ => "Inactive"
                        };
                        if (snapshot.Metrics is not null)
                        {
                            agent.CpuUsage = $"{snapshot.Metrics.CpuUsagePct:F0}%";
                            agent.MemoryUsage = $"{snapshot.Metrics.MemoryUsedMb:F0}MB";
                            agent.DiskFree = $"{snapshot.Metrics.DiskFreeGb:F1}GB";
                        }
                        agent.ConnectionStatus = "Online";
                        agent.ErrorDetail = "";
                    }
                    else
                    {
                        agent.ConnectionStatus = "Offline";
                    }
                    agent.UpdateDetailLine();
                }
                catch { /* silent — background check */ }
            }
        };
        _healthCheckTimer.Start();
    }

    public void StopPeriodicHealthCheck()
    {
        _healthCheckTimer?.Stop();
        _healthCheckTimer = null;
    }

    // ── Agent self-registration via gRPC server events ──────────────

    private void OnAgentSelfRegistered(string name, string address)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            // If already in list, update address; else add new
            var existing = RegisteredAgents.FirstOrDefault(a =>
                string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.Address = address;
                existing.ConnectionStatus = "Online";
                existing.AgentState = "Ready";
                existing.UpdateDetailLine();
            }
            else
            {
                var vm = new AgentInfoViewModel
                {
                    Name = name, Address = address,
                    ConnectionStatus = "Online", AgentState = "Ready"
                };
                vm.UpdateDetailLine();
                RegisteredAgents.Add(vm);
            }
            AddLog($"↗ Agent self-registered: {name} → {address}");
            RefreshAgentStatusSummary();
        });
    }

    private void OnAgentSelfUnregistered(string name)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var existing = RegisteredAgents.FirstOrDefault(a =>
                string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.ConnectionStatus = "Offline";
                existing.AgentState = "Shutdown";
                existing.UpdateDetailLine();
            }
            AddLog($"↘ Agent unregistered: {name}");
            RefreshAgentStatusSummary();
        });
    }

    private void OnAgentHeartbeat(string name, TestAgentGrpc.AgentState state, TestAgentGrpc.ResourceMetrics? metrics)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var existing = RegisteredAgents.FirstOrDefault(a =>
                string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                // Agent is heartbeating but not in the UI list — add it if registered in dispatcher
                var address = _dispatcher.GetAgentAddress(name);
                if (address is null) return;

                existing = new AgentInfoViewModel { Name = name, Address = address };
                RegisteredAgents.Add(existing);
                AddLog($"↗ Agent discovered via heartbeat: {name} → {address}");
            }

            existing.ConnectionStatus = "Online";
            existing.AgentState = state switch
            {
                TestAgentGrpc.AgentState.Ready => "Ready",
                TestAgentGrpc.AgentState.Running => "Running",
                _ => "Inactive"
            };
            if (metrics is not null)
            {
                existing.CpuUsage = $"{metrics.CpuUsagePct:F0}%";
                existing.MemoryUsage = $"{metrics.MemoryUsedMb:F0}MB";
                existing.DiskFree = $"{metrics.DiskFreeGb:F1}GB";
            }
            existing.LastChecked = DateTime.Now.ToString("HH:mm:ss");
            existing.UpdateDetailLine();
            RefreshAgentStatusSummary();
        });
    }

    // ── Move ────────────────────────────────────────────────────────

    [RelayCommand] private void MoveUp() { if (SelectedNode is not null) MoveNode(SelectedNode, -1); }
    [RelayCommand] private void MoveDown() { if (SelectedNode is not null) MoveNode(SelectedNode, +1); }
    [RelayCommand] private void MoveTemplateUp() { if (SelectedTemplateNode is not null) MoveNode(SelectedTemplateNode, -1); }
    [RelayCommand] private void MoveTemplateDown() { if (SelectedTemplateNode is not null) MoveNode(SelectedTemplateNode, +1); }

    /// <summary>Public move method for use from code-behind context menus and drag-drop.</summary>
    public void MoveNodeUp(TreeNodeViewModel node) => MoveNode(node, -1);
    /// <summary>Public move method for use from code-behind context menus and drag-drop.</summary>
    public void MoveNodeDown(TreeNodeViewModel node) => MoveNode(node, +1);

    /// <summary>
    /// Returns true if the given node can be moved in the specified direction (-1=up, +1=down).
    /// Used by context menu to determine visibility of Move Up/Down items.
    /// </summary>
    public static bool CanMoveNode(TreeNodeViewModel node, int dir)
    {
        if (node.NodeKind is "WatchList" or "TemplateList" or "Initialize") return false;
        if (node.Parent is null) return false;
        var siblings = node.Parent.Children;
        var idx = siblings.IndexOf(node);
        if (idx < 0) return false;
        var nIdx = idx + dir;
        return nIdx >= 0 && nIdx < siblings.Count;
    }

    /// <summary>
    /// Moves a node from its current parent to a new parent at a specific index.
    /// Used by drag-and-drop to reparent nodes within the same Event subtree.
    /// </summary>
    public void ReparentNode(TreeNodeViewModel node, TreeNodeViewModel newParent, int insertIndex)
    {
        if (node.Parent is null) return;

        var oldParent = node.Parent;
        var oldSiblings = oldParent.Children;
        var oldIdx = oldSiblings.IndexOf(node);
        if (oldIdx < 0) return;

        // Remove from old parent model
        switch (oldParent.ModelObject)
        {
            case EventConfig ev when node.ModelObject is IActionNode a: ev.Children.Remove(a); break;
            case ActionGroupConfig ag when node.ModelObject is IActionNode a2: ag.Children.Remove(a2); break;
            case TemplateConfig tc when node.ModelObject is IActionNode a3: tc.Children.Remove(a3); break;
        }
        oldSiblings.Remove(node);

        // Adjust insert index if moving within same parent and removing shifted it
        if (ReferenceEquals(oldParent, newParent) && oldIdx < insertIndex)
            insertIndex--;

        // Clamp index
        if (insertIndex < 0) insertIndex = 0;
        if (insertIndex > newParent.Children.Count) insertIndex = newParent.Children.Count;

        // Insert into new parent model
        switch (newParent.ModelObject)
        {
            case EventConfig ev when node.ModelObject is IActionNode a: ev.Children.Insert(insertIndex, a); break;
            case ActionGroupConfig ag when node.ModelObject is IActionNode a2: ag.Children.Insert(insertIndex, a2); break;
            case TemplateConfig tc when node.ModelObject is IActionNode a3: tc.Children.Insert(insertIndex, a3); break;
        }
        node.Parent = newParent;
        newParent.Children.Insert(insertIndex, node);

        oldParent.RefreshDisplayText();
        newParent.RefreshDisplayText();
        AddLog($"Moved: {node.DisplayText}");
    }

    private void MoveNode(TreeNodeViewModel node, int dir)
    {
        if (node.NodeKind is "WatchList" or "TemplateList") return;
        if (node.Parent is null) return;
        var siblings = node.Parent.Children;
        var idx = siblings.IndexOf(node);
        if (idx < 0) return;
        var nIdx = idx + dir;
        if (nIdx < 0 || nIdx >= siblings.Count) return;
        siblings.Move(idx, nIdx);
        switch (node.Parent.ModelObject)
        {
            case WatchListConfig cfg: Swap(cfg.WatchItems, idx, nIdx); break;
            case WatchItemConfig wi: Swap(wi.Events, idx, nIdx); break;
            case EventConfig ev: Swap(ev.Children, idx, nIdx); break;
            case ActionGroupConfig ag: Swap(ag.Children, idx, nIdx); break;
            case TemplateConfig tc: Swap(tc.Children, idx, nIdx); break;
            case List<TemplateConfig> tl: Swap(tl, idx, nIdx); break;
        }
    }

    private static void Swap<T>(List<T> list, int a, int b)
    {
        if (a >= 0 && b >= 0 && a < list.Count && b < list.Count)
            (list[a], list[b]) = (list[b], list[a]);
    }

    // ═══════════════════════════════════════════════════════════════
    // F1-INLINE: INLINE XML EDITOR (AvalonEdit panel in center)
    // ═══════════════════════════════════════════════════════════════

    [RelayCommand]
    private void ToggleInlineXmlEditor()
    {
        if (IsInlineXmlEditorVisible)
        {
            // Close inline editor
            IsInlineXmlEditorVisible = false;
            InlineXmlEditorStatus = "";
            return;
        }

        WriteBackAll();

        if (SelectedNode?.NodeKind == "WatchItem" && SelectedNode.ModelObject is WatchItemConfig wi)
        {
            InlineXmlEditorScope = "WatchItem";
            InlineXmlEditorText = WatchListXmlParser.SerializeWatchItem(wi);
        }
        else
        {
            // Default: entire WatchList
            InlineXmlEditorScope = "WatchList";
            InlineXmlEditorText = WatchListXmlParser.SerializeWatchList(_config);
        }

        InlineXmlEditorStatus = "";
        IsInlineXmlEditorVisible = true;
    }

    [RelayCommand]
    private void ApplyInlineXml()
    {
        try
        {
            if (InlineXmlEditorScope == "WatchItem")
            {
                if (SelectedNode?.NodeKind != "WatchItem") return;
                var oldWi = SelectedNode.ModelObject as WatchItemConfig;
                if (oldWi is null) return;

                var parsed = WatchListXmlParser.DeserializeWatchItem(InlineXmlEditorText);
                if (parsed is null)
                {
                    InlineXmlEditorStatus = "Error: Root element must be <WatchItem>.";
                    return;
                }

                var idx = _config.WatchItems.IndexOf(oldWi);
                if (idx >= 0) _config.WatchItems[idx] = parsed;

                if (WatchListRoot is not null)
                {
                    var treeIdx = WatchListRoot.Children.IndexOf(SelectedNode);
                    if (treeIdx >= 0)
                    {
                        var newNode = TreeNodeViewModel.FromWatchItem(parsed);
                        newNode.Parent = WatchListRoot;
                        WatchListRoot.Children[treeIdx] = newNode;
                        SelectedNode = newNode;
                    }
                }
                WatchListRoot?.RefreshDisplayText();
                InlineXmlEditorStatus = "WatchItem applied successfully.";
                AddLog($"WatchItem updated via inline XML editor: {parsed.Tag}", LogSeverity.Success);
            }
            else
            {
                // Full WatchList
                var parsed = WatchListXmlParser.DeserializeWatchList(InlineXmlEditorText);
                if (parsed is null)
                {
                    InlineXmlEditorStatus = "Error: Root element must be <WatchList>.";
                    return;
                }

                parsed.FilePath = _config.FilePath;
                ApplyConfig(parsed);
                InlineXmlEditorStatus = $"Applied: {parsed.WatchItems.Count} WatchItems, {parsed.Templates.Count} Templates.";
                AddLog($"WatchList updated via inline XML editor", LogSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            InlineXmlEditorStatus = $"XML error: {ex.Message}";
            AddLog($"Inline XML editor error: {ex.Message}", LogSeverity.Error);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // F2: RAW XML EDITOR  (opens separate modal window)
    // ═══════════════════════════════════════════════════════════════

    [RelayCommand]
    private void ToggleXmlEditor()
    {
        if (SelectedNode?.NodeKind != "WatchItem") return;
        OpenRawXmlEditorWindow();
    }

    [RelayCommand]
    private void ApplyXmlEditor()
    {
        // Kept for backward compatibility — delegates to the window flow
        if (SelectedNode?.NodeKind != "WatchItem") return;
        OpenRawXmlEditorWindow();
    }

    [RelayCommand]
    private void RevertXmlEditor()
    {
        // No-op: revert is now handled inside the editor window
    }

    [RelayCommand]
    private void EditWatchItemXml()
    {
        if (SelectedNode?.NodeKind != "WatchItem") return;
        OpenRawXmlEditorWindow();
    }

    /// <summary>Opens the standalone Raw XML Editor window for the selected WatchItem.</summary>
    private void OpenRawXmlEditorWindow()
    {
        if (SelectedNode?.NodeKind != "WatchItem") return;
        if (SelectedNode.ModelObject is not WatchItemConfig oldWi) return;

        WriteBackAll();
        var xml = WatchListXmlParser.SerializeWatchItem(oldWi);

        var editorVm = new WatchItemXmlEditorViewModel(xml);
        editorVm.WindowTitle = $"WatchItem XML Editor — {oldWi.Tag}";

        var editorWindow = new Views.RawXmlEditorWindow(editorVm);

        // Set owner to main window for CenterOwner positioning
        if (Application.Current.MainWindow is { } mainWindow)
            editorWindow.Owner = mainWindow;

        var result = editorWindow.ShowDialog();
        if (result == true && editorVm.DialogAccepted)
        {
            try
            {
                var parsed = WatchListXmlParser.DeserializeWatchItem(editorVm.ResultXml);
                if (parsed is null)
                {
                    XmlEditorStatus = "Error: Root element must be <WatchItem>.";
                    AddLog("XML editor: Root element was not <WatchItem>", LogSeverity.Error);
                    return;
                }

                // Replace in model
                var idx = _config.WatchItems.IndexOf(oldWi);
                if (idx >= 0) _config.WatchItems[idx] = parsed;

                // Replace in tree
                if (WatchListRoot is not null)
                {
                    var treeIdx = WatchListRoot.Children.IndexOf(SelectedNode);
                    if (treeIdx >= 0)
                    {
                        var newNode = TreeNodeViewModel.FromWatchItem(parsed);
                        newNode.Parent = WatchListRoot;
                        WatchListRoot.Children[treeIdx] = newNode;
                        SelectedNode = newNode;
                    }
                }
                WatchListRoot?.RefreshDisplayText();
                XmlEditorStatus = "Applied successfully.";
                IsXmlEditorOpen = false;
                AddLog($"WatchItem updated via XML editor: {parsed.Tag}", LogSeverity.Success);
            }
            catch (Exception ex)
            {
                XmlEditorStatus = $"XML error: {ex.Message}";
                AddLog($"XML editor apply failed: {ex.Message}", LogSeverity.Error);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // F3: TRIGGER EVENTS FROM UI
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Trigger a single Event node's pipeline.</summary>
    [RelayCommand]
    private async Task TriggerEvent()
    {
        if (SelectedNode?.NodeKind != "Event") return;
        if (SelectedNode.ModelObject is not EventConfig ev) return;
        if (IsExecuting) { AddLog("Execution already in progress"); return; }

        WriteBackAll();

        // Find parent WatchItem for context
        var wiNode = SelectedNode.Parent;
        var wiConfig = wiNode?.ModelObject as WatchItemConfig;
        var ctx = new PipelineExecutionContext
        {
            WatchItemPath = wiConfig?.Path ?? "",
            TriggerFileName = $"[ManualTrigger:{ev.Type}]",
        };

        var eventNode = SelectedNode;
        IsExecuting = true;
        _executionCts = new CancellationTokenSource();

        // F4: Mark event subtree as Running
        eventNode.SetStatusRecursive("Running");
        eventNode.PropagateStatusUp();
        AddLog($"Triggered Event: {ev.Type} on {wiConfig?.Tag ?? "?"}");

        try
        {
            await _executor.ExecuteEventAsync(ev, ctx, _executionCts.Token);
            eventNode.ExecutionStatus = "Success";
            eventNode.PropagateStatusUp();
            AddLog($"Event completed: {ev.Type}", LogSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            eventNode.SetFailed("Cancelled by user");
            eventNode.PropagateStatusUp();
            AddLog($"Event cancelled: {ev.Type}", LogSeverity.Warning);
        }
        catch (Exception ex)
        {
            eventNode.SetFailed(ex.Message);
            eventNode.PropagateStatusUp();
            AddLog($"Event failed: {ev.Type} — {ex.Message}", LogSeverity.Error);
            ScrollLogToLastError();
        }
        finally
        {
            IsExecuting = false;
            _executionCts?.Dispose();
            _executionCts = null;
        }
    }

    /// <summary>Trigger ALL events on the selected WatchItem.</summary>
    [RelayCommand]
    private async Task TriggerWatchItem()
    {
        if (SelectedNode?.NodeKind != "WatchItem") return;
        if (SelectedNode.ModelObject is not WatchItemConfig wi) return;
        if (IsExecuting) { AddLog("Execution already in progress"); return; }

        WriteBackAll();

        var wiNode = SelectedNode;
        IsExecuting = true;
        _executionCts = new CancellationTokenSource();

        // F4: Mark entire WatchItem subtree as Running
        wiNode.SetStatusRecursive("Running");
        wiNode.PropagateStatusUp();
        AddLog($"Triggered WatchItem: {wi.Tag} ({wi.Events.Count} events)");

        var allSuccess = true;
        try
        {
            foreach (var ev in wi.Events)
            {
                var ctx = new PipelineExecutionContext
                {
                    WatchItemPath = wi.Path,
                    TriggerFileName = $"[ManualTrigger:{ev.Type}]",
                };
                // Find event node in tree to mark individually
                var evNode = wiNode.Children.FirstOrDefault(c =>
                    ReferenceEquals(c.ModelObject, ev));
                if (evNode is not null) evNode.SetStatusRecursive("Running");

                try
                {
                    await _executor.ExecuteEventAsync(ev, ctx, _executionCts.Token);
                    if (evNode is not null)
                    {
                        evNode.ExecutionStatus = "Success";
                        evNode.PropagateStatusUp();
                    }
                }
                catch (Exception ex)
                {
                    allSuccess = false;
                    if (evNode is not null)
                    {
                        evNode.SetFailed(ex.Message);
                        evNode.PropagateStatusUp();
                    }
                    AddLog($"Event failed: {ev.Type} — {ex.Message}", LogSeverity.Error);
                }
            }
            wiNode.ExecutionStatus = allSuccess ? "Success" : "Failed";
            wiNode.PropagateStatusUp();
            AddLog($"WatchItem {(allSuccess ? "completed" : "completed with errors")}: {wi.Tag}",
                allSuccess ? LogSeverity.Success : LogSeverity.Error);
            if (!allSuccess) ScrollLogToLastError();
        }
        catch (OperationCanceledException)
        {
            wiNode.SetFailed("Cancelled by user");
            wiNode.PropagateStatusUp();
            AddLog($"WatchItem cancelled: {wi.Tag}", LogSeverity.Warning);
        }
        finally
        {
            IsExecuting = false;
            _executionCts?.Dispose();
            _executionCts = null;
        }
    }

    /// <summary>Cancel running execution.</summary>
    [RelayCommand]
    private void CancelExecution()
    {
        _executionCts?.Cancel();
        AddLog("Cancellation requested");
    }

    /// <summary>Reset all execution status indicators to Idle.</summary>
    [RelayCommand]
    private void ResetStatus()
    {
        WatchListRoot?.ResetStatus();
        AddLog("Execution status reset");
    }

    /// <summary>Close the application.</summary>
    [RelayCommand]
    private void Close()
    {
        Application.Current?.Shutdown();
    }

    /// <summary>Trigger ALL WatchItems simultaneously (parallel or sequential per config).</summary>
    [RelayCommand]
    private async Task TriggerAllWatchItems()
    {
        if (IsExecuting) { AddLog("Execution already in progress"); return; }
        if (_config.WatchItems.Count == 0) { AddLog("No WatchItems to execute"); return; }

        WriteBackAll();
        IsExecuting = true;
        _executionCts = new CancellationTokenSource();

        WatchListRoot?.SetStatusRecursive("Running");
        AddLog($"Triggered ALL WatchItems ({_config.WatchItems.Count} items)");

        var allSuccess = true;
        try
        {
            foreach (var wi in _config.WatchItems)
            {
                if (!wi.IsEnabled) continue;

                var wiNode = WatchListRoot?.Children.FirstOrDefault(c =>
                    ReferenceEquals(c.ModelObject, wi));
                if (wiNode is not null) wiNode.SetStatusRecursive("Running");

                var wiSuccess = true;
                foreach (var ev in wi.Events)
                {
                    var ctx = new PipelineExecutionContext
                    {
                        WatchItemPath = wi.Path,
                        TriggerFileName = $"[ManualTriggerAll:{ev.Type}]",
                    };
                    try
                    {
                        await _executor.ExecuteEventAsync(ev, ctx, _executionCts.Token);
                    }
                    catch (Exception ex)
                    {
                        wiSuccess = false;
                        AddLog($"Event failed: {wi.Tag}/{ev.Type} — {ex.Message}", LogSeverity.Error);
                    }
                }
                if (wiNode is not null)
                {
                    wiNode.ExecutionStatus = wiSuccess ? "Success" : "Failed";
                    if (!wiSuccess) wiNode.FailureMessage = "One or more events failed";
                    wiNode.PropagateStatusUp();
                }
                if (!wiSuccess) allSuccess = false;
            }

            if (WatchListRoot is not null)
                WatchListRoot.ExecutionStatus = allSuccess ? "Success" : "Failed";
            AddLog($"All WatchItems {(allSuccess ? "completed" : "completed with errors")}",
                allSuccess ? LogSeverity.Success : LogSeverity.Error);
            if (!allSuccess) ScrollLogToLastError();
        }
        catch (OperationCanceledException)
        {
            WatchListRoot?.SetStatusRecursive("Failed");
            AddLog("Execute All cancelled", LogSeverity.Warning);
        }
        finally
        {
            IsExecuting = false;
            _executionCts?.Dispose();
            _executionCts = null;
        }
    }

    /// <summary>Execute a single ActionGroup and its children.</summary>
    [RelayCommand]
    private async Task ExecuteGroup()
    {
        if (SelectedNode?.NodeKind != "ActionGroup") return;
        if (SelectedNode.ModelObject is not ActionGroupConfig ag) return;
        if (IsExecuting) { AddLog("Execution already in progress"); return; }

        WriteBackAll();

        // Walk up to find the parent WatchItem for context
        var wiConfig = FindAncestorModel<WatchItemConfig>(SelectedNode);
        var ctx = new PipelineExecutionContext
        {
            WatchItemPath = wiConfig?.Path ?? "",
            TriggerFileName = $"[ManualTrigger:Group:{ag.Tag}]",
        };

        var groupNode = SelectedNode;
        IsExecuting = true;
        _executionCts = new CancellationTokenSource();

        groupNode.SetStatusRecursive("Running");
        groupNode.PropagateStatusUp();
        AddLog($"Triggered ActionGroup: {ag.Tag}");

        try
        {
            await _executor.ExecuteGroupAsync(ag, ctx, _executionCts.Token);
            groupNode.ExecutionStatus = "Success";
            groupNode.PropagateStatusUp();
            AddLog($"ActionGroup completed: {ag.Tag}", LogSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            groupNode.SetFailed("Cancelled by user");
            groupNode.PropagateStatusUp();
            AddLog($"ActionGroup cancelled: {ag.Tag}", LogSeverity.Warning);
        }
        catch (Exception ex)
        {
            groupNode.SetFailed(ex.Message);
            groupNode.PropagateStatusUp();
            AddLog($"ActionGroup failed: {ag.Tag} — {ex.Message}", LogSeverity.Error);
            ScrollLogToLastError();
        }
        finally
        {
            IsExecuting = false;
            _executionCts?.Dispose();
            _executionCts = null;
        }
    }

    /// <summary>Execute a single Action node.</summary>
    [RelayCommand]
    private async Task ExecuteSingleAction()
    {
        if (SelectedNode?.NodeKind != "Action") return;
        if (SelectedNode.ModelObject is not ActionConfig action) return;
        if (IsExecuting) { AddLog("Execution already in progress"); return; }

        WriteBackAll();

        var wiConfig = FindAncestorModel<WatchItemConfig>(SelectedNode);
        var ctx = new PipelineExecutionContext
        {
            WatchItemPath = wiConfig?.Path ?? "",
            TriggerFileName = $"[ManualTrigger:Action:{action.Command}]",
        };

        var actionNode = SelectedNode;
        IsExecuting = true;
        _executionCts = new CancellationTokenSource();

        actionNode.ExecutionStatus = "Running";
        actionNode.PropagateStatusUp();
        AddLog($"Triggered Action: {action.Type} — {action.Command}");

        try
        {
            await _executor.ExecuteSingleActionAsync(action, ctx, _executionCts.Token);
            actionNode.ExecutionStatus = "Success";
            actionNode.PropagateStatusUp();
            AddLog($"Action completed: {action.Command}", LogSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            actionNode.SetFailed("Cancelled by user");
            actionNode.PropagateStatusUp();
            AddLog($"Action cancelled: {action.Command}", LogSeverity.Warning);
        }
        catch (Exception ex)
        {
            actionNode.SetFailed(ex.Message);
            actionNode.PropagateStatusUp();
            AddLog($"Action failed: {action.Command} — {ex.Message}", LogSeverity.Error);
            ScrollLogToLastError();
        }
        finally
        {
            IsExecuting = false;
            _executionCts?.Dispose();
            _executionCts = null;
        }
    }

    /// <summary>Walks up the tree to find the nearest ancestor with the given model type.</summary>
    private static T? FindAncestorModel<T>(TreeNodeViewModel node) where T : class
    {
        var current = node.Parent;
        while (current is not null)
        {
            if (current.ModelObject is T model) return model;
            current = current.Parent;
        }
        return null;
    }

    /// <summary>Changes the ExecutionType of the selected Event or ActionGroup node.</summary>
    public void ChangeExecutionType(TreeNodeViewModel node, ExecutionMode newMode)
    {
        node.ExecutionTypeText = newMode.ToString();
        node.ApplyToModel();
        node.RefreshDisplayText();
        AddLog($"Changed ExecutionType of '{node.DisplayText}' to {newMode}");
    }

    /// <summary>Clear log filters.</summary>
    [RelayCommand]
    private void ClearLogFilter()
    {
        LogFilterTag = "";
        LogFilterAgent = "";
        LogLevelFilter = "All";
        LogSearchText = "";
    }

    /// <summary>Applies tag/agent/severity/search filters to the execution log.</summary>
    private void ApplyLogFilter()
    {
        FilteredLogEntries.Clear();

        var hasTagFilter = !string.IsNullOrWhiteSpace(LogFilterTag);
        var hasAgentFilter = !string.IsNullOrWhiteSpace(LogFilterAgent);
        var hasSearchFilter = !string.IsNullOrWhiteSpace(LogSearchText);
        var hasSeverityFilter = LogLevelFilter != "All";

        foreach (var entry in LogEntries)
        {
            if (PassesFilter(entry, hasTagFilter, hasAgentFilter, hasSearchFilter, hasSeverityFilter))
                FilteredLogEntries.Add(entry);
        }
    }

    private bool PassesFilter(LogEntryViewModel entry,
        bool hasTagFilter, bool hasAgentFilter, bool hasSearchFilter, bool hasSeverityFilter)
    {
        var msg = entry.Message;

        if (hasTagFilter && !msg.Contains(LogFilterTag, StringComparison.OrdinalIgnoreCase))
            return false;
        if (hasAgentFilter && !msg.Contains(LogFilterAgent, StringComparison.OrdinalIgnoreCase))
            return false;
        if (hasSearchFilter && !msg.Contains(LogSearchText, StringComparison.OrdinalIgnoreCase)
            && !entry.Timestamp.Contains(LogSearchText, StringComparison.OrdinalIgnoreCase))
            return false;
        if (hasSeverityFilter)
        {
            var requiredSeverity = LogLevelFilter switch
            {
                "Info" => LogSeverity.Info,
                "Success" => LogSeverity.Success,
                "Warning" => LogSeverity.Warning,
                "Error" => LogSeverity.Error,
                _ => (LogSeverity?)null
            };
            if (requiredSeverity.HasValue && entry.Severity != requiredSeverity.Value)
                return false;
        }
        return true;
    }

    // ── Tree search ─────────────────────────────────────────────────

    [RelayCommand]
    private void ClearTreeSearch() => TreeSearchText = "";

    private void ApplyTreeSearch()
    {
        if (WatchListRoot is null) return;
        var search = TreeSearchText;
        if (string.IsNullOrWhiteSpace(search))
        {
            SetTreeVisibilityRecursive(WatchListRoot, true);
            return;
        }
        ApplyTreeSearchRecursive(WatchListRoot, search);
    }

    private static bool ApplyTreeSearchRecursive(TreeNodeViewModel node, string search)
    {
        var selfMatch = node.DisplayText.Contains(search, StringComparison.OrdinalIgnoreCase)
                     || node.Tag.Contains(search, StringComparison.OrdinalIgnoreCase);

        var anyChildMatch = false;
        foreach (var child in node.Children)
        {
            if (ApplyTreeSearchRecursive(child, search))
                anyChildMatch = true;
        }

        var visible = selfMatch || anyChildMatch || node.NodeKind is "WatchList";
        node.IsFilterVisible = visible;
        if (anyChildMatch) node.IsExpanded = true;
        return visible;
    }

    private static void SetTreeVisibilityRecursive(TreeNodeViewModel node, bool visible)
    {
        node.IsFilterVisible = visible;
        foreach (var child in node.Children)
            SetTreeVisibilityRecursive(child, visible);
    }

    // ── WatchList CRUD ──────────────────────────────────────────────

    [RelayCommand]
    private void AddWatchItem()
    {
        var idx = _config.WatchItems.Count + 1;
        var tag = $"WatchItem{idx}";
        while (_config.WatchItems.Any(w => string.Equals(w.Tag, tag, StringComparison.OrdinalIgnoreCase)))
            tag = $"WatchItem{++idx}";
        var wi = new WatchItemConfig { Tag = tag, Path = @"C:\", Filter = "*.txt" };
        _config.WatchItems.Add(wi);
        if (WatchListRoot is not null)
        {
            var node = TreeNodeViewModel.FromWatchItem(wi);
            node.Parent = WatchListRoot;
            WatchListRoot.Children.Add(node);
            WatchListRoot.RefreshDisplayText();
        }
        AddLog($"Added: {tag}");
    }

    [RelayCommand]
    private void AddChildNode()
    {
        if (SelectedNode is null) return;
        if (SelectedNode.NodeKind == "WatchList") { AddWatchItem(); return; }
        if (SelectedNode.NodeKind == "WatchItem")
        {
            var ev = new EventConfig { Type = "Renamed", ExecutionType = ExecutionMode.Sequential };
            if (SelectedNode.ModelObject is WatchItemConfig wi) wi.Events.Add(ev);
            var n = TreeNodeViewModel.FromEvent(ev); n.Parent = SelectedNode;
            SelectedNode.Children.Add(n);
        }
        else if (SelectedNode.NodeKind is "Event" or "ActionGroup")
        {
            var g = new ActionGroupConfig { Tag = "NewGroup", ExecutionType = ExecutionMode.Sequential };
            AddChild(SelectedNode, g);
        }
    }

    [RelayCommand]
    private void AddActionToGroup()
    {
        if (SelectedNode?.NodeKind is not ("ActionGroup" or "Event")) return;
        AddChild(SelectedNode, new ActionConfig { Type = ActionType.RunCommand, Command = "cmd", Parameters = "/c echo hello" });
    }

    [RelayCommand]
    private void AddRefToGroup()
    {
        if (SelectedNode?.NodeKind is not ("ActionGroup" or "Event")) return;
        AddChild(SelectedNode, new RefConfig { TemplateID = AvailableTemplateIds.Count > 0 ? AvailableTemplateIds[0] : "" });
    }

    /// <summary>Adds an Initialize node to the selected Event or ActionGroup in the WatchList tree.</summary>
    [RelayCommand]
    private void AddInitializeToGroup()
    {
        if (SelectedNode?.NodeKind is not ("ActionGroup" or "Event")) return;
        AddChild(SelectedNode, new InitializeConfig { Tag = "Params", ParameterFile = "" });
        AddLog("Added Initialize node");
    }

    /// <summary>Adds an ActionGroup to the selected WatchItem, Event, or ActionGroup in the WatchList tree.</summary>
    [RelayCommand]
    private void AddActionGroup()
    {
        if (SelectedNode is null) return;
        if (SelectedNode.NodeKind is "WatchItem")
        {
            // WatchItem cannot hold ActionGroup directly — it must go under an Event.
            // If the WatchItem has no events, create one first.
            if (SelectedNode.ModelObject is WatchItemConfig wi)
            {
                EventConfig targetEvent;
                if (wi.Events.Count == 0)
                {
                    targetEvent = new EventConfig { Type = "Renamed", ExecutionType = ExecutionMode.Sequential };
                    wi.Events.Add(targetEvent);
                    var evNode = TreeNodeViewModel.FromEvent(targetEvent);
                    evNode.Parent = SelectedNode;
                    SelectedNode.Children.Add(evNode);
                }
                else
                {
                    targetEvent = wi.Events[0];
                }
                // Find the event node and add the group there
                var eventNode = SelectedNode.Children.FirstOrDefault(c => ReferenceEquals(c.ModelObject, targetEvent));
                if (eventNode is not null)
                {
                    var ag = new ActionGroupConfig { Tag = "NewActionGroup", ExecutionType = ExecutionMode.Sequential, FailAndContinue = true };
                    AddChild(eventNode, ag);
                    eventNode.IsExpanded = true;
                    SelectedNode.IsExpanded = true;
                    AddLog("Added ActionGroup under Event");
                }
            }
            return;
        }
        if (SelectedNode.NodeKind is "Event" or "ActionGroup")
        {
            var ag = new ActionGroupConfig { Tag = "NewActionGroup", ExecutionType = ExecutionMode.Sequential, FailAndContinue = true };
            AddChild(SelectedNode, ag);
            SelectedNode.IsExpanded = true;
            AddLog("Added ActionGroup");
            return;
        }
        if (SelectedNode.NodeKind is "Template")
        {
            var ag = new ActionGroupConfig { Tag = "NewActionGroup", ExecutionType = ExecutionMode.Sequential, FailAndContinue = true };
            AddChildT(SelectedNode, ag);
            SelectedNode.IsExpanded = true;
            AddLog("Added ActionGroup to Template");
            return;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // INITIALIZE PARAMETER FILE MANAGEMENT
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Browse for a parameter file and set it on the active Initialize node.</summary>
    [RelayCommand]
    private void BrowseParameterFile()
    {
        if (ActiveEditNode?.NodeKind != "Initialize") return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Text Files|*.txt|All Files|*.*",
            Title = "Select Parameter File"
        };

        // Start in the current file's directory if possible
        if (!string.IsNullOrWhiteSpace(ActiveEditNode.ParameterFile))
        {
            var dir = Path.GetDirectoryName(ActiveEditNode.ParameterFile);
            if (dir is not null && Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }

        if (dlg.ShowDialog() != true) return;

        ActiveEditNode.ParameterFile = dlg.FileName;
        ActiveEditNode.ApplyToModel();
        ActiveEditNode.RefreshDisplayText();
        LoadParameterFileEntries(dlg.FileName);
        AddLog($"Parameter file selected: {dlg.FileName}");
    }

    /// <summary>Loads and displays all entries from a parameter file.</summary>
    [RelayCommand]
    private void LoadParameterFileFromNode()
    {
        if (ActiveEditNode?.NodeKind != "Initialize") return;
        if (string.IsNullOrWhiteSpace(ActiveEditNode.ParameterFile)) return;
        LoadParameterFileEntries(ActiveEditNode.ParameterFile);
    }

    private void LoadParameterFileEntries(string filePath)
    {
        ParameterFileEntries.Clear();
        ParameterFileStatus = "";

        if (!File.Exists(filePath))
        {
            ParameterFileStatus = $"File not found: {filePath}";
            return;
        }

        try
        {
            var entries = ParameterResolver.ParseParameterFile(filePath);
            foreach (var (key, value) in entries)
            {
                ParameterFileEntries.Add(new ParameterEntryViewModel { Key = key, Value = value });

                // Populate the shared token dictionary for UI display resolution
                TreeNodeViewModel.TokenValues[key] = value;
                if (key.StartsWith('_'))
                    TreeNodeViewModel.TokenValues[key[1..]] = value;
            }

            // Refresh resolved display text across all trees
            WatchListRoot?.RefreshResolvedTextRecursive();
            TemplateListRoot?.RefreshResolvedTextRecursive();

            ParameterFileStatus = $"Loaded {entries.Count} parameters from {Path.GetFileName(filePath)}";
            AddLog($"Loaded {entries.Count} parameters from {Path.GetFileName(filePath)}");
        }
        catch (Exception ex)
        {
            ParameterFileStatus = $"Error reading file: {ex.Message}";
        }
    }

    /// <summary>Adds a new empty parameter entry row.</summary>
    [RelayCommand]
    private void AddParameterEntry()
    {
        ParameterFileEntries.Add(new ParameterEntryViewModel
        {
            Key = "_NewKey",
            Value = "",
            IsNew = true
        });
    }

    /// <summary>Removes a parameter entry from the list.</summary>
    [RelayCommand]
    private void RemoveParameterEntry(ParameterEntryViewModel? entry)
    {
        if (entry is not null)
            ParameterFileEntries.Remove(entry);
    }

    /// <summary>Saves all parameter entries back to the parameter file.</summary>
    [RelayCommand]
    private void SaveParameterFile()
    {
        if (ActiveEditNode?.NodeKind != "Initialize") return;
        if (string.IsNullOrWhiteSpace(ActiveEditNode.ParameterFile))
        {
            ParameterFileStatus = "No parameter file path set. Use Browse to select a file.";
            return;
        }

        try
        {
            var entries = ParameterFileEntries
                .Where(e => !string.IsNullOrWhiteSpace(e.Key))
                .Select(e => (e.Key, e.Value))
                .ToList();

            // Ensure directory exists
            var dir = Path.GetDirectoryName(ActiveEditNode.ParameterFile);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            ParameterResolver.SaveParameterFile(ActiveEditNode.ParameterFile, entries);

            // Mark all as not new after save
            foreach (var e in ParameterFileEntries) e.IsNew = false;

            ParameterFileStatus = $"Saved {entries.Count} parameters to {Path.GetFileName(ActiveEditNode.ParameterFile)}";
            AddLog($"Saved parameter file: {ActiveEditNode.ParameterFile}", LogSeverity.Success);
        }
        catch (Exception ex)
        {
            ParameterFileStatus = $"Save error: {ex.Message}";
            AddLog($"Failed to save parameter file: {ex.Message}", LogSeverity.Error);
        }
    }

    [RelayCommand]
    private void DeleteSelectedNode()
    {
        if (SelectedNode is null) return;
        if (SelectedNode.NodeKind is "WatchList" or "TemplateList") return;

        // CRITICAL: Capture references BEFORE Remove(), because Remove() triggers
        // WPF SelectedItemChanged which changes SelectedNode mid-flight.
        var target = SelectedNode;
        var parent = target.Parent;
        if (parent is null) return;

        if (parent.Children.Remove(target))
        {
            switch (parent.ModelObject)
            {
                case WatchListConfig cfg when target.ModelObject is WatchItemConfig wi: cfg.WatchItems.Remove(wi); break;
                case WatchItemConfig wi when target.ModelObject is EventConfig ev: wi.Events.Remove(ev); break;
                case EventConfig ev when target.ModelObject is IActionNode a: ev.Children.Remove(a); break;
                case ActionGroupConfig ag when target.ModelObject is IActionNode a2: ag.Children.Remove(a2); break;
                case TemplateConfig tc when target.ModelObject is IActionNode a3: tc.Children.Remove(a3); break;
                case List<TemplateConfig> tl when target.ModelObject is TemplateConfig tc2: tl.Remove(tc2); break;
            }
            parent.RefreshDisplayText();
            AddLog($"Deleted: {target.DisplayText}");
            SelectedNode = null;
            return;
        }
        if (WatchListRoot is not null && RemoveDeep(WatchListRoot, target))
        { SelectedNode = null; }
    }

    [RelayCommand]
    private void ConfirmDeleteSelectedNode()
    {
        if (SelectedNode is null) return;

        var msg = $"Are you sure you want to delete '{SelectedNode.DisplayText}'?";
        var title = "Confirm Delete";

        // Show confirmation dialog
        var result = MessageBox.Show(msg, title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            // Proceed with deletion
            DeleteSelectedNode();
        }
    }

    [RelayCommand]
    private void ApplyChanges()
    {
        ActiveEditNode?.ApplyToModel();
        ActiveEditNode?.RefreshDisplayText();
        WatchListRoot?.RefreshDisplayText();
        TemplateListRoot?.RefreshDisplayText();
        RebuildTemplateIds();
        AddLog("Changes applied");
    }

    // ── Template CRUD ───────────────────────────────────────────────

    [RelayCommand]
    private void EditTemplateXml()
    {
        OpenTemplateXmlEditorWindow();
    }

    /// <summary>Opens the standalone Template XML Editor window for all templates.</summary>
    private void OpenTemplateXmlEditorWindow()
    {
        WriteBackAll();
        var xml = WatchListXmlParser.SerializeTemplateList(_config.Templates);

        var editorVm = new TemplateXmlEditorViewModel(xml);

        // Build window title with template IDs
        var ids = _config.Templates.Select(t => t.ID).Where(id => !string.IsNullOrEmpty(id));
        editorVm.WindowTitle = $"Template XML Editor - {string.Join(", ", ids)}";

        var editorWindow = new Views.TemplateXmlEditorWindow(editorVm);
        if (Application.Current.MainWindow is { } mainWindow)
            editorWindow.Owner = mainWindow;

        var result = editorWindow.ShowDialog();
        if (result == true && editorVm.DialogAccepted)
        {
            try
            {
                var parsed = WatchListXmlParser.DeserializeTemplateList(editorVm.ResultXml);
                if (parsed is null)
                {
                    AddLog("Template XML editor: Root element was not <Templates>", LogSeverity.Error);
                    return;
                }

                // Replace templates in model
                _config.Templates.Clear();
                _config.Templates.AddRange(parsed);

                // Rebuild template tree
                TemplateRoots.Clear();
                TemplateRoots.Add(TreeNodeViewModel.FromTemplateList(_config.Templates));
                RebuildTemplateIds();
                _executor.LoadTemplates(_config.Templates);

                AddLog($"Templates updated via XML editor: {parsed.Count} template(s)", LogSeverity.Success);
            }
            catch (Exception ex)
            {
                AddLog($"Template XML editor apply failed: {ex.Message}", LogSeverity.Error);
            }
        }
    }

    [RelayCommand]
    private void AddTemplate()
    {
        var t = new TemplateConfig { ID = $"NewTemplate{_config.Templates.Count + 1}" };
        _config.Templates.Add(t);
        if (TemplateListRoot is not null)
        {
            var node = TreeNodeViewModel.FromTemplate(t);
            node.Parent = TemplateListRoot;
            TemplateListRoot.Children.Add(node);
            TemplateListRoot.RefreshDisplayText();
        }
        RebuildTemplateIds();
    }

    [RelayCommand]
    private void DeleteTemplate()
    {
        if (SelectedTemplateNode is null || SelectedTemplateNode.NodeKind == "TemplateList") return;

        // CRITICAL: Capture before Remove triggers SelectedItemChanged
        var target = SelectedTemplateNode;
        var parent = target.Parent;
        if (parent is null) return;

        if (parent.Children.Remove(target))
        {
            switch (parent.ModelObject)
            {
                case List<TemplateConfig> tl when target.ModelObject is TemplateConfig tc: tl.Remove(tc); break;
                case TemplateConfig tc when target.ModelObject is IActionNode a: tc.Children.Remove(a); break;
                case ActionGroupConfig ag when target.ModelObject is IActionNode a: ag.Children.Remove(a); break;
            }
            parent.RefreshDisplayText();
            SelectedTemplateNode = null;
            RebuildTemplateIds();
            return;
        }
        if (TemplateListRoot is not null && RemoveDeep(TemplateListRoot, target))
        { SelectedTemplateNode = null; RebuildTemplateIds(); }
    }

    [RelayCommand]
    private void AddGroupToTemplate()
    {
        var t = SelectedTemplateNode;
        if (t?.NodeKind is not ("Template" or "TemplateList" or "ActionGroup" or "Event")) return;
        if (t.NodeKind == "TemplateList") { AddTemplate(); return; }
        AddChildT(t, new ActionGroupConfig { Tag = "NewGroup", ExecutionType = ExecutionMode.Sequential });
    }

    [RelayCommand]
    private void AddActionToTemplate()
    {
        if (SelectedTemplateNode?.NodeKind is not ("Template" or "ActionGroup")) return;
        AddChildT(SelectedTemplateNode, new ActionConfig { Type = ActionType.RunCommand, Command = "cmd", Parameters = "/c echo hello" });
    }

    [RelayCommand]
    private void AddRefToTemplate()
    {
        if (SelectedTemplateNode?.NodeKind is not ("Template" or "ActionGroup")) return;
        AddChildT(SelectedTemplateNode, new RefConfig { TemplateID = AvailableTemplateIds.Count > 0 ? AvailableTemplateIds[0] : "" });
    }

    [RelayCommand]
    private void AddInitializeToTemplate()
    {
        if (SelectedTemplateNode?.NodeKind is not ("Template" or "ActionGroup")) return;
        AddChildT(SelectedTemplateNode, new InitializeConfig { Tag = "Params" });
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private void AddChild(TreeNodeViewModel parent, IActionNode child)
    {
        var n = TreeNodeViewModel.FromActionNode(child); n.Parent = parent;
        parent.Children.Add(n);
        switch (parent.ModelObject)
        {
            case EventConfig ev: ev.Children.Add(child); break;
            case ActionGroupConfig ag: ag.Children.Add(child); break;
        }
    }

    private void AddChildT(TreeNodeViewModel parent, IActionNode child)
    {
        var n = TreeNodeViewModel.FromActionNode(child); n.Parent = parent;
        parent.Children.Add(n);
        switch (parent.ModelObject)
        {
            case TemplateConfig tc: tc.Children.Add(child); break;
            case ActionGroupConfig ag: ag.Children.Add(child); break;
            case EventConfig ev: ev.Children.Add(child); break;
        }
    }

    private bool RemoveDeep(TreeNodeViewModel parent, TreeNodeViewModel target)
    {
        if (parent.Children.Remove(target))
        {
            switch (parent.ModelObject)
            {
                case WatchListConfig cfg when target.ModelObject is WatchItemConfig wi: cfg.WatchItems.Remove(wi); break;
                case WatchItemConfig wi when target.ModelObject is EventConfig e: wi.Events.Remove(e); break;
                case EventConfig ev when target.ModelObject is IActionNode a: ev.Children.Remove(a); break;
                case ActionGroupConfig ag when target.ModelObject is IActionNode a2: ag.Children.Remove(a2); break;
                case TemplateConfig tc when target.ModelObject is IActionNode a3: tc.Children.Remove(a3); break;
                case List<TemplateConfig> tl when target.ModelObject is TemplateConfig tc2: tl.Remove(tc2); break;
            }
            parent.RefreshDisplayText();
            return true;
        }
        foreach (var c in parent.Children) if (RemoveDeep(c, target)) return true;
        return false;
    }

    private void ApplyConfig(WatchListConfig config)
    {
        _config = config;
        _executor.LoadTemplates(config.Templates);
        _watcherManager.ApplyConfig(config);
        Application.Current?.Dispatcher.Invoke(() =>
        {
            // Single WatchList — replace the root's children, keep one root
            TreeRoots.Clear();
            TreeRoots.Add(TreeNodeViewModel.FromWatchList(_config));
            TemplateRoots.Clear();
            TemplateRoots.Add(TreeNodeViewModel.FromTemplateList(_config.Templates));
            RebuildTemplateIds();
            RebuildFilterOptions();
            LoadTokensFromConfig(config);
            ActiveWatchers = _watcherManager.ActiveWatcherCount;
            StatusMessage = $"{config.WatchItems.Count} WatchItems, {config.Templates.Count} Templates";
        });
    }

    /// <summary>
    /// Scans all Initialize nodes in the config for parameter files and loads
    /// their tokens into the shared TokenValues dictionary for UI display resolution.
    /// </summary>
    private void LoadTokensFromConfig(WatchListConfig config)
    {
        TreeNodeViewModel.TokenValues.Clear();
        foreach (var wi in config.WatchItems)
            foreach (var ev in wi.Events)
                LoadTokensFromChildren(ev.Children);
        foreach (var t in config.Templates)
            LoadTokensFromChildren(t.Children);

        // Refresh resolved text across all trees
        WatchListRoot?.RefreshResolvedTextRecursive();
        TemplateListRoot?.RefreshResolvedTextRecursive();
    }

    private void LoadTokensFromChildren(List<IActionNode> children)
    {
        foreach (var child in children)
        {
            if (child is InitializeConfig init && !string.IsNullOrWhiteSpace(init.ParameterFile))
            {
                try
                {
                    var entries = ParameterResolver.ParseParameterFile(init.ParameterFile);
                    foreach (var (key, value) in entries)
                    {
                        TreeNodeViewModel.TokenValues[key] = value;
                        if (key.StartsWith('_'))
                            TreeNodeViewModel.TokenValues[key[1..]] = value;
                    }
                }
                catch { }
            }
            else if (child is ActionGroupConfig ag)
            {
                LoadTokensFromChildren(ag.Children);
            }
        }
    }

    private void RebuildTemplateIds()
    {
        AvailableTemplateIds.Clear();
        foreach (var t in _config.Templates)
            if (!string.IsNullOrWhiteSpace(t.ID)) AvailableTemplateIds.Add(t.ID);
    }

    /// <summary>Rebuilds the available WatchItem tags and agent names for filter dropdowns.</summary>
    private void RebuildFilterOptions()
    {
        AvailableWatchItemTags.Clear();
        AvailableWatchItemTags.Add(""); // "All" option
        foreach (var wi in _config.WatchItems)
            if (!string.IsNullOrWhiteSpace(wi.Tag))
                AvailableWatchItemTags.Add(wi.Tag);

        AvailableAgentNames.Clear();
        AvailableAgentNames.Add(""); // "All" option
        foreach (var agent in RegisteredAgents)
            if (!string.IsNullOrWhiteSpace(agent.Name))
                AvailableAgentNames.Add(agent.Name);
    }

    // ═══════════════════════════════════════════════════════════════
    // F4: NODE PROGRESS HANDLER (maps IActionNode → TreeNodeViewModel)
    // ═══════════════════════════════════════════════════════════════

    private void OnNodeProgress(IActionNode node, string status)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            // Search in WatchList tree
            var treeNode = WatchListRoot?.FindByModel(node);
            if (treeNode is not null)
            {
                treeNode.ExecutionStatus = status;
                // Propagate aggregated status upward through parents
                treeNode.PropagateStatusUp();
                return;
            }
            // Search in Template tree (for Ref expansions)
            treeNode = TemplateListRoot?.FindByModel(node);
            if (treeNode is not null)
            {
                treeNode.ExecutionStatus = status;
                treeNode.PropagateStatusUp();
            }
        });
    }

    private void WriteBackAll()
    {
        void Recurse(TreeNodeViewModel n) { n.ApplyToModel(); foreach (var c in n.Children) Recurse(c); }
        if (WatchListRoot is not null) foreach (var c in WatchListRoot.Children) Recurse(c);
        if (TemplateListRoot is not null) foreach (var c in TemplateListRoot.Children) Recurse(c);
        RebuildTemplateIds();
    }

    private void OnConfigReloaded(WatchListConfig config) { AddLog("Hot-reloaded"); ApplyConfig(config); }
    private void OnLogEntry(PipelineLogEntry e)
    {
        var severity = e.Message.Contains("Failed", StringComparison.OrdinalIgnoreCase)
                    || e.Message.StartsWith("✗", StringComparison.Ordinal)
                    || e.Message.StartsWith("✖", StringComparison.Ordinal)
            ? LogSeverity.Error
            : e.Message.Contains("Success", StringComparison.OrdinalIgnoreCase)
                    || e.Message.StartsWith("✓", StringComparison.Ordinal)
                    || e.Message.StartsWith("✔", StringComparison.Ordinal)
              ? LogSeverity.Success
              : LogSeverity.Info;
        AddLog($"[{e.Category}] {e.Message}", severity);
    }

    private void OnOutputReceived(string agent, string line, string kind)
    {
        var severity = kind == "stderr" ? LogSeverity.Error : LogSeverity.Info;
        AddLog($"[{agent}:{kind}] {line}", severity);
    }

    private void OnStatusChanged(string agent, string status)
    {
        var severity = status.Contains("Failed", StringComparison.OrdinalIgnoreCase)
                    || status.Contains("Unreachable", StringComparison.OrdinalIgnoreCase)
            ? LogSeverity.Error
            : status.Contains("Ready", StringComparison.OrdinalIgnoreCase)
                    || status.Contains("online", StringComparison.OrdinalIgnoreCase)
              ? LogSeverity.Success
              : LogSeverity.Info;
        AddLog($"[{agent}] {status}", severity);
    }

    private void OnTriggerFired(string p, string f) => AddLog($"Trigger: {p} > {f}");

    // ── Log management ──────────────────────────────────────────────

    /// <summary>Copy all log entries to clipboard.</summary>
    [RelayCommand]
    private void CopyLog()
    {
        var sb = new StringBuilder();
        foreach (var e in LogEntries)
            sb.AppendLine($"[{e.Timestamp}] {e.Message}");
        if (sb.Length > 0)
            Clipboard.SetText(sb.ToString());
    }

    /// <summary>Copy only failed/error log entries to clipboard.</summary>
    [RelayCommand]
    private void CopyFailedLog()
    {
        var sb = new StringBuilder();
        foreach (var e in LogEntries.Where(e => e.Severity == LogSeverity.Error))
            sb.AppendLine($"[{e.Timestamp}] {e.Message}");
        if (sb.Length > 0)
            Clipboard.SetText(sb.ToString());
    }

    /// <summary>Clear all log entries.</summary>
    [RelayCommand]
    private void ClearLog()
    {
        LogEntries.Clear();
        FilteredLogEntries.Clear();
    }

    /// <summary>Toggle the log panel collapsed/expanded state.</summary>
    [RelayCommand]
    private void ToggleLogCollapse()
    {
        IsLogCollapsed = !IsLogCollapsed;
    }

    /// <summary>Toggle pausing live log updates.</summary>
    [RelayCommand]
    private void ToggleLogPause()
    {
        IsLogPaused = !IsLogPaused;
        if (!IsLogPaused)
            ApplyLogFilter(); // refresh filtered entries when resuming
    }

    /// <summary>Export log entries to a text file.</summary>
    [RelayCommand]
    private void ExportLog()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Text Files|*.txt|Log Files|*.log|All Files|*.*",
            FileName = $"ExecutionLog_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            Title = "Export Execution Log"
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new StringBuilder();
        foreach (var e in LogEntries)
            sb.AppendLine($"[{e.Timestamp}] [{e.Severity}] {e.Message}");
        File.WriteAllText(dlg.FileName, sb.ToString());
        AddLog($"Log exported to {dlg.FileName}", LogSeverity.Success);
    }

    private void AddLog(string msg, LogSeverity severity = LogSeverity.Info)
    {
        // Auto-detect severity from message content when using default
        if (severity == LogSeverity.Info)
        {
            if (msg.Contains("error", StringComparison.OrdinalIgnoreCase)
             || msg.Contains("failed", StringComparison.OrdinalIgnoreCase)
             || msg.Contains("✗", StringComparison.Ordinal)
             || msg.Contains("✖", StringComparison.Ordinal)
             || msg.StartsWith("[Action] X", StringComparison.Ordinal))
                severity = LogSeverity.Error;
            else if (msg.Contains("success", StringComparison.OrdinalIgnoreCase)
                  || msg.Contains("completed", StringComparison.OrdinalIgnoreCase)
                  || msg.Contains("✓", StringComparison.Ordinal)
                  || msg.Contains("✔", StringComparison.Ordinal))
                severity = LogSeverity.Success;
        }

        var entry = new LogEntryViewModel
        {
            Timestamp = DateTime.Now.ToString("HH:mm:ss"),
            Message = msg,
            Severity = severity
        };

        if (Application.Current?.Dispatcher.CheckAccess() == true)
        {
            LogEntries.Add(entry);
            while (LogEntries.Count > 5000) LogEntries.RemoveAt(0);
            AddToFilteredLog(entry);
        }
        else
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                LogEntries.Add(entry);
                while (LogEntries.Count > 5000) LogEntries.RemoveAt(0);
                AddToFilteredLog(entry);
            });
        }
    }

    private void AddToFilteredLog(LogEntryViewModel entry)
    {
        if (IsLogPaused) return;

        var hasTagFilter = !string.IsNullOrWhiteSpace(LogFilterTag);
        var hasAgentFilter = !string.IsNullOrWhiteSpace(LogFilterAgent);
        var hasSearchFilter = !string.IsNullOrWhiteSpace(LogSearchText);
        var hasSeverityFilter = LogLevelFilter != "All";

        if (PassesFilter(entry, hasTagFilter, hasAgentFilter, hasSearchFilter, hasSeverityFilter))
        {
            FilteredLogEntries.Add(entry);
            while (FilteredLogEntries.Count > 5000) FilteredLogEntries.RemoveAt(0);
        }
    }

    /// <summary>
    /// Scrolls the execution log to the last error entry and highlights it.
    /// Ensures the error is visible even if auto-scroll is disabled.
    /// </summary>
    private void ScrollLogToLastError()
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var lastError = FilteredLogEntries.LastOrDefault(e => e.Severity == LogSeverity.Error);
            lastError ??= LogEntries.LastOrDefault(e => e.Severity == LogSeverity.Error);

            if (lastError is not null)
            {
                // Signal the view to scroll — uses the existing auto-scroll mechanism
                // by temporarily ensuring the item is the last visible entry
                ScrollToLogEntry?.Invoke(lastError);
            }
        });
    }

    /// <summary>Raised when the log should scroll to a specific entry.</summary>
    public event Action<LogEntryViewModel>? ScrollToLogEntry;

    public void Dispose()
    {
        _vocabMonitor.ConfigReloaded -= OnConfigReloaded;
        _executor.LogEntry -= OnLogEntry;
        _executor.NodeProgress -= OnNodeProgress;
        _dispatcher.OutputReceived -= OnOutputReceived;
        _dispatcher.StatusChanged -= OnStatusChanged;
        _watcherManager.TriggerFired -= OnTriggerFired;
        _executionCts?.Dispose();
    }
}
