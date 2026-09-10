using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Ado;
using TestControllerGrpc.Core.Impact.Index;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Tests.Impact;

public sealed class RetrievalIndexBuilderTests
{
    private static readonly TestCaseCandidate[] TestCases =
    [
        new(new AdoWorkItemRef(101, "Test Case", "Login smoke test", "Area\\Auth", "Design", 1),
            "enter user -> logged in", "Automated", 900, ["smoke"]),
        new(new AdoWorkItemRef(102, "Test Case", "Logout flow", "Area\\Auth", "Design", 1),
            "click logout -> session ends", null, 900, []),
    ];

    private static readonly FeatureCandidate[] Features =
    [
        new(new AdoWorkItemRef(900, "Feature", "Authentication feature", "Area\\Auth", "Active", 1),
            "Handles login and logout", FeatureDiscoveryPath.None, [], 2),
    ];

    [Fact]
    public async Task BuildAsync_Should_IndexAllDocuments_And_ComputeCorpusStatistics()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var factory = new SharedConnectionFactory(connection);
        RetrievalIndexBuilder builder = CreateBuilder(factory, NullEmbeddingProvider.Instance);

        IndexBuildResult result = await builder.BuildAsync(fullRebuild: true, progress: null, CancellationToken.None);

        Assert.Equal(3, result.DocumentsIndexed);
        Assert.Equal(0, result.DocumentsSkipped);
        Assert.Equal(2, result.TestCasesSeen);
        Assert.Equal(1, result.FeaturesSeen);

        await using ImpactIndexDbContext ctx = factory.CreateDbContext();
        Assert.Equal(3, await ctx.Documents.CountAsync());
        Assert.True(await ctx.DocumentTerms.AnyAsync(t => t.Term == "login"));

        // "login" appears in the TC:101 title and the feature description → document frequency ≥ 2.
        int loginDf = await ctx.CorpusStatistics.Where(c => c.Term == "login").Select(c => c.DocumentFrequency).SingleAsync();
        Assert.True(loginDf >= 2);

