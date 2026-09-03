using System.Text.RegularExpressions;

namespace TestControllerGrpc.Core.Impact.Text;

/// <summary>
/// The single source of truth for tokenization across the engine (P03). Index construction and query
/// construction must both call this class — a second tokenizer would silently corrupt BM25. Bump
/// <see cref="Version"/> on any rule change so the index builder forces a full rebuild.
/// </summary>
public static partial class Tokenizer
{
    /// <summary>Tokenization rule version. The index stores this and rebuilds when it changes.</summary>
    public const string Version = "1";

    [GeneratedRegex(@"[^A-Za-z0-9]+")] private static partial Regex Delimiters();
    [GeneratedRegex(@"([a-z0-9])([A-Z])")] private static partial Regex LowerToUpper();
    [GeneratedRegex(@"([A-Z]+)([A-Z][a-z])")] private static partial Regex AcronymToWord();
    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")] private static partial Regex GuidLike();
    [GeneratedRegex(@"\.(cs|csproj|sln|json|xml|yml|yaml|md|txt|dll|exe|config|ts|tsx|js|jsx|razor|xaml|resx|props|targets)$")] private static partial Regex FileExtLike();

    /// <summary>
    /// Splits an identifier into lowercase parts across PascalCase, camelCase, snake_case, kebab-case,
    /// dot-separated and ALLCAPS runs. "GalaxyDeployEngine" -&gt; [galaxy, deploy, engine];
    /// "HTTPRequestHandler" -&gt; [http, request, handler].
    /// </summary>
    public static IReadOnlyList<string> SplitIdentifier(string identifier)
    {
        var parts = new List<string>();
        if (string.IsNullOrWhiteSpace(identifier)) return parts;

        foreach (var segment in Delimiters().Split(identifier))
        {
            if (segment.Length == 0) continue;
            // Acronym-to-word boundary first (HTTPRequest -> HTTP Request), then lower/upper (aB -> a B).
            var spaced = LowerToUpper().Replace(AcronymToWord().Replace(segment, "$1 $2"), "$1 $2");
            foreach (var word in spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                parts.Add(word.ToLowerInvariant());
        }
        return parts;
    }

    /// <summary>Lowercase + trim a single term. Null-safe.</summary>
    public static string NormalizeTerm(string term)
        => string.IsNullOrWhiteSpace(term) ? "" : term.Trim().ToLowerInvariant();

    /// <summary>
    /// Tokenizes free text into deduplicated, first-appearance-ordered terms. Emits each compound token
    /// alongside its split parts (compounds are the strongest lexical signal), then drops tokens under
    /// 3 chars, pure numerics, stop words, GUIDs and file-extension-like tokens.
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string? text, ImpactMappingOptions.KeywordOptions opts)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var stop = StopWordSet(opts);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var term in EnumerateValidTerms(text, stop))
            if (seen.Add(term)) result.Add(term);
        return result;
    }

    /// <summary>
    /// Tokenizes free text into term frequencies using the exact same rules as <see cref="Tokenize"/> but
    /// WITHOUT deduplication — the index builder needs raw counts for BM25 term frequencies. Sharing this
    /// method (rather than a second tokenizer) keeps index and query tokenization identical.
    /// </summary>
    public static IReadOnlyDictionary<string, int> TokenizeWithCounts(string? text, ImpactMappingOptions.KeywordOptions opts)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return counts;

        var stop = StopWordSet(opts);
        foreach (var term in EnumerateValidTerms(text, stop))
            counts[term] = counts.TryGetValue(term, out var current) ? current + 1 : 1;
        return counts;
    }

    private static HashSet<string> StopWordSet(ImpactMappingOptions.KeywordOptions opts)
        => new(opts.StopWords ?? [], StringComparer.OrdinalIgnoreCase);

    /// <summary>Yields each valid compound token followed by its split parts, in document order (no dedupe).</summary>
    private static IEnumerable<string> EnumerateValidTerms(string text, HashSet<string> stop)
    {
        foreach (var raw in Delimiters().Split(text))
        {
            if (raw.Length == 0) continue;
            var compound = NormalizeTerm(raw);
            if (IsValidTerm(compound, stop)) yield return compound; // compound
            foreach (var part in SplitIdentifier(raw))              // parts
                if (IsValidTerm(part, stop)) yield return part;
        }
    }

    private static bool IsValidTerm(string term, HashSet<string> stop)
    {
        if (term.Length < 3) return false;
        if (term.All(char.IsDigit)) return false;
        if (stop.Contains(term)) return false;
        if (GuidLike().IsMatch(term)) return false;
        if (FileExtLike().IsMatch(term)) return false;
        return true;
    }
}
