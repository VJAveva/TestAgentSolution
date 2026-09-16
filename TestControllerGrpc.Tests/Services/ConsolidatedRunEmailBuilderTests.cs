using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

public class ConsolidatedRunEmailBuilderTests
{
    private static readonly DateTime T0 = new(2026, 9, 16, 10, 0, 0, DateTimeKind.Utc);

    private static ExecutionSession Session(params ActionExecutionResult[] results)
    {
        var s = new ExecutionSession
        {
            WatchItemTag = "Revert 9 Nodes - Install SP2023R2SP2",
            EventType = "Renamed",
            StartedUtc = T0,
            ResolvedParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["_BuildNumber"] = "OAK_SP-2023-R2-SP2_20260915.5",
                ["_DropLocation"] = @"\\DevTFSBldoaksp\REPL\Ado",
                ["_VCloudPassword"] = "super-secret-value",
            },
        };
        foreach (var r in results) { s.AddResult(r); s.TrackAgentAction(r); }
        s.CompletedUtc = T0.AddMinutes(84);
        return s;
    }

    private static ActionExecutionResult Act(
        string agent, ActionOutcome outcome, string groupPath, string tag = "step",
        int seconds = 60, string? error = null) => new()
        {
            ActionTag = tag,
            AgentName = agent,
            GroupPath = groupPath,
            Outcome = outcome,
            StartedUtc = T0,
            Duration = TimeSpan.FromSeconds(seconds),
            ErrorMessage = error,
        };

    private static string Html(params ActionExecutionResult[] results) =>
        ConsolidatedRunEmailBuilder.BuildHtml(ConsolidatedRunReportBuilder.Build(Session(results)));

    [Fact]
    public void BuildHtml_Should_RenderDynamicPhaseColumns_When_PipelineHasGroups()
    {
        var html = Html(
            Act("warmgr", ActionOutcome.Success, "PHASE 1 - Revert / WARM Pool"),
            Act("warmgr", ActionOutcome.Success, "PHASE 3 - Install / WARM Pool"));

        Assert.Contains("PHASE 1 - REVERT", html, StringComparison.Ordinal);
        Assert.Contains("PHASE 3 - INSTALL", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHtml_Should_NeverContainSecrets_When_ParametersHoldCredentials()
    {
        var html = Html(Act("warmgr", ActionOutcome.Success, "PHASE 1 / WARM Pool"));

        Assert.DoesNotContain("super-secret-value", html, StringComparison.Ordinal);
        Assert.DoesNotContain("_VCloudPassword", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHtml_Should_EncodeMarkup_When_ActionNameContainsHtml()
    {
        var html = Html(Act("warmgr", ActionOutcome.Failed, "PHASE 1 / Pool",
            tag: "<script>alert('x')</script>", error: "bad <b>things</b> & worse"));

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&amp; worse", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHtml_Should_ExpandFailingAgentBeforePassingAgent_When_MixedResults()
    {
        var html = Html(
            Act("aaa-passing", ActionOutcome.Success, "PHASE 1 / Pool"),
            Act("zzz-failing", ActionOutcome.Failed, "PHASE 1 / Pool"));

        // Alphabetically the passing agent sorts first; the failing agent must still be presented first.
        Assert.True(html.IndexOf("zzz-failing", StringComparison.Ordinal)
                    < html.LastIndexOf("aaa-passing", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildHtml_Should_ShowErrorMessage_When_ActionFailed()
    {
        var html = Html(Act("warmgr", ActionOutcome.Failed, "PHASE 1 / Pool", error: "exit code 1603"));

        Assert.Contains("exit code 1603", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHtml_Should_RenderWallTimeAsHours_When_RunExceedsAnHour()
    {
        var html = Html(Act("warmgr", ActionOutcome.Success, "PHASE 1 / Pool"));

        Assert.Contains("01:24:00", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHtml_Should_OmitButtons_When_NoLinksConfigured()
    {
        var html = Html(Act("warmgr", ActionOutcome.Success, "PHASE 1 / Pool"));

        Assert.DoesNotContain("Open in TestController", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Open results folder", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHtml_Should_RenderConfiguredButtonsOnly_When_SomeLinksSupplied()
    {
        var report = ConsolidatedRunReportBuilder.Build(Session(
            Act("warmgr", ActionOutcome.Success, "PHASE 1 / Pool")));

        var html = ConsolidatedRunEmailBuilder.BuildHtml(report,
            new ConsolidatedRunEmailLinks(ResultsShareUrl: @"file://\\share\results"));

        Assert.Contains("Open results folder", html, StringComparison.Ordinal);
        Assert.DoesNotContain("View Report Card", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHtml_Should_SurviveEmptySession_When_NoActionsRecorded()
    {
        var empty = new ExecutionSession { WatchItemTag = "Pipe", StartedUtc = T0, CompletedUtc = T0 };

        var html = ConsolidatedRunEmailBuilder.BuildHtml(ConsolidatedRunReportBuilder.Build(empty));

        Assert.Contains("Pipe", html, StringComparison.Ordinal);
        Assert.Contains("0 / 0", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHtml_Should_NoteSkippedActions_When_PipelineSkippedSteps()
    {
        var html = Html(
            Act("warmgr", ActionOutcome.Success, "PHASE 1 / Pool"),
            Act("warmgr", ActionOutcome.Skipped, "PHASE 3 / Pool"));

        Assert.Contains("skipped", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildSubject_Should_LeadWithVerdict_When_RunFailed()
    {
        var report = ConsolidatedRunReportBuilder.Build(Session(
            Act("warmgr", ActionOutcome.Failed, "PHASE 1 / Pool")));

        var subject = ConsolidatedRunEmailBuilder.BuildSubject(report);

        Assert.StartsWith("[FAILED]", subject, StringComparison.Ordinal);
        Assert.Contains("OAK_SP-2023-R2-SP2_20260915.5", subject, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSubject_Should_SayPassed_When_EveryAgentSucceeded()
    {
        var report = ConsolidatedRunReportBuilder.Build(Session(
            Act("warmgr", ActionOutcome.Success, "PHASE 1 / Pool")));

        Assert.StartsWith("[PASSED]", ConsolidatedRunEmailBuilder.BuildSubject(report), StringComparison.Ordinal);
    }
}
