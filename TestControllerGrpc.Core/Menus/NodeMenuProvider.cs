using TestControllerGrpc.Models;

namespace TestControllerGrpc.Menus;

/// <summary>Stable item identifiers; hosts bind each one to a command (WPF) or an API action (web).</summary>
public static class NodeMenuIds
{
    // Root
    public const string EnableAllWatchItems = "EnableAllWatchItems";
    public const string DisableAllWatchItems = "DisableAllWatchItems";
    public const string AddWatchItem = "AddWatchItem";
    public const string NewPipeline = "NewPipeline";
    public const string ImportWatchItems = "ImportWatchItems";
    public const string ExportAllWatchItems = "ExportAllWatchItems";
    public const string EditWatchListXml = "EditWatchListXml";
    public const string EditGlobalVariables = "EditGlobalVariables";

    // Pipeline nodes
    public const string TriggerAllEvents = "TriggerAllEvents";
    public const string CheckOnly = "CheckOnly";
    public const string RetryFailed = "RetryFailed";
    public const string TriggerEvent = "TriggerEvent";
    public const string ExecuteGroup = "ExecuteGroup";
    public const string ExecuteAction = "ExecuteAction";
    public const string AddEvent = "AddEvent";
    public const string AddActionGroup = "AddActionGroup";
    public const string AddAction = "AddAction";
    public const string AddRef = "AddRef";
    public const string AddInitialize = "AddInitialize";
    public const string ExportWatchItem = "ExportWatchItem";
    public const string IncludeInTriggerAll = "IncludeInTriggerAll";
    public const string Delete = "Delete";

    // Shared
    public const string ExecutionType = "ExecutionType";
    public const string ExecutionSequential = "ExecutionSequential";
    public const string ExecutionParallel = "ExecutionParallel";
    public const string Skip = "Skip";
    public const string Include = "Include";
    public const string MoveUp = "MoveUp";
    public const string MoveDown = "MoveDown";

    // Template call in a pipeline
    public const string EditTemplate = "EditTemplate";
    public const string DeleteTemplateCall = "DeleteTemplateCall";

    // Library
    public const string AddTemplate = "AddTemplate";
    public const string EditTemplatesXml = "EditTemplatesXml";
    public const string ImportTemplates = "ImportTemplates";
    public const string ExportAllTemplates = "ExportAllTemplates";
    public const string ExecuteTemplate = "ExecuteTemplate";
    public const string SetTemplateContext = "SetTemplateContext";
    public const string ClearTemplateContext = "ClearTemplateContext";
    public const string EditTemplateXml = "EditTemplateXml";
    public const string AddGroupToTemplate = "AddGroupToTemplate";
    public const string AddActionToTemplate = "AddActionToTemplate";
    public const string AddRefToTemplate = "AddRefToTemplate";
    public const string AddInitializeToTemplate = "AddInitializeToTemplate";
    public const string ExportTemplate = "ExportTemplate";
    public const string DeleteFromLibrary = "DeleteFromLibrary";

    // Structural edits (P5)
    public const string AddGroupAbove = "AddGroupAbove";
    public const string AddGroupBelow = "AddGroupBelow";
    public const string AddActionAbove = "AddActionAbove";
    public const string AddActionBelow = "AddActionBelow";
    public const string Rename = "Rename";
    public const string Duplicate = "Duplicate";
    public const string DuplicateForAgents = "DuplicateForAgents";
    public const string Copy = "Copy";
    public const string Paste = "Paste";
    public const string WrapInGroup = "WrapInGroup";
    public const string Ungroup = "Ungroup";
    public const string SaveAsTemplate = "SaveAsTemplate";

    // Run additions (P7)
    public const string RunFromHere = "RunFromHere";
    public const string ShowResolvedCommand = "ShowResolvedCommand";
    public const string OpenLastOutput = "OpenLastOutput";
    public const string Stop = "Stop";

    public static IReadOnlyList<string> All { get; } = typeof(NodeMenuIds)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.IsLiteral)
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToArray();
}

