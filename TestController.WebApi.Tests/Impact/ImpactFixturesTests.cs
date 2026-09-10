using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Ado.Reporting.Llm;
using TestControllerGrpc.Core.Impact;
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
using TestControllerGrpc.Core.Impact.Testing;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Tests.Impact;

public sealed class ImpactFixturesTests
{
    private static ImpactTestMappingService Build(FakeAdoWorkItemClient ado, IRetrievalIndexStore store, IEmbeddingProvider embeddings, ImpactMappingOptions? options = null)
    {
        IOptions<ImpactMappingOptions> opt = Options.Create(options ?? new ImpactMappingOptions());
        var logger = new NoopAppLogger();
        return new ImpactTestMappingService(
            new FakeAnchors(new AnchorResult([], 0, false)), new ChangeDocumentBuilder(opt), new FakeHyde(),
            new KeywordExtractor(opt), embeddings, store, new HybridRetriever(opt, logger), new FeatureRanker(opt),
            new ParentFeatureResolver(ado, logger), new FeatureMerger(opt), new FanOutNormalizer(opt), ado,
            new FakeRelevanceReranker(), new LinearScoreCalibrator(), new BudgetedDiversitySelector(opt),
            new CoverageGapDetector(), new FakeOutcomeStore(), new RunPlanWriter(opt, logger), opt, logger);
    }

    [Fact]
    public async Task MapAsync_Should_FindTitleMismatchedFeatures_ViaBackReferenceBranch()
    {
        FixtureCorpus corpus = ImpactFixtures.BuildGalaxyDeploymentCorpus();
        var embeddings = new FakeEmbeddingProvider();
        IRetrievalIndexStore store = await ImpactFixtures.BuildIndexStoreAsync(corpus, embeddings);

        ImpactMappingResult result = await Build(corpus.Ado, store, embeddings)
            .MapAsync(corpus.Area, corpus.Payload, SelectionTier.Full, CancellationToken.None);

        var featureIds = result.SelectedFeatures.Select(f => f.Value.Item.Id).ToHashSet();
        Assert.All(corpus.TitleMismatchedFeatureIds, id => Assert.Contains(id, featureIds));
    }

    [Fact]
    public async Task MapAsync_Should_RankExpandedTestCases_AboveGenericLexicalHits()
    {
        // Regression guard: expansion candidates once got a flat score of 0.1, which put every test found via
        // the back-reference branch below every direct lexical hit however generic. The fixture's top ranks
        // filled with boilerplate "Deploy Feature N regression" and precision@10 was zero.
        FixtureCorpus corpus = ImpactFixtures.BuildGalaxyDeploymentCorpus();
        var embeddings = new FakeEmbeddingProvider();
        IRetrievalIndexStore store = await ImpactFixtures.BuildIndexStoreAsync(corpus, embeddings);

        ImpactMappingResult result = await Build(corpus.Ado, store, embeddings)
            .MapAsync(corpus.Area, corpus.Payload, SelectionTier.Targeted, CancellationToken.None);

        HashSet<int> groundTruth = new[] { 10, 11, 12, 20, 21 }
            .SelectMany(f => corpus.Ado.ChildrenByFeature.GetValueOrDefault(f, []))
            .ToHashSet();

        int inTop10 = result.MappedTestCases.Take(10).Count(m => groundTruth.Contains(m.TestCase.Item.Id));

        Assert.Equal(10, inTop10);
    }

    [Fact]
    public async Task MapAsync_Should_NotSelectAllNearDuplicates()
    {
        FixtureCorpus corpus = ImpactFixtures.BuildGalaxyDeploymentCorpus();
        var embeddings = new FakeEmbeddingProvider();
        IRetrievalIndexStore store = await ImpactFixtures.BuildIndexStoreAsync(corpus, embeddings);

        // Tighten the total cap so MMR must choose diversity over redundant near-duplicates.
        var options = new ImpactMappingOptions();
        options.Selection.MaxTestCasesTotal = 20;

        ImpactMappingResult result = await Build(corpus.Ado, store, embeddings, options)
            .MapAsync(corpus.Area, corpus.Payload, SelectionTier.Full, CancellationToken.None);

        var selectedIds = result.MappedTestCases.Select(m => m.TestCase.Item.Id).ToHashSet();
        int selectedDuplicates = corpus.NearDuplicateTestCaseIds.Count(selectedIds.Contains);
        Assert.True(selectedDuplicates < corpus.NearDuplicateTestCaseIds.Count, $"MMR selected {selectedDuplicates} of 10 near-duplicates.");
    }

    [Fact]
    public void FixtureImpactedAreaFactory_Should_ParseCsvRow()
    {
        const string csv =
            "AreaId,DisplayName,Subsystem,Vob,ChangedPaths,DeclaredRegressionAreas,RiskTier,LinesAdded,LinesDeleted,FilesTouched,CommitCount,DistinctAuthorCount,LastChangedUtc\n" +
            "GD,Galaxy Deploy,Deploy,GalaxyVob,src/a.cs;src/b.cs,Galaxy;Deploy,High,50,10,3,2,1,2026-01-01T00:00:00Z";

        IReadOnlyList<ImpactedArea> areas = FixtureImpactedAreaFactory.FromCsv(csv);

        ImpactedArea area = Assert.Single(areas);
        Assert.Equal("Galaxy Deploy", area.DisplayName);
        Assert.Equal(RiskTier.High, area.RiskTier);
        Assert.Equal(2, area.ChangedPaths.Count);
        Assert.Equal(50, area.Churn.LinesAdded);
    }

    private sealed class FakeAnchors(AnchorResult result) : IAnchorEdgeProvider
    {
        public Task<AnchorResult> GetAnchorsAsync(ImpactedArea area, ChangePayload payload, CancellationToken ct) => Task.FromResult(result);
    }

    private sealed class FakeHyde : IHydeQueryGenerator
    {
        public Task<HydeQuery> GenerateAsync(ChangeDocument doc, CancellationToken ct)
            => Task.FromResult(new HydeQuery("galaxy deployment", ["Verify galaxy deploy node"], "deploy the galaxy node", ["galaxy", "deploy"]));
    }

    private sealed class FakeOutcomeStore : IOutcomeStore
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
