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
    // ?? Tree search ?????????????????????????????????????????????????

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

    // ?? Node progress handler (maps IActionNode ? TreeNodeViewModel) ??

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

    private void OnConfigReloaded(WatchListConfig config)
    {
        if (_sessionManager.HasAnyActiveExecution)
        {
            // Differential reload — only update changed watchers, don't tear down running pipelines
            AddLog("Hot-reload (differential — executions active)");
            _watcherManager.ApplyDiff(config.WatchItems);
            _config = config;
            _executor.LoadTemplates(config.Templates);
            Application.Current?.Dispatcher.Invoke(() =>
            {
                TreeRoots.Clear();
                TreeRoots.Add(TreeNodeViewModel.FromWatchList(_config));
                TemplateRoots.Clear();
                TemplateRoots.Add(TreeNodeViewModel.FromTemplateList(_config.Templates));
                RebuildTemplateIds();
                RebuildFilterOptions();
                LoadTokensFromConfig(config);
                ActiveWatchers = _watcherManager.ActiveWatcherCount;
                StatusMessage = $"{config.WatchItems.Count} WatchItems, {config.Templates.Count} Templates (diff reload)";
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
    }

    private void OnOutputReceived(string agent, string line, string kind)
    {
        var severity = kind == "stderr" ? LogSeverity.Error : LogSeverity.Info;
        AddLog($"[{agent}:{kind}] {line}", severity);
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
