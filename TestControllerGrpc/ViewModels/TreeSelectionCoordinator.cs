namespace TestControllerGrpc.ViewModels;

/// <summary>
/// Decides which tree owns the active edit/execute target.
/// </summary>
/// <remarks>
/// Extracted from MainViewModel so the rule is testable without an STA WPF app and a full DI
/// container. It exists because two independent faults made a node click act on a different node:
/// <list type="number">
///   <item>MainViewModel switched context only inside the <c>[ObservableProperty]</c> change
///     callbacks, which do not fire when the SAME node is re-selected - so returning to a template
///     node after visiting the WatchList tree left the WatchList active.</item>
///   <item>WPF does not select a TreeViewItem on right-click, so the context menu was built for
///     whatever was selected before.</item>
/// </list>
/// Both surfaced as "selecting a node inside a template sometimes does not work". Activation is
/// therefore UNCONDITIONAL: re-activating the same node still re-asserts its tree.
/// </remarks>
public sealed class TreeSelectionCoordinator
{
    public const string WatchListContext = "WatchList";
    public const string TemplatesContext = "Templates";

    /// <summary>Node the properties pane and the context menu must both show.</summary>
    public TreeNodeViewModel? ActiveNode { get; private set; }

    /// <summary>Which tree is active: <see cref="WatchListContext"/> or <see cref="TemplatesContext"/>.</summary>
    public string ActiveContext { get; private set; } = WatchListContext;

    public TreeNodeViewModel? WatchListNode { get; private set; }
    public TreeNodeViewModel? TemplateNode { get; private set; }

    /// <summary>
    /// Node a run command acts on. Derived from the same state the properties pane uses, so the
    /// two can never disagree about the target.
    /// </summary>
    public TreeNodeViewModel? ExecTarget =>
        ActiveContext == TemplatesContext ? TemplateNode : WatchListNode;

    /// <summary>True when activating this node changed something a caller must react to.</summary>
    public bool ActivateWatchList(TreeNodeViewModel? node)
    {
        if (node is null) return false;
        var changed = !ReferenceEquals(WatchListNode, node) || ActiveContext != WatchListContext;
        WatchListNode = node;
        ActiveNode = node;
        ActiveContext = WatchListContext;
        return changed;
    }

    /// <inheritdoc cref="ActivateWatchList"/>
    public bool ActivateTemplate(TreeNodeViewModel? node)
    {
        if (node is null) return false;
        var changed = !ReferenceEquals(TemplateNode, node) || ActiveContext != TemplatesContext;
        TemplateNode = node;
        ActiveNode = node;
        ActiveContext = TemplatesContext;
        return changed;
    }

    /// <summary>Forgets both trees. Called when the WatchList reloads and every node is replaced.</summary>
    public void Reset()
    {
        ActiveNode = null;
        WatchListNode = null;
        TemplateNode = null;
        ActiveContext = WatchListContext;
    }
}