/// <summary>
/// The single definition of the tree context menus (ContextMenu_Requirement.md rule 12). Pure: the host
/// describes the node in a <see cref="NodeMenuContext"/> and renders whatever comes back.
/// </summary>
public static class NodeMenuProvider
{
    public const string AdminOnly = "Administrator only";
    public const string NoRetryPermission = "You do not have permission to retry this pipeline.";

    public static IReadOnlyList<NodeMenuItem> Build(NodeMenuContext c)
    {
        var m = new List<NodeMenuItem>();
        if (c.Tree == NodeMenuTree.Library) Library(c, m);
        else if (c.InTemplateCall) TemplateCall(c, m);
        else Pipeline(c, m);
        if (c.Tree == NodeMenuTree.WatchList && c.IsPipelineRunning) m = RunItemsBecomeStop(c, m);
        if (c.NodeKind == NodeKinds.WatchItem && c.StructuralEditBlockedReason is { } structural)
            m = m.Select(i => i.Id is NodeMenuIds.Delete or NodeMenuIds.MoveUp or NodeMenuIds.MoveDown
                ? i with { IsEnabled = false, ToolTip = structural } : i).ToList();
        return c.EditBlockedReason is { } reason ? m.Select(i => BlockEdit(i, reason)).ToList() : m;
    }

    /// <summary>Items that change the WatchList; shown disabled with the reason when editing is not allowed.</summary>
    public static readonly IReadOnlySet<string> EditIds = new HashSet<string>
    {
        NodeMenuIds.AddWatchItem, NodeMenuIds.ImportWatchItems, NodeMenuIds.EditWatchListXml,
        NodeMenuIds.AddEvent, NodeMenuIds.AddActionGroup, NodeMenuIds.AddAction, NodeMenuIds.AddRef,
        NodeMenuIds.AddInitialize, NodeMenuIds.Delete, NodeMenuIds.ExecutionType, NodeMenuIds.ExecutionSequential,
        NodeMenuIds.ExecutionParallel, NodeMenuIds.Skip, NodeMenuIds.Include, NodeMenuIds.MoveUp, NodeMenuIds.MoveDown,
        NodeMenuIds.DeleteTemplateCall, NodeMenuIds.AddTemplate, NodeMenuIds.EditTemplatesXml,
        NodeMenuIds.ImportTemplates, NodeMenuIds.EditTemplateXml, NodeMenuIds.AddGroupToTemplate,
        NodeMenuIds.AddActionToTemplate, NodeMenuIds.AddRefToTemplate, NodeMenuIds.AddInitializeToTemplate,
        NodeMenuIds.DeleteFromLibrary,
        NodeMenuIds.AddGroupAbove, NodeMenuIds.AddGroupBelow, NodeMenuIds.AddActionAbove, NodeMenuIds.AddActionBelow,
        NodeMenuIds.Rename, NodeMenuIds.Duplicate, NodeMenuIds.DuplicateForAgents, NodeMenuIds.Paste,
        NodeMenuIds.WrapInGroup, NodeMenuIds.Ungroup,
    };

    private static NodeMenuItem BlockEdit(NodeMenuItem item, string reason) => EditIds.Contains(item.Id)
        ? item with { IsEnabled = false, ToolTip = reason, Children = item.Children.Select(ch => BlockEdit(ch, reason)).ToList() }
        : item;

