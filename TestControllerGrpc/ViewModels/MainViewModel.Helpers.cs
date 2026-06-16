using System.Collections.ObjectModel;
using System.Windows;
using TestControllerGrpc.Helpers;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Generic;

namespace TestControllerGrpc.ViewModels;

// ?? Helpers: BuildTree, RebuildTreeFromConfig, utility methods ???????
public sealed partial class MainViewModel
{
    // ?? Centralized tree rebuild ????????????????????????????????????

    /// <summary>
    /// Rebuilds both tree views and refreshes all dependent state.
    /// Must be called on UI thread.
    /// </summary>
    private void RebuildAllTrees()
    {
        TreeRoots.Clear();
        TreeRoots.Add(TreeNodeViewModel.FromWatchList(_config));
        TemplateRoots.Clear();
        TemplateRoots.Add(TreeNodeViewModel.FromTemplateList(_config.Templates));
        RebuildTemplateIds();
        RebuildFilterOptions();
        LoadTokensFromConfig(_config);
        ActiveWatchers = _watcherManager.ActiveWatcherCount;
        RefreshPipelinePermissions();
    }

    // ?? Tree search ????????????????????????????????????????????????

    [RelayCommand]
    private void ClearTreeSearch() => TreeSearchText = "";

    [RelayCommand]
    private void ClearTemplateSearch() => TemplateSearchText = "";

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

