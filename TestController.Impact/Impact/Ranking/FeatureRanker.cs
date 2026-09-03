using Microsoft.Extensions.Options;
using TestControllerGrpc.Core.Impact.Index;
using TestControllerGrpc.Core.Impact.Text;

namespace TestControllerGrpc.Core.Impact.Ranking;

/// <summary>Evidence that a test case matched, attributed to its parent feature (feeds the P14 tcEvidence signal).</summary>
public sealed record TestCaseEvidence(int FeatureId, int TestCaseId, string TestCaseTitle, double RetrievalScore);

/// <summary>Ranks feature candidates within a keyword group and fuses per-group rankings globally (P14).</summary>
public interface IFeatureRanker
{
    /// <summary>Scores each candidate with six weighted, provenance-preserving components, times the group weight.</summary>
    IReadOnlyList<Scored<FeatureCandidate>> RankWithinGroup(
        KeywordGroup group, IReadOnlyList<FeatureCandidate> candidates,
        IReadOnlyList<TestCaseEvidence> evidence, ImpactedArea area, IndexSnapshot snapshot);

    /// <summary>Fuses per-group rankings with RRF, unioning metadata and OR-ing discovery paths for repeats.</summary>
    IReadOnlyList<Scored<FeatureCandidate>> RankGlobally(
        IReadOnlyList<IReadOnlyList<Scored<FeatureCandidate>>> perGroup, int topN);
}

/// <summary>
/// Feature ranker (P14). Within a group each candidate is scored on retrieval strength, title-term overlap,
/// exact most-specific-term match, area-path affinity, log-damped test-case evidence and active state — the
/// tcEvidence signal is what surfaces features whose titles never match the change but whose test cases do.
/// Globally, per-group rankings are fused with Reciprocal Rank Fusion (scale-free) with metadata unioned and
/// discovery-path flags OR-ed. Deterministic throughout: ties break on work item id ascending.
/// </summary>
public sealed class FeatureRanker : IFeatureRanker
{
    private const double RetrievalWeight = 0.30;
    private const double TitleMatchWeight = 0.20;
    private const double ExactCompoundWeight = 0.15;
    private const double AreaPathWeight = 0.10;
    private const double EvidenceWeight = 0.20;
    private const double StateWeight = 0.05;

    private readonly ImpactMappingOptions.KeywordOptions _keywords;
    private readonly ImpactMappingOptions.RetrievalOptions _retrieval;

    /// <summary>Creates the ranker from impact-mapping options.</summary>
    public FeatureRanker(IOptions<ImpactMappingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _keywords = options.Value.Keywords;
        _retrieval = options.Value.Retrieval;
    }

