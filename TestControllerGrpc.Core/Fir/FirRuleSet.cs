using System.Text.RegularExpressions;

namespace TestControllerGrpc.Core.Fir;

/// <summary>
/// The packs chosen for one test, compiled once. Framework rules come from the first framework pack;
/// product rules are merged across every product pack so two products can contribute noise or error
/// shapes to the same run.
/// </summary>
public sealed class FirRuleSet
{
    /// <summary>A pack author's runaway pattern must not hang a report.</summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    public static FirRuleSet Empty { get; } = new([], []);

    private FirRuleSet(IReadOnlyList<FirRulePack> packs, IReadOnlyList<string> errors)
    {
        Packs = packs;
        Errors = errors;
        FailLevels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SecretLines = [];
        ProductError = [];
        Noise = [];
        Categories = [];
        SlowStepSeconds = 180;
        TestDataLayout = FirTestDataLayout.Default;
        TestDataKeywordMap = new Dictionary<string, IReadOnlyList<string>>();
    }

    public IReadOnlyList<FirRulePack> Packs { get; }
    public IReadOnlyList<string> Errors { get; }
    public IReadOnlyList<string> Labels => [.. Packs.Select(p => p.Label)];

    public Regex? RowMarker { get; private init; }
    public Regex? TestDataLine { get; private init; }
    public Regex? StepLine { get; private init; }
    public Regex? ReasonLine { get; private init; }
    public Regex? ProductCount { get; private init; }
    public Regex? ObjectShortName { get; private init; }
    public IReadOnlyList<Regex> SecretLines { get; private init; }
    public IReadOnlyList<Regex> ProductError { get; private init; }
    public IReadOnlyList<Regex> Noise { get; private init; }
    public IReadOnlySet<string> FailLevels { get; private init; }
    public int SlowStepSeconds { get; private init; }

    public FirTestDataLayout TestDataLayout { get; private init; }

    /// <summary>Sheet keyword (lower-case) to the log keywords it produces.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> TestDataKeywordMap { get; private init; }

    /// <summary>Category name paired with its keyword matcher, in declaration order; first match wins.</summary>
    public IReadOnlyList<(string Name, Regex Keyword)> Categories { get; private init; }

    /// <summary>Rows and steps can only be read when a framework pack supplied both markers.</summary>
    public bool CanParseSteps => RowMarker is not null && StepLine is not null;

    public static FirRuleSet Create(IReadOnlyList<FirRulePack> selected)
    {
        var errors = new List<string>();
        var framework = selected.FirstOrDefault(p => p.Kind == FirPackKind.Framework);
        var products = selected.Where(p => p.Kind == FirPackKind.Product).ToList();

        var failLevels = new HashSet<string>(framework?.FailLevels ?? [], StringComparer.OrdinalIgnoreCase);
        if (failLevels.Count == 0) failLevels.Add("Failed");

        var categories = new List<(string, Regex)>();
        foreach (var product in products)
            foreach (var (name, globs) in product.Categories)
                foreach (var glob in globs)
                    if (TryGlob(glob, product.Id, errors) is { } rx)
                        categories.Add((name, rx));

        return new FirRuleSet(selected, errors)
        {
            RowMarker = TryCompile(framework?.RowMarker, framework?.Id, errors),
            TestDataLine = TryCompile(framework?.TestDataLine, framework?.Id, errors),
            StepLine = TryCompile(framework?.StepLine, framework?.Id, errors),
            ReasonLine = TryCompile(framework?.ReasonLine, framework?.Id, errors),
            SecretLines = CompileAll(framework?.SecretLines, framework?.Id, errors),
            SlowStepSeconds = framework?.SlowStepSeconds is > 0 ? framework.SlowStepSeconds : 180,
            ProductCount = TryCompile(products.Select(p => p.ProductCount).FirstOrDefault(c => c is not null),
                products.FirstOrDefault()?.Id, errors),
            ObjectShortName = TryCompile(products.Select(p => p.ObjectShortName).FirstOrDefault(c => c is not null),
                products.FirstOrDefault()?.Id, errors),
            ProductError = [.. products.SelectMany(p => CompileAll(p.ProductError, p.Id, errors))],
            Noise = [.. products.SelectMany(p => CompileAll(p.Noise, p.Id, errors))],
            FailLevels = failLevels,
            Categories = categories,
            TestDataLayout = framework?.TestData ?? FirTestDataLayout.Default,
            TestDataKeywordMap = framework?.TestDataKeywordMap ?? new Dictionary<string, IReadOnlyList<string>>(),
        };
    }

    /// <summary>The log keywords a sheet keyword turns into; the sheet and the log rarely agree on names.</summary>
    public IReadOnlyList<string> LogKeywordsFor(string? sheetKeyword)
        => string.IsNullOrWhiteSpace(sheetKeyword)
            ? []
            : TestDataKeywordMap.TryGetValue(sheetKeyword.Trim().ToLowerInvariant(), out var mapped) ? mapped : [];

    public string? CategoryFor(string keyword)
        => Categories.FirstOrDefault(c => c.Keyword.IsMatch(keyword)).Name;

    /// <summary>Collapses a generated object name to its prefix; the full name stays in the raw trace.</summary>
    public string Shorten(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        if (ObjectShortName is null) return name;

        try
        {
            var match = ObjectShortName.Match(name);
            if (!match.Success) return name;
            var shortened = match.Groups["short"].Success ? match.Groups["short"].Value : match.Value;
            return shortened.Length < name.Length ? shortened + "\u2026" : name;
        }
        catch (RegexMatchTimeoutException)
        {
            return name;
        }
    }

    private static IReadOnlyList<Regex> CompileAll(IReadOnlyList<string>? patterns, string? packId, List<string> errors)
        => patterns is null ? [] : [.. patterns.Select(p => TryCompile(p, packId, errors)).OfType<Regex>()];

    private static Regex? TryCompile(string? pattern, string? packId, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;

        try
        {
            return new Regex(pattern, RegexOptions.None, MatchTimeout);
        }
        catch (ArgumentException ex)
        {
            errors.Add($"{packId ?? "(pack)"}: invalid pattern '{pattern}' - {ex.Message}");
            return null;
        }
    }

    private static Regex? TryGlob(string glob, string packId, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(glob)) return null;

        var pattern = "^" + string.Join(".*", glob.Split('*').Select(Regex.Escape)) + "$";
        return TryCompile(pattern, packId, errors) is { } rx
            ? new Regex(pattern, RegexOptions.IgnoreCase, MatchTimeout)
            : null;
    }
}
