using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Ado;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Risk;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Tests.Impact;

/// <summary>
/// Unit tests for <see cref="RegressionImpactMatcher"/> — the adapter that turns a <see cref="SubsystemRow"/>
/// into the impact engine's inputs and projects the result to display-ready <see cref="ImpactedTestCaseMatch"/>.
/// A fake <see cref="IImpactTestMappingService"/> isolates the projection and resilience from the real cascade.
/// </summary>
public class RegressionImpactMatcherTests
{
    private static SubsystemRow Row(string comp = "Cybersecurity", params RegressionChangeRef[] changes) =>
        new(
            Component: comp, Subsystem: comp, Category: RegressionCategoryKind.Runtime,
            CategoryConfidence: RegressionEvidenceKind.Declared, FilesModified: ["a.cs"],
            TotalFilesModified: 1, Changes: changes, RiskTier: "succeeded",
            AutomatedSuites: [], ManualSuites: [], EstimatedMinutes: 0, IsEstimate: true);

    private static RegressionChangeRef ChangeWithWorkItem(int wiId) =>
        new(Guid.NewGuid().ToString("N"), "Merged PR", DateTimeOffset.UtcNow, ["a.cs"],
            [new RegressionWorkItemRef(wiId, RegressionWorkItemKind.Story, "story", null)],
            RegressionChangeKind.PullRequest);

    private static MappedTestCase Mapped(int id, string title, int featureId, int grade, double score, string reason, string? steps = null) =>
        new(
            new TestCaseCandidate(new AdoWorkItemRef(id, "Test Case", title, null, "Ready", 1), steps, "Automated", featureId, []),
            featureId, score, MappingConfidence.Observed,
            new RelevanceJudgement(grade, 0.9, reason, ["signal"]), null, []);

    private static RegressionImpactMatcher Matcher(IImpactTestMappingService engine, string org = "AVEVA-VSTS") =>
        new(engine, new RegressionRiskScorer(Options.Create(new ImpactMappingOptions())),
            Options.Create(new AdoOptions { Organization = org }), new NoopAppLogger());

    [Fact]
    public async Task MatchAsync_Should_ProjectEngineResults_When_TestCasesSelected()
    {
        var engine = new FakeEngine(_ => [Mapped(4536616, "BootStrap.TC24", 0, 2, 0.84, "exercises bootstrap sync", "step 1")]);

        var matches = await Matcher(engine).MatchAsync(Row(changes: ChangeWithWorkItem(100)), CancellationToken.None);

        var m = Assert.Single(matches);
        Assert.Equal("Cybersecurity", m.ImpactedArea);
        Assert.Equal(4536616, m.TestCaseId);
        Assert.Equal("BootStrap.TC24", m.TestCaseTitle);
        Assert.Equal("partial", m.MatchType);
        Assert.Equal(84, m.ConfidencePercent);
        Assert.Equal("exercises bootstrap sync", m.MatchReason);
        Assert.Equal("https://dev.azure.com/AVEVA-VSTS/_workitems/edit/4536616", m.TestCaseUrl);
        Assert.Equal("step 1", m.Description);
    }

    [Fact]
    public async Task MatchAsync_Should_MarkFullMatch_When_JudgementGradeIsThree()
    {
        var engine = new FakeEngine(_ => [Mapped(1, "t", 42, 3, 0.99, "strong")]);

        var matches = await Matcher(engine).MatchAsync(Row(changes: ChangeWithWorkItem(1)), CancellationToken.None);

        Assert.Equal("full", Assert.Single(matches).MatchType);
        Assert.Equal(42, matches[0].ParentFeatureId);
    }

    [Fact]
    public async Task MatchAsync_Should_OrderByConfidenceDescending_When_MultipleSelected()
    {
        var engine = new FakeEngine(_ =>
        [
            Mapped(1, "low", 0, 1, 0.40, "r"),
            Mapped(2, "high", 0, 2, 0.90, "r"),
            Mapped(3, "mid", 0, 2, 0.70, "r"),
        ]);

        var matches = await Matcher(engine).MatchAsync(Row(changes: ChangeWithWorkItem(1)), CancellationToken.None);

        Assert.Equal([90, 70, 40], matches.Select(m => m.ConfidencePercent).ToArray());
    }

