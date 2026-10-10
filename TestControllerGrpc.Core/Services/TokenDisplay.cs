namespace TestControllerGrpc.Services;

/// <summary>Which parameter layer a token's value came from. Shown in tooltips.</summary>
/// <remarks>
/// Mirrors <c>ParameterRank</c> in <see cref="ParameterResolver"/>, but names the layers the way
/// operators talk about them. <see cref="Unknown"/> exists because values written through the legacy
/// plain-dictionary API carry no provenance - a tooltip must then say nothing rather than guess.
/// </remarks>
public enum TokenLayer
{
    Unknown = 0,
    Global,
    Profile,
    Pipeline,
    Trigger,
    Run,

    // Appended, not inserted: the members above are ordered by precedence but their numeric values
    // are implicit, so slotting this in beside Pipeline would renumber Trigger and Run.
    GlobalBuild,
}

/// <summary>One token occurrence inside a display string.</summary>
/// <param name="Name">Token name without brackets.</param>
/// <param name="Value">Masked display value, or null when unresolved.</param>
public readonly record struct TokenHit(string Name, string? Value, TokenLayer Layer, bool IsSecret)
{
    public bool IsResolved => Value is not null;
}

/// <summary>Result of resolving one token-bearing field for display.</summary>
public readonly record struct DisplayResult(string Text, IReadOnlyList<TokenHit> Tokens)
{
    public bool HasUnresolved => Tokens.Any(t => !t.IsResolved);

    /// <summary>Tooltip lines in the form "[_Token] -> value (Layer)".</summary>
    public IEnumerable<string> Describe() => Tokens.Select(t => t.IsResolved
        ? t.Layer == TokenLayer.Unknown
            ? $"[{t.Name}] \u2192 {t.Value}"
            : $"[{t.Name}] \u2192 {t.Value} ({t.Layer})"
        : $"[{t.Name}] (not set)");
}

/// <summary>
/// The ONE place a token-bearing string becomes display text. Every label, pill, tooltip, dashboard
/// cell and API payload goes through here.
/// </summary>
/// <remarks>
/// Masking happens HERE, not at the call sites. Resolving a field that was previously shown as
/// "[_VCloudPassword]" would otherwise print the real credential into labels, the Execution
/// Dashboard, history rows and emails - a display change that silently becomes a disclosure. Only
/// the executor reads raw values, and it does not come through this class.
/// </remarks>
public static class TokenDisplay
{
    private static readonly System.Text.RegularExpressions.Regex TokenPattern =
        new(@"\[(\w+)\]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Shown in place of a secret's value.</summary>
    public const string Mask = "\u2022\u2022\u2022\u2022\u2022\u2022";

    /// <summary>Marker appended to a token that has no value in scope.</summary>
    public const string NotSet = " (not set)";

    /// <summary>Whole-field text when a node has no pipeline context at all (Library nodes).</summary>
    public const string NoContextHint = "Not resolved \u2014 choose a pipeline context";

    private static readonly string[] SecretMarkers = ["password", "secret", "token", "pwd"];

    /// <summary>True when a token's NAME marks it as a credential, regardless of its value.</summary>
    public static bool IsSecret(string tokenName)
    {
        foreach (var marker in SecretMarkers)
            if (tokenName.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Resolves every <c>[Token]</c> in <paramref name="raw"/>. Tokens that resolve are replaced by
    /// their (masked, when secret) value; tokens that do not are kept and marked, so a partly
    /// resolved field shows real values beside the specific thing that is missing.
    /// </summary>
    /// <param name="lookup">Returns the value and layer for a token name, or null when not in scope.</param>
    /// <param name="showRawTokens">When true, returns the raw text unchanged (the "Show tokens" toggle).</param>
    /// <param name="isReserved">Bracketed words that are decoration, not tokens; left verbatim and unreported.</param>
    public static DisplayResult Resolve(
        string? raw,
        Func<string, (string Value, TokenLayer Layer)?> lookup,
        bool showRawTokens = false,
        Func<string, bool>? isReserved = null)
    {
        if (string.IsNullOrEmpty(raw)) return new DisplayResult(raw ?? "", []);
        if (!raw.Contains('[')) return new DisplayResult(raw, []);

        var hits = new List<TokenHit>();

        var text = TokenPattern.Replace(raw, match =>
        {
            var name = match.Groups[1].Value;
            if (isReserved is not null && isReserved(name)) return match.Value;

            var secret = IsSecret(name);
            var found = lookup(name);

            if (found is not { } v)
            {
                hits.Add(new TokenHit(name, null, TokenLayer.Unknown, secret));
                return match.Value + NotSet;
            }

            var shown = secret ? Mask : v.Value;
            hits.Add(new TokenHit(name, shown, v.Layer, secret));
            return showRawTokens ? match.Value : shown;
        });

        return new DisplayResult(showRawTokens ? raw : text, hits);
    }

    /// <summary>Masks a value that is already resolved, for text that never carried a token.</summary>
    public static string MaskIfSecret(string tokenName, string value) => IsSecret(tokenName) ? Mask : value;

    /// <summary>Display name for the parameter layer a value won from.</summary>
    public static TokenLayer LayerFromRank(ParameterRank rank) => rank switch
    {
        ParameterRank.Global => TokenLayer.Global,
        ParameterRank.ParameterFile => TokenLayer.Profile,
        ParameterRank.PipelinePin => TokenLayer.Pipeline,
        ParameterRank.GlobalVars => TokenLayer.GlobalBuild,
        ParameterRank.TriggerFile => TokenLayer.Trigger,
        ParameterRank.RunOverride => TokenLayer.Run,
        _ => TokenLayer.Unknown,
    };
}
