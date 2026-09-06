using System.Globalization;
using System.Net;
using System.Text;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Ado.Reporting;

/// <summary>
/// Renders a <see cref="ChurnReport"/> to RFC-4180 CSV or a self-contained, email-friendly HTML
/// document (inline styles only). The HTML leads with the computed <see cref="IChurnSummarizer"/> digest.
/// </summary>
public sealed class ChurnReportBuilder : IChurnReportBuilder
{
    private readonly IChurnSummarizer _summarizer;

    public ChurnReportBuilder(IChurnSummarizer summarizer) => _summarizer = summarizer;

    public string BuildCsv(ChurnReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#,Component,Category,Repository,Branch,Activity,Work Items,Modified Files,Impacted Functionality,Test Use Cases,Build Status");

        var n = 1;
        foreach (var r in report.Rows)
        {
            var pr = r.Changes.Count(c => c.Kind == RegressionChangeKind.PullRequest);
            var commit = r.Changes.Count(c => c.Kind == RegressionChangeKind.Commit);
            var auto = r.Changes.Count(c => c.Kind == RegressionChangeKind.Automated);
            var wi = r.Changes.SelectMany(c => c.WorkItems).Select(w => w.Id).Distinct().Count();

            var fields = new[]
            {
                n.ToString(CultureInfo.InvariantCulture),
                r.Component,
                r.Category.ToString(),
                r.Repository ?? "",
                r.DefaultBranch ?? "",
                $"Files changed: {r.TotalFilesModified}; PR's: {pr}; Commits: {commit}; WI: {wi}; Auto: {auto}",
                WorkItemsText(r),
                FilesText(r),
                Join(r.RegressionAreas),
                Join(r.UseCases),
                BuildStatusText(r),
            };
            sb.AppendLine(string.Join(",", fields.Select(Csv)));
            n++;
        }
        return sb.ToString();
    }

