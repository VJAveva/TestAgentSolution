using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Ado;
using TestControllerGrpc.Core.Impact.Features;
using TestControllerGrpc.Services;
using Microsoft.Extensions.Logging;

namespace TestController.WebApi.Tests.Impact;

public sealed class ParentFeatureResolverTests
{
    private static KeywordGroup Group() => new("grp", "identity", ["x"], 1.0);

    private static Scored<TestCaseCandidate> Tc(int id, int? parent, double score, string title = "TC")
        => new(
            new TestCaseCandidate(new AdoWorkItemRef(id, "Test Case", title, null, "Design", 1), "steps", "Automated", parent, []),
            score, []);

    private static FeatureCandidate Feat(int id)
        => new(new AdoWorkItemRef(id, "Feature", $"F{id}", null, "Active", 1), "desc", FeatureDiscoveryPath.None, [], 0);

    private static ParentFeatureResolver Resolver(FakeAdo ado) => new(ado, new NoopAppLogger());

    [Fact]
    public async Task ResolveAsync_Should_UseParentFeatureId_And_StampBackReference()
    {
        var ado = new FakeAdo { Features = { Feat(900) } };
        Scored<TestCaseCandidate>[] testCases = [Tc(5001, 900, 0.9), Tc(5002, 900, 0.8)];

        ParentResolution result = await Resolver(ado).ResolveAsync(Group(), testCases, CancellationToken.None);

        Assert.Single(result.Features); // deduplicated by id
        Assert.Equal(900, result.Features[0].Item.Id);
        Assert.Equal(FeatureDiscoveryPath.TestCaseBackReference, result.Features[0].DiscoveryPath);
        Assert.Contains("grp", result.Features[0].MatchedGroupIds);
        Assert.Equal(2, result.Evidence.Count);
        Assert.Empty(result.Orphans);
    }

    [Fact]
    public async Task ResolveAsync_Should_ResolveNullParents_ViaBatchCall()
    {
        var ado = new FakeAdo { Parents = { [5001] = 900 }, Features = { Feat(900) } };
        Scored<TestCaseCandidate>[] testCases = [Tc(5001, null, 0.7)];

        ParentResolution result = await Resolver(ado).ResolveAsync(Group(), testCases, CancellationToken.None);

        Assert.Single(result.Features);
        Assert.Single(result.Evidence);
        Assert.Equal(900, result.Evidence[0].FeatureId);
    }

    [Fact]
    public async Task ResolveAsync_Should_ReturnOrphans_When_NoParentResolvable()
    {
        var ado = new FakeAdo();
        Scored<TestCaseCandidate>[] testCases = [Tc(5001, null, 0.7)];

        ParentResolution result = await Resolver(ado).ResolveAsync(Group(), testCases, CancellationToken.None);

        Assert.Single(result.Orphans);
        Assert.Equal(5001, result.Orphans[0].Item.Id);
        Assert.Empty(result.Evidence);
        Assert.Empty(result.Features);
    }

    [Fact]
    public async Task ResolveAsync_Should_EmitEvidenceCarryingRetrievalScore()
    {
        var ado = new FakeAdo { Features = { Feat(900) } };
        Scored<TestCaseCandidate>[] testCases = [Tc(5001, 900, 0.42)];

        ParentResolution result = await Resolver(ado).ResolveAsync(Group(), testCases, CancellationToken.None);

        Assert.Equal(0.42, result.Evidence[0].RetrievalScore);
        Assert.Equal(5001, result.Evidence[0].TestCaseId);
    }

    private sealed class FakeAdo : IAdoWorkItemClient
    {
        public Dictionary<int, int> Parents { get; } = [];

        public List<FeatureCandidate> Features { get; } = [];

        public Task<IReadOnlyDictionary<int, int>> ResolveParentFeaturesAsync(IReadOnlyCollection<int> testCaseIds, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<int, int>>(
                testCaseIds.Where(Parents.ContainsKey).ToDictionary(id => id, id => Parents[id]));

        public Task<IReadOnlyList<FeatureCandidate>> GetFeaturesAsync(IReadOnlyCollection<int> ids, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FeatureCandidate>>(Features.Where(f => ids.Contains(f.Item.Id)).ToList());

        public Task<IReadOnlyList<int>> QueryIdsAsync(string wiql, int maxResults, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<AdoWorkItemRef>> HydrateAsync(IReadOnlyCollection<int> ids, IReadOnlyCollection<string> fields, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TestCaseCandidate>> GetTestCasesAsync(IReadOnlyCollection<int> ids, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> GetChildTestCasesAsync(IReadOnlyCollection<int> featureIds, CancellationToken ct)
            => throw new NotSupportedException();

        public IAsyncEnumerable<AdoWorkItemRef> EnumerateChangedSinceAsync(string workItemType, DateTimeOffset since, int pageSize, CancellationToken ct)
            => throw new NotSupportedException();
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
