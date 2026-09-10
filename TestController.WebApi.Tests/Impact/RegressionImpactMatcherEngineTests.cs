using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Ado;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Ado;
using TestControllerGrpc.Core.Impact.Anchors;
using TestControllerGrpc.Core.Impact.Execution;
using TestControllerGrpc.Core.Impact.Features;
using TestControllerGrpc.Core.Impact.Index;
using TestControllerGrpc.Core.Impact.Learning;
using TestControllerGrpc.Core.Impact.Query;
using TestControllerGrpc.Core.Impact.Ranking;
using TestControllerGrpc.Core.Impact.Rerank;
using TestControllerGrpc.Core.Impact.Retrieval;
using TestControllerGrpc.Core.Impact.Risk;
using TestControllerGrpc.Core.Impact.Selection;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Tests.Impact;

/// <summary>
/// End-to-end tests wiring the REAL <see cref="ImpactTestMappingService"/> behind
/// <see cref="RegressionImpactMatcher"/> to prove the adapter feeds a <see cref="SubsystemRow"/> into the
/// engine correctly. Together these show that "nothing is retrieved" is a data/index gap (an empty retrieval
/// index and no anchor-worthy links) rather than a bug in the mapping algorithm: seed the index (or supply
/// enough linked child test cases) and matches flow through.
/// </summary>
public sealed class RegressionImpactMatcherEngineTests
{
    private static SubsystemRow Row(IReadOnlyList<string>? regressionAreas = null, params int[] workItemIds) =>
        new(
            Component: "Galaxy Deploy", Subsystem: "Deploy", Category: RegressionCategoryKind.Runtime,
            CategoryConfidence: RegressionEvidenceKind.Declared,
            FilesModified: ["src/Deploy/GalaxyEngine.cs"], TotalFilesModified: 1,
            Changes:
            [
                new RegressionChangeRef(
                    "c1", "Deploy the galaxy engine now", DateTimeOffset.UtcNow, ["src/Deploy/GalaxyEngine.cs"],
                    workItemIds.Select(id => new RegressionWorkItemRef(id, RegressionWorkItemKind.Story, $"WI {id}", null)).ToArray(),
                    RegressionChangeKind.PullRequest),
            ],
            RiskTier: "succeeded", AutomatedSuites: [], ManualSuites: [], EstimatedMinutes: 0, IsEstimate: true,
            RegressionAreas: regressionAreas);

    private static RegressionImpactMatcher Matcher(IImpactTestMappingService engine) =>
        new(engine, new RegressionRiskScorer(Options.Create(new ImpactMappingOptions())),
            Options.Create(new AdoOptions { Organization = "AVEVA-VSTS" }), new NoopAppLogger());

    [Fact]
    public async Task MatchAsync_Should_RetrieveMatches_When_IndexIsPopulated()
    {
        // A seeded retrieval index (the piece missing in the live "nothing retrieved" report) + ADO corpus.
        var store = new FakeIndexStore(
            Snapshot(IndexKind.Feature, (900, ["galaxy", "deploy", "engine"], null)),
            Snapshot(IndexKind.TestCase, (101, ["galaxy", "deploy"], null), (102, ["deploy"], null)));
        ImpactTestMappingService engine = BuildEngine(new FakeAnchors(new AnchorResult([], 0, false)), store, AdoWithCorpus());

        var matches = await Matcher(engine).MatchAsync(Row(workItemIds: 900), CancellationToken.None);

        Assert.NotEmpty(matches);
        Assert.All(matches, m => Assert.Equal("Galaxy Deploy", m.ImpactedArea));
        Assert.All(matches, m => Assert.StartsWith("https://dev.azure.com/AVEVA-VSTS/_workitems/edit/", m.TestCaseUrl));
        Assert.Contains(matches, m => m.TestCaseId is 101 or 102);
    }

