using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Index;
using TestControllerGrpc.Core.Impact.Retrieval;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Tests.Impact;

public sealed class HybridRetrieverTests
{
    private static HybridRetriever Retriever(ImpactMappingOptions? options = null)
        => new(Options.Create(options ?? new ImpactMappingOptions()), new NoopAppLogger());

    private static KeywordGroup Group(double weight, params string[] terms)
        => new("g1", "identity", terms, weight);

    private static IndexSnapshot Snapshot(params SnapshotDocument[] docs)
    {
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (SnapshotDocument doc in docs)
        {
            foreach (string term in doc.TermFrequencies.Keys)
            {
                df[term] = df.GetValueOrDefault(term) + 1;
            }
        }

        double avg = docs.Length == 0 ? 1 : docs.Average(d => (double)d.Length);
        return new IndexSnapshot(docs.Length, avg, df, docs);
    }

    private static SnapshotDocument Doc(int id, IReadOnlyDictionary<string, int> terms, float[]? vector)
        => new(id, IndexKind.TestCase, 10, 0, terms, vector);

    [Fact]
    public async Task RetrieveAsync_Should_FuseLexicalAndDense_WithRrf()
    {
        IndexSnapshot snapshot = Snapshot(
            Doc(1, new Dictionary<string, int> { ["login"] = 5 }, [1f, 0f]),
            Doc(2, new Dictionary<string, int> { ["login"] = 1 }, null),
            Doc(3, new Dictionary<string, int> { ["other"] = 1 }, [1f, 0f]));

        IReadOnlyList<Scored<int>> results =
            await Retriever().RetrieveAsync(snapshot, Group(1.0, "login"), [1f, 0f], topK: 10, CancellationToken.None);

        Assert.Equal([1, 2, 3], results.Select(r => r.Value));
        Scored<int> top = results[0];
        Assert.Contains(top.Components, c => c.Name == "bm25");
        Assert.Contains(top.Components, c => c.Name == "cosine");
        Assert.Contains(top.Components, c => c.Name == "rrf");
    }

    [Fact]
    public async Task RetrieveAsync_Should_UseOnlyLexical_When_DenseQueryNull()
    {
        IndexSnapshot snapshot = Snapshot(
            Doc(1, new Dictionary<string, int> { ["login"] = 5 }, [1f, 0f]),
            Doc(2, new Dictionary<string, int> { ["login"] = 1 }, null),
            Doc(3, new Dictionary<string, int> { ["other"] = 1 }, [1f, 0f]));

        IReadOnlyList<Scored<int>> results =
            await Retriever().RetrieveAsync(snapshot, Group(1.0, "login"), denseQuery: null, topK: 10, CancellationToken.None);

        Assert.All(results, r => Assert.DoesNotContain(r.Components, c => c.Name == "cosine"));
        Assert.All(results, r => Assert.True(r.Value is 1 or 2)); // doc 3 has no query term and dense is off
    }

    [Fact]
    public async Task RetrieveAsync_Should_MultiplyScoreByGroupWeight()
    {
        IndexSnapshot snapshot = Snapshot(Doc(1, new Dictionary<string, int> { ["login"] = 5 }, null));

        IReadOnlyList<Scored<int>> full =
            await Retriever().RetrieveAsync(snapshot, Group(1.0, "login"), null, 10, CancellationToken.None);
        IReadOnlyList<Scored<int>> half =
            await Retriever().RetrieveAsync(snapshot, Group(0.5, "login"), null, 10, CancellationToken.None);

        Assert.Equal(full[0].Score, half[0].Score * 2, 10);
    }

    [Fact]
    public async Task RetrieveAsync_Should_RespectTopK()
    {
        IndexSnapshot snapshot = Snapshot(
            Doc(1, new Dictionary<string, int> { ["login"] = 5 }, null),
            Doc(2, new Dictionary<string, int> { ["login"] = 3 }, null),
            Doc(3, new Dictionary<string, int> { ["login"] = 1 }, null));

        IReadOnlyList<Scored<int>> results =
            await Retriever().RetrieveAsync(snapshot, Group(1.0, "login"), null, topK: 1, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(1, results[0].Value);
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
