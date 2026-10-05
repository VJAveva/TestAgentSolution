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
        // Order matters: tokens must be loaded before contexts resolve, and session values must
        // land before the trees refresh, or the first paint shows predicted values for a real run.
        RefreshSessionValues();
        RefreshTemplateAutoContexts();
        foreach (var root in TreeRoots) root.RefreshResolvedTextRecursive();
        foreach (var root in TemplateRoots) root.RefreshResolvedTextRecursive();
        ActiveWatchers = _watcherManager.ActiveWatcherCount;
        RefreshPipelinePermissions();
        // Skip origin is a traversal result, so it only exists once the tree does.
        foreach (var root in TreeRoots) root.RecomputeSkipOrigins(SkipState.NotSkipped);
        foreach (var root in TemplateRoots) root.RecomputeSkipOrigins(SkipState.NotSkipped);
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

    // ── Expand all / Collapse all ──────────────────────────────────────

    [RelayCommand]
    private void ExpandAllWatchTree()
    {
        if (WatchListRoot is not null) SetExpandedRecursive(WatchListRoot, true);
    }

    [RelayCommand]
    private void CollapseAllWatchTree()
    {
        if (WatchListRoot is not null) SetExpandedRecursive(WatchListRoot, false);
    }

    [RelayCommand]
    private void ExpandAllTemplateTree()
    {
        if (TemplateListRoot is not null) SetExpandedRecursive(TemplateListRoot, true);
    }

    [RelayCommand]
    private void CollapseAllTemplateTree()
    {
        if (TemplateListRoot is not null) SetExpandedRecursive(TemplateListRoot, false);
    }

    private static void SetExpandedRecursive(TreeNodeViewModel node, bool expanded)
    {
        node.IsExpanded = expanded;
        foreach (var child in node.Children)
            SetExpandedRecursive(child, expanded);
    }

    // ?? Child addition helpers ??????????????????????????????????????

    /// <summary>
    /// Adds <paramref name="child"/> under <paramref name="parent"/> in both the tree and the
    /// underlying model, and selects the new node so it auto-expands its ancestors and scrolls
    /// into view (see <see cref="TreeNodeViewModel.ExpandAncestors"/> /
    /// <see cref="Views.Behaviors.TreeViewItemBehavior"/>).
    /// </summary>
    private TreeNodeViewModel AddChild(TreeNodeViewModel parent, IActionNode child)
    {
        var n = TreeNodeViewModel.FromActionNode(child); n.Parent = parent;
        parent.Children.Add(n);
        switch (parent.ModelObject)
        {
            case EventConfig ev: ev.Children.Add(child); break;
            case ActionGroupConfig ag: ag.Children.Add(child); break;
        }
        n.IsSelected = true;
        return n;
    }

    private TreeNodeViewModel AddChildT(TreeNodeViewModel parent, IActionNode child)
    {
        var n = TreeNodeViewModel.FromActionNode(child); n.Parent = parent;
        parent.Children.Add(n);
        switch (parent.ModelObject)
        {
            case TemplateConfig tc: tc.Children.Add(child); break;
            case ActionGroupConfig ag: ag.Children.Add(child); break;
            case EventConfig ev: ev.Children.Add(child); break;
        }
        n.IsSelected = true;
        return n;
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
                // Templates arrive with the config, which loads after the view model is built.
                RefreshTemplateIds();
                StatusMessage = $"{config.WatchItems.Count} watch {(config.WatchItems.Count == 1 ? "item" : "items")}, {config.Templates.Count} {(config.Templates.Count == 1 ? "template" : "templates")}";
            }
            catch (Exception ex)
            {
                _appLogger.Error("UI", "Failed to rebuild tree after ApplyConfig", ex);
            }
        });
    }

    /// <summary>
    /// Scans all Initialize nodes in the config for parameter files and loads their tokens into
    /// that pipeline's token scope for UI display resolution.
    /// </summary>
    private void LoadTokensFromConfig(WatchListConfig config)
    {
        // Taken BEFORE the clear: a file caught mid-save parses as garbage, and without a rollback
        // the clear-then-reload would leave every label reading "(not set)".
        var previous = TreeNodeViewModel.SnapshotTokens();
        TreeNodeViewModel.ClearTokenScopes();

        // Every node object is about to be replaced, and the pipelines a context named may be gone.
        TemplateRunContext.ClearAll();
        _selection.Reset();

        // Global variables belong to the SHARED scope: every pipeline inherits them, and a
        // per-pipeline Initialize may still override any key.
        if (!string.IsNullOrWhiteSpace(config.GlobalVariablesFile))
        {
            try
            {
                foreach (var (key, value) in ParameterResolver.ParseParameterFile(config.GlobalVariablesFile))
                {
                    TreeNodeViewModel.SetToken(TreeNodeViewModel.SharedScope, key, value, TokenLayer.Global);
                    if (key.StartsWith('_'))
                        TreeNodeViewModel.SetToken(TreeNodeViewModel.SharedScope, key[1..], value, TokenLayer.Global);
                }
            }
            catch (Exception ex)
            {
                TreeNodeViewModel.RestoreScopeFrom(previous, TreeNodeViewModel.SharedScope);
                _appLogger.Warn("Tokens",
                    $"Global variables file '{config.GlobalVariablesFile}' could not be read ({ex.Message}). "
                    + "Keeping the last good values; will retry on the next change.");
            }
        }

        foreach (var wi in config.WatchItems)
            foreach (var ev in wi.Events)
                LoadTokensFromChildren(ev.Children, wi.Tag, previous);

        // Templates are deliberately NOT loaded: a template has no settings of its own, so any value
        // previewed against it would be a guess at which pipeline will run it.

        WatchParameterFiles(config);

        // Refresh resolved text across all trees
        WatchListRoot?.RefreshResolvedTextRecursive();
        TemplateListRoot?.RefreshResolvedTextRecursive();
    }

    /// <summary>
    /// Keeps the parameter-file watch set in step with the config, so editing a value in
    /// pipeline-config.json re-resolves every label without a restart.
    /// </summary>
    private void WatchParameterFiles(WatchListConfig config)
    {
        _parameterFileMonitor ??= CreateParameterFileMonitor();

        var files = new List<string?>();
        if (!string.IsNullOrWhiteSpace(config.GlobalVariablesFile))
            files.Add(config.GlobalVariablesFile);
        foreach (var wi in config.WatchItems)
            foreach (var init in ParameterResolver.CollectInitializeNodes(wi))
                files.Add(init.ParameterFile);

        _parameterFileMonitor.Watch(files);
    }

    private ParameterFileMonitor CreateParameterFileMonitor()
    {
        var monitor = new ParameterFileMonitor();
        monitor.ParametersChanged += () =>
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (_config is null) return;
                LoadTokensFromConfig(_config);
                RefreshTokenDisplay();
                AddLog("Parameter file changed - refreshed resolved values.", LogSeverity.Info);
            });
        return monitor;
    }

    private void LoadTokensFromChildren(
        List<IActionNode> children, string pipelineTag, TreeNodeViewModel.TokenSnapshot previous)
    {
        foreach (var child in children)
        {
            if (child is InitializeConfig init && !string.IsNullOrWhiteSpace(init.ParameterFile))
            {
                try
                {
                    // Layered loader, not the CSV parser: a JSON config parsed as CSV yields whole
                    // lines as keys, so every [Token] in the tree would render unresolved.
                    var ctx = new PipelineExecutionContext { WatchItemTag = pipelineTag };
                    if (init.ParameterFile.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        // Try*, not LoadJsonConfig: a malformed or half-written file returns false
                        // rather than throwing, so the plain loader would silently yield no values.
                        if (!ParameterResolver.TryLoadJsonConfig(ctx, init.ParameterFile, init.Profile, pipelineTag))
                        {
                            TreeNodeViewModel.RestoreScopeFrom(previous, pipelineTag);
                            _appLogger.Warn("Tokens",
                                $"Parameter file '{init.ParameterFile}' could not be read (missing, or invalid JSON "
                                + $"- it may have been caught mid-save). Keeping the last good values for "
                                + $"'{pipelineTag}'; will retry on the next change.");
                            continue;
                        }
                    }
                    else
                    {
                        ParameterResolver.LoadParameterFile(ctx, init.ParameterFile);
                    }

                    // Each key carries the rank it won at, so a tooltip can name the layer the
                    // value actually came from rather than guessing from the file it was read in.
                    foreach (var entry in ctx.Parameters)
                    {
                        var rank = ctx.ParameterRanks.TryGetValue(entry.Key, out var r)
                            ? (ParameterRank)r
                            : ParameterRank.ParameterFile;
                        TreeNodeViewModel.SetToken(pipelineTag, entry.Key, entry.Value, TokenDisplay.LayerFromRank(rank));
                    }
                }
                catch (Exception ex)
                {
                    // Only this pipeline rolls back - a broken file must not blank the other eight.
                    TreeNodeViewModel.RestoreScopeFrom(previous, pipelineTag);
                    _appLogger.Warn("Tokens",
                        $"Parameter file '{init.ParameterFile}' could not be read ({ex.Message}). "
                        + $"Keeping the last good values for '{pipelineTag}'; will retry on the next change.");
                }
            }
            else if (child is ActionGroupConfig ag)
            {
                LoadTokensFromChildren(ag.Children, pipelineTag, previous);
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

        FilterAgentNamesByPipeline(LogFilterTag);
    }

    /// <summary>
    /// Rebuilds <see cref="AvailableAgentNames"/> for the log filter. When a pipeline
    /// (WatchItem tag) is selected, the list narrows to agents used within that pipeline;
    /// otherwise it shows all registered and WatchList-referenced agents.
    /// </summary>
    private void FilterAgentNamesByPipeline(string? pipelineTag)
    {
        var previousAgent = LogFilterAgent;

        AvailableAgentNames.Clear();
        AvailableAgentNames.Add(""); // "All" option

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(pipelineTag))
        {
            // No pipeline selected: include registered (connected) agents...
            foreach (var agent in RegisteredAgents)
                if (!string.IsNullOrWhiteSpace(agent.Name))
                    names.Add(agent.Name);

            // ...and agent names referenced anywhere in the loaded WatchList / Templates.
            CollectAgentNamesFromChildren(
                _config.WatchItems.SelectMany(wi => wi.Events).SelectMany(ev => ev.Children),
                names);
            CollectAgentNamesFromChildren(
                _config.Templates.SelectMany(t => t.Children),
                names);
        }
        else
        {
            // Pipeline selected: only agents used within WatchItems carrying that tag.
            var matching = _config.WatchItems
                .Where(wi => string.Equals(wi.Tag, pipelineTag, StringComparison.OrdinalIgnoreCase));
            CollectAgentNamesFromChildren(
                matching.SelectMany(wi => wi.Events).SelectMany(ev => ev.Children),
                names);
        }

        foreach (var name in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            AvailableAgentNames.Add(name);

        // Drop a stale agent selection that no longer belongs to the chosen pipeline.
        if (!string.IsNullOrEmpty(previousAgent) &&
            !AvailableAgentNames.Contains(previousAgent, StringComparer.OrdinalIgnoreCase))
        {
            LogFilterAgent = "";
        }
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

    /// <summary>
    /// Skip state is shown on the row itself, so there is deliberately no status-bar message here - the tree
    /// is the feedback.
    /// </summary>
    public void SetNodeSkip(TreeNodeViewModel node, bool skip, string? reason = null)
    {
        if (node.ModelObject is not ISkippableNode) return;

        // Set the reason first: OnSkipChanged triggers the cascade that paints the row.
        node.SkipReason = skip ? reason ?? "" : "";
        node.Skip = skip;
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
    /// Called when a trigger file is parsed - merges the extracted key-value pairs into THAT
    /// pipeline's token scope so the tree UI shows resolved text for it alone.
    /// </summary>
    private void OnTriggerParametersLoaded(string watchItemTag, Dictionary<string, string> parameters)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            foreach (var (key, value) in parameters)
            {
                TreeNodeViewModel.SetToken(watchItemTag, key, value, TokenLayer.Trigger);
                if (key.StartsWith('_'))
                    TreeNodeViewModel.SetToken(watchItemTag, key[1..], value, TokenLayer.Trigger);
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
}