    [Fact]
    public async Task MatchAsync_Should_RetrieveViaAnchors_When_IndexEmptyButLinkedTestCasesExist()
    {
        // Empty index → retrieval yields nothing; the change's linked work item has 5 child test cases,
        // enough for the anchor early-exit gate (MinAnchorsForEarlyExit=5) so matches still flow.
        var emptyIndex = new FakeIndexStore(Snapshot(IndexKind.Feature), Snapshot(IndexKind.TestCase));
        FakeAdo ado = AdoWithCorpus();
        ado.Children[900] = [101, 102, 103, 104, 105];
        ado.TestCases[104] = new TestCaseCandidate(new AdoWorkItemRef(104, "Test Case", "Galaxy deploy 4", null, "Design", 1), "steps", "Automated", 900, []);
        ado.TestCases[105] = new TestCaseCandidate(new AdoWorkItemRef(105, "Test Case", "Galaxy deploy 5", null, "Design", 1), "steps", "Automated", 900, []);
        var anchors = new AnchorEdgeProvider(ado, new FakeOutcomes(), NullDeclaredMappingSource.Instance,
            Options.Create(new ImpactMappingOptions()), new NoopAppLogger());
        ImpactTestMappingService engine = BuildEngine(anchors, emptyIndex, ado);

        // No declared regression areas → anchor coverage is 1.0 so the 5 links satisfy the early-exit gate.
        var matches = await Matcher(engine).MatchAsync(Row(workItemIds: 900), CancellationToken.None);

        Assert.NotEmpty(matches);
        Assert.True(matches.Count >= 5);
    }

    [Fact]
    public async Task MatchAsync_Should_SurfaceAnchorTestCases_When_BelowEarlyExit_AndIndexEmpty()
    {
        // Only 3 linked child test cases (< MinAnchorsForEarlyExit=5), so early-exit cannot fire. They must
        // still surface via the full path now that anchors are folded into the candidate set.
        var emptyIndex = new FakeIndexStore(Snapshot(IndexKind.Feature), Snapshot(IndexKind.TestCase));
        FakeAdo ado = AdoWithCorpus(); // Children[900] = [101,102,103]
        var anchors = new AnchorEdgeProvider(ado, new FakeOutcomes(), NullDeclaredMappingSource.Instance,
            Options.Create(new ImpactMappingOptions()), new NoopAppLogger());
        ImpactTestMappingService engine = BuildEngine(anchors, emptyIndex, ado);

        var matches = await Matcher(engine).MatchAsync(Row(regressionAreas: ["Deploy", "Galaxy"], workItemIds: 900), CancellationToken.None);

        Assert.NotEmpty(matches);
        Assert.All(matches, m => Assert.Contains(m.TestCaseId, new[] { 101, 102, 103 }));
    }

    [Fact]
    public async Task MatchAsync_Should_ReturnEmpty_When_IndexEmptyAndNoLinkedTestCases()
    {
        // The live situation: empty index, and the change's work item resolves to no child test cases.
        var emptyIndex = new FakeIndexStore(Snapshot(IndexKind.Feature), Snapshot(IndexKind.TestCase));
        var ado = new FakeAdo(); // no features, test cases or children
        var anchors = new AnchorEdgeProvider(ado, new FakeOutcomes(), NullDeclaredMappingSource.Instance,
            Options.Create(new ImpactMappingOptions()), new NoopAppLogger());
        ImpactTestMappingService engine = BuildEngine(anchors, emptyIndex, ado);

        var matches = await Matcher(engine).MatchAsync(Row(workItemIds: 900), CancellationToken.None);

        Assert.Empty(matches);
    }

    [Fact]
    public async Task MatchAsync_Should_SurfaceAnchorTestCases_When_IndexUnusableAndLinkedTestCasesExist()
    {
        // The live production state: impact-index.db exists but holds 0 documents, so the store throws instead
        // of returning an empty snapshot. Anchors come from ADO links and need no index, so they must survive.
        var health = new ImpactIndexHealth
        {
            Status = ImpactIndexStatus.Empty,
            IndexFilePath = @"C:\ProgramData\TestAgentSolution\ImpactIndex\impact-index.db",
            Message = "contains 0 documents",
        };
        FakeAdo ado = AdoWithCorpus();
        var anchors = new AnchorEdgeProvider(ado, new FakeOutcomes(), NullDeclaredMappingSource.Instance,
            Options.Create(new ImpactMappingOptions()), new NoopAppLogger());
        ImpactTestMappingService engine = BuildEngine(anchors, new ThrowingIndexStore(health), ado);

        var matches = await Matcher(engine).MatchAsync(Row(regressionAreas: ["Deploy"], workItemIds: 900), CancellationToken.None);

        Assert.NotEmpty(matches);
        Assert.All(matches, m => Assert.Contains(m.TestCaseId, new[] { 101, 102, 103 }));
    }

