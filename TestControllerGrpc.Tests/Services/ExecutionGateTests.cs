using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Workstream B / P24. The properties that matter: a skipped parent means children never dispatch, a skipped
/// node does not fail the run, and the cascade is never written to the children.
/// </summary>
public class ExecutionGateTests
{
    private readonly ExecutionGate _gate = new();

    private static ActionConfig Action(string tag, bool skip = false, string? reason = null) =>
        new() { Tag = tag, Command = "cmd", Skip = skip, SkipReason = reason };

    private static ActionGroupConfig Group(string tag, bool skip, params IActionNode[] children) =>
        new() { Tag = tag, Skip = skip, Children = [.. children] };

    private static WatchItemConfig WatchItem(bool skip, params IActionNode[] children) => new()
    {
        Tag = "Nightly",
        Skip = skip,
        Events = [new EventConfig { Type = "Renamed", Children = [.. children] }],
    };

    [Fact]
    public void Evaluate_Should_Run_When_NotSkipped()
    {
        Assert.True(_gate.Evaluate(Action("a"), SkipState.NotSkipped).ShouldRun);
    }

    [Fact]
    public void Evaluate_Should_Skip_When_NodeExplicitlySkipped()
    {
        ExecutionDecision decision = _gate.Evaluate(Action("a", skip: true, reason: "flaky"), SkipState.NotSkipped);

        Assert.False(decision.ShouldRun);
        Assert.Equal(SkipOrigin.Explicit, decision.Skip.Origin);
        Assert.Equal("flaky", decision.Skip.Reason);
    }

    [Fact]
    public void Evaluate_Should_Skip_When_AncestorSkipped_EvenIfNodeIsNot()
    {
        var inherited = new SkipState(SkipOrigin.Inherited, "parent off", "ActionGroup 'Setup'");

        ExecutionDecision decision = _gate.Evaluate(Action("child"), inherited);

        Assert.False(decision.ShouldRun);
        Assert.Equal(SkipOrigin.Inherited, decision.Skip.Origin);
        Assert.Equal("ActionGroup 'Setup'", decision.Skip.SkippedByNode);
    }

    [Fact]
    public void BuildManifest_Should_ListChildren_When_GroupSkipped()
    {
        WatchItemConfig wi = WatchItem(false,
            Group("Setup", skip: true, Action("a"), Action("b")),
            Action("standalone"));

        IReadOnlyList<SkippedNode> manifest = SkipEvaluator.BuildManifest(wi);

        // Group + its two children; the standalone action still runs.
        Assert.Equal(3, manifest.Count);
        Assert.Contains(manifest, m => m.Name == "Setup" && m.State.Origin == SkipOrigin.Explicit);
        Assert.Contains(manifest, m => m.Name == "a" && m.State.Origin == SkipOrigin.Inherited);
        Assert.DoesNotContain(manifest, m => m.Name == "standalone");
    }

    [Fact]
    public void BuildManifest_Should_NotMutateChildren()
    {
        var child = Action("a");
        WatchItemConfig wi = WatchItem(false, Group("Setup", skip: true, child));

        SkipEvaluator.BuildManifest(wi);

        // The cascade is computed; persisting it on children is the stale-state bug this guards against.
        Assert.False(child.Skip);
    }

    [Fact]
    public void BuildManifest_Should_BeEmpty_When_NothingSkipped()
    {
        Assert.Empty(SkipEvaluator.BuildManifest(WatchItem(false, Action("a"), Action("b"))));
    }

    [Fact]
    public void BuildManifest_Should_CascadeFromWatchItem()
    {
        WatchItemConfig wi = WatchItem(true, Action("a"));

        IReadOnlyList<SkippedNode> manifest = SkipEvaluator.BuildManifest(wi);

        Assert.Contains(manifest, m => m.NodeKind == "WatchItem");
        Assert.Contains(manifest, m => m.Name == "a" && m.State.Origin == SkipOrigin.Inherited);
    }

    [Theory]
    [InlineData("RevertWarmAgents", true)]
    [InlineData("RestoreSnapshot", true)]
    [InlineData("RebootNode", true)]
    [InlineData("RunSmokeTests", false)]
    public void SkippedNode_Should_FlagMachineStateOperations(string name, bool expected)
    {
        // Skipping a revert means every later phase runs on a machine that was never reset.
        var node = new SkippedNode("Action", name, new SkipState(SkipOrigin.Explicit, null, null));

        Assert.Equal(expected, node.IsMachineStateOperation);
    }

    [Fact]
    public void BuildManifest_Should_PreserveExecutionOrder()
    {
        WatchItemConfig wi = WatchItem(false,
            Action("first", skip: true),
            Group("second", skip: true, Action("third")));

        IReadOnlyList<SkippedNode> manifest = SkipEvaluator.BuildManifest(wi);

        Assert.Equal(["first", "second", "third"], manifest.Select(m => m.Name));
    }

    [Fact]
    public void Skipped_Should_BeDistinctOutcomeValue()
    {
        // Appended, never renumbered: this enum is persisted and crosses gRPC.
        Assert.Equal(5, (int)ActionOutcome.Skipped);
        Assert.Equal(1, (int)ActionOutcome.Success);
        Assert.Equal(2, (int)ActionOutcome.Failed);
    }
}
