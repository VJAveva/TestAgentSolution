using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Editing;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.ViewModels;

// ContextMenu_Requirement.md P5: the missing structural edits for groups and actions (2.5 - 2.7). Each runs through
// the P4 rights check and the P3 undoable edit scope. Keyboard versions act on ActiveEditNode (rule 10).
public sealed partial class MainViewModel
{
    /// <summary>Copied node as XML. In-app only: actions can carry credentials, so nothing goes to the OS clipboard.</summary>
    private string? _nodeClipboard;

    public bool HasNodeClipboard => _nodeClipboard is not null;

    /// <summary>Raised when the view should put the cursor in the Tag field of Node Properties (Rename, F2).</summary>
    public event Action? RenameRequested;

    private static List<IActionNode>? ChildListOf(TreeNodeViewModel? parent) => parent?.ModelObject switch
    {
        EventConfig ev => ev.Children,
        ActionGroupConfig ag => ag.Children,
        TemplateConfig tc => tc.Children,
        _ => null,
    };

    /// <summary>The node's own list and index, when it is a real (non-mirrored) group or action.</summary>
    private static bool TryLocate(TreeNodeViewModel? node, out List<IActionNode> list, out int index)
    {
        list = null!;
        index = -1;
        if (node is null || node.IsRefExpansion || node.ModelObject is not IActionNode model) return false;
        if (ChildListOf(node.Parent) is not { } l) return false;
        list = l;
        index = l.IndexOf(model);
        return index >= 0;
    }

    /// <summary>Rebuilds a parent's rows from its model list and selects one of them.</summary>
    private void RefreshChildren(TreeNodeViewModel parent, int selectIndex)
    {
        if (ChildListOf(parent) is not { } list) return;
        var templates = TreeNodeViewModel.TemplateIndex(_config.Templates);
        parent.Children.Clear();
        foreach (var child in list)
        {
            var row = TreeNodeViewModel.FromActionNode(child, templates);
            row.Parent = parent;
            parent.Children.Add(row);
        }
        parent.IsExpanded = true;
        parent.RefreshDisplayText();
        var root = TreeNodeViewModel.RootOf(parent);
        root.RefreshResolvedTextRecursive();
        root.RecomputeSkipOrigins(SkipState.NotSkipped);
        if (selectIndex >= 0 && selectIndex < parent.Children.Count) parent.Children[selectIndex].IsSelected = true;
    }

    private void EditInParent(TreeNodeViewModel node, string label, Func<List<IActionNode>, int, int> edit)
    {
        if (!TryLocate(node, out var list, out var index) || node.Parent is not { } parent) return;
        if (!EnsureCanEdit(node)) return;
        int select;
        using (BeginEdit(label)) select = edit(list, index);
        RefreshChildren(parent, select);
        _executor.LoadTemplates(_config.Templates);
    }

    public void DuplicateNode(TreeNodeViewModel node) =>
        EditInParent(node, "Duplicate", NodeEdits.Duplicate);

    public void AddSibling(TreeNodeViewModel node, bool group, bool above) =>
        EditInParent(node, group ? "Add ActionGroup" : "Add Action", (list, i) =>
            NodeEdits.InsertSibling(list, i, group
                ? new ActionGroupConfig { Tag = "NewActionGroup", ExecutionType = ExecutionMode.Sequential, FailAndContinue = true }
                : _suggestions.NewActionDefaults(ActionType.RunCommand), above));

    public void WrapInGroup(TreeNodeViewModel node) =>
        EditInParent(node, "Wrap in group", (list, i) => { NodeEdits.Wrap(list, i); return i; });

    public void UngroupNode(TreeNodeViewModel node) =>
        EditInParent(node, "Ungroup", (list, i) => NodeEdits.Ungroup(list, i) ? i : -1);

    public void CopyNode(TreeNodeViewModel node)
    {
        if (node.ModelObject is not IActionNode model || node.NodeKind is not (NodeKinds.ActionGroup or NodeKinds.Action)) return;
        _nodeClipboard = NodeEdits.ToXml(model);
        OnPropertyChanged(nameof(HasNodeClipboard));
        AddLog($"Copied: {node.DisplayText}");
    }

