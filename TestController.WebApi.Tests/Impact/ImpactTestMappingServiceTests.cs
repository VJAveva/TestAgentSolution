using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
using TestControllerGrpc.Core.Impact.Selection;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Tests.Impact;

public sealed class ImpactTestMappingServiceTests
{
    private static ImpactedArea Area()
        => new("AREA-1", "Galaxy Deploy", "Deploy", "GalaxyVob", ["src/Deploy/GalaxyEngine.cs"], [], RiskTier.High,
            new ChurnMetrics(10, 2, 1, 1, 1, DateTimeOffset.UnixEpoch));

    private static ChangePayload Payload()
        => new(42, "Deploy fix", "Fixes the galaxy deployment", [],
            [new TestControllerGrpc.Core.Impact.FileDiff("src/Deploy/GalaxyEngine.cs",
                [new DiffHunk(1, 2, "+    public void DeployGalaxy(string vob)\n+    { _log.Info(\"Deploy the galaxy engine now\"); }")])],
            [900]);

    private static IndexSnapshot Snapshot(IndexKind kind, params (int id, string[] terms, float[]? vector)[] docs)
    {
        SnapshotDocument[] snapshotDocs = docs
            .Select(d => new SnapshotDocument(d.id, kind, d.terms.Length, 0,
                d.terms.GroupBy(t => t, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
                d.vector))
            .ToArray();

        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (SnapshotDocument doc in snapshotDocs)
        {
            foreach (string term in doc.TermFrequencies.Keys)
            {
                df[term] = df.GetValueOrDefault(term) + 1;
            }
        }

        double avg = snapshotDocs.Length == 0 ? 1 : snapshotDocs.Average(d => (double)d.Length);
        return new IndexSnapshot(snapshotDocs.Length, avg, df, snapshotDocs);
    }

    private static FakeAdo AdoWithCorpus()
    {
        var ado = new FakeAdo();
        ado.Features[900] = new FeatureCandidate(new AdoWorkItemRef(900, "Feature", "Galaxy Deploy Engine", "Proj\\Deploy", "Active", 1), "desc", FeatureDiscoveryPath.None, [], 2);
        ado.TestCases[101] = new TestCaseCandidate(new AdoWorkItemRef(101, "Test Case", "Galaxy deploy smoke", null, "Design", 1), "steps", "Automated", 900, []);
        ado.TestCases[102] = new TestCaseCandidate(new AdoWorkItemRef(102, "Test Case", "Deploy rollback", null, "Design", 1), "steps", "Automated", 900, []);
        ado.TestCases[103] = new TestCaseCandidate(new AdoWorkItemRef(103, "Test Case", "Galaxy child test", null, "Design", 1), "steps", "Automated", 900, []);
        ado.Children[900] = [101, 102, 103];
        return ado;
    }

    private static ImpactTestMappingService Build(IAnchorEdgeProvider anchors, IRetrievalIndexStore store, FakeAdo ado, ImpactMappingOptions? options = null)
    {
        IOptions<ImpactMappingOptions> opt = Options.Create(options ?? new ImpactMappingOptions());
        var logger = new NoopAppLogger();
        return new ImpactTestMappingService(
            anchors, new ChangeDocumentBuilder(opt), new FakeHyde(), new KeywordExtractor(opt),
            NullEmbeddingProvider.Instance, store, new HybridRetriever(opt, logger), new FeatureRanker(opt),
            new ParentFeatureResolver(ado, logger), new FeatureMerger(opt), new FanOutNormalizer(opt), ado,
            new PassThroughReranker(), new LinearScoreCalibrator(), new BudgetedDiversitySelector(opt),
            new CoverageGapDetector(), new FakeOutcomes(), new RunPlanWriter(opt, logger), opt, logger);
    }

    [Fact]
    public async Task MapAsync_Should_ProduceMappedTestCases_WithProvenance()
    {
        var store = new FakeIndexStore(
            Snapshot(IndexKind.Feature, (900, ["galaxy", "deploy", "engine"], [1f, 0f])),
            Snapshot(IndexKind.TestCase, (101, ["galaxy", "deploy"], [1f, 0f]), (102, ["deploy"], [0f, 1f])));
        ImpactTestMappingService service = Build(new FakeAnchors(new AnchorResult([], 0, false)), store, AdoWithCorpus());

        ImpactMappingResult result = await service.MapAsync(Area(), Payload(), SelectionTier.Targeted, CancellationToken.None);

        Assert.NotEmpty(result.MappedTestCases);
        Assert.All(result.MappedTestCases, m => Assert.NotEmpty(m.Provenance));
    }

    [Fact]
    public async Task MapAsync_Should_ReturnSensibleResults_When_AllOptionalDependenciesDegraded()
    {
        var options = new ImpactMappingOptions();
        options.Rerank.EnableHyde = false;
        options.Rerank.EnableLlmRerank = false;

        var store = new FakeIndexStore(
            Snapshot(IndexKind.Feature, (900, ["galaxy", "deploy", "engine"], null)),
            Snapshot(IndexKind.TestCase, (101, ["galaxy", "deploy"], null), (102, ["deploy"], null)));
        ImpactTestMappingService service = Build(new FakeAnchors(new AnchorResult([], 0, false)), store, AdoWithCorpus(), options);

        ImpactMappingResult result = await service.MapAsync(Area(), Payload(), SelectionTier.Targeted, CancellationToken.None);

        Assert.NotEmpty(result.MappedTestCases); // lexical-only, no LLM, no HyDE — still runs
    }

    [Fact]
    public async Task MapAsync_Should_EarlyExit_When_AnchorsSufficient()
    {
        var anchors = new AnchorResult([new AnchorEdge(101, 900, AnchorSource.LinkedWorkItem, 1.0, "linked")], 1.0, true);
        var store = new FakeIndexStore(Snapshot(IndexKind.Feature), Snapshot(IndexKind.TestCase));
        ImpactTestMappingService service = Build(new FakeAnchors(anchors), store, AdoWithCorpus());

        var stopwatch = Stopwatch.StartNew();
        ImpactMappingResult result = await service.MapAsync(Area(), Payload(), SelectionTier.Targeted, CancellationToken.None);
        stopwatch.Stop();

        Assert.True(result.EarlyExit);
        Assert.NotEmpty(result.MappedTestCases);
        Assert.False(store.SnapshotRequested); // T2 was skipped
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task MapWithProgressAsync_Should_YieldTerminalTick_CarryingResult()
    {
        var store = new FakeIndexStore(
            Snapshot(IndexKind.Feature, (900, ["galaxy", "deploy"], null)),
            Snapshot(IndexKind.TestCase, (101, ["galaxy", "deploy"], null)));
        ImpactTestMappingService service = Build(new FakeAnchors(new AnchorResult([], 0, false)), store, AdoWithCorpus());

        var ticks = new List<ImpactMappingProgress>();
        await foreach (ImpactMappingProgress tick in service.MapWithProgressAsync(Area(), Payload(), SelectionTier.Targeted, CancellationToken.None))
        {
            ticks.Add(tick);
        }

        Assert.NotEmpty(ticks);
        Assert.NotNull(ticks[^1].Result); // only the terminal tick carries a Result
        Assert.All(ticks.Take(ticks.Count - 1), t => Assert.Null(t.Result));
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
        public bool SnapshotRequested { get; private set; }
        public IReadOnlyCollection<string>? LastTerms { get; private set; }

        public Task<IndexSnapshot> GetSnapshotAsync(IndexKind kind, IReadOnlyCollection<string>? terms, CancellationToken ct)
        {
            SnapshotRequested = true;
            LastTerms = terms;
            return Task.FromResult(kind == IndexKind.Feature ? feature : testCase);
        }
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
