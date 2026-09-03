using Microsoft.Extensions.Options;
using TestControllerGrpc.Core.Impact.Index;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Impact.Retrieval;

/// <summary>Retrieves scored work item ids for a keyword group by fusing lexical and dense legs (P13).</summary>
public interface IHybridRetriever
{
    /// <summary>
    /// Runs the BM25 and (optional) dense legs against <paramref name="snapshot"/> and fuses them with
    /// Reciprocal Rank Fusion. Returns scored work item ids; hydration happens at the caller so retrieval
    /// stays independent of Azure DevOps.
    /// </summary>
    Task<IReadOnlyList<Scored<int>>> RetrieveAsync(
        IndexSnapshot snapshot, KeywordGroup group, float[]? denseQuery, int topK, CancellationToken ct);
}

/// <summary>
/// Hybrid lexical + dense retriever (P13). BM25 scores and cosine similarities live on incompatible scales,
/// so the legs are fused with Reciprocal Rank Fusion — scale-free, with <c>RrfK</c> as the only knob — rather
/// than by normalizing and adding. Each result preserves "bm25", "cosine" and "rrf" score components for
/// provenance, and the fused score is multiplied by the group weight. Ties break on work item id ascending.
/// </summary>
public sealed class HybridRetriever : IHybridRetriever
{
    private readonly ImpactMappingOptions.RetrievalOptions _options;
    private readonly IAppLogger _logger;

    /// <summary>Creates the retriever from impact-mapping retrieval options.</summary>
    public HybridRetriever(IOptions<ImpactMappingOptions> options, IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value.Retrieval;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Scored<int>>> RetrieveAsync(
        IndexSnapshot snapshot, KeywordGroup group, float[]? denseQuery, int topK, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(group);
        ct.ThrowIfCancellationRequested();

        int legLimit = Math.Max(1, topK * 4);
        IReadOnlyList<RankedDocument> lexical = snapshot.SearchBm25(group.Terms, legLimit);
        IReadOnlyList<RankedDocument> dense = denseQuery is { Length: > 0 }
            ? snapshot.SearchDense(denseQuery, legLimit)
            : [];

        Dictionary<int, int> lexRank = ToRankMap(lexical);
        Dictionary<int, double> lexScore = ToScoreMap(lexical);
        Dictionary<int, int> denseRank = ToRankMap(dense);
        Dictionary<int, double> denseScore = ToScoreMap(dense);

        var ids = new HashSet<int>(lexRank.Keys);
        ids.UnionWith(denseRank.Keys);

        double rrfK = _options.RrfK;
        var results = new List<Scored<int>>(ids.Count);
        foreach (int id in ids)
        {
            var components = new List<ScoreComponent>();
            double rrf = 0;

            if (lexRank.TryGetValue(id, out int rankLex))
            {
                double contribution = _options.LexicalWeight / (rrfK + rankLex);
                rrf += contribution;
                components.Add(new ScoreComponent("bm25", lexScore[id], _options.LexicalWeight, contribution));
            }

            if (denseRank.TryGetValue(id, out int rankDense))
            {
                double contribution = _options.SemanticWeight / (rrfK + rankDense);
                rrf += contribution;
                components.Add(new ScoreComponent("cosine", denseScore[id], _options.SemanticWeight, contribution));
            }

            double finalScore = rrf * group.Weight;
            components.Add(new ScoreComponent("rrf", rrf, group.Weight, finalScore));
            results.Add(new Scored<int>(id, finalScore, components));
        }

        List<Scored<int>> top = results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Value) // deterministic tie-break on work item id
            .Take(topK)
            .ToList();

        _logger.Info("ImpactRetrieval",
            $"group={group.GroupId} candidates={ids.Count} dense={dense.Count > 0} top={(top.Count > 0 ? top[0].Score : 0):0.####}");

        return Task.FromResult<IReadOnlyList<Scored<int>>>(top);
    }

    private static Dictionary<int, int> ToRankMap(IReadOnlyList<RankedDocument> ranked)
    {
        var map = new Dictionary<int, int>(ranked.Count);
        for (int i = 0; i < ranked.Count; i++)
        {
            map[ranked[i].WorkItemId] = i + 1; // 1-based rank
        }

        return map;
    }

    private static Dictionary<int, double> ToScoreMap(IReadOnlyList<RankedDocument> ranked)
    {
        var map = new Dictionary<int, double>(ranked.Count);
        foreach (RankedDocument doc in ranked)
        {
            map[doc.WorkItemId] = doc.Score;
        }

        return map;
    }
}