    private static void Pipeline(NodeMenuContext c, List<NodeMenuItem> m)
    {
        switch (c.NodeKind)
        {
            case NodeKinds.WatchList:
                Root(c, m);
                return;

            case NodeKinds.WatchItem:
                m.Add(Item(NodeMenuIds.TriggerAllEvents, "Trigger All Events", NodeMenuGroup.Run));
                m.Add(Item(NodeMenuIds.CheckOnly, "Check only\u2026", NodeMenuGroup.Run));
                Retry(c, m);
                m.Add(Item(NodeMenuIds.AddEvent, "Add Event", NodeMenuGroup.Add));
                m.Add(Item(NodeMenuIds.AddActionGroup, "Add ActionGroup", NodeMenuGroup.Add));
                m.Add(Item(NodeMenuIds.ExportWatchItem, "Export WatchItem\u2026", NodeMenuGroup.Copy));
                Move(c, m);
                m.Add(Item(NodeMenuIds.IncludeInTriggerAll, "Include in Trigger All", NodeMenuGroup.State) with
                {
                    IsChecked = c.IsEnabled,
                    IsEnabled = c.CanToggleThisPipeline,
                    ToolTip = c.CanToggleThisPipeline ? null : AdminOnly,
                });
                m.Add(Item(NodeMenuIds.Delete, "Delete", NodeMenuGroup.Delete));
                return;

            case NodeKinds.Event:
                m.Add(Item(NodeMenuIds.TriggerEvent, "Trigger Event", NodeMenuGroup.Run));
                Retry(c, m);
                AddChildren(m);
                m.Add(ExecutionTypeMenu(c));
                Move(c, m);
                m.Add(Item(NodeMenuIds.Delete, "Delete", NodeMenuGroup.Delete));
                return;

            case NodeKinds.ActionGroup:
                m.Add(Runnable(NodeMenuIds.ExecuteGroup, "Execute Group", c));
                m.Add(Runnable(NodeMenuIds.RunFromHere, "Run from here to end\u2026", c));
                AddChildren(m);
                GroupSiblings(m);
                m.Add(ExecutionTypeMenu(c));
                m.Add(Item(NodeMenuIds.Rename, "Rename\u2026", NodeMenuGroup.Edit));
                CopyItems(c, m, isGroup: true);
                m.Add(Item(NodeMenuIds.SaveAsTemplate, "Save group as template\u2026", NodeMenuGroup.Copy) with
                {
                    IsEnabled = c.TemplateEditBlockedReason is null,
                    ToolTip = c.TemplateEditBlockedReason,
                });
                Move(c, m);
                SkipItem(c, m);
                m.Add(Item(NodeMenuIds.Delete, "Delete", NodeMenuGroup.Delete));
                return;

            case NodeKinds.Action:
                m.Add(Runnable(NodeMenuIds.ExecuteAction, "Execute Action", c));
                m.Add(Runnable(NodeMenuIds.RunFromHere, "Run from here to end\u2026", c));
                m.Add(Item(NodeMenuIds.ShowResolvedCommand, "Show resolved command", NodeMenuGroup.Run));
                m.Add(Item(NodeMenuIds.OpenLastOutput, "Open last output", NodeMenuGroup.Run));
                ActionSiblings(m);
                m.Add(Item(NodeMenuIds.Rename, "Rename\u2026", NodeMenuGroup.Edit));
                m.Add(Item(NodeMenuIds.Duplicate, "Duplicate", NodeMenuGroup.Copy));
                m.Add(Item(NodeMenuIds.DuplicateForAgents, "Duplicate for agents\u2026", NodeMenuGroup.Copy));
                CopyItems(c, m, isGroup: false, includeDuplicate: false);
                Move(c, m);
                SkipItem(c, m);
                m.Add(Item(NodeMenuIds.Delete, "Delete", NodeMenuGroup.Delete));
                return;

            default:
                Move(c, m);
                m.Add(Item(NodeMenuIds.Delete, "Delete", NodeMenuGroup.Delete));
                return;
        }
    }

    /// <summary>Today's WatchList menu, kept as it was (rule 11): only on the root and empty space.</summary>
    private static void Root(NodeMenuContext c, List<NodeMenuItem> m)
    {
        m.Add(Item(NodeMenuIds.EnableAllWatchItems, "Enable All WatchItems", NodeMenuGroup.State) with
        {
            IsEnabled = c.CanEnablePipelines,
            ToolTip = c.CanEnablePipelines ? null : AdminOnly,
        });
        m.Add(Item(NodeMenuIds.DisableAllWatchItems, "Disable All WatchItems", NodeMenuGroup.State) with
        {
            IsEnabled = c.CanDisablePipelines,
            ToolTip = c.CanDisablePipelines ? null : AdminOnly,
        });
        m.Add(Item(NodeMenuIds.AddWatchItem, "Add WatchItem", NodeMenuGroup.Add));
        if (c.PipelineBuilderEnabled)
            m.Add(Item(NodeMenuIds.NewPipeline, "New Pipeline\u2026", NodeMenuGroup.Add) with
            {
                IsEnabled = c.CanAuthorPipelines,
                ToolTip = c.CanAuthorPipelines ? null : AdminOnly,
            });
        m.Add(Item(NodeMenuIds.ImportWatchItems, "Import WatchItems\u2026", NodeMenuGroup.Copy));
        m.Add(Item(NodeMenuIds.ExportAllWatchItems, "Export All WatchItems\u2026", NodeMenuGroup.Copy));
        m.Add(Item(NodeMenuIds.EditWatchListXml, "Edit WatchList XML\u2026", NodeMenuGroup.Edit));
        if (c.GlobalVariablesEnabled)
            m.Add(Item(NodeMenuIds.EditGlobalVariables, "Edit Global Variables\u2026", NodeMenuGroup.Edit));
    }

