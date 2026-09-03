using Microsoft.Extensions.Options;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Features;

namespace TestController.WebApi.Tests.Impact;

public sealed class FeatureMergerTests
{
    private static FeatureMerger Merger(ImpactMappingOptions? options = null)
        => new(Options.Create(options ?? new ImpactMappingOptions()));

    private static Scored<FeatureCandidate> SF(
        int id, double score, FeatureDiscoveryPath path, IReadOnlyList<string>? groups = null, int children = 0)
        => new(
            new FeatureCandidate(new AdoWorkItemRef(id, "Feature", $"F{id}", null, "Active", 1), "d", path, groups ?? [], children),
            score, []);

    [Fact]
    public void Merge_Should_FuseCommonFeature_AboveSingleBranchFeature()
    {
        Scored<FeatureCandidate>[] direct = [SF(900, 1.0, FeatureDiscoveryPath.DirectFeatureSearch)];
        Scored<FeatureCandidate>[] back =
        [
            SF(900, 1.0, FeatureDiscoveryPath.TestCaseBackReference),
            SF(901, 0.5, FeatureDiscoveryPath.TestCaseBackReference),
        ];

        IReadOnlyList<Scored<FeatureCandidate>> result = Merger().Merge(direct, back, 10);

        Assert.Equal(900, result[0].Value.Item.Id);
    }

    [Fact]
    public void Merge_Should_ApplyCorroborationBonus_When_BothFlagsSet()
    {
        Scored<FeatureCandidate>[] direct = [SF(900, 1.0, FeatureDiscoveryPath.DirectFeatureSearch)];
        Scored<FeatureCandidate>[] back = [SF(900, 1.0, FeatureDiscoveryPath.TestCaseBackReference)];

        IReadOnlyList<Scored<FeatureCandidate>> result = Merger().Merge(direct, back, 10);

        ScoreComponent corroboration = result[0].Components.Single(c => c.Name == "corroboration");
        Assert.Equal(1.0, corroboration.Raw);
        Assert.True(corroboration.Weighted > 0);
    }

    [Fact]
    public void Merge_Should_NotCorroborate_When_OnlyOneBranch()
    {
        Scored<FeatureCandidate>[] direct = [SF(901, 1.0, FeatureDiscoveryPath.DirectFeatureSearch)];

        IReadOnlyList<Scored<FeatureCandidate>> result = Merger().Merge(direct, [], 10);

        ScoreComponent corroboration = result.Single(s => s.Value.Item.Id == 901).Components.Single(c => c.Name == "corroboration");
        Assert.Equal(0.0, corroboration.Raw);
    }

    [Fact]
    public void Merge_Should_RespectMaxFeatures()
    {
        Scored<FeatureCandidate>[] direct =
        [
            SF(900, 1.0, FeatureDiscoveryPath.DirectFeatureSearch),
            SF(901, 0.9, FeatureDiscoveryPath.DirectFeatureSearch),
            SF(902, 0.8, FeatureDiscoveryPath.DirectFeatureSearch),
        ];

        IReadOnlyList<Scored<FeatureCandidate>> result = Merger().Merge(direct, [], maxFeatures: 2);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Merge_Should_UnionMetadata_And_KeepMaxChildCount()
    {
        Scored<FeatureCandidate>[] direct = [SF(900, 1.0, FeatureDiscoveryPath.DirectFeatureSearch, ["g1"], 5)];
        Scored<FeatureCandidate>[] back = [SF(900, 1.0, FeatureDiscoveryPath.TestCaseBackReference, ["g2"], 5)];

        FeatureCandidate merged = Merger().Merge(direct, back, 10).Single(s => s.Value.Item.Id == 900).Value;

        Assert.Contains("g1", merged.MatchedGroupIds);
        Assert.Contains("g2", merged.MatchedGroupIds);
        Assert.Equal(5, merged.ChildTestCaseCount);
        Assert.True(merged.DiscoveryPath.HasFlag(FeatureDiscoveryPath.DirectFeatureSearch));
        Assert.True(merged.DiscoveryPath.HasFlag(FeatureDiscoveryPath.TestCaseBackReference));
    }
}