    [Fact]
    public async Task MatchAsync_Should_Rethrow_When_IndexUnusableAndNoAnchors()
    {
        // With no anchors there is nothing to answer from, so the index problem must stay loud rather than
        // degrade into an empty list that reads as "no impacted tests".
        var health = new ImpactIndexHealth
        {
            Status = ImpactIndexStatus.Empty, IndexFilePath = "x", Message = "contains 0 documents",
        };
        var ado = new FakeAdo();
        var anchors = new AnchorEdgeProvider(ado, new FakeOutcomes(), NullDeclaredMappingSource.Instance,
            Options.Create(new ImpactMappingOptions()), new NoopAppLogger());
        ImpactTestMappingService engine = BuildEngine(anchors, new ThrowingIndexStore(health), ado);

        await Assert.ThrowsAsync<ImpactIndexUnavailableException>(
            () => Matcher(engine).MatchAsync(Row(workItemIds: 900), CancellationToken.None));
    }

    // ── Engine wiring + fakes (mirrors ImpactTestMappingServiceTests) ────────────────────────────

    private static ImpactTestMappingService BuildEngine(IAnchorEdgeProvider anchors, IRetrievalIndexStore store, FakeAdo ado)
    {
        IOptions<ImpactMappingOptions> opt = Options.Create(new ImpactMappingOptions());
        var logger = new NoopAppLogger();
        return new ImpactTestMappingService(
            anchors, new ChangeDocumentBuilder(opt), new FakeHyde(), new KeywordExtractor(opt),
            NullEmbeddingProvider.Instance, store, new HybridRetriever(opt, logger), new FeatureRanker(opt),
            new ParentFeatureResolver(ado, logger), new FeatureMerger(opt), new FanOutNormalizer(opt), ado,
            new PassThroughReranker(), new LinearScoreCalibrator(), new BudgetedDiversitySelector(opt),
            new CoverageGapDetector(), new FakeOutcomes(), new RunPlanWriter(opt, logger), opt, logger);
    }

    private static FakeAdo AdoWithCorpus()
    {
        var ado = new FakeAdo();
        ado.Features[900] = new FeatureCandidate(new AdoWorkItemRef(900, "Feature", "Galaxy Deploy Engine", "Proj\\Deploy", "Active", 1), "desc", FeatureDiscoveryPath.None, [], 3);
        ado.TestCases[101] = new TestCaseCandidate(new AdoWorkItemRef(101, "Test Case", "Galaxy deploy smoke", null, "Design", 1), "steps", "Automated", 900, []);
        ado.TestCases[102] = new TestCaseCandidate(new AdoWorkItemRef(102, "Test Case", "Deploy rollback", null, "Design", 1), "steps", "Automated", 900, []);
        ado.TestCases[103] = new TestCaseCandidate(new AdoWorkItemRef(103, "Test Case", "Galaxy child test", null, "Design", 1), "steps", "Automated", 900, []);
        ado.Children[900] = [101, 102, 103];
        return ado;
    }

