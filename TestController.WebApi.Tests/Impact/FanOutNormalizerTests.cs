using Microsoft.Extensions.Options;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Index;
using TestControllerGrpc.Core.Impact.Ranking;

namespace TestController.WebApi.Tests.Impact;

public sealed class FanOutNormalizerTests
{
    private static FanOutNormalizer Normalizer(ImpactMappingOptions? options = null)
        => new(Options.Create(options ?? new ImpactMappingOptions()));

    private static IndexSnapshot SnapshotWithMedian(params int[] childCounts)
    {
        SnapshotDocument[] docs = childCounts
            .Select((c, i) => new SnapshotDocument(1000 + i, IndexKind.Feature, 1, c, new Dictionary<string, int> { ["f"] = 1 }, null))
            .ToArray();
        return new IndexSnapshot(docs.Length, 1, new Dictionary<string, int>(), docs);
    }

    private static Scored<FeatureCandidate> SF(int id, double score, int children)
        => new(
            new FeatureCandidate(new AdoWorkItemRef(id, "Feature", $"F{id}", null, "Active", 1), "d", FeatureDiscoveryPath.None, [], children),
            score, [new ScoreComponent("base", score, 1, score)]);

    [Fact]
    public void Normalize_Should_BoostSpecificFeature_And_DampHub()
    {
        IndexSnapshot snapshot = SnapshotWithMedian(10, 10, 10);
        Scored<FeatureCandidate>[] features = [SF(900, 1.0, children: 6), SF(901, 1.0, children: 800)];

        IReadOnlyList<Scored<FeatureCandidate>> result = Normalizer().Normalize(features, snapshot, new List<string>());

        Assert.Equal(900, result[0].Value.Item.Id);
        Assert.True(result.Single(s => s.Value.Item.Id == 900).Score > 1.0);
        Assert.True(result.Single(s => s.Value.Item.Id == 901).Score < 1.0);
    }

    [Fact]
    public void Normalize_Should_WarnOnHubFeature()
    {
        IndexSnapshot snapshot = SnapshotWithMedian(10, 10, 10);
        var warnings = new List<string>();

        Normalizer().Normalize([SF(901, 1.0, children: 800)], snapshot, warnings);

        Assert.Contains(warnings, w => w.Contains("901"));
    }

    [Fact]
    public void Normalize_Should_EmitFanOutComponent()
    {
        IndexSnapshot snapshot = SnapshotWithMedian(10);

        IReadOnlyList<Scored<FeatureCandidate>> result = Normalizer().Normalize([SF(900, 1.0, 6)], snapshot, new List<string>());

        Assert.Contains(result[0].Components, c => c.Name == "fanOut");
    }

    [Fact]
    public void Normalize_Should_BeNeutral_When_MedianZero()
    {
        IndexSnapshot snapshot = SnapshotWithMedian(); // no features → median 0
        var warnings = new List<string>();

        IReadOnlyList<Scored<FeatureCandidate>> result = Normalizer().Normalize([SF(900, 1.0, 100)], snapshot, warnings);

        Assert.Equal(1.0, result[0].Score, 10);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Normalize_Should_NeverZeroScore_ForExtremeHub()
    {
        IndexSnapshot snapshot = SnapshotWithMedian(5, 5, 5);

        IReadOnlyList<Scored<FeatureCandidate>> result =
            Normalizer().Normalize([SF(901, 1.0, children: 100000)], snapshot, new List<string>());

        Assert.True(result[0].Score > 0);
    }
}