    /// <summary>A Ref and the template nodes mirrored under it: no direct edits (rule 6).</summary>
    private static void TemplateCall(NodeMenuContext c, List<NodeMenuItem> m)
    {
        m.Add(Item(NodeMenuIds.EditTemplate, "Edit template", NodeMenuGroup.Edit) with
        {
            ToolTip = !c.CalledTemplateExists
                ? string.IsNullOrEmpty(c.CalledTemplateId) ? "This Ref does not name a template."
                    : $"Template '{c.CalledTemplateId}' is not in the Library - choose to create it or delete the call."
                : c.TemplateEditBlockedReason is null ? null : "You can view but not edit templates",
            IsEnabled = !string.IsNullOrEmpty(c.CalledTemplateId),
        });
        if (c.NodeKind == NodeKinds.ActionGroup) m.Add(Runnable(NodeMenuIds.ExecuteGroup, "Execute Group", c));
        else if (c.NodeKind == NodeKinds.Action) m.Add(Runnable(NodeMenuIds.ExecuteAction, "Execute Action", c));
        if (c.IsTemplateCallRoot) Move(c, m);
        m.Add(Item(NodeMenuIds.DeleteTemplateCall, "Delete template call\u2026", NodeMenuGroup.Delete));
    }

    private static void Library(NodeMenuContext c, List<NodeMenuItem> m)
    {
        switch (c.NodeKind)
        {
            case NodeKinds.TemplateList:
                m.Add(Item(NodeMenuIds.AddTemplate, "Add Template", NodeMenuGroup.Add));
                m.Add(Item(NodeMenuIds.EditTemplatesXml, "Edit Template XML", NodeMenuGroup.Edit));
                m.Add(Item(NodeMenuIds.ImportTemplates, "Import Templates\u2026", NodeMenuGroup.Copy));
                m.Add(Item(NodeMenuIds.ExportAllTemplates, "Export All Templates\u2026", NodeMenuGroup.Copy));
                return;

            case NodeKinds.Template:
                m.Add(Runnable(NodeMenuIds.ExecuteTemplate, "Execute Template", c));
                m.Add(Item(NodeMenuIds.CheckOnly, "Check only\u2026", NodeMenuGroup.Run));
                ContextItems(c, m);
                m.Add(Item(NodeMenuIds.AddGroupToTemplate, "Add ActionGroup", NodeMenuGroup.Add));
                m.Add(Item(NodeMenuIds.AddActionToTemplate, "Add Action", NodeMenuGroup.Add));
                m.Add(Item(NodeMenuIds.AddRefToTemplate, "Add Ref", NodeMenuGroup.Add));
                m.Add(Item(NodeMenuIds.AddInitializeToTemplate, "Add Initialize", NodeMenuGroup.Add));
                m.Add(Item(NodeMenuIds.EditTemplateXml, "Edit Template XML\u2026", NodeMenuGroup.Edit));
                m.Add(Item(NodeMenuIds.ExportTemplate, "Export Template\u2026", NodeMenuGroup.Copy));
                Move(c, m);
                m.Add(Item(NodeMenuIds.DeleteFromLibrary, "Delete", NodeMenuGroup.Delete));
                return;

            case NodeKinds.ActionGroup:
                m.Add(Runnable(NodeMenuIds.ExecuteGroup, "Execute Group", c));
                ContextItems(c, m);
                m.Add(Item(NodeMenuIds.AddActionToTemplate, "Add Action", NodeMenuGroup.Add));
                m.Add(Item(NodeMenuIds.AddGroupToTemplate, "Add ActionGroup", NodeMenuGroup.Add));
                m.Add(Item(NodeMenuIds.AddRefToTemplate, "Add Ref", NodeMenuGroup.Add));
                GroupSiblings(m);
                m.Add(ExecutionTypeMenu(c));
                m.Add(Item(NodeMenuIds.Rename, "Rename\u2026", NodeMenuGroup.Edit));
                CopyItems(c, m, isGroup: true);
                Move(c, m);
                SkipItem(c, m);
                m.Add(Item(NodeMenuIds.DeleteFromLibrary, "Delete", NodeMenuGroup.Delete));
                return;

            case NodeKinds.Action:
                m.Add(Runnable(NodeMenuIds.ExecuteAction, "Execute Action", c));
                ContextItems(c, m);
                ActionSiblings(m);
                m.Add(Item(NodeMenuIds.Rename, "Rename\u2026", NodeMenuGroup.Edit));
                CopyItems(c, m, isGroup: false);
                Move(c, m);
                SkipItem(c, m);
                m.Add(Item(NodeMenuIds.DeleteFromLibrary, "Delete", NodeMenuGroup.Delete));
                return;

            default:
                Move(c, m);
                m.Add(Item(NodeMenuIds.DeleteFromLibrary, "Delete", NodeMenuGroup.Delete));
                return;
        }
    }

