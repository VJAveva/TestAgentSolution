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
        sb.AppendLine("#,Component,Category,Subsystem,Repository,Branch,FilesModified,PullRequests,Commits,Automated,WorkItems,Risk,BuildNumber,LatestOkBuild,RepositoryUrl,Summary,ImpactedFunctionality,TestUseCases");

        var n = 1;
        foreach (var r in report.Rows)
        {
            var pr = r.Changes.Count(c => c.Kind == RegressionChangeKind.PullRequest);
            var commit = r.Changes.Count(c => c.Kind == RegressionChangeKind.Commit);
            var auto = r.Changes.Count(c => c.Kind == RegressionChangeKind.Automated);
            var wi = r.Changes.SelectMany(c => c.WorkItems).Select(w => w.Id).Distinct().Count();
            var summary = r.Changes.Count == 0 ? "" : r.Changes[0].Summary;

            var fields = new[]
            {
                n.ToString(CultureInfo.InvariantCulture),
                r.Component,
                r.Category.ToString(),
                r.Subsystem,
                r.Repository ?? "",
                r.DefaultBranch ?? "",
                r.TotalFilesModified.ToString(CultureInfo.InvariantCulture),
                pr.ToString(CultureInfo.InvariantCulture),
                commit.ToString(CultureInfo.InvariantCulture),
                auto.ToString(CultureInfo.InvariantCulture),
                wi.ToString(CultureInfo.InvariantCulture),
                r.RiskTier ?? "",
                r.BuildNumber ?? "",
                r.LatestSuccessfulBuild ?? "",
                r.RepositoryUrl ?? "",
                summary,
                Join(r.RegressionAreas),
                Join(r.UseCases),
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
        sb.Append("<div style=\"max-width:1120px;margin:0 auto;background:#ffffff;border:1px solid #e2e2ea;border-radius:8px;overflow:hidden;\">");

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
        foreach (var h in new[] { "#", "Component", "Category", "Repo", "Branch", "Files", "PR", "Commit", "Auto", "WI", "Risk", "Latest OK", "Summary", "Impacted Functionality", "Test Use Cases" })
            sb.Append($"<th style=\"padding:6px 8px;\">{Enc(h)}</th>");
        sb.Append("</tr></thead><tbody>");

        var n = 1;
        foreach (var r in report.Rows)
        {
            var pr = r.Changes.Count(c => c.Kind == RegressionChangeKind.PullRequest);
            var commit = r.Changes.Count(c => c.Kind == RegressionChangeKind.Commit);
            var auto = r.Changes.Count(c => c.Kind == RegressionChangeKind.Automated);
            var wi = r.Changes.SelectMany(c => c.WorkItems).Select(w => w.Id).Distinct().Count();
            var summary = r.Changes.Count == 0 ? "" : r.Changes[0].Summary;
            var risk = r.RiskTier ?? "";
            var riskColor = risk.Equals("succeeded", StringComparison.OrdinalIgnoreCase) ? "#2e7d32" : "#c62828";
            var catColor = r.Category switch
            {
                RegressionCategoryKind.Runtime => "#b26a00",
                RegressionCategoryKind.Config => "#00697a",
                RegressionCategoryKind.Both => "#6a1b9a",
                _ => "#555555",
            };
            var bg = n % 2 == 0 ? "#faf9ff" : "#ffffff";

            sb.Append($"<tr style=\"background:{bg};border-bottom:1px solid #eee;\">");
            sb.Append($"<td style=\"padding:6px 8px;color:#999;\">{n}</td>");
            sb.Append($"<td style=\"padding:6px 8px;font-weight:600;\">{Enc(r.Component)}</td>");
            sb.Append($"<td style=\"padding:6px 8px;color:{catColor};font-weight:600;\">{Enc(r.Category.ToString())}</td>");
            sb.Append($"<td style=\"padding:6px 8px;\">{Enc(r.Repository ?? "")}</td>");
            sb.Append($"<td style=\"padding:6px 8px;color:#666;\">{Enc(r.DefaultBranch ?? "")}</td>");
            sb.Append($"<td style=\"padding:6px 8px;text-align:right;\">{r.TotalFilesModified}</td>");
            sb.Append($"<td style=\"padding:6px 8px;text-align:right;\">{pr}</td>");
            sb.Append($"<td style=\"padding:6px 8px;text-align:right;\">{commit}</td>");
            sb.Append($"<td style=\"padding:6px 8px;text-align:right;color:#999;\">{auto}</td>");
            sb.Append($"<td style=\"padding:6px 8px;text-align:right;\">{wi}</td>");
            sb.Append($"<td style=\"padding:6px 8px;color:{riskColor};\">{Enc(risk)}</td>");
            sb.Append($"<td style=\"padding:6px 8px;color:#666;\">{Enc(r.LatestSuccessfulBuild ?? "\u2014")}</td>");
            sb.Append($"<td style=\"padding:6px 8px;color:#444;\">{Enc(Truncate(summary, 90))}</td>");
            sb.Append($"<td style=\"padding:6px 8px;color:#4b3f8f;min-width:150px;\">{Enc(JoinOrDash(r.RegressionAreas))}</td>");
            sb.Append($"<td style=\"padding:6px 8px;color:#00697a;min-width:150px;\">{Enc(JoinOrDash(r.UseCases))}</td>");
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

    private static string Enc(string? value) => WebUtility.HtmlEncode(value ?? "");

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "\u2026";
}
