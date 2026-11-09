using TestControllerGrpc.Menus;
using TestControllerGrpc.Models;
using static TestControllerGrpc.Menus.NodeMenuIds;

namespace TestControllerGrpc.Tests.Menus;

/// <summary>
/// ContextMenu_Requirement.md P2: the tree menus are decided by one pure provider in Core (rule 12).
/// One row per node type, plus the cross-cutting rules (2, 3, 6, 11) checked over every row.
/// </summary>
public class NodeMenuProviderTests
{
    private static NodeMenuContext Ctx(string kind, NodeMenuTree tree = NodeMenuTree.WatchList) => new()
    {
        NodeKind = kind,
        Tree = tree,
        CanMoveUp = true,
        CanMoveDown = true,
        CanEnablePipelines = true,
        CanDisablePipelines = true,
        CanToggleThisPipeline = true,
        CanAuthorPipelines = true,
        PipelineBuilderEnabled = true,
        GlobalVariablesEnabled = true,
        CanRetry = true,
        CalledTemplateExists = true,
        CalledTemplateId = "T1",
    };

    private static NodeMenuContext Call(string kind, bool isRoot) =>
        Ctx(kind) with { InTemplateCall = true, IsTemplateCallRoot = isRoot };

    private static NodeMenuContext Lib(string kind) =>
        Ctx(kind, NodeMenuTree.Library) with { OwningTemplateId = "T1" };

    public static TheoryData<string, NodeMenuContext, string[]> Rows => new()
    {
        { "Root", Ctx(NodeKinds.WatchList),
            [EnableAllWatchItems, DisableAllWatchItems, AddWatchItem, NewPipeline, ImportWatchItems,
             ExportAllWatchItems, EditWatchListXml, EditGlobalVariables] },
        { "Root, features off", Ctx(NodeKinds.WatchList) with { PipelineBuilderEnabled = false, GlobalVariablesEnabled = false },
            [EnableAllWatchItems, DisableAllWatchItems, AddWatchItem, ImportWatchItems, ExportAllWatchItems, EditWatchListXml] },
        { "WatchItem", Ctx(NodeKinds.WatchItem) with { RetryFailedCount = 2 },
            [TriggerAllEvents, CheckOnly, RetryFailed, AddEvent, AddActionGroup, ExportWatchItem,
             MoveUp, MoveDown, IncludeInTriggerAll, Delete] },
        { "Event", Ctx(NodeKinds.Event),
            [TriggerEvent, AddActionGroup, AddAction, AddRef, AddInitialize, ExecutionType, MoveUp, MoveDown, Delete] },
        { "ActionGroup", Ctx(NodeKinds.ActionGroup) with { IsSkippable = true },
            [ExecuteGroup, AddActionGroup, AddAction, AddRef, AddInitialize, AddGroupAbove, AddGroupBelow, ExecutionType, Rename,
             Duplicate, Copy, WrapInGroup, Ungroup, SaveAsTemplate, MoveUp, MoveDown, Skip, Delete] },
        { "Action, skipped", Ctx(NodeKinds.Action) with { IsSkippable = true, IsSkipped = true },
            [ExecuteAction, AddActionAbove, AddActionBelow, Rename, Duplicate, DuplicateForAgents, Copy, WrapInGroup,
             MoveUp, MoveDown, Include, Delete] },
        { "Initialize", Ctx(NodeKinds.Initialize), [Delete] },
        { "Template call (Ref)", Call(NodeKinds.Ref, isRoot: true), [EditTemplate, MoveUp, MoveDown, DeleteTemplateCall] },
        { "Group inside a template call", Call(NodeKinds.ActionGroup, isRoot: false) with { IsSkippable = true },
            [EditTemplate, ExecuteGroup, DeleteTemplateCall] },
        { "Action inside a template call", Call(NodeKinds.Action, isRoot: false) with { IsSkippable = true },
            [EditTemplate, ExecuteAction, DeleteTemplateCall] },
        { "Library root", Ctx(NodeKinds.TemplateList, NodeMenuTree.Library),
            [AddTemplate, EditTemplatesXml, ImportTemplates, ExportAllTemplates] },
        { "Template, context chosen", Lib(NodeKinds.Template) with { TemplateContext = "SP2026" },
            [ExecuteTemplate, CheckOnly, SetTemplateContext, ClearTemplateContext, AddGroupToTemplate, AddActionToTemplate,
             AddRefToTemplate, AddInitializeToTemplate, EditTemplateXml, ExportTemplate, MoveUp, MoveDown, DeleteFromLibrary] },
        { "Library ActionGroup", Lib(NodeKinds.ActionGroup) with { IsSkippable = true },
            [ExecuteGroup, SetTemplateContext, AddActionToTemplate, AddGroupToTemplate, AddRefToTemplate, AddGroupAbove,
             AddGroupBelow, ExecutionType, Rename, Duplicate, Copy, WrapInGroup, Ungroup, MoveUp, MoveDown, Skip, DeleteFromLibrary] },
        { "Library Action", Lib(NodeKinds.Action) with { IsSkippable = true },
            [ExecuteAction, SetTemplateContext, AddActionAbove, AddActionBelow, Rename, Duplicate, Copy, WrapInGroup,
             MoveUp, MoveDown, Skip, DeleteFromLibrary] },
    };