    private void ApplyTemplateSearch()
    {
        if (TemplateListRoot is null) return;
        if (string.IsNullOrWhiteSpace(TemplateSearchText))
        {
            SetTreeVisibilityRecursive(TemplateListRoot, true);
            return;
        }
        ApplyTreeSearchRecursive(TemplateListRoot, TemplateSearchText);
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

        var visible = selfMatch || anyChildMatch || node.NodeKind is NodeKinds.WatchList;
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

    // ?? Child addition helpers ??????????????????????????????????????

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
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                RebuildAllTrees();
                StatusMessage = $"{config.WatchItems.Count} watch {(config.WatchItems.Count == 1 ? "item" : "items")}, {config.Templates.Count} {(config.Templates.Count == 1 ? "template" : "templates")}";
            }
            catch (Exception ex)
            {
                _appLogger.Error("UI", "Failed to rebuild tree after ApplyConfig", ex);
            }
        });
    }

    /// <summary>
    /// Scans all Initialize nodes in the config for parameter files and loads
    /// their tokens into the shared TokenValues dictionary for UI display resolution.
    /// </summary>
    private void LoadTokensFromConfig(WatchListConfig config)
    {
        TreeNodeViewModel.TokenValues.Clear();

        // Load global variables file first (lowest priority — can be overridden by per-WatchItem Initialize files)
        if (!string.IsNullOrWhiteSpace(config.GlobalVariablesFile))
        {
            try
            {
                var entries = ParameterResolver.ParseParameterFile(config.GlobalVariablesFile);
                foreach (var (key, value) in entries)
                {
                    TreeNodeViewModel.TokenValues[key] = value;
                    if (key.StartsWith('_'))
                        TreeNodeViewModel.TokenValues[key[1..]] = value;
                }
            }
            catch (Exception ex)
            {
                _appLogger.Warn("Tokens", $"Failed to load global variables file '{config.GlobalVariablesFile}': {ex.Message}");
            }
        }

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
                catch (Exception ex)
                {
                    _appLogger.Warn("Tokens", $"Failed to load parameter file '{init.ParameterFile}': {ex.Message}");
                }
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

        // Include registered (connected) agents
        foreach (var agent in RegisteredAgents)
            if (!string.IsNullOrWhiteSpace(agent.Name))
                AvailableAgentNames.Add(agent.Name);

        // Also include agent names already used in the loaded WatchList XML
        // so Remote Command actions show the correct agent even before connecting
        var usedAgentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectAgentNamesFromChildren(
            _config.WatchItems.SelectMany(wi => wi.Events).SelectMany(ev => ev.Children),
            usedAgentNames);
        CollectAgentNamesFromChildren(
            _config.Templates.SelectMany(t => t.Children),
            usedAgentNames);

        foreach (var name in usedAgentNames)
            if (!AvailableAgentNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                AvailableAgentNames.Add(name);
    }

    private static void CollectAgentNamesFromChildren(
        IEnumerable<IActionNode> nodes, HashSet<string> names)
    {
        foreach (var node in nodes)
        {
            if (node is ActionConfig a && !string.IsNullOrWhiteSpace(a.AgentName))
                names.Add(a.AgentName);
            else if (node is ActionGroupConfig ag)
                CollectAgentNamesFromChildren(ag.Children, names);
        }
    }

    // ?? Node progress handler (maps IActionNode ? TreeNodeViewModel) ??

    private void OnNodeProgress(IActionNode node, string status)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            // Update tree node status
            var treeNode = WatchListRoot?.FindByModel(node);
            if (treeNode is not null)
            {
                treeNode.ExecutionStatus = status;
                treeNode.PropagateStatusUp();
            }
            else
            {
                treeNode = TemplateListRoot?.FindByModel(node);
                if (treeNode is not null)
                {
                    treeNode.ExecutionStatus = status;
                    treeNode.PropagateStatusUp();
                }
            }
        });
    }

    private void OnNodeFailed(IActionNode node, int exitCode, string errorMessage)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var treeNode = WatchListRoot?.FindByModel(node);
            if (treeNode is not null)
            {
                treeNode.LastExitCode = exitCode;
                treeNode.LastExecutionError = errorMessage;
                return;
            }
            treeNode = TemplateListRoot?.FindByModel(node);
            if (treeNode is not null)
            {
                treeNode.LastExitCode = exitCode;
                treeNode.LastExecutionError = errorMessage;
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

    private void OnConfigReloaded(WatchListConfig config)
    {
        if (_sessionManager.HasAnyActiveExecution)
        {
            // Differential reload � only update changed watchers, don't tear down running pipelines
            AddLog("Hot-reload (differential � executions active)");
            _watcherManager.ApplyDiff(config.WatchItems);
            _config = config;
            _executor.LoadTemplates(config.Templates);
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    RebuildAllTrees();
                    StatusMessage = $"{config.WatchItems.Count} watch {(config.WatchItems.Count == 1 ? "item" : "items")}, {config.Templates.Count} {(config.Templates.Count == 1 ? "template" : "templates")} (diff reload)";
                }
                catch (Exception ex)
                {
                    _appLogger.Error("UI", "Failed to rebuild tree during differential hot-reload", ex);
                }
            });
        }
        else
        {
            AddLog("Hot-reloaded");
            ApplyConfig(config);
        }
    }

    private void OnLogEntry(PipelineLogEntry e)
    {
        var severity = e.Message.Contains("Failed", StringComparison.OrdinalIgnoreCase)
                    || e.Message.Contains(LogIcons.Error, StringComparison.Ordinal)
            ? LogSeverity.Error
            : e.Message.Contains("Success", StringComparison.OrdinalIgnoreCase)
                    || e.Message.Contains(LogIcons.Success, StringComparison.Ordinal)
              ? LogSeverity.Success
              : LogSeverity.Info;
        AddLog($"[{e.Category}] {e.Message}", severity);

        // P2-1: feed the multi-session dashboard's per-session log buffer.
        // The executor stamps SessionId on tracked emissions; un-tagged
        // entries (e.g. legacy untracked Log()) are still surfaced via the
        // global UI log above.
        if (!string.IsNullOrEmpty(e.SessionId))
            _sessionManager.GetSession(e.SessionId)?.AddLogEntry(e);
    }

    private void OnOutputReceived(string agent, string line, string kind)
    {
        var severity = kind == "stderr" ? LogSeverity.Error : LogSeverity.Info;
        AddLog($"[{agent}:{kind}] {line}", severity);

        // P2-1: per-agent stdout/stderr is the canonical source of agent-
        // attributed log lines. Resolve agent -> owning session via the lock
        // manager and append to that session's buffer so the dashboard's
        // per-agent filter has something to filter on.
        var sessionId = _lockManager.GetLock(agent)?.SessionId;
        if (!string.IsNullOrEmpty(sessionId))
        {
            _sessionManager.GetSession(sessionId)?.AddLogEntry(
                new PipelineLogEntry(
                    DateTime.Now,
                    Category: kind,
                    Message: line,
                    AgentName: agent,
                    SessionId: sessionId,
                    Severity: kind == "stderr" ? "Error" : "Info"));
        }

        // Phase 1.13: publish a typed AgentOutputEvent so the dashboard
        // (and any future subscriber) can react in real time without
        // polling the per-session log buffer.
        _events.Publish(new AgentOutputEvent(
            Timestamp: DateTime.Now,
            AgentName: agent,
            SessionId: sessionId ?? "",
            Kind: kind,
            Line: line));
    }

    private readonly Dictionary<string, string> _lastAgentStatus = new(StringComparer.OrdinalIgnoreCase);

    private void OnStatusChanged(string agent, string status)
    {
        // Only log when the status actually changes for this agent to avoid noise
        if (_lastAgentStatus.TryGetValue(agent, out var previous) &&
            string.Equals(previous, status, StringComparison.OrdinalIgnoreCase))
            return;

        _lastAgentStatus[agent] = status;

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

    /// <summary>
    /// Called when a trigger file is parsed � merges all extracted key-value pairs
    /// into the shared TokenValues dictionary so the tree UI shows resolved text.
    /// </summary>
    private void OnTriggerParametersLoaded(string watchItemTag, Dictionary<string, string> parameters)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            foreach (var (key, value) in parameters)
            {
                TreeNodeViewModel.TokenValues[key] = value;
                if (key.StartsWith('_'))
                    TreeNodeViewModel.TokenValues[key[1..]] = value;
            }

            // Refresh resolved display text across all trees
            WatchListRoot?.RefreshResolvedTextRecursive();
            TemplateListRoot?.RefreshResolvedTextRecursive();

            AddLog($"[{watchItemTag}] Trigger parameters loaded ({parameters.Count} tokens)", LogSeverity.Success);
        });
    }

    private void OnTriggerMetadataParsed(string watchItemTag, string buildNumber, string dropLocation)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (WatchListRoot is null) return;
            foreach (var child in WatchListRoot.Children)
            {
                if (string.Equals(child.Tag, watchItemTag, StringComparison.OrdinalIgnoreCase)
                    && child.NodeKind == NodeKinds.WatchItem)
                {
                    child.LastBuildNumber = buildNumber;
                    child.LastDropLocation = dropLocation;
                    break;
                }
            }
        });
    }

    /// <summary>Opens the bundled help file in the default browser.</summary>
    [RelayCommand]
    private void OpenHelp()
    {
        var helpPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Help", "TestController_Help.html");
        if (System.IO.File.Exists(helpPath))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(helpPath) { UseShellExecute = true });
        else
            AddLog("Help file not found. Expected at: " + helpPath);
    }

    /// <summary>
    /// Resolves [Token] placeholders in AgentName fields across all Actions in the config
    /// so that the saved XML persists the actual resolved agent names.
    /// </summary>
    private static void ResolveAgentNamesInConfig(WatchListConfig config)
    {
        foreach (var wi in config.WatchItems)
            foreach (var ev in wi.Events)
                ResolveAgentNamesInChildren(ev.Children);
        foreach (var t in config.Templates)
            ResolveAgentNamesInChildren(t.Children);
    }

    private static void ResolveAgentNamesInChildren(List<IActionNode> children)
    {
        foreach (var child in children)
        {
            if (child is ActionConfig a && !string.IsNullOrWhiteSpace(a.AgentName))
            {
                var resolved = TreeNodeViewModel.ResolveTokens(a.AgentName);
                if (!string.Equals(a.AgentName, resolved, StringComparison.Ordinal))
                    a.AgentName = resolved;
            }
            else if (child is ActionGroupConfig ag)
            {
                ResolveAgentNamesInChildren(ag.Children);
            }
        }
    }
}
