using TestControllerGrpc.Models;

namespace TestControllerGrpc.Ado.Reporting;

/// <summary>
/// Deterministic, offline "AI" summary of the current regression scope. Turns the churn rows into a
/// headline + highlight bullets + a short narrative — no external model call, so it always works and
/// leaks nothing. Designed to be swappable for an LLM-backed summarizer behind <see cref="IChurnSummarizer"/>.
/// </summary>
public sealed class ChurnSummarizer : IChurnSummarizer
{
    public ChurnSummary Summarize(ChurnReport report)
    {
        var rows = report.Rows;
        if (rows.Count == 0)
        {
            return new ChurnSummary(
                Headline: $"No component changes in {report.RangeText}.",
                Highlights: [],
                Narrative: $"No component pipelines reported commits, pull requests or work items in {report.RangeText}. " +
                           "There is nothing to regress for this window.");
        }

        var changes = rows.SelectMany(r => r.Changes).ToList();
        var prCount = changes.Count(c => c.Kind == RegressionChangeKind.PullRequest);
        var commitCount = changes.Count(c => c.Kind == RegressionChangeKind.Commit);
        var autoCount = changes.Count(c => c.Kind == RegressionChangeKind.Automated);
        var fileCount = rows.Sum(r => r.TotalFilesModified);

        var runtime = rows.Count(r => r.Category is RegressionCategoryKind.Runtime or RegressionCategoryKind.Both);
        var config = rows.Count(r => r.Category is RegressionCategoryKind.Config or RegressionCategoryKind.Both);

        var workItems = changes
            .SelectMany(c => c.WorkItems)
            .GroupBy(w => w.Id)
            .Select(g => g.First())
            .ToList();
        var bugs = workItems.Count(w => w.Kind == RegressionWorkItemKind.Bug);
        var stories = workItems.Count(w => w.Kind == RegressionWorkItemKind.Story);
        var ims = workItems.Count(w => w.Kind == RegressionWorkItemKind.Ims);

        var failing = rows
            .Where(r => !string.IsNullOrEmpty(r.RiskTier) &&
                        !r.RiskTier.Equals("succeeded", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Component)
            .ToList();

        var hotspots = rows
            .Where(r => r.TotalFilesModified > 0)
            .OrderByDescending(r => r.TotalFilesModified)
            .Take(5)
            .ToList();

        var headline = $"{rows.Count} component(s) changed in {report.RangeText}: " +
                       $"{changes.Count} change(s) across {fileCount} file(s).";

        var highlights = new List<string>();

        var mix = new List<string>();
        if (prCount > 0) mix.Add($"{prCount} pull request(s)");
        if (commitCount > 0) mix.Add($"{commitCount} direct commit(s)");
        if (autoCount > 0) mix.Add($"{autoCount} automated sync(s)");
        if (mix.Count > 0) highlights.Add("Change mix: " + string.Join(", ", mix) + ".");

        highlights.Add($"Category split: {runtime} Runtime \u00b7 {config} Config.");

        var wi = new List<string>();
        if (bugs > 0) wi.Add($"{bugs} bug(s)");
        if (stories > 0) wi.Add($"{stories} story(ies)");
        if (ims > 0) wi.Add($"{ims} IMS/issue(s)");
        if (wi.Count > 0) highlights.Add("Linked work items: " + string.Join(", ", wi) + ".");

        if (hotspots.Count > 0)
            highlights.Add("Highest churn: " +
                string.Join(", ", hotspots.Select(h => $"{h.Component} ({h.TotalFilesModified} files)")) + ".");

        if (failing.Count > 0)
            highlights.Add($"\u26a0 {failing.Count} component(s) whose latest build did not succeed: " +
                string.Join(", ", failing.Take(8)) + (failing.Count > 8 ? "\u2026" : "") + ".");

        var noSuite = rows.Count(r => r.AutomatedSuites.Count == 0 && r.ManualSuites.Count == 0);
        if (noSuite > 0)
            highlights.Add($"{noSuite} component(s) have no mapped test suite yet.");

        var narrative =
            $"Between {report.From:yyyy-MM-dd} and {report.To:yyyy-MM-dd}, {rows.Count} component pipeline(s) " +
            $"recorded {changes.Count} change(s) touching {fileCount} file(s). " +
            (prCount > 0 ? $"{prCount} arrived through pull requests" : "No pull requests were merged") +
            (commitCount > 0 ? $" and {commitCount} as direct commits. " : ". ") +
            $"The scope skews {(runtime >= config ? "Runtime" : "Config")} ({runtime} Runtime vs {config} Config). " +
            (bugs > 0 ? $"{bugs} bug(s) are linked, so prioritise their subsystems. " : "") +
            (failing.Count > 0
                ? $"Note {failing.Count} component(s) with a non-successful latest build \u2014 verify those first. "
                : "All changed components have a successful latest build. ") +
            (hotspots.Count > 0
                ? $"Focus regression on the highest-churn areas: {string.Join(", ", hotspots.Take(3).Select(h => h.Component))}."
                : "");

        return new ChurnSummary(headline, highlights, narrative);
    }

    public string SummarizeComponent(SubsystemRow row)
    {
        var changes = row.Changes;
        var pr = changes.Count(c => c.Kind == RegressionChangeKind.PullRequest);
        var commit = changes.Count(c => c.Kind == RegressionChangeKind.Commit);
        var auto = changes.Count(c => c.Kind == RegressionChangeKind.Automated);
        var workItems = changes.SelectMany(c => c.WorkItems).GroupBy(w => w.Id).Select(g => g.First()).ToList();
        var bugs = workItems.Count(w => w.Kind == RegressionWorkItemKind.Bug);

        var mix = new List<string>();
        if (pr > 0) mix.Add($"{pr} PR");
        if (commit > 0) mix.Add($"{commit} commit");
        if (auto > 0) mix.Add($"{auto} auto");

        var parts = new List<string>
        {
            $"{row.Component} ({row.Category}) has {changes.Count} change(s)" +
            (mix.Count > 0 ? $" [{string.Join(", ", mix)}]" : "") +
            $" touching {row.TotalFilesModified} file(s).",
        };

        if (bugs > 0)
            parts.Add($"{bugs} bug(s) linked \u2014 prioritise these.");

        parts.Add(string.Equals(row.RiskTier, "succeeded", StringComparison.OrdinalIgnoreCase)
            ? "Latest build succeeded."
            : $"Latest build: {row.RiskTier} \u2014 verify before testing.");

        var tests = FunctionalTests(row);
        parts.Add(tests.Count > 0
            ? $"Run functional tests: {string.Join(", ", tests)}."
            : "No functional test areas mapped for this component yet.");

        return string.Join(" ", parts);
    }

    /// <summary>Functional test areas to execute for a component = its use cases + regression areas.</summary>
    public static IReadOnlyList<string> FunctionalTests(SubsystemRow row) =>
        (row.UseCases ?? [])
            .Concat(row.RegressionAreas ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
