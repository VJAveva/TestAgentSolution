using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Ado;
using TestControllerGrpc.Core.Impact.Anchors;
using TestControllerGrpc.Core.Impact.Learning;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Tests.Impact;

public sealed class AnchorEdgeProviderTests
{
    private static ImpactedArea Area(RiskTier tier = RiskTier.High, IReadOnlyList<string>? declared = null)
        => new("A", "Name", "Sub", "Vob", [], declared ?? [], tier, new ChurnMetrics(0, 0, 0, 0, 0, DateTimeOffset.UnixEpoch));

    private static ChangePayload Payload(params int[] linkedIds)
        => new(null, null, null, [], [], linkedIds);

    private static AnchorEdgeProvider Provider(
        FakeAdo ado, FakeOutcomes outcomes, IDeclaredMappingSource declared, ImpactMappingOptions? options = null)
        => new(ado, outcomes, declared, Options.Create(options ?? new ImpactMappingOptions()), new NoopAppLogger());

    [Fact]
    public async Task GetAnchors_Should_ProduceLinkedWorkItemEdges_AtWeightOne()
    {
        var ado = new FakeAdo { Children = { [900] = [101, 102] } };

        AnchorResult result = await Provider(ado, new FakeOutcomes(), NullDeclaredMappingSource.Instance)
            .GetAnchorsAsync(Area(), Payload(900), CancellationToken.None);

        Assert.Equal(2, result.Edges.Count);
        AnchorEdge edge = result.Edges.Single(e => e.TestCaseId == 101);
        Assert.Equal(AnchorSource.LinkedWorkItem, edge.Source);
        Assert.Equal(1.0, edge.Weight);
    }

    [Fact]
    public async Task GetAnchors_Should_IncludeHistoricalFailureEdges()
    {
        var outcomes = new FakeOutcomes { Historical = [new AnchorEdge(103, 900, AnchorSource.HistoricalFailure, 0.8, "failed before")] };

        AnchorResult result = await Provider(new FakeAdo(), outcomes, NullDeclaredMappingSource.Instance)
            .GetAnchorsAsync(Area(), Payload(), CancellationToken.None);

        Assert.Contains(result.Edges, e => e.TestCaseId == 103 && e.Source == AnchorSource.HistoricalFailure);
    }

    [Fact]
    public async Task GetAnchors_Should_MarkEarlyExit_When_CoverageAndAnchorsSufficient()
    {
        var options = new ImpactMappingOptions();
        options.Anchors.MinAnchorsForEarlyExit = 1;
        options.Anchors.EarlyExitCoverageThreshold = 0.8;

        var declared = new FakeDeclared([new AnchorEdge(104, null, AnchorSource.DeclaredMapping, 0.9, "declared")], "A1");

        AnchorResult result = await Provider(new FakeAdo(), new FakeOutcomes(), declared, options)
            .GetAnchorsAsync(Area(RiskTier.High, ["A1"]), Payload(), CancellationToken.None);

        Assert.Equal(1.0, result.CoverageScore);
        Assert.True(result.SufficientForEarlyExit);
    }

    [Fact]
    public async Task GetAnchors_Should_NotEarlyExit_ForCriticalTier_ByDefault()
    {
        var options = new ImpactMappingOptions();
        options.Anchors.MinAnchorsForEarlyExit = 1;
        options.Anchors.EarlyExitCoverageThreshold = 0.8;

        var declared = new FakeDeclared([new AnchorEdge(104, null, AnchorSource.DeclaredMapping, 0.9, "declared")], "A1");

        AnchorResult result = await Provider(new FakeAdo(), new FakeOutcomes(), declared, options)
            .GetAnchorsAsync(Area(RiskTier.Critical, ["A1"]), Payload(), CancellationToken.None);

        Assert.False(result.SufficientForEarlyExit);
    }

    [Fact]
    public async Task GetAnchors_Should_DedupeEdges_BySourceAndTestCase()
    {
        var ado = new FakeAdo { Children = { [900] = [101], [901] = [101] } };

        AnchorResult result = await Provider(ado, new FakeOutcomes(), NullDeclaredMappingSource.Instance)
            .GetAnchorsAsync(Area(), Payload(900, 901), CancellationToken.None);

        Assert.Single(result.Edges, e => e.TestCaseId == 101 && e.Source == AnchorSource.LinkedWorkItem);
    }

