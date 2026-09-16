using System.Globalization;
using System.Net;
using System.Text;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>Optional deep links rendered as action buttons. Any link left blank omits its button.</summary>
public sealed record ConsolidatedRunEmailLinks(
    string ControllerUrl = "", string ResultsShareUrl = "", string ReportCardUrl = "");

/// <summary>
/// Renders <see cref="ConsolidatedRunReport"/> as email-safe HTML: tables and inline styles only, because
/// Outlook's renderer drops stylesheets, flexbox and grid. Mirrors the conventions already used by
/// <see cref="BuildReportHtmlGenerator"/>. Every interpolated value is HTML-encoded.
/// </summary>
public static class ConsolidatedRunEmailBuilder
{
    private const string Ink = "#1c2430";
    private const string Muted = "#5a6472";
    private const string Faint = "#8a94a3";
    private const string Line = "#eef1f5";
    private const string Blue = "#1f6feb";
    private const string PassFg = "#1a9c62";
    private const string PassBg = "#e6f6ee";
    private const string FailFg = "#c0392b";
    private const string FailBg = "#fdecea";

    /// <summary>Subject line: verdict first so a failure is visible in a notification preview.</summary>
    public static string BuildSubject(ConsolidatedRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var build = string.IsNullOrWhiteSpace(report.BuildNumber) ? "" : $" \u00b7 {report.BuildNumber}";
        return $"[{report.Verdict}] {report.PipelineTag}{build} \u2014 {report.Headline}";
    }