    private static readonly HashSet<string> StartRunIds =
    [
        NodeMenuIds.TriggerAllEvents, NodeMenuIds.TriggerEvent, NodeMenuIds.ExecuteGroup,
        NodeMenuIds.ExecuteAction, NodeMenuIds.RunFromHere, NodeMenuIds.RetryFailed,
    ];

    /// <summary>Rule 5: while the pipeline runs, its run items are replaced by one Stop item.</summary>
    private static List<NodeMenuItem> RunItemsBecomeStop(NodeMenuContext c, List<NodeMenuItem> m)
    {
        var first = m.FindIndex(i => StartRunIds.Contains(i.Id));
        if (first < 0) return m;
        var result = m.Where(i => !StartRunIds.Contains(i.Id)).ToList();
        result.Insert(Math.Min(first, result.Count), Item(NodeMenuIds.Stop, "Stop", NodeMenuGroup.Run) with
        {
            IsEnabled = c.CanStop,
            ToolTip = c.CanStop ? "Stop the running pipeline" : "You do not have permission to stop this pipeline.",
        });
        return result;
    }

    private static void GroupSiblings(List<NodeMenuItem> m)
    {
        m.Add(Item(NodeMenuIds.AddGroupAbove, "Add ActionGroup above", NodeMenuGroup.Add));
        m.Add(Item(NodeMenuIds.AddGroupBelow, "Add ActionGroup below", NodeMenuGroup.Add));
    }

    private static void ActionSiblings(List<NodeMenuItem> m)
    {
        m.Add(Item(NodeMenuIds.AddActionAbove, "Add Action above", NodeMenuGroup.Add));
        m.Add(Item(NodeMenuIds.AddActionBelow, "Add Action below", NodeMenuGroup.Add));
    }

    private static void CopyItems(NodeMenuContext c, List<NodeMenuItem> m, bool isGroup, bool includeDuplicate = true)
    {
        if (includeDuplicate) m.Add(Item(NodeMenuIds.Duplicate, "Duplicate", NodeMenuGroup.Copy));
        m.Add(Item(NodeMenuIds.Copy, "Copy", NodeMenuGroup.Copy));
        if (c.HasClipboard)
            m.Add(Item(NodeMenuIds.Paste, isGroup ? "Paste inside" : "Paste below", NodeMenuGroup.Copy));
        m.Add(Item(NodeMenuIds.WrapInGroup, "Wrap in new group", NodeMenuGroup.Copy));
        if (isGroup) m.Add(Item(NodeMenuIds.Ungroup, "Ungroup", NodeMenuGroup.Copy));
    }

