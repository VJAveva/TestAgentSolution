using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Index;

namespace TestController.WebApi.Tests.Impact;

public sealed class IndexSnapshotSearchTests
{
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

    private static SnapshotDocument Tc(int id, int length, params (string term, int tf)[] terms)
        => new(id, IndexKind.TestCase, length, 0, terms.ToDictionary(t => t.term, t => t.tf, StringComparer.Ordinal), null);

    private static SnapshotDocument Vec(int id, float[] vector)
        => new(id, IndexKind.TestCase, 1, 0, new Dictionary<string, int> { ["x"] = 1 }, vector);

    private static SnapshotDocument Feature(int id, int childCount)
        => new(id, IndexKind.Feature, 1, childCount, new Dictionary<string, int> { ["f"] = 1 }, null);

    [Fact]
    public void SearchBm25_Should_RankDocumentsContainingQueryTerms()
    {
        IndexSnapshot snapshot = Snapshot(
            Tc(1, 10, ("login", 5), ("user", 2)),
            Tc(2, 10, ("logout", 3)),
            Tc(3, 10, ("login", 1)));

        IReadOnlyList<RankedDocument> results = snapshot.SearchBm25(["login"], 10);

        Assert.Equal(1, results[0].WorkItemId);
        Assert.DoesNotContain(results, r => r.WorkItemId == 2);
    }

    [Fact]
    public void SearchDense_Should_RankByCosineSimilarity()
    {
        IndexSnapshot snapshot = Snapshot(Vec(1, [1f, 0f]), Vec(2, [0f, 1f]), Vec(3, [0.9f, 0.1f]));

        IReadOnlyList<RankedDocument> results = snapshot.SearchDense([1f, 0f], 10);

        Assert.Equal(1, results[0].WorkItemId);
        Assert.Equal(3, results[1].WorkItemId);
    }

    [Fact]
    public void SearchDense_Should_SkipVectorsOfWrongDimension()
    {
        IndexSnapshot snapshot = Snapshot(
            Vec(1, [1f, 0f]),
            new SnapshotDocument(2, IndexKind.TestCase, 1, 0, new Dictionary<string, int> { ["x"] = 1 }, [1f, 0f, 0f]));

        IReadOnlyList<RankedDocument> results = snapshot.SearchDense([1f, 0f], 10);

        Assert.Single(results);
        Assert.Equal(1, results[0].WorkItemId);
    }

    [Fact]
    public void MedianChildCount_Should_ComputeOverFeatures()
    {
        IndexSnapshot snapshot = Snapshot(Feature(1, 2), Feature(2, 10), Feature(3, 100));

        Assert.Equal(10, snapshot.MedianChildCount);
    }

    [Fact]
    public void SearchBm25_Should_ReturnEmpty_When_NoDocuments()
    {
        Assert.Empty(IndexSnapshot.Empty.SearchBm25(["login"], 5));
    }
}
