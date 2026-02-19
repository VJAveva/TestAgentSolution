using System.Collections.ObjectModel;
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

    // ── Execution state ─────────────────────────────────────────────
    [ObservableProperty] private bool _isExecuting;
    private CancellationTokenSource? _executionCts;

    /// <summary>TreeRoots[0] is the single "WatchList" root node — always present.</summary>
    public ObservableCollection<TreeNodeViewModel> TreeRoots { get; } = new();
    public ObservableCollection<TreeNodeViewModel> TemplateRoots { get; } = new();
    public ObservableCollection<string> LogEntries { get; } = new();
    public ObservableCollection<AgentInfoViewModel> RegisteredAgents { get; } = new();
    public ObservableCollection<string> AvailableTemplateIds { get; } = new();

    private WatchListConfig _config = new();

    private TreeNodeViewModel? WatchListRoot => TreeRoots.Count > 0 ? TreeRoots[0] : null;
    private TreeNodeViewModel? TemplateListRoot => TemplateRoots.Count > 0 ? TemplateRoots[0] : null;

    partial void OnSelectedNodeChanged(TreeNodeViewModel? value)
    {
        if (value is not null)
        {
            ActiveEditNode = value;
            // Auto-close XML editor when selection changes
            IsXmlEditorOpen = false;
            XmlEditorStatus = "";
        }
    }

    // Note: TemplateTree ActiveEditNode is now set from code-behind
    // after _initialLayoutComplete guard, to prevent startup auto-selection override.
    partial void OnSelectedTemplateNodeChanged(TreeNodeViewModel? value) { }

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
    }

    private async Task TestAllAgentsAsync()
    {
        AddLog($"Testing {RegisteredAgents.Count} agent(s)...");
        var tasks = RegisteredAgents.Select(TestSingleAgentAsync).ToArray();
        await Task.WhenAll(tasks);
        var online = RegisteredAgents.Count(a => a.ConnectionStatus == "Online");
        AddLog($"Agent check complete: {online}/{RegisteredAgents.Count} online");
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
        });
    }

    private void OnAgentHeartbeat(string name, TestAgentGrpc.AgentState state, TestAgentGrpc.ResourceMetrics? metrics)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var existing = RegisteredAgents.FirstOrDefault(a =>
                string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is null) return;

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
            existing.UpdateDetailLine();
        });
    }

    // ── Move ────────────────────────────────────────────────────────

    [RelayCommand] private void MoveUp() { if (SelectedNode is not null) MoveNode(SelectedNode, -1); }
    [RelayCommand] private void MoveDown() { if (SelectedNode is not null) MoveNode(SelectedNode, +1); }
    [RelayCommand] private void MoveTemplateUp() { if (SelectedTemplateNode is not null) MoveNode(SelectedTemplateNode, -1); }
    [RelayCommand] private void MoveTemplateDown() { if (SelectedTemplateNode is not null) MoveNode(SelectedTemplateNode, +1); }

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
    // F2: INLINE XML EDITOR  (in WatchItem property panel)
    // ═══════════════════════════════════════════════════════════════

    [RelayCommand]
    private void ToggleXmlEditor()
    {
        if (SelectedNode?.NodeKind != "WatchItem") return;
        if (!IsXmlEditorOpen)
        {
            // Opening — serialize current WatchItem to XML
            WriteBackAll();
            if (SelectedNode.ModelObject is WatchItemConfig wi)
                XmlEditorText = WatchListXmlParser.SerializeWatchItem(wi);
            XmlEditorStatus = "";
        }
        IsXmlEditorOpen = !IsXmlEditorOpen;
    }

    [RelayCommand]
    private void ApplyXmlEditor()
    {
        if (SelectedNode?.NodeKind != "WatchItem") return;
        if (SelectedNode.ModelObject is not WatchItemConfig oldWi) return;
        try
        {
            var parsed = WatchListXmlParser.DeserializeWatchItem(XmlEditorText);
            if (parsed is null) { XmlEditorStatus = "Error: Root element must be <WatchItem>."; return; }

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
            AddLog($"WatchItem updated via XML editor: {parsed.Tag}");
        }
        catch (Exception ex) { XmlEditorStatus = $"XML error: {ex.Message}"; }
    }

    [RelayCommand]
    private void RevertXmlEditor()
    {
        if (SelectedNode?.ModelObject is WatchItemConfig wi)
        {
            WriteBackAll();
            XmlEditorText = WatchListXmlParser.SerializeWatchItem(wi);
            XmlEditorStatus = "Reverted to saved.";
        }
    }

    // Legacy popup editor — still available from context menu
    [RelayCommand]
    private void EditWatchItemXml()
    {
        // Open inline editor instead of popup
        if (SelectedNode?.NodeKind != "WatchItem") return;
        if (!IsXmlEditorOpen) ToggleXmlEditor();
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
        AddLog($"Triggered Event: {ev.Type} on {wiConfig?.Tag ?? "?"}");

        try
        {
            await _executor.ExecuteEventAsync(ev, ctx, _executionCts.Token);
            eventNode.ExecutionStatus = "Success";
            AddLog($"Event completed: {ev.Type}");
        }
        catch (OperationCanceledException)
        {
            eventNode.SetStatusRecursive("Failed");
            AddLog($"Event cancelled: {ev.Type}");
        }
        catch (Exception ex)
        {
            eventNode.ExecutionStatus = "Failed";
            AddLog($"Event failed: {ev.Type} — {ex.Message}");
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
                    if (evNode is not null) evNode.ExecutionStatus = "Success";
                }
                catch (Exception ex)
                {
                    allSuccess = false;
                    if (evNode is not null) evNode.ExecutionStatus = "Failed";
                    AddLog($"Event failed: {ev.Type} — {ex.Message}");
                }
            }
            wiNode.ExecutionStatus = allSuccess ? "Success" : "Failed";
            AddLog($"WatchItem {(allSuccess ? "completed" : "completed with errors")}: {wi.Tag}");
        }
        catch (OperationCanceledException)
        {
            wiNode.SetStatusRecursive("Failed");
            AddLog($"WatchItem cancelled: {wi.Tag}");
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
                return;
            }
            // Search in Template tree (for Ref expansions)
            treeNode = TemplateListRoot?.FindByModel(node);
            treeNode?.SetStatusRecursive(status == "Running" ? status : treeNode.ExecutionStatus);
        });
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
                case ActionGroupConfig ag when target.ModelObject is IActionNode a2: ag.Children.Remove(a2); break;
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
            TreeRoots.Add(TreeNodeViewModel.FromWatchList(config));
            TemplateRoots.Clear();
            TemplateRoots.Add(TreeNodeViewModel.FromTemplateList(config.Templates));
            RebuildTemplateIds();
            ActiveWatchers = _watcherManager.ActiveWatcherCount;
            StatusMessage = $"{config.WatchItems.Count} WatchItems, {config.Templates.Count} Templates";
        });
    }

    private void RebuildTemplateIds()
    {
        AvailableTemplateIds.Clear();
        foreach (var t in _config.Templates)
            if (!string.IsNullOrWhiteSpace(t.ID)) AvailableTemplateIds.Add(t.ID);
    }

    private void WriteBackAll()
    {
        void Recurse(TreeNodeViewModel n) { n.ApplyToModel(); foreach (var c in n.Children) Recurse(c); }
        if (WatchListRoot is not null) foreach (var c in WatchListRoot.Children) Recurse(c);
        if (TemplateListRoot is not null) foreach (var c in TemplateListRoot.Children) Recurse(c);
        RebuildTemplateIds();
    }

    private void OnConfigReloaded(WatchListConfig config) { AddLog("Hot-reloaded"); ApplyConfig(config); }
    private void OnLogEntry(PipelineLogEntry e) => AddLog($"[{e.Category}] {e.Message}");
    private void OnOutputReceived(string a, string l, string k) => AddLog($"[{a}:{k}] {l}");
    private void OnStatusChanged(string a, string s) => AddLog($"[{a}] {s}");
    private void OnTriggerFired(string p, string f) => AddLog($"Trigger: {p} > {f}");

    private void AddLog(string msg)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        if (Application.Current?.Dispatcher.CheckAccess() == true)
        { LogEntries.Add(entry); while (LogEntries.Count > 5000) LogEntries.RemoveAt(0); }
        else Application.Current?.Dispatcher.InvokeAsync(() =>
        { LogEntries.Add(entry); while (LogEntries.Count > 5000) LogEntries.RemoveAt(0); });
    }

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
