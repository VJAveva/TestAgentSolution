using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Node-level skip: persistence of the include direction, the inherited cascade the tree paints, the parent
/// count, and the executor actually honouring the flag (which it did not before this feature).
/// </summary>
public class NodeSkipTests
{
    private const string Xml = """
        <WatchList>
          <WatchItem Tag="Nightly" Path="C:\drop" Filter="*.trg">
            <Event Type="Renamed" ExecutionType="Sequential">
              <ActionGroup Tag="Setup" ExecutionType="Sequential">
                <Action Type="RunCommand" Command="a.exe" Tag="Step1" />
                <Action Type="RunCommand" Command="b.exe" Tag="Step2" />
              </ActionGroup>
            </Event>
          </WatchItem>
        </WatchList>
        """;

    private static (ActionGroupConfig Group, ActionConfig First) Load()
    {
        WatchListConfig config = WatchListXmlParser.DeserializeWatchList(Xml)!;
        var group = (ActionGroupConfig)config.WatchItems[0].Events[0].Children[0];
        return (group, (ActionConfig)group.Children[0]);
    }

    // ── 1. skip / include round-trips through XML ──────────────────────

    [Fact]
    public void Skip_Should_SurviveRoundTrip_When_ReasonSet()
    {
        var (group, _) = Load();
        group.Skip = true;
        group.SkipReason = "Hardware unavailable";

        string xml = WatchListXmlParser.SerializeWatchList(
            new WatchListConfig { WatchItems = { Owner(group) } });
        var reloaded = WatchListXmlParser.DeserializeWatchList(xml)!;
        var reloadedGroup = (ActionGroupConfig)reloaded.WatchItems[0].Events[0].Children[0];

        Assert.True(reloadedGroup.Skip);
        Assert.Equal("Hardware unavailable", reloadedGroup.SkipReason);
    }

    [Fact]
    public void Include_Should_RemoveSkipAndReason_When_RoundTripped()
    {
        var (group, _) = Load();
        group.Skip = true;
        group.SkipReason = "Hardware unavailable";

        // Include again: both the flag and the reason must go, or a later skip inherits stale text.
        group.Skip = false;
        group.SkipReason = null;

        string xml = WatchListXmlParser.SerializeWatchList(
            new WatchListConfig { WatchItems = { Owner(group) } });

        Assert.DoesNotContain("Skip=", xml);
        Assert.DoesNotContain("SkipReason=", xml);

        var reloaded = WatchListXmlParser.DeserializeWatchList(xml)!;
        var reloadedGroup = (ActionGroupConfig)reloaded.WatchItems[0].Events[0].Children[0];
        Assert.False(reloadedGroup.Skip);
        Assert.Null(reloadedGroup.SkipReason);
    }

    // ── 2. a child inherits its parent's skip ──────────────────────────

    [Fact]
    public void Child_Should_BeInherited_When_ParentGroupSkipped()
    {
        var (group, _) = Load();
        group.Skip = true;
        group.SkipReason = "Whole phase off";

        TreeNodeViewModel root = BuildTree(group);
        root.RecomputeSkipOrigins(SkipState.NotSkipped);

        Assert.Equal(SkipOrigin.Explicit, root.SkipOrigin);
        Assert.True(root.IsSkippedExplicit);

        foreach (var child in root.Children)
        {
            Assert.Equal(SkipOrigin.Inherited, child.SkipOrigin);
            Assert.True(child.IsSkippedByParent);
            // Children are dimmed but must NOT carry a pill: only one node owns the decision.
            Assert.False(child.IsSkippedExplicit);
            Assert.False(child.Skip);
        }
    }

    [Fact]
    public void Child_Should_NotBeSkipped_When_OnlySiblingSkipped()
    {
        var (group, first) = Load();
        first.Skip = true;

        TreeNodeViewModel root = BuildTree(group);
        root.RecomputeSkipOrigins(SkipState.NotSkipped);

        Assert.Equal(SkipOrigin.None, root.SkipOrigin);
        Assert.Equal(SkipOrigin.Explicit, root.Children[0].SkipOrigin);
        Assert.Equal(SkipOrigin.None, root.Children[1].SkipOrigin);
    }

    // ── 3. parent count ───────────────────────────────────────────────

    [Fact]
    public void SkippedChildCount_Should_CountOnlySkippedChildren()
    {
        var (group, first) = Load();
        first.Skip = true;

        TreeNodeViewModel root = BuildTree(group);
        root.RecomputeSkipOrigins(SkipState.NotSkipped);

        Assert.Equal(1, root.SkippedChildCount);
    }

    [Fact]
    public void SkippedChildCount_Should_CountInheritedChildren_When_GroupItselfSkipped()
    {
        var (group, _) = Load();
        group.Skip = true;

        TreeNodeViewModel root = BuildTree(group);
        root.RecomputeSkipOrigins(SkipState.NotSkipped);

        // Inherited children still read as skipped to the user, so the parent meta must count them.
        Assert.Equal(2, root.SkippedChildCount);
    }