    private static void AddChildren(List<NodeMenuItem> m)
    {
        m.Add(Item(NodeMenuIds.AddActionGroup, "Add ActionGroup", NodeMenuGroup.Add));
        m.Add(Item(NodeMenuIds.AddAction, "Add Action", NodeMenuGroup.Add));
        m.Add(Item(NodeMenuIds.AddRef, "Add Ref", NodeMenuGroup.Add));
        m.Add(Item(NodeMenuIds.AddInitialize, "Add Initialize", NodeMenuGroup.Add));
    }

    private static void Retry(NodeMenuContext c, List<NodeMenuItem> m)
    {
        if (c.RetryFailedCount <= 0) return;
        var label = $"Retry Failed ({c.RetryFailedCount} action{(c.RetryFailedCount > 1 ? "s" : "")})";
        m.Add(Item(NodeMenuIds.RetryFailed, label, NodeMenuGroup.Run) with
        {
            IsEnabled = c.CanRetry,
            ToolTip = c.CanRetry ? null : NoRetryPermission,
        });
    }

    private static void Move(NodeMenuContext c, List<NodeMenuItem> m)
    {
        if (c.NodeKind is NodeKinds.WatchList or NodeKinds.TemplateList or NodeKinds.Initialize) return;
        if (c.CanMoveUp) m.Add(Item(NodeMenuIds.MoveUp, "Move Up", NodeMenuGroup.Move));
        if (c.CanMoveDown) m.Add(Item(NodeMenuIds.MoveDown, "Move Down", NodeMenuGroup.Move));
    }

    /// <summary>Skip asks for a reason and include clears it, so they are two items rather than one checkbox.</summary>
    private static void SkipItem(NodeMenuContext c, List<NodeMenuItem> m)
    {
        if (!c.IsSkippable) return;
        m.Add(c.IsSkipped
            ? Item(NodeMenuIds.Include, "Include again", NodeMenuGroup.State) with
              { ToolTip = "Run this node again and clear its skip reason" }
            : Item(NodeMenuIds.Skip, "Skip this node...", NodeMenuGroup.State) with
              { ToolTip = "Skip this node and everything under it during execution" });
    }

    private static void ContextItems(NodeMenuContext c, List<NodeMenuItem> m)
    {
        if (c.OwningTemplateId is null) return;
        m.Add(Item(NodeMenuIds.SetTemplateContext,
            c.TemplateContext is null ? "Set pipeline context\u2026" : $"Change context (now '{c.TemplateContext}')\u2026",
            NodeMenuGroup.Context) with
        { ToolTip = "Choose which pipeline supplies this template's values for previews and runs." });
        if (c.TemplateContext is not null)
            m.Add(Item(NodeMenuIds.ClearTemplateContext, "Clear context", NodeMenuGroup.Context) with
            { ToolTip = "Show this template's raw [_Tokens] again." });
    }

    private static NodeMenuItem ExecutionTypeMenu(NodeMenuContext c) =>
        Item(NodeMenuIds.ExecutionType, "Change Execution Type", NodeMenuGroup.Edit) with
        {
            Children =
            [
                Item(NodeMenuIds.ExecutionSequential, "Sequential", NodeMenuGroup.Edit) with
                { IsChecked = string.Equals(c.ExecutionType, "Sequential", StringComparison.OrdinalIgnoreCase) },
                Item(NodeMenuIds.ExecutionParallel, "Parallel", NodeMenuGroup.Edit) with
                { IsChecked = string.Equals(c.ExecutionType, "Parallel", StringComparison.OrdinalIgnoreCase) },
            ],
        };

    /// <summary>A run item disabled with its reason when the node cannot run now (rule 3).</summary>
    private static NodeMenuItem Runnable(string id, string label, NodeMenuContext c) =>
        Item(id, label, NodeMenuGroup.Run) with
        {
            IsEnabled = string.IsNullOrEmpty(c.RunBlockedReason),
            ToolTip = string.IsNullOrEmpty(c.RunBlockedReason) ? null : c.RunBlockedReason,
        };

    private static NodeMenuItem Item(string id, string label, NodeMenuGroup group) => new(id, label, group);
}
