using Microsoft.Extensions.Options;
using TestControllerGrpc.Ado;
using TestControllerGrpc.Core.Impact.Index;
using TestControllerGrpc.Core.Impact.Text;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Impact.Anchors;

/// <summary>
/// Declared mapping over the reviewed component map (docs/impact/component-usecase-map.v1.json): each
/// component's regression areas and use cases are resolved to real Test Case work items by exact term match
/// against the retrieval index.
/// </summary>
/// <remarks>
/// The map carries prose and UC/US/FR tokens, never ADO ids, so the index is what turns a declared phrase into
/// a test case. Matching is conjunctive (every term of the phrase must be present) because an anchor is
/// deterministic evidence — BM25's "any term" ranking is the wrong bar and would flood the anchor set.
///
/// An area counts as covered only when it actually resolved to at least one test case. Reporting a declared
/// area as covered merely because the map names it would let the cascade exit early on evidence that does not
/// exist, which is the one failure this source must not introduce.
/// </remarks>
public sealed class ComponentMapDeclaredMappingSource : IDeclaredMappingSource
{
    // Below a linked work item (1.0, specific to this change) and above the historical-failure floor (0.5).
    private const double DeclaredWeight = 0.9;
    private const int MaxEdgesPerPhrase = 25;

    private static readonly DeclaredMappingResult Empty =
        new([], new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private readonly IComponentBuildMap _map;
    private readonly IRetrievalIndexStore _index;
    private readonly ImpactMappingOptions _options;
    private readonly IAppLogger _logger;

    public ComponentMapDeclaredMappingSource(
        IComponentBuildMap map, IRetrievalIndexStore index, IOptions<ImpactMappingOptions> options, IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _map = map ?? throw new ArgumentNullException(nameof(map));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<DeclaredMappingResult> GetDeclaredEdgesAsync(ImpactedArea area, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(area);

        List<Phrase> phrases = BuildPhrases(area);
        if (phrases.Count == 0)
        {
            return Empty;
        }

        string[] terms = phrases.SelectMany(p => p.Terms).Distinct(StringComparer.Ordinal).ToArray();

        IndexSnapshot snapshot;
        try
        {
            snapshot = await _index.GetSnapshotAsync(IndexKind.TestCase, terms, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // AnchorEdgeProvider awaits all three sources together, so throwing here would discard the
            // linked-work-item and historical anchors as well.
            _logger.Warn("ImpactDeclared", $"Declared mapping unavailable for '{area.AreaId}': {ex.Message}");
            return Empty;
        }

        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var edges = new Dictionary<int, AnchorEdge>();
        foreach (Phrase phrase in phrases)
        {
            IReadOnlyList<int> hits = snapshot.MatchAllTerms(phrase.Terms, MaxEdgesPerPhrase);
            if (hits.Count == 0)
            {
                continue;
            }

            if (phrase.IsRegressionArea)
            {
                covered.Add(phrase.Text);
            }

            foreach (int testCaseId in hits)
            {
                edges.TryAdd(testCaseId, new AnchorEdge(
                    testCaseId, null, AnchorSource.DeclaredMapping, DeclaredWeight,
                    $"Declared mapping '{phrase.Text}' for {area.AreaId}."));
            }
        }

        _logger.Info("ImpactDeclared",
            $"area={area.AreaId} phrases={phrases.Count} edges={edges.Count} coveredAreas={covered.Count}");

        return new DeclaredMappingResult(edges.Values.OrderBy(e => e.TestCaseId).ToList(), covered);
    }

    private List<Phrase> BuildPhrases(ImpactedArea area)
    {
        var phrases = new List<Phrase>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Only declared regression areas can mark coverage — coverage is scored against exactly this list.
        foreach (string declared in area.DeclaredRegressionAreas)
        {
            Add(declared, isRegressionArea: true);
        }

        // Use cases carry the FR/UC/US tokens that test case titles embed; they widen recall but are not areas.
        foreach (string useCase in ResolveComponent(area)?.UseCases ?? [])
        {
            Add(useCase, isRegressionArea: false);
        }

        return phrases;

        void Add(string text, bool isRegressionArea)
        {
            if (string.IsNullOrWhiteSpace(text) || !seen.Add(text))
            {
                return;
            }

            string[] terms = Tokenizer.Tokenize(text, _options.Keywords).ToArray();
            if (terms.Length > 0)
            {
                phrases.Add(new Phrase(text.Trim(), terms, isRegressionArea));
            }
        }
    }

    private ComponentBuildInfo? ResolveComponent(ImpactedArea area)
    {
        foreach (string? candidate in new[] { area.AreaId, area.Subsystem, area.DisplayName })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            ComponentBuildInfo? exact = _map.All()
                .FirstOrDefault(c => string.Equals(c.ComponentId, candidate, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        return string.IsNullOrWhiteSpace(area.AreaId) ? null : _map.Resolve(area.AreaId);
    }

    private sealed record Phrase(string Text, string[] Terms, bool IsRegressionArea);
}