    public static string BuildHtml(ConsolidatedRunReport report, ConsolidatedRunEmailLinks? links = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        links ??= new ConsolidatedRunEmailLinks();

        var sb = new StringBuilder(16_384);
        sb.Append("<html><body style=\"margin:0;padding:24px;background:#e9edf2;")
          .Append("font-family:'Segoe UI',Arial,sans-serif;\">")
          .Append("<table role=\"presentation\" width=\"760\" cellpadding=\"0\" cellspacing=\"0\" align=\"center\" ")
          .Append("style=\"background:#ffffff;border-radius:12px;overflow:hidden;border:1px solid #dfe4ea;\">");

        AppendHeader(sb, report);
        AppendVerdictStrip(sb, report);
        AppendConfirmation(sb, report);
        AppendAgentSummary(sb, report);
        AppendPerAgentDetail(sb, report);
        AppendButtons(sb, links);
        AppendFooter(sb, report);

        sb.Append("</table></body></html>");
        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, ConsolidatedRunReport r)
    {
        sb.Append("<tr><td style=\"background:").Append(Blue).Append(";padding:22px 28px;\">")
          .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr>")
          .Append("<td style=\"color:#fff;font-size:12px;letter-spacing:.06em;opacity:.85;\">")
          .Append("TESTCONTROLLER &middot; PIPELINE EXECUTION SUMMARY</td>")
          .Append("<td align=\"right\" style=\"color:#fff;font-size:12px;opacity:.85;\">")
          .Append(E(r.CompletedUtc.ToLocalTime().ToString("ddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)))
          .Append("</td></tr><tr><td colspan=\"2\" style=\"padding-top:8px;\">")
          .Append("<span style=\"color:#fff;font-size:21px;font-weight:700;\">").Append(E(r.PipelineTag))
          .Append("</span></td></tr><tr><td colspan=\"2\" style=\"padding-top:5px;\">")
          .Append("<span style=\"color:#d7e6ff;font-size:12.5px;\">");

        if (r.BuildNumber.Length > 0)
            sb.Append("Build <b style=\"color:#fff;font-family:Consolas,monospace;\">")
              .Append(E(r.BuildNumber)).Append("</b> &middot; ");

        sb.Append(E($"{r.AgentsTotal} agent(s)"));
        if (r.TriggeredBy.Length > 0)
            sb.Append(" &middot; triggered by ").Append(E(r.TriggeredBy));
        sb.Append("</span></td></tr></table></td></tr>");
    }

    private static void AppendVerdictStrip(StringBuilder sb, ConsolidatedRunReport r)
    {
        var fg = r.Passed ? PassFg : FailFg;
        var bg = r.Passed ? PassBg : FailBg;

        sb.Append("<tr><td><table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr>");
        Cell(sb, "33%", bg, $"<div style=\"font-size:24px;font-weight:800;color:{fg};\">{E(r.Verdict)}</div>"
            + $"<div style=\"font-size:11px;color:{Muted};\">{E(r.Headline)}</div>");
        Cell(sb, "33%", "#f4f7fb", $"<div style=\"font-size:24px;font-weight:800;color:{Ink};\">{r.AgentsPassed} / {r.AgentsTotal}</div>"
            + $"<div style=\"font-size:11px;color:{Muted};\">agents succeeded</div>");
        Cell(sb, "34%", "#f4f7fb", $"<div style=\"font-size:24px;font-weight:800;color:{Ink};\">{E(Hms(r.WallTime))}</div>"
            + $"<div style=\"font-size:11px;color:{Muted};\">total wall time</div>", last: true);
        sb.Append("</tr></table></td></tr>");

        static void Cell(StringBuilder sb, string width, string bg, string inner, bool last = false)
            => sb.Append("<td width=\"").Append(width).Append("\" align=\"center\" style=\"background:").Append(bg)
                 .Append(";padding:15px 8px;").Append(last ? "" : "border-right:1px solid #fff;").Append("\">")
                 .Append(inner).Append("</td>");
    }

    private static void AppendConfirmation(StringBuilder sb, ConsolidatedRunReport r)
    {
        var names = string.Join(", ", r.Agents.Select(a => a.AgentName));
        sb.Append("<tr><td style=\"padding:18px 28px 4px;\">")
          .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" ")
          .Append("style=\"background:#f6f8fb;border-radius:8px;\"><tr><td style=\"padding:13px 16px;font-size:13px;color:")
          .Append(Ink).Append(";line-height:1.6;\">")
          .Append(E($"{r.AgentsTotal} agent(s) ({names}) completed pipeline \u201c{r.PipelineTag}\u201d."));

        if (r.BuildNumber.Length > 0)
            sb.Append(" Build <b style=\"font-family:Consolas,monospace;\">").Append(E(r.BuildNumber)).Append("</b>.");

        if (r.ActionsSkipped > 0)
            sb.Append("<br><span style=\"color:").Append(Muted).Append(";\">")
              .Append(E($"{r.ActionsSkipped} action(s) were skipped and are counted as neither passed nor failed."))
              .Append("</span>");

        if (r.DropLocation.Length > 0)
            sb.Append("<br><br><span style=\"color:").Append(Muted).Append(";\">Drop Location = </span>")
              .Append("<span style=\"font-family:Consolas,monospace;font-size:12px;color:").Append(Blue).Append(";\">")
              .Append(E(r.DropLocation)).Append("</span>");

        sb.Append("</td></tr></table></td></tr>");
    }

    private static void AppendAgentSummary(StringBuilder sb, ConsolidatedRunReport r)
    {
        Section(sb, "Agent Summary", "One row per agent \u2014 the at-a-glance overview.");
        sb.Append("<tr><td style=\"padding:8px 28px 18px;\">")
          .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" ")
          .Append("style=\"border-collapse:collapse;\"><thead><tr style=\"background:#f0f3f7;\">");

        Th(sb, "AGENT", "left");
        Th(sb, "POOL", "left");
        foreach (var phase in r.Phases)
            Th(sb, phase.ToUpperInvariant(), "center");
        Th(sb, "STATUS", "center");
        Th(sb, "DURATION", "center");
        sb.Append("</tr></thead><tbody>");

        var i = 0;
        foreach (var agent in r.Agents)
        {
            var zebra = i++ % 2 == 1 ? "#fafbfc" : "#fff";
            sb.Append("<tr style=\"background:").Append(zebra).Append("\">")
              .Append(Td($"font-size:12.5px;color:{Ink};font-weight:600;", E(agent.AgentName)))
              .Append(Td($"font-size:11.5px;color:{Muted};", E(agent.Pool.Length > 0 ? agent.Pool : "\u2014")));

            foreach (var phase in r.Phases)
                sb.Append(TdCentre(PhaseGlyph(agent.PhaseResults.GetValueOrDefault(phase, PhaseOutcome.NotRun))));

            sb.Append(TdCentre(Pill(agent.Passed ? "PASSED" : "FAILED", agent.Passed)))
              .Append(TdCentre($"<span style=\"font-size:11.5px;color:{Muted};font-family:Consolas,monospace;\">{E(Hms(agent.WallTime))}</span>"))
              .Append("</tr>");
        }

        // Totals row. Phase columns roll up as passed/total so a partial phase cannot read as a clean pass.
        sb.Append("<tr style=\"background:#f0f3f7;\">")
          .Append(Td($"font-size:12.5px;color:{Ink};font-weight:700;", E($"TOTAL ({r.AgentsTotal})")))
          .Append(Td("", ""));
        foreach (var phase in r.Phases)
        {
            var passed = r.Agents.Count(a => a.PhaseResults.GetValueOrDefault(phase) == PhaseOutcome.Passed);
            var ran = r.Agents.Count(a => a.PhaseResults.GetValueOrDefault(phase) != PhaseOutcome.NotRun);
            var ok = ran > 0 && passed == ran;
            sb.Append(TdCentre($"<span style=\"font-size:12px;font-weight:700;color:{(ok ? PassFg : FailFg)};\">{passed}/{ran}</span>"));
        }
        sb.Append(TdCentre(Pill(r.Passed ? "ALL PASSED" : "ATTENTION", r.Passed)))
          .Append(TdCentre($"<span style=\"font-size:12px;color:{Ink};font-weight:700;font-family:Consolas,monospace;\">{E(Hms(r.WallTime))}</span>"))
          .Append("</tr></tbody></table></td></tr>");
    }

    private static void AppendPerAgentDetail(StringBuilder sb, ConsolidatedRunReport r)
    {
        sb.Append("<tr><td style=\"padding:0 28px;\"><div style=\"border-top:1px solid #e6eaf0;\"></div></td></tr>");
        Section(sb, "Per-Agent Detail",
            "Failing agents are expanded in full; passing agents are summarised on one line.");

        // Failures first and in full: email clients cannot collapse, so the ordering has to do that job.
        foreach (var agent in r.Agents.Where(a => !a.Passed))
            AppendAgentCard(sb, agent, expanded: true);

        foreach (var agent in r.Agents.Where(a => a.Passed))
            AppendAgentCard(sb, agent, expanded: false);
    }

    private static void AppendAgentCard(StringBuilder sb, ConsolidatedAgentRow agent, bool expanded)
    {
        var fg = agent.Passed ? PassFg : FailFg;
        var bg = agent.Passed ? "#f6faf7" : "#fdf7f6";
        var border = agent.Passed ? "#d7ebe0" : "#f0d4cf";
        var radius = expanded ? "8px 8px 0 0" : "8px";

        sb.Append("<tr><td style=\"padding:").Append(expanded ? "12px 28px 0" : "0 28px 6px").Append(";\">")
          .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:").Append(bg)
          .Append(";border:1px solid ").Append(border).Append(";border-radius:").Append(radius).Append(";\"><tr>")
          .Append("<td style=\"padding:10px 12px;\">")
          .Append("<span style=\"font-size:13px;font-weight:700;color:").Append(Ink).Append(";\">")
          .Append(E(agent.AgentName)).Append("</span> ")
          .Append(Pill($"{(agent.Passed ? "PASSED" : "FAILED")} \u00b7 {agent.Succeeded} of {agent.Total}", agent.Passed))
          .Append("</td><td align=\"right\" style=\"padding:10px 12px;font-size:11px;color:").Append(Faint).Append(";\">")
          .Append(E(agent.Pool.Length > 0 ? agent.Pool : "\u2014")).Append(" &middot; ").Append(E(Hms(agent.WallTime)));

        if (agent.Skipped > 0) sb.Append(" &middot; ").Append(E($"{agent.Skipped} skipped"));
        sb.Append("</td></tr></table></td></tr>");

        if (!expanded) return;

        sb.Append("<tr><td style=\"padding:0 28px 14px;\">")
          .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" ")
          .Append("style=\"border-collapse:collapse;border:1px solid ").Append(Line).Append(";border-top:none;\">")
          .Append("<thead><tr style=\"background:#fafbfc;\">");
        Th(sb, "#", "left", small: true);
        Th(sb, "ACTION", "left", small: true);
        Th(sb, "PHASE", "left", small: true);
        Th(sb, "RESULT", "center", small: true);
        Th(sb, "DURATION", "center", small: true);
        sb.Append("</tr></thead><tbody>");

        foreach (var a in agent.Actions)
        {
            sb.Append("<tr>")
              .Append(Td($"font-size:11.5px;color:{Faint};border-top:1px solid {Line};", a.Index.ToString("00", CultureInfo.InvariantCulture)))
              .Append(Td($"font-size:11.5px;color:{Ink};border-top:1px solid {Line};", E(a.Name)))
              .Append(Td($"font-size:11px;color:{Muted};border-top:1px solid {Line};", E(a.Phase)))
              .Append(TdCentre($"<span style=\"color:{OutcomeColour(a.Outcome)};font-weight:700;font-size:11.5px;\">{E(OutcomeLabel(a.Outcome))}</span>", $"border-top:1px solid {Line};"))
              .Append(TdCentre($"<span style=\"font-size:11px;color:{Muted};font-family:Consolas,monospace;\">{E(Ms(a.Duration))}</span>", $"border-top:1px solid {Line};"))
              .Append("</tr>");

            // The error is why the mail was opened at all, so it gets its own full-width row.
            if (a.IsFailure && !string.IsNullOrWhiteSpace(a.ErrorMessage))
                sb.Append("<tr><td colspan=\"5\" style=\"padding:4px 10px 8px 34px;font-size:11px;color:").Append(FailFg)
                  .Append(";font-family:Consolas,monospace;word-break:break-word;\">")
                  .Append(E(Truncate(a.ErrorMessage!, 500))).Append("</td></tr>");
        }

        sb.Append("</tbody></table></td></tr>");
    }