    private static IEnumerable<NodeMenuContext> AllContexts() => Rows.Select(r => (NodeMenuContext)r[1]);

    [Theory]
    [MemberData(nameof(Rows))]
    public void Build_Should_ReturnExactlyTheItemsForTheNodeType_When_RightClicked(
        string row, NodeMenuContext context, string[] expected)
    {
        var ids = NodeMenuProvider.Build(context).Select(i => i.Id).ToArray();

        Assert.True(expected.SequenceEqual(ids), $"{row}: expected [{string.Join(", ", expected)}] but got [{string.Join(", ", ids)}]");
    }

    [Fact]
    public void Build_Should_KeepTheRootItemsOffEveryOtherNode_When_RightClicked()
    {
        var rootIds = NodeMenuProvider.Build(Ctx(NodeKinds.WatchList)).Select(i => i.Id).ToHashSet();

        foreach (var ctx in AllContexts().Where(c => c.NodeKind != NodeKinds.WatchList))
            Assert.DoesNotContain(NodeMenuProvider.Build(ctx), i => rootIds.Contains(i.Id));
    }

    [Fact]
    public void Build_Should_OrderGroupsRunAddEditMoveStateDelete_When_NodeIsNotTheRoot()
    {
        foreach (var ctx in AllContexts().Where(c =>
                     c.NodeKind is not (NodeKinds.WatchList or NodeKinds.TemplateList) && !c.InTemplateCall))
        {
            var groups = NodeMenuProvider.Build(ctx).Select(i => (int)i.Group).ToArray();
            Assert.True(groups.Zip(groups.Skip(1)).All(p => p.First <= p.Second),
                $"{ctx.Tree}/{ctx.NodeKind}: groups out of order: {string.Join(",", groups)}");
            Assert.Equal(NodeMenuGroup.Delete, NodeMenuProvider.Build(ctx)[^1].Group);
        }
    }

    [Theory]
    [InlineData(NodeKinds.Ref, true)]
    [InlineData(NodeKinds.ActionGroup, false)]
    [InlineData(NodeKinds.Action, false)]
    [InlineData(NodeKinds.Initialize, false)]
    public void Build_Should_OfferNoDirectEdits_When_TheNodeIsPartOfATemplateCall(string kind, bool isRoot)
    {
        var allowed = new[] { EditTemplate, ExecuteGroup, ExecuteAction, MoveUp, MoveDown, DeleteTemplateCall };
        var ctx = Call(kind, isRoot) with { IsSkippable = true, ExecutionType = "Sequential" };

        Assert.All(NodeMenuProvider.Build(ctx), i => Assert.Contains(i.Id, allowed));
    }

    [Fact]
    public void Build_Should_NeverOfferMove_When_TheNodeIsAMirroredTemplateStep()
    {
        var ids = NodeMenuProvider.Build(Call(NodeKinds.Action, isRoot: false)).Select(i => i.Id);

        Assert.DoesNotContain(MoveUp, ids);
        Assert.DoesNotContain(MoveDown, ids);
    }

    [Fact]
    public void Build_Should_HideMoveItems_When_TheNodeCannotMove()
    {
        var ids = NodeMenuProvider.Build(Ctx(NodeKinds.Action) with { CanMoveUp = false, CanMoveDown = false }).Select(i => i.Id);

        Assert.DoesNotContain(MoveUp, ids);
        Assert.DoesNotContain(MoveDown, ids);
    }

