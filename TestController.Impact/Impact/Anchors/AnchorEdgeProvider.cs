using Microsoft.Extensions.Options;
using TestControllerGrpc.Core.Impact.Ado;
using TestControllerGrpc.Core.Impact.Learning;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Impact.Anchors;

/// <summary>Declared (impact-map) anchor edges plus the regression areas they cover (P21).</summary>
public sealed record DeclaredMappingResult(IReadOnlyList<AnchorEdge> Edges, IReadOnlySet<string> CoveredRegressionAreas);

/// <summary>
/// Supplies evidenced area-to-test edges from the impact map (vobs.csv regression-area columns and the churn
/// workbook). Abstracted so the engine degrades cleanly where the impact map is unavailable (P21).
/// </summary>
public interface IDeclaredMappingSource
{
    /// <summary>Returns declared edges for an area, weighted by confidence, and the areas they cover.</summary>
    Task<DeclaredMappingResult> GetDeclaredEdgesAsync(ImpactedArea area, CancellationToken ct);
}

/// <summary>The degradation declared-mapping source: no impact map, no declared edges (P21).</summary>
public sealed class NullDeclaredMappingSource : IDeclaredMappingSource
{
    /// <summary>Shared stateless instance.</summary>
    public static NullDeclaredMappingSource Instance { get; } = new();

    /// <inheritdoc />
    public Task<DeclaredMappingResult> GetDeclaredEdgesAsync(ImpactedArea area, CancellationToken ct)
        => Task.FromResult(new DeclaredMappingResult([], new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
}

/// <summary>Produces the deterministic Tier-0 anchor edges that can short-circuit the cascade (P21).</summary>
public interface IAnchorEdgeProvider
{
    /// <summary>Gathers linked-work-item, historical-failure and declared-mapping anchors and scores coverage.</summary>
    Task<AnchorResult> GetAnchorsAsync(ImpactedArea area, ChangePayload payload, CancellationToken ct);
}

/// <summary>
/// Deterministic Tier-0 anchor provider (P21). Three high-precision sources are queried in parallel: explicit
/// linked work items (weight 1.0 — the highest-precision signal, one query, routinely ignored), historical
/// failures for this area (a test that caught a regression here before is the best predictor it will again),
/// and declared impact-map mappings. Coverage of the declared regression areas plus a minimum anchor count
/// decide whether the run may exit early — which is what makes this pipeline affordable to run per pull
/// request rather than nightly. Never gates early exit for a Critical area unless explicitly allowed.
/// </summary>
public sealed class AnchorEdgeProvider : IAnchorEdgeProvider
{
    private readonly IAdoWorkItemClient _ado;
    private readonly IOutcomeStore _outcomes;
    private readonly IDeclaredMappingSource _declared;
    private readonly ImpactMappingOptions.AnchorOptions _anchors;
    private readonly IAppLogger _logger;

    /// <summary>Creates the provider from its anchor sources and options.</summary>
    public AnchorEdgeProvider(
        IAdoWorkItemClient ado, IOutcomeStore outcomes, IDeclaredMappingSource declared,
        IOptions<ImpactMappingOptions> options, IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ado = ado ?? throw new ArgumentNullException(nameof(ado));
        _outcomes = outcomes ?? throw new ArgumentNullException(nameof(outcomes));
        _declared = declared ?? throw new ArgumentNullException(nameof(declared));
        _anchors = options.Value.Anchors;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<AnchorResult> GetAnchorsAsync(ImpactedArea area, ChangePayload payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(payload);

        Task<IReadOnlyList<AnchorEdge>> linkedTask = GetLinkedWorkItemEdgesAsync(payload, ct);
        Task<IReadOnlyList<AnchorEdge>> historicalTask = _outcomes.GetHistoricalAnchorsAsync(area.AreaId, Math.Max(1, _anchors.LookbackRuns), ct);
        Task<DeclaredMappingResult> declaredTask = _declared.GetDeclaredEdgesAsync(area, ct);

        await Task.WhenAll(linkedTask, historicalTask, declaredTask).ConfigureAwait(false);
        DeclaredMappingResult declared = declaredTask.Result;

        List<AnchorEdge> edges = linkedTask.Result
            .Concat(historicalTask.Result)
            .Concat(declared.Edges)
            .GroupBy(e => (e.TestCaseId, e.Source)) // one edge per (test case, source), strongest wins
            .Select(g => g.OrderByDescending(x => x.Weight).First())
            .OrderBy(e => e.Source)
            .ThenByDescending(e => e.Weight)
            .ThenBy(e => e.TestCaseId)
            .ToList();

        double coverage = ComputeCoverage(area, declared.CoveredRegressionAreas, edges.Count);
        bool sufficient = IsSufficientForEarlyExit(area, coverage, edges.Count);

        _logger.Info("ImpactAnchor",
            $"area={area.AreaId} anchors={edges.Count} coverage={coverage:0.##} earlyExit={sufficient}");

        return new AnchorResult(edges, coverage, sufficient);
    }

    private async Task<IReadOnlyList<AnchorEdge>> GetLinkedWorkItemEdgesAsync(ChangePayload payload, CancellationToken ct)
    {
        if (payload.LinkedWorkItemIds.Count == 0)
        {
            return [];
        }

        IReadOnlyDictionary<int, IReadOnlyList<int>> childTestCases =
            await _ado.GetChildTestCasesAsync(payload.LinkedWorkItemIds, ct).ConfigureAwait(false);

        var edges = new List<AnchorEdge>();
        foreach ((int workItemId, IReadOnlyList<int> testCaseIds) in childTestCases)
        {
            foreach (int testCaseId in testCaseIds)
            {
                edges.Add(new AnchorEdge(
                    testCaseId, workItemId, AnchorSource.LinkedWorkItem, 1.0,
                    $"Linked from work item {workItemId}."));
            }
        }

        return edges;
    }

    private double ComputeCoverage(ImpactedArea area, IReadOnlySet<string> coveredAreas, int edgeCount)
    {
        if (area.DeclaredRegressionAreas.Count == 0)
        {
            return edgeCount > 0 ? 1.0 : 0.0;
        }

        var covered = new HashSet<string>(coveredAreas, StringComparer.OrdinalIgnoreCase);
        int hit = area.DeclaredRegressionAreas.Count(covered.Contains);
        return (double)hit / area.DeclaredRegressionAreas.Count;
    }

    private bool IsSufficientForEarlyExit(ImpactedArea area, double coverage, int edgeCount)
    {
        if (area.RiskTier == RiskTier.Critical && !_anchors.AllowEarlyExitForCriticalTier)
        {
            return false;
        }

        return coverage >= _anchors.EarlyExitCoverageThreshold && edgeCount >= _anchors.MinAnchorsForEarlyExit;
    }
}
