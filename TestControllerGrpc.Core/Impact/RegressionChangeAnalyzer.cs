using System.Text;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Core.Impact;

/// <summary>A functional test recommended for a change; <see cref="Relevant"/> when its name matches the change's themes.</summary>
public sealed record RecommendedTest(string Name, bool Relevant);

/// <summary>
/// The row-expand payload: the engine's matched ADO test cases plus a deterministic, offline change analysis
/// (summary + recommended functional tests). The analysis is the fallback shown when the engine retrieves no
/// test cases (empty retrieval index) so a real bug-fix merge still yields "what changed" and "what to run".
/// </summary>
public sealed record ImpactedComponentAnalysis(
    IReadOnlyList<ImpactedTestCaseMatch> Matches,
    string ChangeSummary,
    IReadOnlyList<RecommendedTest> RecommendedTests,
    string? IndexHealthMessage = null);

/// <summary>
/// Deterministic, offline analysis of an impacted component's changes — grounded in the PR/commit titles and
/// linked work items already on the <see cref="SubsystemRow"/>. Needs no ADO index or LLM, so it always
/// produces a useful summary and a relevance-ranked test recommendation even when retrieval finds nothing.
/// </summary>
public static class RegressionChangeAnalyzer
{
    private const int MinRelevantTokenLength = 3;

    /// <summary>Summarizes what the merge(s) changed in this component, leading with linked bug fixes.</summary>
    public static string Summarize(SubsystemRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var parts = new List<string>();
        int prCount = row.Changes.Count(c => c.Kind == RegressionChangeKind.PullRequest);
        parts.Add(prCount > 0
            ? $"{prCount} merged PR(s) in {row.Component} touching {row.TotalFilesModified} file(s)."
            : $"{row.Changes.Count} change(s) in {row.Component} touching {row.TotalFilesModified} file(s).");

        List<string> intents = row.Changes
            .Where(c => c.Kind != RegressionChangeKind.Automated)
            .Select(c => CleanIntent(c.Summary))
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (intents.Count > 0)
            parts.Add("Changes: " + string.Join("; ", intents) + ".");

        List<RegressionWorkItemRef> workItems = row.Changes
            .SelectMany(c => c.WorkItems)
            .GroupBy(w => w.Id)
            .Select(g => g.First())
            .ToList();

        List<string> bugs = workItems.Where(w => w.Kind == RegressionWorkItemKind.Bug).Select(w => w.Title).ToList();
        if (bugs.Count > 0)
            parts.Add($"Fixes bug(s) (prioritise): {string.Join("; ", bugs)}.");

        List<string> stories = workItems.Where(w => w.Kind == RegressionWorkItemKind.Story).Select(w => w.Title).ToList();
        if (stories.Count > 0)
            parts.Add($"Story: {string.Join("; ", stories)}.");

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Recommends the component's mapped functional tests (use cases + regression areas), ordering the ones
    /// whose names appear in the change's PR/work-item text first and flagging them <see cref="RecommendedTest.Relevant"/>.
    /// </summary>
    public static IReadOnlyList<RecommendedTest> RecommendTests(SubsystemRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        List<string> tests = (row.UseCases ?? [])
            .Concat(row.RegressionAreas ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (tests.Count == 0)
            return [];

        string changeText = NormalizeAlphanumeric(ChangeText(row));
        return tests
            .Select(t => new RecommendedTest(t, IsRelevant(t, changeText)))
            .OrderByDescending(r => r.Relevant)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ChangeText(SubsystemRow row)
    {
        var sb = new StringBuilder();
        foreach (RegressionChangeRef c in row.Changes)
        {
            sb.Append(' ').Append(c.Summary);
            foreach (RegressionWorkItemRef w in c.WorkItems)
                sb.Append(' ').Append(w.Title);
        }
        return sb.ToString();
    }

    private static bool IsRelevant(string testName, string normalizedChangeText)
    {
        string t = NormalizeAlphanumeric(testName);
        return t.Length >= MinRelevantTokenLength && normalizedChangeText.Contains(t, StringComparison.Ordinal);
    }

    private static string CleanIntent(string? summary)
    {
        string s = (summary ?? "").Trim();
        // Drop the "Merged PR 1354537:" bookkeeping prefix so the functional intent leads.
        if (s.StartsWith("Merged PR ", StringComparison.OrdinalIgnoreCase))
        {
            int colon = s.IndexOf(':');
            if (colon is > 0 and < 24)
                s = s[(colon + 1)..].Trim();
        }
        return s;
    }

    private static string NormalizeAlphanumeric(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s)
            if (char.IsLetterOrDigit(ch))
                sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }
}
