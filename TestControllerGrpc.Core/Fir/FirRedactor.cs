using System.Text.RegularExpressions;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Fir;

/// <summary>
/// Masks secrets in report text. <see cref="SecurityRedactor"/>'s value registry is populated at run time
/// by the parameter resolver, so a report built from last month's TRX sees it empty - these patterns have
/// to stand on their own, and the registry is only a bonus when the report is built during a live run.
/// </summary>
public static partial class FirRedactor
{
    public static string Marker => SecurityRedactor.Redacted;

    public static string Mask(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var masked = JwtRegex().Replace(text, SecurityRedactor.Redacted);
        masked = BearerRegex().Replace(masked, m => $"{m.Groups[1].Value}{SecurityRedactor.Redacted}");
        return SecurityRedactor.Redact(masked) ?? string.Empty;
    }

    /// <summary>
    /// Counts the markers masking ADDED. A shape count cannot verify a shape-preserving substitution -
    /// "***REDACTED***" is itself a non-whitespace run - so the only honest measure is the difference.
    /// </summary>
    public static int CountSecrets(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return CountMarkers(Mask(text)) - CountMarkers(text);
    }

    public static int CountSecrets(IEnumerable<string> sections)
        => sections.Sum(CountSecrets);

    private static int CountMarkers(string text)
    {
        var count = 0;
        var at = text.IndexOf(SecurityRedactor.Redacted, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = text.IndexOf(SecurityRedactor.Redacted, at + SecurityRedactor.Redacted.Length, StringComparison.Ordinal);
        }

        return count;
    }

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]*")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"(?i)\b(Bearer\s+)[A-Za-z0-9\-._~+/]{16,}={0,2}")]
    private static partial Regex BearerRegex();
}
