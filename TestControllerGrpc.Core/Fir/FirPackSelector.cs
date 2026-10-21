using System.Text.RegularExpressions;

namespace TestControllerGrpc.Core.Fir;

/// <summary>
/// Decides which packs describe a given test. Nothing here is product-specific: a pack is chosen by
/// what it declares about itself, so adding a team's rules file is the whole integration step.
/// </summary>
public static class FirPackSelector
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    public static IReadOnlyList<FirRulePack> Select(IReadOnlyList<FirRulePack>? packs, FirTrxInput input)
    {
        if (packs is null || packs.Count == 0) return [];

        var output = string.Join('\n', input.AllText());
        var framework = packs.FirstOrDefault(p => p.Kind == FirPackKind.Framework && Applies(p, input, output));
        var products = packs.Where(p => p.Kind == FirPackKind.Product && Applies(p, input, output));

        return framework is null ? [.. products] : [framework, .. products];
    }

    internal static bool Applies(FirRulePack pack, FirTrxInput input, string output)
    {
        var applies = pack.AppliesTo;
        if (applies.IsEmpty) return false;

        if (applies.TestAssembly.Any(a => Contains(input.TestAssembly, a))) return true;
        if (applies.ClassName.Any(p => IsMatch(input.ClassName, p))) return true;

        // Conjunctive on purpose - this group is a fingerprint, not a list of alternatives.
        return applies.OutputContains.Count > 0
               && applies.OutputContains.All(s => Contains(output, s));
    }

    private static bool Contains(string? haystack, string needle)
        => !string.IsNullOrEmpty(haystack)
           && !string.IsNullOrEmpty(needle)
           && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static bool IsMatch(string? value, string pattern)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrWhiteSpace(pattern)) return false;

        try
        {
            return Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase, MatchTimeout);
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