    private static IndexSnapshot Snapshot(IndexKind kind, params (int id, string[] terms, float[]? vector)[] docs)
    {
        SnapshotDocument[] snapshotDocs = docs
            .Select(d => new SnapshotDocument(d.id, kind, d.terms.Length, 0,
                d.terms.GroupBy(t => t, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
                d.vector))
            .ToArray();

        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (SnapshotDocument doc in snapshotDocs)
            foreach (string term in doc.TermFrequencies.Keys)
                df[term] = df.GetValueOrDefault(term) + 1;

        double avg = snapshotDocs.Length == 0 ? 1 : snapshotDocs.Average(d => (double)d.Length);
        return new IndexSnapshot(snapshotDocs.Length, avg, df, snapshotDocs);
    }

    private sealed class FakeAnchors(AnchorResult result) : IAnchorEdgeProvider
    {
        public Task<AnchorResult> GetAnchorsAsync(ImpactedArea area, ChangePayload payload, CancellationToken ct) => Task.FromResult(result);
    }

    private sealed class FakeHyde : IHydeQueryGenerator
    {
        public Task<HydeQuery> GenerateAsync(ChangeDocument doc, CancellationToken ct)
            => Task.FromResult(new HydeQuery("galaxy deploy summary", ["Verify galaxy deploy"], "deploy the galaxy", ["deployment"]));
    }

    private sealed class FakeIndexStore(IndexSnapshot feature, IndexSnapshot testCase) : IRetrievalIndexStore
    {
        public Task<IndexSnapshot> GetSnapshotAsync(IndexKind kind, IReadOnlyCollection<string>? terms, CancellationToken ct)
            => Task.FromResult(kind == IndexKind.Feature ? feature : testCase);
    }

    private sealed class ThrowingIndexStore(ImpactIndexHealth health) : IRetrievalIndexStore
    {
        public Task<IndexSnapshot> GetSnapshotAsync(IndexKind kind, IReadOnlyCollection<string>? terms, CancellationToken ct)
            => throw new ImpactIndexUnavailableException(health);
    }

    private sealed class FakeAdo : IAdoWorkItemClient
    {
        public Dictionary<int, FeatureCandidate> Features { get; } = [];
        public Dictionary<int, TestCaseCandidate> TestCases { get; } = [];
        public Dictionary<int, IReadOnlyList<int>> Children { get; } = [];

        public Task<IReadOnlyList<FeatureCandidate>> GetFeaturesAsync(IReadOnlyCollection<int> ids, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FeatureCandidate>>(ids.Where(Features.ContainsKey).Select(id => Features[id]).ToList());

        public Task<IReadOnlyList<TestCaseCandidate>> GetTestCasesAsync(IReadOnlyCollection<int> ids, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TestCaseCandidate>>(ids.Where(TestCases.ContainsKey).Select(id => TestCases[id]).ToList());

        public Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> GetChildTestCasesAsync(IReadOnlyCollection<int> featureIds, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<int, IReadOnlyList<int>>>(Children.Where(kv => featureIds.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));

        public Task<IReadOnlyDictionary<int, int>> ResolveParentFeaturesAsync(IReadOnlyCollection<int> testCaseIds, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<int, int>>(new Dictionary<int, int>());

        public Task<IReadOnlyList<int>> QueryIdsAsync(string wiql, int maxResults, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<AdoWorkItemRef>> HydrateAsync(IReadOnlyCollection<int> ids, IReadOnlyCollection<string> fields, CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<AdoWorkItemRef> EnumerateChangedSinceAsync(string workItemType, DateTimeOffset since, int pageSize, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeOutcomes : IOutcomeStore
    {
        public Task RecordRunAsync(ImpactMappingResult result, CancellationToken ct) => Task.CompletedTask;
        public Task RecordExecutionAsync(Guid runId, IReadOnlyList<ExecutionOutcome> outcomes, CancellationToken ct) => Task.CompletedTask;
        public Task RecordEscapeAsync(string areaId, int testCaseId, string source, string? notes, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<AnchorEdge>> GetHistoricalAnchorsAsync(string areaId, int lookbackRuns, CancellationToken ct) => Task.FromResult<IReadOnlyList<AnchorEdge>>([]);
        public Task<IReadOnlyDictionary<int, double>> GetFailureRatesAsync(IReadOnlyCollection<int> ids, CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<int, double>>(new Dictionary<int, double>());
        public Task<IReadOnlyDictionary<int, TimeSpan>> GetDurationsAsync(IReadOnlyCollection<int> ids, CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<int, TimeSpan>>(new Dictionary<int, TimeSpan>());
        public Task<IReadOnlyList<LabelledScore>> GetTrainingPairsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<LabelledScore>>([]);
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
