using System.Text.RegularExpressions;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Features;

/// <summary>Finds pipeline steps that call a toggleable feature, so pre-flight can warn before the step fails.</summary>
public static class FeatureUsage
{
    // Most specific first: an impact-mapping URL must not also count as Code Churn.
    private static readonly (Regex Pattern, string Feature, string What)[] Markers =
    [
        (new Regex(@"/api/results/\S*/fir\b", RegexOptions.IgnoreCase), FeatureKeys.FirReport, "the FIR report API"),
        (new Regex(@"/api/results\b", RegexOptions.IgnoreCase), FeatureKeys.Results, "the Results API"),
        (new Regex(@"/api/reportcard\b", RegexOptions.IgnoreCase), FeatureKeys.Reports, "the Report Card API"),
        (new Regex(@"/api/impact-mapping\b|Rebuild-ImpactIndex|Prepare-ImpactIndexStorage", RegexOptions.IgnoreCase),
            FeatureKeys.ImpactMapping, "the impact index"),
        (new Regex(@"/api/impact(?![-\w])", RegexOptions.IgnoreCase), FeatureKeys.CodeChurn, "the Code Churn API"),
    ];

    /// <summary>Features the action's command line, parameters, completion check or mail attachments refer to.</summary>
    public static IReadOnlyList<(string Feature, string What)> Detect(ActionConfig action, Func<string, string>? resolve = null)
    {
        resolve ??= s => s;
        var text = string.Join("\n",
            new[] { action.Command, action.Parameters, action.CompletionCheckCommand, action.Attachment, action.Embed }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(resolve));
        if (text.Length == 0) return [];

        var found = new List<(string, string)>();
        foreach (var (pattern, feature, what) in Markers)
        {
            if (found.Any(f => f.Item1 == feature)) continue;
            if (pattern.IsMatch(text)) found.Add((feature, what));
        }
        // A FIR URL is also a Results URL; FirReport is off whenever Results is, so one warning is enough.
        if (found.Any(f => f.Item1 == FeatureKeys.FirReport))
            found.RemoveAll(f => f.Item1 == FeatureKeys.Results);
        return found;
    }
}