    // ── 4. runtime status is Skipped, and the node does not run ───────

    [Fact]
    public async Task Executor_Should_ReportSkipped_And_NotDispatch_When_ActionSkipped()
    {
        var (group, first) = Load();
        first.Skip = true;
        first.SkipReason = "flaky";

        var dispatcher = new Mock<IAgentGrpcDispatcher>();
        var dispatched = new List<string>();
        dispatcher
            .Setup(d => d.ExecuteLocalCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<ActionConfig, PipelineExecutionContext, CancellationToken>((a, _, _) =>
            {
                dispatched.Add(a.Command);
                return Task.FromResult(new ActionResult(true, 0, ""));
            });

        var config = new BuildResultsConfig();
        var executor = new ActionPipelineExecutor(
            dispatcher.Object, new ExecutionSessionManager(),
            NullLogger<ActionPipelineExecutor>.Instance, new TrxResultsParser(),
            new BuildResultsAggregator(config), new BuildReportHtmlGenerator(config), config);

        var statuses = new List<(string Tag, string Status)>();
        executor.NodeProgress += (node, status, _) =>
        {
            if (node is ActionConfig a) statuses.Add((a.Tag, status));
        };

        bool ok = await executor.ExecuteGroupAsync(group, new PipelineExecutionContext(), CancellationToken.None);

        Assert.True(ok);
        Assert.DoesNotContain("a.exe", dispatched);
        Assert.Contains("b.exe", dispatched);
        Assert.Contains(("Step1", "Skipped"), statuses);
        // Skipped must never surface as Failed - that is the whole point of the status.
        Assert.DoesNotContain(statuses, s => s.Tag == "Step1" && s.Status == "Failed");
    }

    [Fact]
    public async Task Executor_Should_NotStopSequence_When_SkippedUnderFailAndContinueFalse()
    {
        var (group, first) = Load();
        group.FailAndContinue = false;
        first.Skip = true;

        var dispatcher = new Mock<IAgentGrpcDispatcher>();
        var dispatched = new List<string>();
        dispatcher
            .Setup(d => d.ExecuteLocalCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<ActionConfig, PipelineExecutionContext, CancellationToken>((a, _, _) =>
            {
                dispatched.Add(a.Command);
                return Task.FromResult(new ActionResult(true, 0, ""));
            });

        var config = new BuildResultsConfig();
        var executor = new ActionPipelineExecutor(
            dispatcher.Object, new ExecutionSessionManager(),
            NullLogger<ActionPipelineExecutor>.Instance, new TrxResultsParser(),
            new BuildResultsAggregator(config), new BuildReportHtmlGenerator(config), config);

        await executor.ExecuteGroupAsync(group, new PipelineExecutionContext(), CancellationToken.None);

        // A skip is not a failure, so the following action must still run.
        Assert.Contains("b.exe", dispatched);
    }

    /// <summary>
    /// The TRACKED paths snapshot-clone the tree first, and a clone that drops <c>Skip</c> makes the gate a
    /// no-op. That is how a skipped action ran and reported Success in the Templates tree.
    /// </summary>
    [Fact]
    public async Task TrackedExecution_Should_NotDispatch_SkippedAction()
    {
        var (group, first) = Load();
        first.Skip = true;
        first.SkipReason = "Already reverted";

        var dispatcher = new Mock<IAgentGrpcDispatcher>();
        var dispatched = new List<string>();
        dispatcher
            .Setup(d => d.ExecuteLocalCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<ActionConfig, PipelineExecutionContext, CancellationToken>((a, _, _) =>
            {
                dispatched.Add(a.Command);
                return Task.FromResult(new ActionResult(true, 0, ""));
            });

        var config = new BuildResultsConfig();
        var executor = new ActionPipelineExecutor(
            dispatcher.Object, new ExecutionSessionManager(),
            NullLogger<ActionPipelineExecutor>.Instance, new TrxResultsParser(),
            new BuildResultsAggregator(config), new BuildReportHtmlGenerator(config), config);

        var statuses = new List<(string Tag, string Status)>();
        executor.NodeProgress += (node, status, _) =>
        {
            if (node is ActionConfig a) statuses.Add((a.Tag, status));
        };

        await executor.ExecuteGroupTrackedAsync("Pipeline", group, new PipelineExecutionContext(), CancellationToken.None);

        Assert.DoesNotContain("a.exe", dispatched);
        Assert.Contains("b.exe", dispatched);
        Assert.Contains(("Step1", "Skipped"), statuses);
        Assert.DoesNotContain(statuses, s => s.Tag == "Step1" && s.Status == "Success");
    }

    // ── 5. every public entry point refuses to dispatch a skipped target ─

    private static (Mock<IAgentGrpcDispatcher> Dispatcher, List<string> Dispatched, ActionPipelineExecutor Executor) Harness()
    {
        var dispatcher = new Mock<IAgentGrpcDispatcher>();
        var dispatched = new List<string>();
        dispatcher
            .Setup(d => d.ExecuteLocalCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<ActionConfig, PipelineExecutionContext, CancellationToken>((a, _, _) =>
            {
                dispatched.Add(a.Command);
                return Task.FromResult(new ActionResult(true, 0, ""));
            });
        var config = new BuildResultsConfig();
        var executor = new ActionPipelineExecutor(
            dispatcher.Object, new ExecutionSessionManager(),
            NullLogger<ActionPipelineExecutor>.Instance, new TrxResultsParser(),
            new BuildResultsAggregator(config), new BuildReportHtmlGenerator(config), config);
        return (dispatcher, dispatched, executor);
    }

    [Fact]
    public async Task ExecuteSingleActionAsync_Should_NotDispatch_When_ActionSkipped()
    {
        var (_, first) = Load();
        first.Skip = true;
        var h = Harness();

        var ok = await h.Executor.ExecuteSingleActionAsync(first, new PipelineExecutionContext(), CancellationToken.None);

        Assert.True(ok);
        Assert.Empty(h.Dispatched);
    }

    [Fact]
    public async Task ExecuteSingleActionTrackedAsync_Should_NotDispatch_When_ActionSkipped()
    {
        var (_, first) = Load();
        first.Skip = true;
        var h = Harness();

        // This is the "Execute Action" context-menu path on a skipped row.
        var ok = await h.Executor.ExecuteSingleActionTrackedAsync("Pipeline", first, new PipelineExecutionContext(), CancellationToken.None);

        Assert.True(ok);
        Assert.Empty(h.Dispatched);
    }

    [Fact]
    public async Task ExecuteGroupAsync_Should_NotDispatch_When_GroupItselfSkipped()
    {
        var (group, _) = Load();
        group.Skip = true;
        var h = Harness();

        var ok = await h.Executor.ExecuteGroupAsync(group, new PipelineExecutionContext(), CancellationToken.None);

        Assert.True(ok);
        Assert.Empty(h.Dispatched);
    }

    [Fact]
    public async Task ExecuteGroupTrackedAsync_Should_NotDispatch_When_GroupItselfSkipped()
    {
        var (group, _) = Load();
        group.Skip = true;
        var h = Harness();

        var ok = await h.Executor.ExecuteGroupTrackedAsync("Pipeline", group, new PipelineExecutionContext(), CancellationToken.None);

        Assert.True(ok);
        Assert.Empty(h.Dispatched);
    }

    [Fact]
    public async Task ExecuteEventTrackedAsync_Should_NotDispatch_When_EventSkipped()
    {
        WatchListConfig config = WatchListXmlParser.DeserializeWatchList(Xml)!;
        EventConfig evt = config.WatchItems[0].Events[0];
        evt.Skip = true;
        evt.SkipReason = "Whole event off";
        var h = Harness();

        await h.Executor.ExecuteEventTrackedAsync("Pipeline", evt, new PipelineExecutionContext(), CancellationToken.None);

        Assert.Empty(h.Dispatched);
    }

    [Fact]
    public async Task ExecuteTemplateTrackedAsync_Should_NotDispatch_SkippedChild()
    {
        WatchListConfig config = WatchListXmlParser.DeserializeWatchList(Xml)!;
        var group = (ActionGroupConfig)config.WatchItems[0].Events[0].Children[0];
        ((ActionConfig)group.Children[0]).Skip = true;
        var template = new TemplateConfig { ID = "T1", Children = { group } };
        var h = Harness();

        // Templates run as a synthetic event, so they inherit the event path's snapshot clone.
        await h.Executor.ExecuteTemplateTrackedAsync("Pipeline", template, new PipelineExecutionContext(), CancellationToken.None);

        Assert.DoesNotContain("a.exe", h.Dispatched);
        Assert.Contains("b.exe", h.Dispatched);
    }

    // ── 6. runtime status renders grey, and survives child propagation ─

    [Fact]
    public void ExecutionStatus_Skipped_Should_RenderGrey_And_NotFailed()
    {
        var node = new TreeNodeViewModel { NodeKind = NodeKinds.Action, SkipReason = "flaky" };

        node.ExecutionStatus = "Skipped";

        Assert.Equal("#FF9399B2", node.StatusColor);     // grey, same token as Cancelled
        Assert.Equal("\u2212", node.StatusSymbol);
        Assert.Contains("flaky", node.StatusTooltip);
        Assert.NotEqual("#FFF38BA8", node.StatusColor);  // never the failure red
    }

    [Fact]
    public void SkippedGroup_Should_StaySkipped_When_ChildrenPropagateUp()
    {
        var (group, _) = Load();
        group.Skip = true;
        TreeNodeViewModel root = BuildTree(group);

        // Mirrors MarkSubtreeSkipped: the group is stamped first, then each child.
        root.ExecutionStatus = "Skipped";
        foreach (var child in root.Children)
        {
            child.ExecutionStatus = "Skipped";
            child.PropagateStatusUp();
        }

        // Without Skipped in the aggregation the children reset their own parent to Idle.
        Assert.Equal("Skipped", root.ExecutionStatus);
    }

    [Fact]
    public void SkippedSibling_Should_NotDowngrade_GroupThatSucceeded()
    {
        var (group, _) = Load();
        TreeNodeViewModel root = BuildTree(group);

        root.Children[0].ExecutionStatus = "Skipped";
        root.Children[1].ExecutionStatus = "Success";
        root.Children[1].PropagateStatusUp();

        Assert.Equal("Success", root.ExecutionStatus);
    }

    [Fact]
    public void SetStatusRecursive_Should_NotPaintSkippedNodesRunning()
    {
        var (group, first) = Load();
        first.Skip = true;
        TreeNodeViewModel root = BuildTree(group);
        root.RecomputeSkipOrigins(SkipState.NotSkipped);

        // The execution paths pre-paint the whole subtree before dispatch.
        root.SetStatusRecursive("Running");

        Assert.Equal("Skipped", root.Children[0].ExecutionStatus);
        Assert.Equal("Running", root.Children[1].ExecutionStatus);
    }

    [Fact]
    public void SetStatusRecursive_Should_PaintInheritedChildrenSkipped_When_GroupSkipped()
    {
        var (group, _) = Load();
        group.Skip = true;
        TreeNodeViewModel root = BuildTree(group);
        root.RecomputeSkipOrigins(SkipState.NotSkipped);

        root.SetStatusRecursive("Running");

        Assert.Equal("Skipped", root.ExecutionStatus);
        Assert.All(root.Children, c => Assert.Equal("Skipped", c.ExecutionStatus));
    }

    [Fact]
    public void SetStatusRecursive_Should_StillResetSkippedNodesToIdle()
    {
        var (group, first) = Load();
        first.Skip = true;
        TreeNodeViewModel root = BuildTree(group);
        root.RecomputeSkipOrigins(SkipState.NotSkipped);

        // Only "Running" is guarded; a reset between runs must still clear the row.
        root.ResetStatus();

        Assert.All(root.Children, c => Assert.Equal("Idle", c.ExecutionStatus));
    }

    // ── 6. validator warning ──────────────────────────────────────────
    [Fact]
    public void Analyze_Should_Warn_When_EveryChildOfGroupIsSkipped()
    {
        WatchListConfig config = WatchListXmlParser.DeserializeWatchList(Xml)!;
        var group = (ActionGroupConfig)config.WatchItems[0].Events[0].Children[0];
        foreach (var child in group.Children)
            ((ISkippableNode)child).Skip = true;

        var issues = WatchListValidator.Analyze(config);

        Assert.Contains(issues, i =>
            i.Severity == WatchIssueSeverity.Warning &&
            i.Message.Contains("Every child", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_Should_NotWarn_When_OneChildStillRuns()
    {
        WatchListConfig config = WatchListXmlParser.DeserializeWatchList(Xml)!;
        var group = (ActionGroupConfig)config.WatchItems[0].Events[0].Children[0];
        ((ISkippableNode)group.Children[0]).Skip = true;

        var issues = WatchListValidator.Analyze(config);

        Assert.DoesNotContain(issues, i => i.Message.Contains("Every child", StringComparison.OrdinalIgnoreCase));
    }

    // ── helpers ───────────────────────────────────────────────────────

    private static WatchItemConfig Owner(ActionGroupConfig group) =>
        new()
        {
            Tag = "Nightly",
            Path = @"C:\drop",
            Filter = "*.trg",
            Events = { new EventConfig { Type = "Renamed", Children = { group } } },
        };

    /// <summary>
    /// Built by hand rather than via the tree factory: this exercises the cascade itself, with no WPF
    /// dependency and nothing to go stale if the factory changes.
    /// </summary>
    private static TreeNodeViewModel BuildTree(ActionGroupConfig group)
    {
        var root = new TreeNodeViewModel
        {
            NodeKind = NodeKinds.ActionGroup,
            Tag = group.Tag,
            ModelObject = group,
        };

        foreach (ActionConfig action in group.Children.OfType<ActionConfig>())
        {
            var child = new TreeNodeViewModel
            {
                NodeKind = NodeKinds.Action,
                Tag = action.Tag,
                ModelObject = action,
                Parent = root,
            };
            root.Children.Add(child);
        }

        return root;
    }
}