    private static void AppendButtons(StringBuilder sb, ConsolidatedRunEmailLinks links)
    {
        if (links.ControllerUrl.Length == 0 && links.ResultsShareUrl.Length == 0 && links.ReportCardUrl.Length == 0)
            return;

        sb.Append("<tr><td style=\"padding:12px 28px 20px;\">")
          .Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\"><tr>");
        Button(sb, links.ControllerUrl, "Open in TestController", primary: true);
        Button(sb, links.ResultsShareUrl, "Open results folder");
        Button(sb, links.ReportCardUrl, "View Report Card");
        sb.Append("</tr></table></td></tr>");

        static void Button(StringBuilder sb, string url, string label, bool primary = false)
        {
            if (url.Length == 0) return;
            sb.Append("<td style=\"padding-right:10px;\"><a href=\"").Append(E(url)).Append("\" style=\"")
              .Append(primary ? $"background:{Blue};color:#fff;" : $"background:#fff;color:{Muted};border:1px solid #d6dce4;")
              .Append("font-size:13px;font-weight:600;text-decoration:none;padding:10px 20px;border-radius:7px;display:inline-block;\">")
              .Append(E(label)).Append("</a></td>");
        }
    }

    private static void AppendFooter(StringBuilder sb, ConsolidatedRunReport r)
    {
        sb.Append("<tr><td style=\"background:#f0f3f7;padding:15px 28px;\">")
          .Append("<div style=\"font-size:11px;color:").Append(Faint).Append(";line-height:1.6;\">")
          .Append("Sent automatically by TestController when the pipeline completed.<br>")
          .Append(E($"Session {r.SessionId} \u00b7 event {r.EventType}"));
        if (r.Source.Length > 0) sb.Append(E($" \u00b7 via {r.Source}"));
        sb.Append("</div></td></tr>");
    }

    private static void Section(StringBuilder sb, string title, string subtitle)
        => sb.Append("<tr><td style=\"padding:20px 28px 6px;\">")
             .Append("<div style=\"font-size:15px;font-weight:700;color:").Append(Ink).Append(";\">").Append(E(title))
             .Append("</div><div style=\"font-size:12px;color:").Append(Faint).Append(";padding-top:2px;\">")
             .Append(E(subtitle)).Append("</div></td></tr>");

    private static void Th(StringBuilder sb, string text, string align, bool small = false)
        => sb.Append("<th align=\"").Append(align).Append("\" style=\"padding:").Append(small ? "7px 10px" : "8px 10px")
             .Append(";font-size:").Append(small ? "10px" : "10.5px").Append(";color:").Append(small ? Faint : Muted)
             .Append(small ? ";\">" : ";border-bottom:2px solid #dfe4ea;\">").Append(E(text)).Append("</th>");

    private static string Td(string style, string inner)
        => $"<td style=\"padding:8px 10px;{style}\">{inner}</td>";

    private static string TdCentre(string inner, string style = "")
        => $"<td align=\"center\" style=\"padding:8px 8px;{style}\">{inner}</td>";

    private static string Pill(string text, bool pass)
        => $"<span style=\"background:{(pass ? PassBg : FailBg)};color:{(pass ? PassFg : FailFg)};font-size:9.5px;"
           + $"font-weight:700;padding:3px 8px;border-radius:9px;\">{E(text)}</span>";

    private static string PhaseGlyph(PhaseOutcome outcome) => outcome switch
    {
        PhaseOutcome.Passed => $"<span style=\"color:{PassFg};font-weight:700;\">&#10003;</span>",
        PhaseOutcome.Failed => $"<span style=\"color:{FailFg};font-weight:700;\">&#10007;</span>",
        PhaseOutcome.Skipped => $"<span style=\"color:{Faint};font-weight:700;\">&#8709;</span>",
        _ => $"<span style=\"color:#d6dce4;\">&mdash;</span>",
    };

    private static string OutcomeLabel(ActionOutcome outcome) => outcome switch
    {
        ActionOutcome.Success => "PASS",
        ActionOutcome.Failed => "FAIL",
        ActionOutcome.Terminated => "CANCELLED",
        ActionOutcome.TimedOut => "TIMEOUT",
        ActionOutcome.Skipped => "SKIPPED",
        _ => "PENDING",
    };

    private static string OutcomeColour(ActionOutcome outcome) => outcome switch
    {
        ActionOutcome.Success => PassFg,
        ActionOutcome.Skipped => Faint,
        ActionOutcome.Unknown => Faint,
        _ => FailFg,
    };

    /// <summary>hh:mm:ss for pipeline/agent spans, which routinely exceed an hour.</summary>
    private static string Hms(TimeSpan t) =>
        ((int)t.TotalHours).ToString("00", CultureInfo.InvariantCulture) + t.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture);

    private static string Ms(TimeSpan t) =>
        t.TotalHours >= 1 ? Hms(t) : t.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "\u2026";

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
}