    [Fact]
    public void Build_Should_KeepEditTemplateEnabledWithAReason_When_TheTemplateIsMissing()
    {
        var item = NodeMenuProvider.Build(Call(NodeKinds.Ref, true) with { CalledTemplateExists = false, CalledTemplateId = "Gone" })
            .Single(i => i.Id == EditTemplate);

        Assert.True(item.IsEnabled); // 2.8.1-5: opens the "not found" choice instead of navigating
        Assert.Equal("Template 'Gone' is not in the Library - choose to create it or delete the call.", item.ToolTip);
    }

    [Theory]
    [InlineData(NodeKinds.ActionGroup, ExecuteGroup, NodeMenuTree.WatchList)]
    [InlineData(NodeKinds.Action, ExecuteAction, NodeMenuTree.Library)]
    [InlineData(NodeKinds.Template, ExecuteTemplate, NodeMenuTree.Library)]
    public void Build_Should_DisableRunWithTheReason_When_TheNodeCannotRun(string kind, string id, NodeMenuTree tree)
    {
        var item = NodeMenuProvider.Build(Ctx(kind, tree) with { RunBlockedReason = "Templates run only inside a pipeline" })
            .Single(i => i.Id == id);

        Assert.False(item.IsEnabled);
        Assert.Equal("Templates run only inside a pipeline", item.ToolTip);
    }

    /// <summary>Rule 3: anything shown disabled must say why.</summary>
    [Fact]
    public void Build_Should_ExplainEveryDisabledItem_When_NothingIsAllowed()
    {
        var denied = AllContexts().Select(c => c with
        {
            CanEnablePipelines = false,
            CanDisablePipelines = false,
            CanToggleThisPipeline = false,
            CanAuthorPipelines = false,
            CanRetry = false,
            RetryFailedCount = 1,
            CalledTemplateExists = false,
            RunBlockedReason = "blocked",
        }).ToList();

        var disabled = denied.SelectMany(NodeMenuProvider.Build).Where(i => !i.IsEnabled).ToList();

        Assert.NotEmpty(disabled);
        Assert.All(disabled, i => Assert.False(string.IsNullOrWhiteSpace(i.ToolTip), $"{i.Id} is disabled without a reason"));
    }

    [Fact]
    public void Build_Should_ShowRetryCount_When_TheLastRunHadFailures()
    {
        Assert.Equal("Retry Failed (1 action)",
            NodeMenuProvider.Build(Ctx(NodeKinds.Event) with { RetryFailedCount = 1 }).Single(i => i.Id == RetryFailed).Label);
        Assert.Equal("Retry Failed (3 actions)",
            NodeMenuProvider.Build(Ctx(NodeKinds.WatchItem) with { RetryFailedCount = 3 }).Single(i => i.Id == RetryFailed).Label);
    }

    [Fact]
    public void Build_Should_CheckTheCurrentExecutionType_When_TheSubmenuIsBuilt()
    {
        var sub = NodeMenuProvider.Build(Ctx(NodeKinds.Event) with { ExecutionType = "parallel" })
            .Single(i => i.Id == ExecutionType).Children;

        Assert.Equal([ExecutionSequential, ExecutionParallel], sub.Select(c => c.Id));
        Assert.Equal([false, true], sub.Select(c => c.IsChecked == true));
    }

    [Fact]
    public void Build_Should_ShowTheChosenContext_When_ATemplateHasOne()
    {
        var item = NodeMenuProvider.Build(Lib(NodeKinds.Action) with { TemplateContext = "SP2026" })
            .Single(i => i.Id == SetTemplateContext);

        Assert.Equal("Change context (now 'SP2026')\u2026", item.Label);
    }

    /// <summary>A new id with no WPF handler would throw at right-click time; catch it here instead.</summary>
    [Fact]
    public void WpfRenderer_Should_BindEveryMenuId()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TestAgentSolution.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var renderer = File.ReadAllText(Path.Combine(dir!.FullName, "TestControllerGrpc", "Views", "MainWindow.ContextMenus.cs"));

        var fields = typeof(NodeMenuIds).GetFields().Where(f => f.IsLiteral).ToList();
        Assert.Equal(fields.Count, All.Count);
        Assert.All(fields, f => Assert.Equal(f.Name, (string)f.GetRawConstantValue()!));
        Assert.All(All, id => Assert.Contains($"NodeMenuIds.{id} =>", renderer));
    }
}