    [Fact]
    public async Task GetAnchors_Should_CoverDeclaredAreas_When_LinkedWorkItemsPresent()
    {
        // Declared coverage comes from IDeclaredMappingSource, which defaults to the null source. Without
        // crediting linked work items, any component declaring regression areas scored 0 and could never
        // take the deterministic path.
        var options = new ImpactMappingOptions();
        options.Anchors.MinAnchorsForEarlyExit = 2;
        var ado = new FakeAdo { Children = { [900] = [101, 102] } };

        AnchorResult result = await Provider(ado, new FakeOutcomes(), NullDeclaredMappingSource.Instance, options)
            .GetAnchorsAsync(Area(RiskTier.High, ["AreaA", "AreaB"]), Payload(900), CancellationToken.None);

        Assert.Equal(1.0, result.CoverageScore);
        Assert.True(result.SufficientForEarlyExit);
    }

    [Fact]
    public async Task GetAnchors_Should_ReportZeroCoverage_When_NoLinkedOrDeclaredEvidence()
    {
        var outcomes = new FakeOutcomes { Historical = [new AnchorEdge(103, null, AnchorSource.HistoricalFailure, 0.8, "failed before")] };

        AnchorResult result = await Provider(new FakeAdo(), outcomes, NullDeclaredMappingSource.Instance)
            .GetAnchorsAsync(Area(RiskTier.High, ["AreaA"]), Payload(), CancellationToken.None);

        Assert.Equal(0.0, result.CoverageScore);
        Assert.False(result.SufficientForEarlyExit);
    }

    private sealed class FakeAdo : IAdoWorkItemClient
    {
        public Dictionary<int, IReadOnlyList<int>> Children { get; } = [];

        public Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> GetChildTestCasesAsync(IReadOnlyCollection<int> featureIds, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<int, IReadOnlyList<int>>>(
                Children.Where(kv => featureIds.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));

        public Task<IReadOnlyList<int>> QueryIdsAsync(string wiql, int maxResults, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<AdoWorkItemRef>> HydrateAsync(IReadOnlyCollection<int> ids, IReadOnlyCollection<string> fields, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<TestCaseCandidate>> GetTestCasesAsync(IReadOnlyCollection<int> ids, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<FeatureCandidate>> GetFeaturesAsync(IReadOnlyCollection<int> ids, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<int, int>> ResolveParentFeaturesAsync(IReadOnlyCollection<int> testCaseIds, CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<AdoWorkItemRef> EnumerateChangedSinceAsync(string workItemType, DateTimeOffset since, int pageSize, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeOutcomes : IOutcomeStore
    {
        public IReadOnlyList<AnchorEdge> Historical { get; init; } = [];

        public Task<IReadOnlyList<AnchorEdge>> GetHistoricalAnchorsAsync(string areaId, int lookbackRuns, CancellationToken ct)
            => Task.FromResult(Historical);

        public Task RecordRunAsync(ImpactMappingResult result, CancellationToken ct) => throw new NotSupportedException();
        public Task RecordExecutionAsync(Guid runId, IReadOnlyList<ExecutionOutcome> outcomes, CancellationToken ct) => throw new NotSupportedException();
        public Task RecordEscapeAsync(string areaId, int testCaseId, string source, string? notes, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<int, double>> GetFailureRatesAsync(IReadOnlyCollection<int> ids, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<int, TimeSpan>> GetDurationsAsync(IReadOnlyCollection<int> ids, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<LabelledScore>> GetTrainingPairsAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeDeclared(IReadOnlyList<AnchorEdge> edges, params string[] covered) : IDeclaredMappingSource
    {
        private readonly DeclaredMappingResult _result = new(edges, new HashSet<string>(covered, StringComparer.OrdinalIgnoreCase));

        public Task<DeclaredMappingResult> GetDeclaredEdgesAsync(ImpactedArea area, CancellationToken ct) => Task.FromResult(_result);
    }

    private sealed class NoopAppLogger : IAppLogger
    {
#pragma warning disable CS0067 // event is part of the interface but unused in tests
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