    /// <inheritdoc />
    public IReadOnlyList<Scored<FeatureCandidate>> RankWithinGroup(
        KeywordGroup group, IReadOnlyList<FeatureCandidate> candidates,
        IReadOnlyList<TestCaseEvidence> evidence, ImpactedArea area, IndexSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(snapshot);

        Dictionary<int, double> retrievalScores = snapshot
            .SearchBm25(group.Terms, Math.Max(candidates.Count * 4, 64))
            .ToDictionary(r => r.WorkItemId, r => r.Score);
        double maxRetrieval = retrievalScores.Count > 0 ? retrievalScores.Values.Max() : 0;

        Dictionary<int, double> evidenceSums = evidence
            .GroupBy(e => e.FeatureId)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.RetrievalScore));
        double maxEvidence = evidenceSums.Count > 0 ? evidenceSums.Values.Max() : 0;

        string? mostSpecificTerm = group.Terms
            .OrderByDescending(t => t.Length)
            .ThenBy(t => t, StringComparer.Ordinal)
            .FirstOrDefault();
        HashSet<string> areaSegments = Segments(area.Subsystem);

        var scored = new List<Scored<FeatureCandidate>>(candidates.Count);
        foreach (FeatureCandidate feature in candidates)
        {
            double retrieval = maxRetrieval > 0 ? retrievalScores.GetValueOrDefault(feature.Item.Id) / maxRetrieval : 0;
            double titleMatch = TitleMatchFraction(group.Terms, feature.Item.Title);
            double exactCompound = mostSpecificTerm is not null
                && feature.Item.Title.Contains(mostSpecificTerm, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            double areaPathMatch = SharedSegmentCount(areaSegments, Segments(feature.Item.AreaPath)) >= 2 ? 1 : 0;
            double evidenceSum = evidenceSums.GetValueOrDefault(feature.Item.Id);
            double tcEvidence = maxEvidence > 0 ? Math.Log(1 + evidenceSum) / Math.Log(1 + maxEvidence) : 0;
            double stateActive = IsInactive(feature.Item.State) ? 0 : 1;

            var components = new List<ScoreComponent>
            {
                new("retrieval", retrieval, RetrievalWeight, retrieval * RetrievalWeight),
                new("titleMatch", titleMatch, TitleMatchWeight, titleMatch * TitleMatchWeight),
                new("exactCompound", exactCompound, ExactCompoundWeight, exactCompound * ExactCompoundWeight),
                new("areaPathMatch", areaPathMatch, AreaPathWeight, areaPathMatch * AreaPathWeight),
                new("tcEvidence", tcEvidence, EvidenceWeight, tcEvidence * EvidenceWeight),
                new("stateActive", stateActive, StateWeight, stateActive * StateWeight),
            };

            double finalScore = components.Sum(c => c.Weighted) * group.Weight;
            scored.Add(new Scored<FeatureCandidate>(feature, finalScore, components));
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Value.Item.Id)
            .Take(Math.Max(1, _retrieval.TopFeaturesPerGroup))
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<Scored<FeatureCandidate>> RankGlobally(
        IReadOnlyList<IReadOnlyList<Scored<FeatureCandidate>>> perGroup, int topN)
    {
        ArgumentNullException.ThrowIfNull(perGroup);

        var merged = new Dictionary<int, MergedFeature>();
        foreach (IReadOnlyList<Scored<FeatureCandidate>> group in perGroup)
        {
            for (int rank = 0; rank < group.Count; rank++)
            {
                FeatureCandidate feature = group[rank].Value;
                int id = feature.Item.Id;
                double contribution = 1.0 / (_retrieval.RrfK + rank + 1);

                merged[id] = merged.TryGetValue(id, out MergedFeature? existing)
                    ? new MergedFeature(MergeCandidate(existing.Candidate, feature), existing.Rrf + contribution)
                    : new MergedFeature(feature, contribution);
            }
        }

        return merged.Values
            .OrderByDescending(m => m.Rrf)
            .ThenBy(m => m.Candidate.Item.Id)
            .Take(Math.Max(1, topN))
            .Select(m => new Scored<FeatureCandidate>(m.Candidate, m.Rrf, [new ScoreComponent("globalRrf", m.Rrf, 1.0, m.Rrf)]))
            .ToList();
    }

    private double TitleMatchFraction(IReadOnlyList<string> terms, string title)
    {
        if (terms.Count == 0)
        {
            return 0;
        }

        var titleTokens = new HashSet<string>(Tokenizer.Tokenize(title, _keywords), StringComparer.Ordinal);
        int matched = terms.Count(titleTokens.Contains);
        return (double)matched / terms.Count;
    }

    private static FeatureCandidate MergeCandidate(FeatureCandidate a, FeatureCandidate b)
        => new(
            a.Item,
            a.Description ?? b.Description,
            a.DiscoveryPath | b.DiscoveryPath,
            a.MatchedGroupIds.Concat(b.MatchedGroupIds).Distinct(StringComparer.Ordinal).ToList(),
            Math.Max(a.ChildTestCaseCount, b.ChildTestCaseCount));

    private static HashSet<string> Segments(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return path
            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static int SharedSegmentCount(HashSet<string> a, HashSet<string> b)
        => a.Count(b.Contains);

    private static bool IsInactive(string? state)
        => state is not null
        && (state.Equals("Closed", StringComparison.OrdinalIgnoreCase)
            || state.Equals("Removed", StringComparison.OrdinalIgnoreCase));

    private sealed record MergedFeature(FeatureCandidate Candidate, double Rrf);
}
