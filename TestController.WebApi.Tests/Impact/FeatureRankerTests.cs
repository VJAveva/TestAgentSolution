using Microsoft.Extensions.Options;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Index;
using TestControllerGrpc.Core.Impact.Ranking;

namespace TestController.WebApi.Tests.Impact;

public sealed class FeatureRankerTests
{
    private static FeatureRanker Ranker(ImpactMappingOptions? options = null)
        => new(Options.Create(options ?? new ImpactMappingOptions()));

    private static KeywordGroup Group(double weight, params string[] terms)
        => new("g", "identity", terms, weight);

    private static FeatureCandidate Feat(
        int id, string title, string? area = null, string state = "Active",
        FeatureDiscoveryPath path = FeatureDiscoveryPath.None, IReadOnlyList<string>? groups = null, int children = 0)
        => new(new AdoWorkItemRef(id, "Feature", title, area, state, 1), "desc", path, groups ?? [], children);

    private static ImpactedArea Area(string? subsystem = "Deploy\\Galaxy")
        => new("A", "Galaxy Deploy", subsystem, "GalaxyVob", [], [], RiskTier.High,
            new ChurnMetrics(0, 0, 0, 0, 0, DateTimeOffset.UnixEpoch));

    private static IndexSnapshot FeatureSnapshot(params (int id, string[] terms)[] features)
    {
        SnapshotDocument[] docs = features.Select(f => new SnapshotDocument(
            f.id, IndexKind.Feature, f.terms.Length, 0,
            f.terms.GroupBy(t => t, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            null)).ToArray();

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

    [Fact]
    public void RankWithinGroup_Should_RankTitleAndRetrievalMatchFirst()
    {
        IndexSnapshot snapshot = FeatureSnapshot((900, ["galaxy", "deploy", "engine"]), (901, ["unrelated"]));
        FeatureCandidate[] candidates =
        [
            Feat(900, "Galaxy Deploy Engine", "Proj\\Deploy\\Galaxy"),
            Feat(901, "Unrelated", "Proj\\Other"),
        ];

        IReadOnlyList<Scored<FeatureCandidate>> result =
            Ranker().RankWithinGroup(Group(1.0, "galaxy", "deploy"), candidates, [], Area(), snapshot);

        Assert.Equal(900, result[0].Value.Item.Id);
        Assert.Equal(6, result[0].Components.Count);
    }

    [Fact]
    public void RankWithinGroup_Should_RewardTestCaseEvidence_ForBackReferenceFeature()
    {
        IndexSnapshot snapshot = FeatureSnapshot((900, ["galaxy"]), (901, ["nomatch"]));
        FeatureCandidate[] candidates = [Feat(900, "Galaxy"), Feat(901, "Nomatch")];
        TestCaseEvidence[] evidence = [new(901, 5001, "TC", 10.0)];

        IReadOnlyList<Scored<FeatureCandidate>> result =
            Ranker().RankWithinGroup(Group(1.0, "galaxy"), candidates, evidence, Area(subsystem: null), snapshot);

        Scored<FeatureCandidate> feature901 = result.Single(s => s.Value.Item.Id == 901);
        ScoreComponent evidenceComponent = feature901.Components.Single(c => c.Name == "tcEvidence");
        Assert.Equal(1.0, evidenceComponent.Raw);
        Assert.True(feature901.Score > 0);
    }

    [Fact]
    public void RankWithinGroup_Should_PenalizeInactiveState()
    {
        IndexSnapshot snapshot = FeatureSnapshot((900, ["galaxy", "deploy"]), (901, ["galaxy", "deploy"]));
        FeatureCandidate[] candidates =
        [
            Feat(900, "Galaxy Deploy", "Proj\\Deploy\\Galaxy", "Active"),
            Feat(901, "Galaxy Deploy", "Proj\\Deploy\\Galaxy", "Removed"),
        ];

        IReadOnlyList<Scored<FeatureCandidate>> result =
            Ranker().RankWithinGroup(Group(1.0, "galaxy", "deploy"), candidates, [], Area(), snapshot);

        double active = result.Single(s => s.Value.Item.Id == 900).Score;
        double removed = result.Single(s => s.Value.Item.Id == 901).Score;
        Assert.True(active > removed);
    }

    [Fact]
    public void RankWithinGroup_Should_RespectTopFeaturesPerGroup()
    {
        var options = new ImpactMappingOptions();
        options.Retrieval.TopFeaturesPerGroup = 1;
        IndexSnapshot snapshot = FeatureSnapshot((900, ["galaxy"]), (901, ["galaxy"]), (902, ["galaxy"]));
        FeatureCandidate[] candidates = [Feat(900, "Galaxy"), Feat(901, "Galaxy"), Feat(902, "Galaxy")];

        IReadOnlyList<Scored<FeatureCandidate>> result =
            Ranker(options).RankWithinGroup(Group(1.0, "galaxy"), candidates, [], Area(subsystem: null), snapshot);

        Assert.Single(result);
    }

    [Fact]
    public void RankGlobally_Should_FuseWithRrf_And_UnionMetadata()
    {
        List<Scored<FeatureCandidate>> group1 =
        [
            new(Feat(900, "F900", path: FeatureDiscoveryPath.DirectFeatureSearch, groups: ["g1"]), 1.0, []),
        ];
        List<Scored<FeatureCandidate>> group2 =
        [
            new(Feat(900, "F900", path: FeatureDiscoveryPath.TestCaseBackReference, groups: ["g2"]), 1.0, []),
            new(Feat(901, "F901", groups: ["g2"]), 0.5, []),
        ];

        IReadOnlyList<Scored<FeatureCandidate>> result = Ranker().RankGlobally([group1, group2], 10);

        FeatureCandidate top = result[0].Value;
        Assert.Equal(900, top.Item.Id);
        Assert.True(top.DiscoveryPath.HasFlag(FeatureDiscoveryPath.DirectFeatureSearch));
        Assert.True(top.DiscoveryPath.HasFlag(FeatureDiscoveryPath.TestCaseBackReference));
        Assert.Contains("g1", top.MatchedGroupIds);
        Assert.Contains("g2", top.MatchedGroupIds);
    }
}
