using Microsoft.Extensions.Options;

namespace TestControllerGrpc.Core.Impact.Features;

/// <summary>Merges the direct-feature-match and test-case-back-reference branches into one ranking (P16).</summary>
public interface IFeatureMerger
{
    /// <summary>Fuses the two branches, keying on work item id and rewarding independent agreement.</summary>
    IReadOnlyList<Scored<FeatureCandidate>> Merge(
        IReadOnlyList<Scored<FeatureCandidate>> directBranch,
        IReadOnlyList<Scored<FeatureCandidate>> backReferenceBranch,
        int maxFeatures);
}

/// <summary>
/// Fuses the two feature-discovery branches (P16). Because the branches score on different scales, common
/// features are fused with Reciprocal Rank Fusion over the two branch rankings rather than by adding raw
/// scores. A feature carrying both discovery-path flags — an independent direct match AND a test-case
/// back-reference — receives the corroboration bonus, the strongest signal this pipeline produces. Flags are
/// OR-ed, matched-group ids unioned, and child counts kept consistent (max, never double-counted).
/// </summary>
public sealed class FeatureMerger : IFeatureMerger
{
    private readonly ImpactMappingOptions.RetrievalOptions _options;

    /// <summary>Creates the merger from impact-mapping retrieval options.</summary>
    public FeatureMerger(IOptions<ImpactMappingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value.Retrieval;
    }

    /// <inheritdoc />
    public IReadOnlyList<Scored<FeatureCandidate>> Merge(
        IReadOnlyList<Scored<FeatureCandidate>> directBranch,
        IReadOnlyList<Scored<FeatureCandidate>> backReferenceBranch,
        int maxFeatures)
    {
        ArgumentNullException.ThrowIfNull(directBranch);
        ArgumentNullException.ThrowIfNull(backReferenceBranch);

        Dictionary<int, int> directRank = RankMap(directBranch);
        Dictionary<int, double> directScore = ScoreMap(directBranch);
        Dictionary<int, int> backRank = RankMap(backReferenceBranch);
        Dictionary<int, double> backScore = ScoreMap(backReferenceBranch);

        Dictionary<int, FeatureCandidate> byId = [];
        foreach (Scored<FeatureCandidate> scored in directBranch.Concat(backReferenceBranch))
        {
            FeatureCandidate feature = scored.Value;
            int id = feature.Item.Id;
            byId[id] = byId.TryGetValue(id, out FeatureCandidate? existing) ? MergeMetadata(existing, feature) : feature;
        }

        double rrfK = _options.RrfK;
        var results = new List<Scored<FeatureCandidate>>(byId.Count);
        foreach ((int id, FeatureCandidate feature) in byId)
        {
            var components = new List<ScoreComponent>();
            double rrf = 0;

            if (directRank.TryGetValue(id, out int rankDirect))
            {
                double contribution = 1.0 / (rrfK + rankDirect);
                rrf += contribution;
                components.Add(new ScoreComponent("directBranch", directScore[id], 1.0, contribution));
            }

            if (backRank.TryGetValue(id, out int rankBack))
            {
                double contribution = 1.0 / (rrfK + rankBack);
                rrf += contribution;
                components.Add(new ScoreComponent("backReferenceBranch", backScore[id], 1.0, contribution));
            }

            bool corroborated = feature.DiscoveryPath.HasFlag(FeatureDiscoveryPath.DirectFeatureSearch)
                && feature.DiscoveryPath.HasFlag(FeatureDiscoveryPath.TestCaseBackReference);
            double bonus = corroborated ? rrf * _options.CorroborationBonus : 0;
            components.Add(new ScoreComponent("corroboration", corroborated ? 1 : 0, _options.CorroborationBonus, bonus));

            results.Add(new Scored<FeatureCandidate>(feature, rrf + bonus, components));
        }

        return results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Value.Item.Id)
            .Take(Math.Max(1, maxFeatures))
            .ToList();
    }

    private static Dictionary<int, int> RankMap(IReadOnlyList<Scored<FeatureCandidate>> branch)
    {
        var map = new Dictionary<int, int>(branch.Count);
        for (int i = 0; i < branch.Count; i++)
        {
            map.TryAdd(branch[i].Value.Item.Id, i + 1); // 1-based rank; first occurrence wins
        }

        return map;
    }

    private static Dictionary<int, double> ScoreMap(IReadOnlyList<Scored<FeatureCandidate>> branch)
    {
        var map = new Dictionary<int, double>(branch.Count);
        foreach (Scored<FeatureCandidate> scored in branch)
        {
            map.TryAdd(scored.Value.Item.Id, scored.Score);
        }

        return map;
    }

    private static FeatureCandidate MergeMetadata(FeatureCandidate a, FeatureCandidate b)
        => a with
        {
            DiscoveryPath = a.DiscoveryPath | b.DiscoveryPath,
            MatchedGroupIds = a.MatchedGroupIds.Concat(b.MatchedGroupIds).Distinct(StringComparer.Ordinal).ToList(),
            ChildTestCaseCount = Math.Max(a.ChildTestCaseCount, b.ChildTestCaseCount),
        };
}
