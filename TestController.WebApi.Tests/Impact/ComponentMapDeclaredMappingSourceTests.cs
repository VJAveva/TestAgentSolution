using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Ado;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Anchors;
using TestControllerGrpc.Core.Impact.Index;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Tests.Impact;

/// <summary>
/// Tests for <see cref="ComponentMapDeclaredMappingSource"/> — the declared (impact-map) anchor source.
/// The component map holds prose and UC/FR tokens rather than ADO ids, so these prove the resolution to real
/// test cases, the honest coverage contract, and that an index outage can never take the other anchors down.
/// </summary>
public sealed class ComponentMapDeclaredMappingSourceTests
{
    private static ImpactedArea Area(string areaId = "aabootstrap", params string[] declaredAreas)
        => new(areaId, areaId, areaId, null, [], declaredAreas, RiskTier.High,
            new ChurnMetrics(0, 0, 0, 0, 0, DateTimeOffset.UnixEpoch));

    private static ComponentMapDeclaredMappingSource Source(IRetrievalIndexStore index, params string[] useCases)
        => new(new FakeMap(useCases), index, Options.Create(new ImpactMappingOptions()), new NoopAppLogger());

    private static IRetrievalIndexStore IndexWith(params (int id, string[] terms)[] docs)
    {
        SnapshotDocument[] snapshotDocs = docs
            .Select(d => new SnapshotDocument(d.id, IndexKind.TestCase, d.terms.Length, 0,
                d.terms.ToDictionary(t => t, _ => 1, StringComparer.Ordinal), null))
            .ToArray();
        return new FakeIndex(new IndexSnapshot(snapshotDocs.Length, 1, new Dictionary<string, int>(), snapshotDocs));
    }

    [Fact]
    public async Task GetDeclaredEdges_Should_ResolveDeclaredArea_ToTestCases()
    {
        IRetrievalIndexStore index = IndexWith(
            (101, ["platform", "manager", "restart"]),
            (102, ["unrelated", "thing"]));

        DeclaredMappingResult result = await Source(index)
            .GetDeclaredEdgesAsync(Area("aabootstrap", "platform manager"), CancellationToken.None);

        AnchorEdge edge = Assert.Single(result.Edges);
        Assert.Equal(101, edge.TestCaseId);
        Assert.Equal(AnchorSource.DeclaredMapping, edge.Source);
        Assert.Contains("platform manager", edge.Justification);
        Assert.Contains("platform manager", result.CoveredRegressionAreas);
    }

    [Fact]
    public async Task GetDeclaredEdges_Should_RequireEveryTerm_When_PhraseHasMultipleWords()
    {
        // BM25 would rank the partial match; a deterministic anchor must not accept it.
        IRetrievalIndexStore index = IndexWith((101, ["platform", "unrelated"]));

        DeclaredMappingResult result = await Source(index)
            .GetDeclaredEdgesAsync(Area("aabootstrap", "platform manager"), CancellationToken.None);

        Assert.Empty(result.Edges);
    }

    [Fact]
    public async Task GetDeclaredEdges_Should_NotReportCoverage_When_AreaResolvesToNothing()
    {
        // Naming an area in the map is not evidence; claiming coverage here would let the cascade exit early
        // on test cases that do not exist.
        IRetrievalIndexStore index = IndexWith((101, ["something", "else"]));

        DeclaredMappingResult result = await Source(index)
            .GetDeclaredEdgesAsync(Area("aabootstrap", "platform manager"), CancellationToken.None);

        Assert.Empty(result.Edges);
        Assert.Empty(result.CoveredRegressionAreas);
    }

    [Fact]
    public async Task GetDeclaredEdges_Should_ResolveUseCaseTokens_WithoutMarkingAreaCoverage()
    {
        IRetrievalIndexStore index = IndexWith((501, ["uc152", "galaxy", "deploy"]));

        DeclaredMappingResult result = await Source(index, "UC152")
            .GetDeclaredEdgesAsync(Area("aabootstrap", "platform manager"), CancellationToken.None);

        Assert.Equal(501, Assert.Single(result.Edges).TestCaseId);
        Assert.Empty(result.CoveredRegressionAreas);
    }

    [Fact]
    public async Task GetDeclaredEdges_Should_ReturnEmpty_When_IndexUnavailable()
    {
        // Throwing would propagate through AnchorEdgeProvider's Task.WhenAll and discard the linked-work-item
        // and historical anchors too.
        var health = new ImpactIndexHealth
        {
            Status = ImpactIndexStatus.Empty, IndexFilePath = "x", Message = "contains 0 documents",
        };

        DeclaredMappingResult result = await Source(new ThrowingIndex(health))
            .GetDeclaredEdgesAsync(Area("aabootstrap", "platform manager"), CancellationToken.None);

        Assert.Empty(result.Edges);
        Assert.Empty(result.CoveredRegressionAreas);
    }

    [Fact]
    public async Task GetDeclaredEdges_Should_ReturnEmpty_When_NothingDeclared()
    {
        DeclaredMappingResult result = await Source(IndexWith((101, ["anything"])))
            .GetDeclaredEdgesAsync(Area("unmapped"), CancellationToken.None);

        Assert.Empty(result.Edges);
    }

    private sealed class FakeMap(string[] useCases) : IComponentBuildMap
    {
        private readonly ComponentBuildInfo _entry = new("aabootstrap", 4017, 14, "AppServer.AABootstrap", [], useCases);

        public IReadOnlyList<ComponentBuildInfo> All() => [_entry];

        public ComponentBuildInfo? Resolve(string manifestComponentName)
            => manifestComponentName.Contains("aabootstrap", StringComparison.OrdinalIgnoreCase) ? _entry : null;
    }

    private sealed class FakeIndex(IndexSnapshot snapshot) : IRetrievalIndexStore
    {
        public Task<IndexSnapshot> GetSnapshotAsync(IndexKind kind, IReadOnlyCollection<string>? terms, CancellationToken ct)
            => Task.FromResult(snapshot);
    }

    private sealed class ThrowingIndex(ImpactIndexHealth health) : IRetrievalIndexStore
    {
        public Task<IndexSnapshot> GetSnapshotAsync(IndexKind kind, IReadOnlyCollection<string>? terms, CancellationToken ct)
            => throw new ImpactIndexUnavailableException(health);
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
