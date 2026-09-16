using TestControllerGrpc.Models;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Guards the data the consolidated run email is built from. Each case here is a way the report can come out
/// plausible but wrong: durations summed across parallel agents, bag ordering, and Skipped counted as a pass.
/// </summary>
public class ConsolidatedRunDataTests
{
    private static ActionExecutionResult Action(
        string agent, ActionOutcome outcome, DateTime startedUtc, int seconds,
        string groupPath = "", string tag = "step") => new()
        {
            ActionTag = tag,
            AgentName = agent,
            GroupPath = groupPath,
            Outcome = outcome,
            StartedUtc = startedUtc,
            Duration = TimeSpan.FromSeconds(seconds),
        };

    private static readonly DateTime T0 = new(2026, 9, 16, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void WallTime_Should_UseOverlap_When_AgentActionsRunInParallel()
    {
        // Two overlapping 10-minute actions span 15 minutes of wall time, not 20.
        var summary = new AgentSessionSummary { AgentName = "warmgr" };
        summary.Actions.Add(Action("warmgr", ActionOutcome.Success, T0, 600));
        summary.Actions.Add(Action("warmgr", ActionOutcome.Success, T0.AddMinutes(5), 600));

        Assert.Equal(TimeSpan.FromMinutes(15), summary.WallTime);
    }

    [Fact]
    public void WallTime_Should_ReturnZero_When_AgentHasNoActions()
    {
        var summary = new AgentSessionSummary { AgentName = "idle" };

        Assert.Equal(TimeSpan.Zero, summary.WallTime);
    }

    [Fact]
    public void OrderedActions_Should_FollowSequence_When_BagEnumerationIsUnordered()
    {
        var summary = new AgentSessionSummary { AgentName = "jvhist" };
        var first = Action("jvhist", ActionOutcome.Success, T0, 1, tag: "first");
        var second = Action("jvhist", ActionOutcome.Success, T0, 1, tag: "second");
        var third = Action("jvhist", ActionOutcome.Success, T0, 1, tag: "third");
        summary.Actions.Add(third);
        summary.Actions.Add(first);
        summary.Actions.Add(second);

        var ordered = summary.OrderedActions.Select(a => a.ActionTag).ToList();

        Assert.Equal(["first", "second", "third"], ordered);
    }

    [Fact]
    public void Status_Should_BeSuccess_When_AgentHasSkippedActions()
    {
        var summary = new AgentSessionSummary { AgentName = "warmbak" };
        summary.Actions.Add(Action("warmbak", ActionOutcome.Success, T0, 1));
        summary.Actions.Add(Action("warmbak", ActionOutcome.Skipped, T0, 0));

        Assert.Equal("Success", summary.Status);
        Assert.Equal(1, summary.SkippedCount);
        Assert.Equal(1, summary.SucceededCount);
    }

    [Theory]
    [InlineData(ActionOutcome.Failed)]
    [InlineData(ActionOutcome.Terminated)]
    [InlineData(ActionOutcome.TimedOut)]
    public void Status_Should_BeFailed_When_AnyActionIsRetryable(ActionOutcome outcome)
    {
        var summary = new AgentSessionSummary { AgentName = "jvgr1" };
        summary.Actions.Add(Action("jvgr1", ActionOutcome.Success, T0, 1));
        summary.Actions.Add(Action("jvgr1", outcome, T0, 1));

        Assert.Equal("Failed", summary.Status);
    }

    [Fact]
    public void SkippedCount_Should_ExplainGap_When_SucceededPlusFailedIsBelowTotal()
    {
        var session = new ExecutionSession { WatchItemTag = "Pipe" };
        session.AddResult(Action("a", ActionOutcome.Success, T0, 1));
        session.AddResult(Action("a", ActionOutcome.Failed, T0, 1));
        session.AddResult(Action("a", ActionOutcome.Skipped, T0, 0));

        Assert.Equal(3, session.TotalActions);
        Assert.Equal(1, session.SucceededCount);
        Assert.Equal(1, session.FailedCount);
        Assert.Equal(1, session.SkippedCount);
        Assert.NotEqual(session.TotalActions, session.SucceededCount + session.FailedCount);
    }

    [Fact]
    public void TopGroup_Should_ReturnOutermostTag_When_GroupPathIsNested()
    {
        var nested = Action("a", ActionOutcome.Success, T0, 1,
            groupPath: "PHASE 1 - Revert All Nine Nodes / Revert WARM Pool (4 nodes)");

        Assert.Equal("PHASE 1 - Revert All Nine Nodes", nested.TopGroup);
    }

    [Fact]
    public void TopGroup_Should_BeEmpty_When_ActionHasNoGroup()
    {
        Assert.Equal("", Action("a", ActionOutcome.Success, T0, 1).TopGroup);
    }

    [Fact]
    public void GroupPhases_Should_BeDistinctInExecutionOrder_When_PhasesRepeat()
    {
        var session = new ExecutionSession { WatchItemTag = "Pipe" };
        session.AddResult(Action("a", ActionOutcome.Success, T0, 1, groupPath: "PHASE 1 / WARM Pool"));
        session.AddResult(Action("b", ActionOutcome.Success, T0, 1, groupPath: "PHASE 1 / Sanity Pool"));
        session.AddResult(Action("a", ActionOutcome.Success, T0, 1, groupPath: "PHASE 3 / WARM Pool"));
        session.AddResult(Action("b", ActionOutcome.Success, T0, 1, groupPath: "PHASE 3 / Sanity Pool"));

        Assert.Equal(["PHASE 1", "PHASE 3"], session.GroupPhases);
    }

    [Fact]
    public void GroupPhases_Should_IgnoreUngroupedActions_When_SomeRunDirectlyUnderTheEvent()
    {
        var session = new ExecutionSession { WatchItemTag = "Pipe" };
        session.AddResult(Action("a", ActionOutcome.Success, T0, 1));
        session.AddResult(Action("a", ActionOutcome.Success, T0, 1, groupPath: "PHASE 1"));

        Assert.Equal(["PHASE 1"], session.GroupPhases);
    }

    [Fact]
    public void WallTime_Should_UseSessionSpan_When_ActionsRanInParallel()
    {
        // Nine agents x 10 minutes of work finishing in 12 minutes of wall clock.
        var session = new ExecutionSession { WatchItemTag = "Pipe", StartedUtc = T0 };
        for (var i = 0; i < 9; i++)
            session.AddResult(Action($"agent{i}", ActionOutcome.Success, T0, 600));
        session.CompletedUtc = T0.AddMinutes(12);

        Assert.Equal(TimeSpan.FromMinutes(12), session.WallTime);
    }
}
