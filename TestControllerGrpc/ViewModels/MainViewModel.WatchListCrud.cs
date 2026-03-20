using System.Windows;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.ViewModels;

// ?? WatchList CRUD (Add/Delete/Move WatchItem/Event/Action/Group/Ref/Init) ??
public sealed partial class MainViewModel
{
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
        IsDirty = true;
        AddLog($"Added: {tag}");
    }

    [RelayCommand]
    private void AddChildNode()
    {
        if (SelectedNode is null) return;
        if (SelectedNode.NodeKind == NodeKinds.WatchList) { AddWatchItem(); return; }
        if (SelectedNode.NodeKind == NodeKinds.WatchItem)
        {
            var ev = new EventConfig { Type = "Renamed", ExecutionType = ExecutionMode.Sequential };
            if (SelectedNode.ModelObject is WatchItemConfig wi) wi.Events.Add(ev);
            var n = TreeNodeViewModel.FromEvent(ev); n.Parent = SelectedNode;
            SelectedNode.Children.Add(n);
        }
        else if (SelectedNode.NodeKind is NodeKinds.Event or NodeKinds.ActionGroup)
        {
            var g = new ActionGroupConfig { Tag = "NewGroup", ExecutionType = ExecutionMode.Sequential };
            AddChild(SelectedNode, g);
        }
    }

    [RelayCommand]
    private void AddActionToGroup()
    {
        if (SelectedNode?.NodeKind is not (NodeKinds.ActionGroup or NodeKinds.Event)) return;
        AddChild(SelectedNode, new ActionConfig { Type = ActionType.RunCommand, Command = "cmd", Parameters = "/c echo hello" });
    }

    [RelayCommand]
    private void AddRefToGroup()
    {
        if (SelectedNode?.NodeKind is not (NodeKinds.ActionGroup or NodeKinds.Event)) return;
        AddChild(SelectedNode, new RefConfig { TemplateID = AvailableTemplateIds.Count > 0 ? AvailableTemplateIds[0] : "" });
    }

    /// <summary>Adds an Initialize node to the selected Event or ActionGroup in the WatchList tree.</summary>
    [RelayCommand]
    private void AddInitializeToGroup()
    {
        if (SelectedNode?.NodeKind is not (NodeKinds.ActionGroup or NodeKinds.Event)) return;
        AddChild(SelectedNode, new InitializeConfig { Tag = "Params", ParameterFile = "" });
        AddLog("Added Initialize node");
    }

    /// <summary>Adds an ActionGroup to the selected WatchItem, Event, or ActionGroup in the WatchList tree.</summary>
    [RelayCommand]
    private void AddActionGroup()
    {
        if (SelectedNode is null) return;
        if (SelectedNode.NodeKind is NodeKinds.WatchItem)
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
        if (SelectedNode.NodeKind is NodeKinds.Event or NodeKinds.ActionGroup)
        {
            var ag = new ActionGroupConfig { Tag = "NewActionGroup", ExecutionType = ExecutionMode.Sequential, FailAndContinue = true };
            AddChild(SelectedNode, ag);
            SelectedNode.IsExpanded = true;
            AddLog("Added ActionGroup");
            return;
        }
        if (SelectedNode.NodeKind is NodeKinds.Template)
        {
            var ag = new ActionGroupConfig { Tag = "NewActionGroup", ExecutionType = ExecutionMode.Sequential, FailAndContinue = true };
            AddChildT(SelectedNode, ag);
            SelectedNode.IsExpanded = true;
            AddLog("Added ActionGroup to Template");
            return;
        }
    }

    [RelayCommand]
    private void DeleteSelectedNode()
    {
        if (SelectedNode is null) return;
        if (SelectedNode.NodeKind is NodeKinds.WatchList or NodeKinds.TemplateList) return;

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
            IsDirty = true;
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
        IsDirty = true;
        AddLog("Changes applied");
    }

    // ?? Move ????????????????????????????????????????????????????????

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
        if (node.NodeKind is NodeKinds.WatchList or NodeKinds.TemplateList or NodeKinds.Initialize) return false;
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
        if (node.NodeKind is NodeKinds.WatchList or NodeKinds.TemplateList) return;
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
}