    [Fact]
    public async Task MatchAsync_Should_AttachLinkedWorkItems_When_AnchoredByLinkedWorkItem()
    {
        // The reviewer's question is "why is this test in my list?" — answer with the Bug/Story it verifies.
        var bug = new RegressionWorkItemRef(3522032, RegressionWorkItemKind.Bug, "Bootstrap crash", "https://ado/wi/3522032");
        var row = Row(changes: new RegressionChangeRef(
            "c1", "Merged PR", DateTimeOffset.UtcNow, ["a.cs"], [bug], RegressionChangeKind.PullRequest));
        var engine = new FakeEngine(
            _ => [Mapped(4536616, "BootStrap.TC24", 0, 3, 0.9, "anchor edge")],
            anchors: [new AnchorEdge(4536616, 3522032, AnchorSource.LinkedWorkItem, 1.0, "Linked from work item 3522032.")]);

        var matches = await Matcher(engine).MatchAsync(row, CancellationToken.None);

        RegressionWorkItemRef linked = Assert.Single(Assert.Single(matches).LinkedWorkItems!);
        Assert.Equal(3522032, linked.Id);
        Assert.Equal(RegressionWorkItemKind.Bug, linked.Kind);
        Assert.Equal("Bootstrap crash", linked.Title);
    }

    [Fact]
    public async Task MatchAsync_Should_LeaveLinkedWorkItemsEmpty_When_MatchedByRetrievalOnly()
    {
        var engine = new FakeEngine(_ => [Mapped(4536616, "BootStrap.TC24", 0, 2, 0.8, "text match")]);

        var matches = await Matcher(engine).MatchAsync(Row(changes: ChangeWithWorkItem(100)), CancellationToken.None);

        Assert.True(Assert.Single(matches).LinkedWorkItems is null or { Count: 0 });
    }

    [Fact]
    public async Task MatchAsync_Should_ReturnEmpty_When_EngineThrows()
    {
        var engine = new FakeEngine(_ => throw new InvalidOperationException("no anchors"));

        var matches = await Matcher(engine).MatchAsync(Row(changes: ChangeWithWorkItem(1)), CancellationToken.None);

        Assert.Empty(matches);
    }

    [Fact]
    public async Task MatchAsync_Should_Rethrow_When_IndexUnavailable()
    {
        // An unusable index must NOT be reported as "no impacted test cases" — that is indistinguishable
        // from a genuine empty result, and test selection here is recall-biased.
        var health = new ImpactIndexHealth
        {
            Status = ImpactIndexStatus.Missing,
            IndexFilePath = @"C:\ProgramData\TestAgentSolution\ImpactIndex\impact-index.db",
            Message = "No impact index. Rebuild with: powershell -File deploy\\Rebuild-ImpactIndex.ps1",
        };
        var engine = new FakeEngine(_ => throw new ImpactIndexUnavailableException(health));

        var ex = await Assert.ThrowsAsync<ImpactIndexUnavailableException>(
            () => Matcher(engine).MatchAsync(Row(changes: ChangeWithWorkItem(1)), CancellationToken.None));

        Assert.Equal(ImpactIndexStatus.Missing, ex.Health.Status);
        Assert.Contains("Rebuild-ImpactIndex.ps1", ex.Message);
    }

    [Fact]
    public async Task MatchManyAsync_Should_Rethrow_When_IndexUnavailable()
    {
        var health = new ImpactIndexHealth
        {
            Status = ImpactIndexStatus.Corrupt,
            IndexFilePath = "x",
            Message = "corrupt",
        };
        var engine = new FakeEngine(_ => throw new ImpactIndexUnavailableException(health));
        var rows = new[] { Row("A", changes: ChangeWithWorkItem(1)), Row("B", changes: ChangeWithWorkItem(2)) };

        await Assert.ThrowsAsync<ImpactIndexUnavailableException>(
            () => Matcher(engine).MatchManyAsync(rows, CancellationToken.None));
    }

    [Fact]
    public async Task MatchAsync_Should_SkipEngine_When_NoChangesOrFiles()
    {
        var engine = new FakeEngine(_ => throw new Exception("engine must not be called"));
        var empty = new SubsystemRow(
            Component: "Empty", Subsystem: "Empty", Category: RegressionCategoryKind.Runtime,
            CategoryConfidence: RegressionEvidenceKind.Declared, FilesModified: [],
            TotalFilesModified: 0, Changes: [], RiskTier: "succeeded",
            AutomatedSuites: [], ManualSuites: [], EstimatedMinutes: 0, IsEstimate: true);

        var matches = await Matcher(engine).MatchAsync(empty, CancellationToken.None);

        Assert.Empty(matches);
    }

    [Fact]
    public async Task MatchAsync_Should_OmitUrl_When_OrganizationMissing()
    {
        var engine = new FakeEngine(_ => [Mapped(7, "t", 0, 2, 0.5, "r")]);

        var matches = await Matcher(engine, org: "").MatchAsync(Row(changes: ChangeWithWorkItem(1)), CancellationToken.None);

        Assert.Null(Assert.Single(matches).TestCaseUrl);
    }