    public string BuildHtml(ChurnReport report)
    {
        var s = _summarizer.Summarize(report);
        var sb = new StringBuilder();

        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Code Churn Report</title></head>");
        sb.Append("<body style=\"font-family:Segoe UI,Arial,sans-serif;background:#f4f5f7;color:#1b1b28;margin:0;padding:24px;\">");
        sb.Append("<div style=\"max-width:1320px;margin:0 auto;background:#ffffff;border:1px solid #e2e2ea;border-radius:8px;overflow:hidden;\">");

        // Header band
        sb.Append("<div style=\"background:#232140;color:#e6e3f5;padding:18px 24px;\">");
        sb.Append("<div style=\"font-size:18px;font-weight:600;\">Code Churn &amp; Regression Scope</div>");
        sb.Append($"<div style=\"font-size:12px;color:#a7a2cc;margin-top:4px;\">{Enc(report.ScopeLabel)} \u00b7 {Enc(report.RangeText)} \u00b7 generated {report.GeneratedUtc.LocalDateTime:yyyy-MM-dd HH:mm}</div>");
        sb.Append("</div>");

        // AI summary section
        sb.Append("<div style=\"padding:18px 24px;border-bottom:1px solid #eee;\">");
        sb.Append("<div style=\"font-size:13px;font-weight:600;color:#4b3f8f;margin-bottom:8px;\">AI Summary</div>");
        sb.Append($"<div style=\"font-size:14px;font-weight:600;margin-bottom:10px;\">{Enc(s.Headline)}</div>");
        sb.Append($"<div style=\"font-size:13px;color:#33334a;line-height:1.5;margin-bottom:10px;\">{Enc(s.Narrative)}</div>");
        if (s.Highlights.Count > 0)
        {
            sb.Append("<ul style=\"margin:0;padding-left:18px;font-size:13px;color:#33334a;line-height:1.6;\">");
            foreach (var h in s.Highlights)
                sb.Append($"<li>{Enc(h)}</li>");
            sb.Append("</ul>");
        }
        sb.Append("</div>");

        // Detail table
        sb.Append("<div style=\"padding:8px 24px 24px;\">");
        sb.Append("<table style=\"width:100%;border-collapse:collapse;font-size:12px;\">");
        sb.Append("<thead><tr style=\"text-align:left;color:#726d9b;border-bottom:2px solid #e2e2ea;\">");
        foreach (var h in new[] { "#", "Component", "Category", "Repository", "Branch", "Activity", "Work Items", "Modified Files", "Impacted Functionality", "Test Use Cases", "Build Status" })
            sb.Append($"<th style=\"padding:8px;\">{Enc(h)}</th>");
        sb.Append("</tr></thead><tbody>");

        var n = 1;
        foreach (var r in report.Rows)
        {
            var pr = r.Changes.Count(c => c.Kind == RegressionChangeKind.PullRequest);
            var commit = r.Changes.Count(c => c.Kind == RegressionChangeKind.Commit);
            var auto = r.Changes.Count(c => c.Kind == RegressionChangeKind.Automated);
            var wi = r.Changes.SelectMany(c => c.WorkItems).Select(w => w.Id).Distinct().Count();
            var catColor = r.Category switch
            {
                RegressionCategoryKind.Runtime => "#b26a00",
                RegressionCategoryKind.Config => "#00697a",
                RegressionCategoryKind.Both => "#6a1b9a",
                _ => "#555555",
            };
            var bg = n % 2 == 0 ? "#faf9ff" : "#ffffff";

            sb.Append($"<tr style=\"background:{bg};border-bottom:1px solid #eee;vertical-align:top;\">");
            sb.Append($"<td style=\"padding:8px;color:#999;\">{n}</td>");
            sb.Append($"<td style=\"padding:8px;font-weight:600;\">{Enc(r.Component)}</td>");
            sb.Append($"<td style=\"padding:8px;color:{catColor};font-weight:600;\">{Enc(r.Category.ToString())}</td>");
            sb.Append($"<td style=\"padding:8px;\">{RepoHtml(r)}</td>");
            sb.Append($"<td style=\"padding:8px;color:#666;\">{Enc(r.DefaultBranch ?? "")}</td>");
            sb.Append($"<td style=\"padding:8px;white-space:nowrap;color:#444;\">Files changed: {r.TotalFilesModified}<br>PR's: {pr}<br>Commits: {commit}<br>WI: {wi}<br>Auto: {auto}</td>");
            sb.Append($"<td style=\"padding:8px;min-width:190px;\">{WorkItemsHtml(r)}</td>");
            sb.Append($"<td style=\"padding:8px;font-family:Consolas,monospace;font-size:11px;min-width:170px;\">{FilesHtml(r)}</td>");
            sb.Append($"<td style=\"padding:8px;color:#4b3f8f;min-width:150px;\">{Enc(JoinOrDash(r.RegressionAreas))}</td>");
            sb.Append($"<td style=\"padding:8px;color:#00697a;min-width:150px;\">{Enc(JoinOrDash(r.UseCases))}</td>");
            sb.Append($"<td style=\"padding:8px;white-space:nowrap;\">{BuildStatusHtml(r)}</td>");
            sb.Append("</tr>");
            n++;
        }

        sb.Append("</tbody></table></div>");
        sb.Append("<div style=\"padding:12px 24px;background:#faf9ff;border-top:1px solid #eee;font-size:11px;color:#999;\">Generated by TestController \u00b7 Regression tab \u00b7 Azure DevOps live ingest</div>");
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    private static string Csv(string? value)
    {
        value ??= "";
        if (value.IndexOfAny(['"', ',', '\n', '\r']) >= 0)
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    // Impacted functionality (regression areas) / test use cases mapped to a component.
    private static string Join(IReadOnlyList<string>? items) =>
        items is { Count: > 0 } ? string.Join("; ", items) : "";

    private static string JoinOrDash(IReadOnlyList<string>? items) =>
        items is { Count: > 0 } ? string.Join("; ", items) : "\u2014";

    private static IReadOnlyList<RegressionWorkItemRef> DistinctWorkItems(SubsystemRow r) =>
        r.Changes.SelectMany(c => c.WorkItems).GroupBy(w => w.Id).Select(g => g.First()).ToList();

    /// <summary>Work items grouped for display, in the required order: Bugs, IMS, User Stories, then anything else.</summary>
    internal static IReadOnlyList<(string Heading, IReadOnlyList<RegressionWorkItemRef> Items)> GroupWorkItems(SubsystemRow r)
    {
        var all = DistinctWorkItems(r);
        var groups = new List<(string, IReadOnlyList<RegressionWorkItemRef>)>();

        void Add(string heading, Func<RegressionWorkItemRef, bool> match)
        {
            var items = all.Where(match).OrderBy(w => w.Id).ToList();
            if (items.Count > 0) groups.Add((heading, items));
        }

        Add("Bugs", w => w.Kind == RegressionWorkItemKind.Bug);
        Add("IMS", w => w.Kind == RegressionWorkItemKind.Ims);
        Add("User Stories", w => w.Kind == RegressionWorkItemKind.Story);
        Add("Other", w => w.Kind is RegressionWorkItemKind.Feature or RegressionWorkItemKind.Other);
        return groups;
    }

    private static string WorkItemLabel(RegressionWorkItemRef w) => w.Kind switch
    {
        RegressionWorkItemKind.Bug => $"Bug {w.Id}",
        RegressionWorkItemKind.Story => $"User Story {w.Id}",
        RegressionWorkItemKind.Feature => $"Feature {w.Id}",
        RegressionWorkItemKind.Ims => $"IMS {w.Id}",
        _ => $"Work Item {w.Id}",
    };

    // CSV: "Bugs: 123 (url) | IMS: 789 (url) | User Stories: 456 (url)".
    private static string WorkItemsText(SubsystemRow r)
    {
        var groups = GroupWorkItems(r);
        if (groups.Count == 0) return "";
        return string.Join(" | ", groups.Select(g =>
            $"{g.Heading}: " + string.Join("; ", g.Items.Select(w =>
                string.IsNullOrEmpty(w.Url) ? $"{w.Id}" : $"{w.Id} ({w.Url})"))));
    }

    // HTML: one labelled block per category, links stacked underneath.
    private static string WorkItemsHtml(SubsystemRow r)
    {
        var groups = GroupWorkItems(r);
        if (groups.Count == 0) return "<span style=\"color:#999;\">\u2014</span>";

        var sb = new StringBuilder();
        foreach (var (heading, items) in groups)
        {
            sb.Append("<div style=\"margin-bottom:8px;\">");
            sb.Append($"<div style=\"font-size:10px;font-weight:700;letter-spacing:.4px;text-transform:uppercase;color:#726d9b;border-bottom:1px solid #e2e2ea;padding-bottom:2px;margin-bottom:4px;\">{Enc(heading)}</div>");
            foreach (var w in items)
            {
                var label = Enc(WorkItemLabel(w));
                var line = string.IsNullOrEmpty(w.Url)
                    ? label
                    : $"<a href=\"{Enc(w.Url)}\" style=\"color:#3b5bdb;text-decoration:none;\">{label}</a>";
                sb.Append($"<div style=\"padding:1px 0;\">{line}</div>");
            }
            sb.Append("</div>");
        }
        return sb.ToString();
    }

    private static string RepoHtml(SubsystemRow r)
    {
        var name = Enc(r.Repository ?? "");
        return string.IsNullOrEmpty(r.RepositoryUrl)
            ? name
            : $"<a href=\"{Enc(r.RepositoryUrl)}\" style=\"color:#3b5bdb;text-decoration:none;\">{name}</a>";
    }

    // Requirement 1: the latest-OK build number and its result live in one trailing column.
    private static string BuildStatusText(SubsystemRow r)
    {
        var build = r.LatestSuccessfulBuild ?? "";
        var result = r.RiskTier ?? "";
        return string.IsNullOrEmpty(build) ? result : $"{build} - {result}";
    }

    private static string BuildStatusHtml(SubsystemRow r)
    {
        var build = r.LatestSuccessfulBuild;
        var result = r.RiskTier ?? "";
        var succeeded = result.Equals("succeeded", StringComparison.OrdinalIgnoreCase);
        var color = succeeded ? "#2e7d32" : "#c62828";
        var bg = succeeded ? "#e6f4ea" : "#fdecea";

        var sb = new StringBuilder();
        if (string.IsNullOrEmpty(build))
        {
            sb.Append("<div style=\"color:#999;\">\u2014</div>");
        }
        else if (string.IsNullOrEmpty(r.LatestSuccessfulBuildUrl))
        {
            sb.Append($"<div style=\"font-weight:600;\">{Enc(build)}</div>");
        }
        else
        {
            sb.Append($"<div><a href=\"{Enc(r.LatestSuccessfulBuildUrl)}\" style=\"color:#3b5bdb;text-decoration:none;font-weight:600;\">{Enc(build)}</a></div>");
        }

        if (!string.IsNullOrEmpty(result))
            sb.Append($"<div style=\"margin-top:4px;display:inline-block;padding:1px 7px;border-radius:9px;background:{bg};color:{color};font-size:11px;font-weight:600;\">{Enc(Capitalize(result))}</div>");

        return sb.ToString();
    }

    // CSV: "src/Engine.cpp (change-url); src/App.cs".
    private static string FilesText(SubsystemRow r)
    {
        var files = LinkedFiles(r);
        return files.Count > 0
            ? string.Join("; ", files.Select(f => string.IsNullOrEmpty(f.Url) ? f.Path : $"{f.Path} ({f.Url})"))
            : "";
    }

    /// <summary>
    /// Maps each source file to the ADO URL of the change that touched it, so the file list carries the change
    /// links directly instead of needing a separate column.
    /// </summary>
    internal static IReadOnlyList<(string Path, string? Url)> LinkedFiles(SubsystemRow r)
    {
        var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in r.Changes)
        {
            if (string.IsNullOrEmpty(c.Url)) continue;
            foreach (var f in c.FilePaths)
                if (!owner.ContainsKey(f)) owner[f] = c.Url;
        }

        return r.FilesModified
            .Where(FileNoiseFilter.IsSourceFile)
            .Select(f => (Path: f, Url: owner.TryGetValue(f, out var u) ? u : null))
            .ToList();
    }

    private static string FilesHtml(SubsystemRow r)
    {
        var files = LinkedFiles(r);
        if (files.Count == 0)
            return "<span style=\"color:#999;\">\u2014</span>";

        // Email clients choke on huge cells — show up to a handful of source files, one per line.
        const int max = 8;
        var sb = new StringBuilder();
        foreach (var (path, url) in files.Take(max))
        {
            var name = Enc(System.IO.Path.GetFileName(path));
            var dir = Enc(System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "");
            var label = string.IsNullOrEmpty(url)
                ? $"<span style=\"color:#333;\">{name}</span>"
                : $"<a href=\"{Enc(url)}\" style=\"color:#3b5bdb;text-decoration:none;\">{name}</a>";
            sb.Append($"<div style=\"padding:2px 0;white-space:nowrap;\">{label}</div>");
            if (!string.IsNullOrEmpty(dir))
                sb.Append($"<div style=\"color:#999;font-size:10px;margin:-1px 0 3px;\">{dir}</div>");
        }
        var extra = files.Count - max;
        if (extra > 0)
            sb.Append($"<div style=\"color:#999;\">\u2026 +{extra} more</div>");
        return sb.ToString();
    }

    private static string Capitalize(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static string Enc(string? value) => WebUtility.HtmlEncode(value ?? "");
}
