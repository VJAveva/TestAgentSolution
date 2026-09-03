using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Core.Impact.Text;

namespace TestControllerGrpc.Core.Impact.Query;

/// <summary>Builds weighted, labelled keyword groups from a change (P11).</summary>
public interface IKeywordExtractor
{
    /// <summary>Produces the keyword groups that drive retrieval. Pure and deterministic; no I/O.</summary>
    IReadOnlyList<KeywordGroup> Extract(ImpactedArea area, ChangeDocument doc, HydeQuery? hyde);
}

/// <summary>
/// Groups change terms by facet rather than emitting one group per token (P11): identity, changed
/// literals (the strongest facet — never down-weighted), changed public API, per-folder path clusters and
/// optional HyDE-expanded terms. Groups are capped by merging the lowest-weight groups into the nearest
/// higher-weight one (identity and literals are never dropped), terms are ordered by specificity, and each
/// group's id is a stable hash of its sorted terms so caching and provenance are reproducible.
/// </summary>
public sealed class KeywordExtractor : IKeywordExtractor
{
    private readonly ImpactMappingOptions.KeywordOptions _keywords;

    /// <summary>Creates the extractor from impact-mapping keyword options.</summary>
    public KeywordExtractor(IOptions<ImpactMappingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _keywords = options.Value.Keywords;
    }

    /// <inheritdoc />
    public IReadOnlyList<KeywordGroup> Extract(ImpactedArea area, ChangeDocument doc, HydeQuery? hyde)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(doc);

        List<RawGroup> groups = BuildGroups(area, doc, hyde);
        groups = MergeToGroupCap(groups, Math.Max(2, _keywords.MaxKeywordGroups));

        return groups
            .Select(Finalize)
            .OrderByDescending(g => g.Weight)
            .ThenBy(g => g.Label, StringComparer.Ordinal)
            .ToList();
    }

    private List<RawGroup> BuildGroups(ImpactedArea area, ChangeDocument doc, HydeQuery? hyde)
    {
        var groups = new List<RawGroup>
        {
            new("identity", 1.00, TokenizeAll([area.DisplayName, area.Vob, area.Subsystem])),
            new("literals", 0.95, TokenizeAll(doc.ChangedLiterals)),
            new("api", 0.85, TokenizeAll(doc.PublicApiChanges)),
        };

        int totalFiles = area.ChangedPaths.Count;
        if (totalFiles > 0)
        {
            foreach (IGrouping<string, string> cluster in area.ChangedPaths
                .GroupBy(TopFolder)
                .OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                double weight = 0.3 + (0.7 * cluster.Count() / totalFiles);
                groups.Add(new RawGroup($"paths:{cluster.Key}", weight, TokenizeAll(cluster)));
            }
        }

        if (hyde is { ExpandedTerms.Count: > 0 })
        {
            groups.Add(new RawGroup("expanded", 0.60, TokenizeAll(hyde.ExpandedTerms)));
        }

        return groups.Where(g => g.Terms.Count > 0).ToList();
    }

    private List<string> TokenizeAll(IEnumerable<string?> texts)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();
        foreach (string? text in texts)
        {
            foreach (string token in Tokenizer.Tokenize(text, _keywords))
            {
                if (seen.Add(token))
                {
                    ordered.Add(token);
                }
            }
        }

        return ordered;
    }

    private static string TopFolder(string path)
    {
        string[] segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 ? segments[0] : path;
    }

    private static List<RawGroup> MergeToGroupCap(List<RawGroup> groups, int maxGroups)
    {
        var working = groups.ToList();
        while (working.Count > maxGroups)
        {
            // Identity (1.0) and literals (0.95) are the highest-weight groups, so they are never the
            // lowest and are therefore never merged away.
            RawGroup lowest = working
                .OrderBy(g => g.Weight)
                .ThenBy(g => g.Label, StringComparer.Ordinal)
                .First();

            RawGroup target = NearestHigher(working, lowest);
            foreach (string term in lowest.Terms)
            {
                target.AddTerm(term);
            }

            working.Remove(lowest);
        }

        return working;
    }

    private static RawGroup NearestHigher(List<RawGroup> groups, RawGroup lowest)
    {
        List<RawGroup> others = groups.Where(g => !ReferenceEquals(g, lowest)).ToList();

        List<RawGroup> higher = others
            .Where(g => g.Weight >= lowest.Weight)
            .OrderBy(g => g.Weight)
            .ThenBy(g => g.Label, StringComparer.Ordinal)
            .ToList();

        return higher.Count > 0
            ? higher[0]
            : others.OrderByDescending(g => g.Weight).ThenBy(g => g.Label, StringComparer.Ordinal).First();
    }

    private KeywordGroup Finalize(RawGroup group)
    {
        List<string> ordered = group.Terms
            .OrderByDescending(t => t.Length) // compound/most-specific tokens are the longest
            .ThenBy(t => t, StringComparer.Ordinal)
            .Take(Math.Max(1, _keywords.MaxTermsPerGroup))
            .ToList();

        return new KeywordGroup(StableGroupId(ordered), group.Label, ordered, group.Weight);
    }

    private static string StableGroupId(IReadOnlyList<string> terms)
    {
        string joined = string.Join('\u0001', terms.OrderBy(t => t, StringComparer.Ordinal));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private sealed class RawGroup(string label, double weight, List<string> terms)
    {
        private readonly HashSet<string> _seen = new(terms, StringComparer.Ordinal);

        public string Label { get; } = label;

        public double Weight { get; } = weight;

        public List<string> Terms { get; } = terms;

        public void AddTerm(string term)
        {
            if (_seen.Add(term))
            {
                Terms.Add(term);
            }
        }
    }
}