        Assert.Equal("3", (await ctx.Metadata.SingleAsync(m => m.Key == "DocumentCount")).Value);
        Assert.Equal(0, await ctx.DocumentVectors.CountAsync()); // embeddings disabled
    }

    [Fact]
    public async Task BuildAsync_Should_SkipUnchangedDocuments_OnSecondRun()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var factory = new SharedConnectionFactory(connection);
        RetrievalIndexBuilder builder = CreateBuilder(factory, NullEmbeddingProvider.Instance);

        IndexBuildResult first = await builder.BuildAsync(fullRebuild: true, progress: null, CancellationToken.None);
        IndexBuildResult second = await builder.BuildAsync(fullRebuild: true, progress: null, CancellationToken.None);

        Assert.Equal(3, first.DocumentsIndexed);
        Assert.Equal(0, second.DocumentsIndexed);
        Assert.Equal(3, second.DocumentsSkipped);
    }

    [Fact]
    public async Task BuildAsync_Should_StoreVectors_When_EmbeddingsEnabled()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var factory = new SharedConnectionFactory(connection);
        RetrievalIndexBuilder builder = CreateBuilder(factory, new FixedEmbeddingProvider(dimension: 3));

        await builder.BuildAsync(fullRebuild: true, progress: null, CancellationToken.None);

        await using ImpactIndexDbContext ctx = factory.CreateDbContext();
        Assert.Equal(3, await ctx.DocumentVectors.CountAsync());
        DocumentVector vector = await ctx.DocumentVectors.FirstAsync();
        Assert.Equal(3, vector.Dimension);
        Assert.Equal(3, VectorBlob.ToFloats(vector.Vector).Length);
    }

    [Fact]
    public async Task BuildAsync_Should_Succeed_When_AdoReturnsSameWorkItemTwice()
    {
        // Adaptive WIQL date windows can return one work item in two windows. Prior rows are deleted once per
        // batch, so the repeat inserted (DocumentId, Term) twice and killed the whole build with
        // "SQLite Error 19: UNIQUE constraint failed: DocumentTerms.DocumentId, DocumentTerms.Term".
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var factory = new SharedConnectionFactory(connection);
        var builder = new RetrievalIndexBuilder(
            factory, new FakeAdoWorkItemClient(TestCases, Features, duplicateEveryItem: true),
            NullEmbeddingProvider.Instance, Options.Create(new ImpactMappingOptions()), new NoopAppLogger());

        IndexBuildResult result = await builder.BuildAsync(fullRebuild: true, progress: null, CancellationToken.None);

        Assert.Equal(3, result.DocumentsIndexed);

        await using ImpactIndexDbContext ctx = factory.CreateDbContext();
        Assert.Equal(3, await ctx.Documents.CountAsync());
        Assert.Equal(3, (await ctx.DocumentTerms.Select(t => t.DocumentId).ToListAsync()).Distinct().Count());
    }

    private static RetrievalIndexBuilder CreateBuilder(SharedConnectionFactory factory, IEmbeddingProvider embeddings)
        => new(factory, new FakeAdoWorkItemClient(TestCases, Features), embeddings,
            Options.Create(new ImpactMappingOptions()), new NoopAppLogger());

    [Fact]
    public async Task BuildAsync_Should_PurgeDocuments_When_WorkItemMovedToExcludedState()
    {
        // The state filter keeps new deletions out; only the purge removes what is already stored, and a
        // document for a deleted test case is worse than a missing one because it gets recommended.
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var factory = new SharedConnectionFactory(connection);

        var options = new ImpactMappingOptions();
        options.Ado.ExcludedStates = ["Removed"];
        var ado = new FakeAdoWorkItemClient(TestCases, Features) { RemovedTestCaseIds = [101] };
        var builder = new RetrievalIndexBuilder(
            factory, ado, NullEmbeddingProvider.Instance, Options.Create(options), new NoopAppLogger());

        await builder.BuildAsync(fullRebuild: true, progress: null, CancellationToken.None);

        await using ImpactIndexDbContext ctx = factory.CreateDbContext();
        Assert.DoesNotContain("TC:101", await ctx.Documents.Select(d => d.Id).ToListAsync());
        Assert.Contains("TC:102", await ctx.Documents.Select(d => d.Id).ToListAsync());
        Assert.DoesNotContain("TC:101", await ctx.DocumentTerms.Select(t => t.DocumentId).ToListAsync());
    }

    [Fact]
    public async Task BuildAsync_Should_NotPurge_When_NoExcludedStatesConfigured()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var factory = new SharedConnectionFactory(connection);

        var options = new ImpactMappingOptions();
        options.Ado.ExcludedStates = [];
        var ado = new FakeAdoWorkItemClient(TestCases, Features) { RemovedTestCaseIds = [101] };
        var builder = new RetrievalIndexBuilder(
            factory, ado, NullEmbeddingProvider.Instance, Options.Create(options), new NoopAppLogger());

        await builder.BuildAsync(fullRebuild: true, progress: null, CancellationToken.None);

        await using ImpactIndexDbContext ctx = factory.CreateDbContext();
        Assert.Equal(3, await ctx.Documents.CountAsync());
    }

    [Fact]
    public async Task BuildAsync_Should_RecomputeCorpusStatistics_AfterPurge()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var factory = new SharedConnectionFactory(connection);

        var options = new ImpactMappingOptions();
        options.Ado.ExcludedStates = ["Removed"];
        var ado = new FakeAdoWorkItemClient(TestCases, Features) { RemovedTestCaseIds = [101] };
        var builder = new RetrievalIndexBuilder(
            factory, ado, NullEmbeddingProvider.Instance, Options.Create(options), new NoopAppLogger());

        await builder.BuildAsync(fullRebuild: true, progress: null, CancellationToken.None);

        await using ImpactIndexDbContext ctx = factory.CreateDbContext();
        List<string> remaining = await ctx.DocumentTerms.Select(t => t.Term).Distinct().ToListAsync();
        List<string> stats = await ctx.CorpusStatistics.Select(c => c.Term).ToListAsync();

        // A purged document must not leave its terms behind in the corpus statistics.
        Assert.Empty(stats.Except(remaining));
    }

    private sealed class SharedConnectionFactory : IDbContextFactory<ImpactIndexDbContext>
    {
        private readonly DbContextOptions<ImpactIndexDbContext> _options;

        public SharedConnectionFactory(SqliteConnection connection)
            => _options = new DbContextOptionsBuilder<ImpactIndexDbContext>().UseSqlite(connection).Options;

        public ImpactIndexDbContext CreateDbContext() => new(_options);

        public Task<ImpactIndexDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class FakeAdoWorkItemClient(
        IReadOnlyList<TestCaseCandidate> testCases, IReadOnlyList<FeatureCandidate> features,
        bool duplicateEveryItem = false) : IAdoWorkItemClient
    {
        public IReadOnlyList<int> RemovedTestCaseIds { get; init; } = [];

        public async IAsyncEnumerable<int> EnumerateIdsInStatesAsync(
            string workItemType, IReadOnlyCollection<string> states, DateTimeOffset since,
            [EnumeratorCancellation] CancellationToken ct)
        {
            if (workItemType == "Test Case")
            {
                foreach (int id in RemovedTestCaseIds)
                {
                    yield return id;
                }
            }

            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<AdoWorkItemRef> EnumerateChangedSinceAsync(
            string workItemType, DateTimeOffset since, int pageSize, [EnumeratorCancellation] CancellationToken ct)
        {
            IEnumerable<AdoWorkItemRef> source = workItemType == "Test Case"
                ? testCases.Select(t => t.Item)
                : features.Select(f => f.Item);

            foreach (AdoWorkItemRef item in source)
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
                if (duplicateEveryItem)
                {
                    yield return item;
                }
            }

            await Task.CompletedTask;
        }

        public Task<IReadOnlyList<TestCaseCandidate>> GetTestCasesAsync(IReadOnlyCollection<int> ids, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TestCaseCandidate>>(testCases.Where(t => ids.Contains(t.Item.Id)).ToArray());

        public Task<IReadOnlyList<FeatureCandidate>> GetFeaturesAsync(IReadOnlyCollection<int> ids, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FeatureCandidate>>(features.Where(f => ids.Contains(f.Item.Id)).ToArray());

        public Task<IReadOnlyList<int>> QueryIdsAsync(string wiql, int maxResults, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<int>>([]);

        public Task<IReadOnlyList<AdoWorkItemRef>> HydrateAsync(
            IReadOnlyCollection<int> ids, IReadOnlyCollection<string> fields, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AdoWorkItemRef>>([]);

        public Task<IReadOnlyDictionary<int, int>> ResolveParentFeaturesAsync(IReadOnlyCollection<int> testCaseIds, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<int, int>>(new Dictionary<int, int>());

        public Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> GetChildTestCasesAsync(IReadOnlyCollection<int> featureIds, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<int, IReadOnlyList<int>>>(new Dictionary<int, IReadOnlyList<int>>());
    }

    private sealed class FixedEmbeddingProvider(int dimension) : IEmbeddingProvider
    {
        public string ModelId => "fixed";

        public bool IsEnabled => true;

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<float[]>>(inputs.Select(_ => Enumerable.Repeat(0.5f, dimension).ToArray()).ToArray());
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
