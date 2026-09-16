using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

public class ConsolidatedRunReportBuilderTests
{
    private static readonly DateTime T0 = new(2026, 9, 16, 10, 0, 0, DateTimeKind.Utc);

    private static ExecutionSession Session(params ActionExecutionResult[] results)
    {
        var session = new ExecutionSession
        {
            WatchItemTag = "Revert 9 Nodes - Install SP2023R2SP2",
            EventType = "Renamed",
            StartedUtc = T0,
            ResolvedParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["_BuildNumber"] = "OAK_SP-2023-R2-SP2_20260915.5",
                ["_DropLocation"] = @"\\DevTFSBldoaksp\REPL\Ado\SP-2023-R2-SP2",
                ["_VCloudPassword"] = "super-secret-value",
            },
        };
        foreach (var r in results)
        {
            session.AddResult(r);
            session.TrackAgentAction(r);
        }
        session.CompletedUtc = T0.AddMinutes(84);
        return session;
    }

    private static ActionExecutionResult Act(
        string agent, ActionOutcome outcome, string groupPath, string tag = "step", int seconds = 60,
        DateTime? started = null) => new()
        {
            ActionTag = tag,
            AgentName = agent,
            GroupPath = groupPath,
            Outcome = outcome,
            StartedUtc = started ?? T0,
            Duration = TimeSpan.FromSeconds(seconds),
        };

    [Fact]
    public void Build_Should_ProjectPassedVerdict_When_EveryAgentSucceeded()
    {
        var report = ConsolidatedRunReportBuilder.Build(Session(
            Act("warmgr", ActionOutcome.Success, "PHASE 1 / WARM Pool"),
            Act("jvgr1", ActionOutcome.Success, "PHASE 1 / Sanity Pool")));

        Assert.True(report.Passed);
        Assert.Equal("PASSED", report.Verdict);
        Assert.Equal(2, report.AgentsPassed);
        Assert.Equal(2, report.AgentsTotal);
    }

    [Fact]
    public void Build_Should_FailVerdict_When_AnyAgentFailed()
    {
        var report = ConsolidatedRunReportBuilder.Build(Session(
            Act("warmgr", ActionOutcome.Success, "PHASE 1 / WARM Pool"),
            Act("jvgr1", ActionOutcome.Failed, "PHASE 1 / Sanity Pool")));

        Assert.False(report.Passed);
        Assert.Equal(1, report.AgentsPassed);
        Assert.Contains("1 of 2", report.Headline);
    }

    [Fact]
    public void Build_Should_UseSessionSpanForWallTime_When_AgentsRanInParallel()
    {
        // Nine agents each doing an hour of work, finished in 84 minutes of wall clock.
        var actions = Enumerable.Range(0, 9)
            .Select(i => Act($"agent{i}", ActionOutcome.Success, "PHASE 1 / Pool", seconds: 3600))
            .ToArray();

        var report = ConsolidatedRunReportBuilder.Build(Session(actions));

        Assert.Equal(TimeSpan.FromMinutes(84), report.WallTime);
    }

    [Fact]
    public void Build_Should_DeriveDynamicPhases_When_PipelineHasMultipleGroups()
    {
        var report = ConsolidatedRunReportBuilder.Build(Session(
            Act("warmgr", ActionOutcome.Success, "PHASE 1 - Revert / WARM Pool"),
            Act("warmgr", ActionOutcome.Success, "PHASE 3 - Install / WARM Pool")));

        Assert.Equal(["PHASE 1 - Revert", "PHASE 3 - Install"], report.Phases);
    }

    [Fact]
    public void Build_Should_MarkPhaseNotRun_When_AgentNeverEnteredIt()
    {
        var report = ConsolidatedRunReportBuilder.Build(Session(
            Act("warmgr", ActionOutcome.Success, "PHASE 1 / WARM Pool"),
            Act("jvgr1", ActionOutcome.Success, "PHASE 3 / Sanity Pool")));

        var warmgr = report.Agents.Single(a => a.AgentName == "warmgr");
        Assert.Equal(PhaseOutcome.Passed, warmgr.PhaseResults["PHASE 1"]);
        Assert.Equal(PhaseOutcome.NotRun, warmgr.PhaseResults["PHASE 3"]);
    }

    [Fact]
    public void Build_Should_MarkPhaseFailed_When_AnyActionInThatPhaseFailed()
    {
        var report = ConsolidatedRunReportBuilder.Build(Session(
            Act("warmgr", ActionOutcome.Success, "PHASE 1 / WARM Pool", "ok"),
            Act("warmgr", ActionOutcome.Failed, "PHASE 1 / WARM Pool", "boom")));

        var warmgr = report.Agents.Single();
        Assert.Equal(PhaseOutcome.Failed, warmgr.PhaseResults["PHASE 1"]);
        Assert.False(warmgr.Passed);
    }

    [Fact]
    public void Build_Should_TreatSkippedAsNeitherPassNorFail_When_PhaseWhollySkipped()
    {
        var report = ConsolidatedRunReportBuilder.Build(Session(
            Act("warmgr", ActionOutcome.Success, "PHASE 1 / WARM Pool"),
            Act("warmgr", ActionOutcome.Skipped, "PHASE 3 / WARM Pool")));

        var warmgr = report.Agents.Single();
        Assert.Equal(PhaseOutcome.Skipped, warmgr.PhaseResults["PHASE 3"]);
        Assert.True(warmgr.Passed);
        Assert.Equal(1, report.ActionsSkipped);
        Assert.Equal(1, report.ActionsSucceeded);
        Assert.Equal(2, report.ActionsTotal);
    }

    [Fact]
    public void Build_Should_NumberActionsInSequenceOrder_When_BagIsUnordered()
    {
        var report = ConsolidatedRunReportBuilder.Build(Session(
            Act("warmgr", ActionOutcome.Success, "PHASE 1 / WARM Pool", "first"),
            Act("warmgr", ActionOutcome.Success, "PHASE 1 / WARM Pool", "second"),
            Act("warmgr", ActionOutcome.Success, "PHASE 1 / WARM Pool", "third")));

        var names = report.Agents.Single().Actions.Select(a => a.Name).ToList();
        Assert.Equal(["first", "second", "third"], names);
        Assert.Equal([1, 2, 3], report.Agents.Single().Actions.Select(a => a.Index).ToList());
    }

    [Fact]
    public void Build_Should_ExposePoolFromSecondLevelGroup_When_PathIsNested()
    {
        var report = ConsolidatedRunReportBuilder.Build(Session(
            Act("warmgr", ActionOutcome.Success, "PHASE 1 - Revert / Revert WARM Pool (4 nodes) / Copy")));

        Assert.Equal("Revert WARM Pool (4 nodes)", report.Agents.Single().Pool);
    }

    [Fact]
    public void Build_Should_CopyOnlyWhitelistedParameters_When_ResolvedSetHoldsSecrets()
    {
        var report = ConsolidatedRunReportBuilder.Build(Session(
            Act("warmgr", ActionOutcome.Success, "PHASE 1 / WARM Pool")));

        Assert.Equal("OAK_SP-2023-R2-SP2_20260915.5", report.BuildNumber);
        Assert.StartsWith(@"\\DevTFSBldoaksp", report.DropLocation);

        // The report must carry no trace of a credential from ResolvedParameters.
        var flattened = string.Join("|", report.GetType().GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => p.GetValue(report) as string ?? ""));
        Assert.DoesNotContain("super-secret-value", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_Should_GroupControllerActions_When_ActionHasNoAgent()
    {
        var report = ConsolidatedRunReportBuilder.Build(Session(
            new ActionExecutionResult
            {
                ActionTag = "local step", AgentName = null, GroupPath = "PHASE 1",
                Outcome = ActionOutcome.Success, StartedUtc = T0, Duration = TimeSpan.FromSeconds(5),
            }));

        Assert.Equal("Controller", report.Agents.Single().AgentName);
    }

    [Fact]
    public void Build_Should_NotPass_When_SessionRecordedNoAgents()
    {
        var empty = new ExecutionSession { WatchItemTag = "Pipe", StartedUtc = T0, CompletedUtc = T0 };

        var report = ConsolidatedRunReportBuilder.Build(empty);

        Assert.False(report.Passed);
        Assert.Equal(0, report.AgentsTotal);
    }

    [Theory]
    [InlineData("Renamed", true)]
    [InlineData("Created", true)]
    [InlineData("Action:Copy Prepare-Agent.bat", false)]
    [InlineData("Group:WARM Pool", false)]
    [InlineData("Template:Install", false)]
    public void IsFullPipelineRun_Should_ExcludeOneOffRuns_When_EventTypeIsPrefixed(string eventType, bool expected)
    {
        var session = new ExecutionSession { WatchItemTag = "Pipe", EventType = eventType };

        Assert.Equal(expected, session.IsFullPipelineRun);
    }
}