    [Fact]
    public async Task MatchManyAsync_Should_PreserveComponentOrder()
    {
        var engine = new FakeEngine(area => [Mapped(1, $"{area.DisplayName}-tc", 0, 2, 0.5, "r")]);
        var rows = new[] { Row("Alpha", ChangeWithWorkItem(1)), Row("Beta", ChangeWithWorkItem(2)), Row("Gamma", ChangeWithWorkItem(3)) };

        var matches = await Matcher(engine).MatchManyAsync(rows, CancellationToken.None);

        Assert.Equal(["Alpha", "Beta", "Gamma"], matches.Select(m => m.ImpactedArea).ToArray());
    }

    [Fact]
    public async Task MatchAsync_Should_FeedWorkItemTitlesAsPrNarrative()
    {
        ChangePayload? captured = null;
        var engine = new CapturingEngine(p => captured = p);
        var change = new RegressionChangeRef("c1", "Merged PR 1: fix", DateTimeOffset.UtcNow, ["a.cs"],
            [new RegressionWorkItemRef(5011025, RegressionWorkItemKind.Bug, "Non-Warm Redundant Engine LMX Rejection", null)],
            RegressionChangeKind.PullRequest);

        await Matcher(engine).MatchAsync(Row(changes: change), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Contains("Non-Warm Redundant Engine LMX Rejection", captured!.PrDescription);
        Assert.Contains(5011025, captured.LinkedWorkItemIds);
    }

    private sealed class CapturingEngine(Action<ChangePayload> capture) : IImpactTestMappingService
    {
        public Task<ImpactMappingResult> MapAsync(ImpactedArea area, ChangePayload payload, SelectionTier tier, CancellationToken ct)
        {
            capture(payload);
            return Task.FromResult(new ImpactMappingResult(
                area, [], [], [], [], new AnchorResult([], 0, false),
                new SelectionDiagnostics(TimeSpan.Zero, TimeSpan.Zero, 0, 0, 0, null),
                tier, false, [], TimeSpan.Zero, Guid.NewGuid()));
        }

        public async IAsyncEnumerable<ImpactMappingProgress> MapWithProgressAsync(
            ImpactedArea area, ChangePayload payload, SelectionTier tier, [EnumeratorCancellation] CancellationToken ct)
        {
            ImpactMappingResult result = await MapAsync(area, payload, tier, ct);
            yield return new ImpactMappingProgress("Done", 1, 1, "done", result);
        }
    }

    private sealed class FakeEngine(
        Func<ImpactedArea, IReadOnlyList<MappedTestCase>> select,
        IReadOnlyList<AnchorEdge>? anchors = null) : IImpactTestMappingService
    {
        public Task<ImpactMappingResult> MapAsync(ImpactedArea area, ChangePayload payload, SelectionTier tier, CancellationToken ct)
        {
            IReadOnlyList<MappedTestCase> selected = select(area);
            return Task.FromResult(new ImpactMappingResult(
                area, [], [], selected, [], new AnchorResult(anchors ?? [], 0, false),
                new SelectionDiagnostics(TimeSpan.Zero, TimeSpan.Zero, 0, 0, 0, null),
                tier, false, [], TimeSpan.Zero, Guid.NewGuid()));
        }

        public async IAsyncEnumerable<ImpactMappingProgress> MapWithProgressAsync(
            ImpactedArea area, ChangePayload payload, SelectionTier tier, [EnumeratorCancellation] CancellationToken ct)
        {
            ImpactMappingResult result = await MapAsync(area, payload, tier, ct);
            yield return new ImpactMappingProgress("Done", 1, 1, "done", result);
        }
    }

    private sealed class NoopAppLogger : IAppLogger
    {
#pragma warning disable CS0067
        public event Action<AppLogEntry>? EntryAdded;
#pragma warning restore CS0067
        public void Log(LogLevel level, string category, string message, Exception? ex = null) { }
        public void Log(LogLevel level, string category, string message, string? correlationId, long elapsedMs = 0, Exception? ex = null) { }
        public void LogStructured(LogLevel level, string category, string message, string? agent = null, string? runId = null, string? pipeline = null, string? action = null, long elapsedMs = 0, Exception? ex = null) { }
        public void Info(string category, string message) { }
        public void Warn(string category, string message) { }
        public void Error(string category, string message, Exception? ex = null) { }
        public IReadOnlyList<AppLogEntry> GetRecentEntries(int count = 500) => [];
    }
}