    /// <summary>Pastes inside a group (last child) or below an action.</summary>
    public void PasteNode(TreeNodeViewModel node)
    {
        if (_nodeClipboard is not { } xml) return;
        if (node.NodeKind is NodeKinds.ActionGroup or NodeKinds.Event or NodeKinds.Template && !node.IsRefExpansion)
        {
            if (ChildListOf(node) is not { } inside || !EnsureCanEdit(node)) return;
            using (BeginEdit("Paste")) inside.Add(NodeEdits.FromXml(xml));
            RefreshChildren(node, inside.Count - 1);
            return;
        }
        EditInParent(node, "Paste", (list, i) => NodeEdits.InsertSibling(list, i, NodeEdits.FromXml(xml), above: false));
    }

    public void SaveGroupAsTemplate(TreeNodeViewModel node)
    {
        if (node.ModelObject is not ActionGroupConfig group || node.IsRefExpansion) return;
        if (!EnsureCanEdit(EditTarget.Template)) return;
        TemplateConfig template;
        using (BeginEdit("Save group as template"))
        {
            template = NodeEdits.GroupAsTemplate(group, _config.Templates.Select(t => t.ID));
            _config.Templates.Add(template);
        }
        if (TemplateListRoot is { } root)
        {
            var row = TreeNodeViewModel.FromTemplate(template, TreeNodeViewModel.TemplateIndex(_config.Templates));
            row.Parent = root;
            root.Children.Add(row);
            root.RefreshDisplayText();
        }
        RebuildTemplateIds();
        _executor.LoadTemplates(_config.Templates);
        AddLog($"Saved group '{group.Tag}' as template '{template.ID}'");
    }

    public void RequestRename(TreeNodeViewModel node)
    {
        if (node.IsRefExpansion || !EnsureCanEdit(node)) return;
        RenameRequested?.Invoke();
    }

    // Keyboard (tree focus only): the node last activated in either tree.
    [RelayCommand] private void DuplicateActive() { if (ActiveEditNode is { } n) DuplicateNode(n); }
    [RelayCommand] private void CopyActive() { if (ActiveEditNode is { } n) CopyNode(n); }
    [RelayCommand] private void PasteActive() { if (ActiveEditNode is { } n) PasteNode(n); }
    [RelayCommand] private void RenameActive() { if (ActiveEditNode is { } n) RequestRename(n); }
    [RelayCommand] private void MoveActiveUp() { if (ActiveEditNode is { } n) MoveNode(n, -1); }
    [RelayCommand] private void MoveActiveDown() { if (ActiveEditNode is { } n) MoveNode(n, +1); }

    /// <summary>F5 on a tree: run the selected node, after a confirm (rule 10).</summary>
    [RelayCommand]
    private void RunActiveWithConfirm()
    {
        if (ActiveEditNode is not { } node) return;
        System.Windows.Input.ICommand? command = node.NodeKind switch
        {
            NodeKinds.WatchItem => TriggerWatchItemCommand,
            NodeKinds.Event => TriggerEventCommand,
            NodeKinds.ActionGroup => ExecuteGroupCommand,
            NodeKinds.Action => ExecuteSingleActionCommand,
            NodeKinds.Template => ExecuteTemplateCommand,
            _ => null,
        };
        if (command is null || !command.CanExecute(null)) return;
        if (Views.Dialogs.ThemedMessageBox.Show($"Run '{node.DisplayText}'?", "Run",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes) return;
        command.Execute(null);
    }

    /// <summary>Ctrl+Shift+F5: Trigger All, always after a confirm (rule 10).</summary>
    [RelayCommand]
    private void TriggerAllWithConfirm()
    {
        if (!TriggerAllWatchItemsCommand.CanExecute(null)) return;
        var count = _config.WatchItems.Count(w => w.IsEnabled);
        if (Views.Dialogs.ThemedMessageBox.Show($"Run all {count} enabled pipelines now?", "Trigger All",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes) return;
        TriggerAllWatchItemsCommand.Execute(null);
    }
}
