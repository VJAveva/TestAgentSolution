using System.Net;
using System.Text;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Generates HTML reports for build results. Extracted from BuildResultsViewModel
/// to keep presentation logic separate from ViewModel orchestration.
/// </summary>
public class BuildReportHtmlGenerator
{
    private readonly BuildResultsConfig _config;
    private readonly FailurePatternAnalyzer? _patternAnalyzer;

    public BuildReportHtmlGenerator(BuildResultsConfig config, FailurePatternAnalyzer? patternAnalyzer = null)
    {
        _config = config;
        _patternAnalyzer = patternAnalyzer;
    }

    private double GoodThreshold => _config.GoodThreshold;
    private double WarningThreshold => _config.WarningThreshold;

    private string GetRateClass(double rate) =>
        rate > GoodThreshold ? "pass" : rate >= WarningThreshold ? "warn" : "fail";

    private static string Enc(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string TruncateError(string? msg)
    {
        if (msg is null) return "";
        if (msg.Length <= 200) return msg;
        // Cut at last space before limit to avoid splitting mid-word/label
        var cutPoint = msg.LastIndexOf(' ', 200);
        if (cutPoint < 100) cutPoint = 200;
        return msg[..cutPoint] + "…";
    }
    private string GetPatternLabel(string testName)
    {
        try { return _patternAnalyzer?.GetCompactLabel(testName) ?? "\u2014"; }
        catch { return "\u2014"; }
    }

    private static (string bg, string color) GetPatternBadgeColors(string label)
    {
        if (label.StartsWith("REGRESSION", StringComparison.Ordinal)) return ("#FFE5E5", "#C00000");
        if (label.StartsWith("CASCADING", StringComparison.Ordinal))  return ("#FFE5C0", "#9C5500");
        if (label.StartsWith("FLAKY", StringComparison.Ordinal))      return ("#FFF4D6", "#7A5C00");
        if (label.StartsWith("CHRONIC", StringComparison.Ordinal))    return ("#E5D6FF", "#5C00C0");
        if (label == "NEW FAILURE")                                   return ("#FFD6D6", "#990000");
        if (label == "RESOLVED")                                      return ("#D6F5D6", "#005C00");
        return ("#FFFFFF", "#64748b");
    }

    /// <summary>
    /// Maps internal pattern labels to the email classification scheme: Critical, New, Flaky.
    /// Returns (classification, bgColor) for inline pill rendering.
    /// </summary>
    private static (string classification, string bgColor) GetEmailClassification(string patternLabel)
    {
        if (patternLabel.StartsWith("REGRESSION", StringComparison.Ordinal) ||
            patternLabel.StartsWith("CHRONIC", StringComparison.Ordinal) ||
            patternLabel.StartsWith("CASCADING", StringComparison.Ordinal))
            return ("Critical", "#b91c1c");

        if (patternLabel == "NEW FAILURE")
            return ("New", "#b45309");

        if (patternLabel.StartsWith("FLAKY", StringComparison.Ordinal))
            return ("Flaky", "#6d28d9");

        // Default to New when history is unavailable
        return ("New", "#b45309");
    }
    private static string Timestamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    // ?? Shared CSS blocks ???????????????????????????????????????????

    private static string DarkThemeBase() => """
        body { font-family: 'Segoe UI', Arial, sans-serif; background: #0F1629; color: #E2E8F0; margin: 0; padding: 20px; }
        .card { background: #1A2238; border-radius: 8px; padding: 16px; margin: 8px 0; }
        table { width: 100%; border-collapse: collapse; margin: 8px 0; }
        th { background: #1E293B; color: #94A3B8; text-align: left; padding: 8px 12px; font-size: 11px; text-transform: uppercase; }
        td { padding: 8px 12px; border-bottom: 1px solid #1E293B; font-size: 13px; }
        tr:hover { background: #1E293B; }
        .fail { color: #EF4444; font-weight: 600; }
        .pass { color: #10B981; }
        .warn { color: #F59E0B; }
        svg text { font-family: 'Segoe UI', Arial, sans-serif; }
        """;

    private static string KpiCss() => """
        .kpi-row { display: flex; gap: 16px; margin: 16px 0; }
        .kpi-card { flex: 1; background: #1A2238; border-radius: 10px; padding: 20px 24px; text-align: center; border: 1px solid #1E293B; }
        .kpi-value { font-size: 32px; font-weight: 800; line-height: 1.1; }
        .kpi-label { font-size: 11px; color: #94A3B8; text-transform: uppercase; letter-spacing: 1px; margin-top: 6px; }
        .kpi-sub { font-size: 11px; color: #64748B; margin-top: 4px; }
        """;

    private static string StatsCss() => """
        .stats { display: flex; gap: 12px; margin: 12px 0; }
        .stat { background: #1A2238; border-radius: 8px; padding: 12px 20px; text-align: center; flex: 1; }
        .stat .num { font-size: 28px; font-weight: bold; }
        .stat .lbl { font-size: 11px; color: #94A3B8; margin-top: 4px; }
        """;

    private static string DetailTableCss() => """
        .detail-table { width: 100%; border-collapse: collapse; }
        .detail-table th { background: #0F1629; color: #94A3B8; text-align: left; padding: 10px 14px; font-size: 11px; text-transform: uppercase; letter-spacing: 0.5px; border-bottom: 2px solid #334155; }
        .detail-table td { padding: 10px 14px; font-size: 13px; border-bottom: 1px solid #1E293B; }
        .detail-table tr:hover { background: #263350 !important; }
        .row-even { background: #1A2238; }
        .row-odd { background: #151D30; }
        .build-short { cursor: default; border-bottom: 1px dotted #64748B; }
        .fail-link { cursor: pointer; text-decoration: none; display: inline-flex; align-items: center; gap: 4px; }
        .fail-link:hover { text-decoration: underline; }
        """;

    private static void AppendFooter(StringBuilder sb)
    {
        sb.AppendLine($"<p style='font-size:11px;color:#64748B;margin-top:20px'>Generated {Timestamp()} by TestController</p>");
        sb.AppendLine("</body></html>");
    }

    // ?? Stat KPI row helper ?????????????????????????????????????????

    private static void AppendStatCards(StringBuilder sb, int total, int passed, int failed, int timeout)
    {
        sb.AppendLine("<div class='stats'>");
        sb.AppendLine($"<div class='stat'><div class='num' style='color:#60A5FA'>{total}</div><div class='lbl'>Total</div></div>");
        sb.AppendLine($"<div class='stat'><div class='num' style='color:#10B981'>{passed}</div><div class='lbl'>Passed</div></div>");
        sb.AppendLine($"<div class='stat'><div class='num' style='color:#EF4444'>{failed}</div><div class='lbl'>Failed</div></div>");
        sb.AppendLine($"<div class='stat'><div class='num' style='color:#F59E0B'>{timeout}</div><div class='lbl'>Timeout</div></div>");
        sb.AppendLine("</div>");
    }

    // ?? Health color helper ?????????????????????????????????????????

    private static string HealthToColor(HealthStatus health) => health switch
    {
        HealthStatus.Good => "#10B981",
        HealthStatus.Warning => "#F59E0B",
        _ => "#EF4444",
    };

    private static string HealthToCssClass(HealthStatus health) => health switch
    {
        HealthStatus.Good => "good-bg",
        HealthStatus.Warning => "warn-bg",
        _ => "bad-bg",
    };

    // ???????????????????????????????????????????????????????????????
    // Test Detail HTML (single test result)
    // ???????????????????????????????????????????????????????????????

    public record TestDetailContext(
        string TestName, string UseCaseName, string BuildNumber,
        string Outcome, string Duration,
        string ErrorMessage, string StackTrace, string StdOut, string DebugTrace, string TrxFilePath,
        IReadOnlyList<StepInfo> ExecutionSteps);

    public record StepInfo(string StepName, string Outcome, string OutcomeIcon, string DurationText);

    public string GenerateTestDetailHtml(TestDetailContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'/>");
        sb.AppendLine($"<title>Test Result: {Enc(ctx.TestName)}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body{font-family:'Segoe UI',Arial;background:#0F1629;color:#E2E8F0;margin:0;padding:24px}");
        sb.AppendLine(".card{background:#1A2238;border-radius:8px;padding:16px;margin:12px 0}");
        sb.AppendLine(".badge{display:inline-block;padding:4px 12px;border-radius:4px;font-weight:bold;color:#fff}");
        sb.AppendLine("pre{background:#0F1629;border:1px solid #334155;border-radius:6px;padding:12px;overflow-x:auto;font-size:12px;color:#94A3B8;white-space:pre-wrap}");
        sb.AppendLine(".debug-trace-pre{font-size:14px;line-height:1.6;padding:16px}");
        sb.AppendLine(".debug-line{padding:2px 4px;border-radius:2px}");
        sb.AppendLine(".debug-line-fail{color:#FCA5A5;background:#450A0A;font-weight:bold}");
        sb.AppendLine(".debug-filter-bar{display:flex;gap:10px;align-items:center;margin-bottom:8px}");
        sb.AppendLine(".debug-filter-btn{background:#334155;color:#E2E8F0;border:1px solid #475569;border-radius:4px;padding:5px 14px;cursor:pointer;font-size:13px;font-family:inherit}");
        sb.AppendLine(".debug-filter-btn:hover{background:#475569}");
        sb.AppendLine(".debug-filter-btn.active{background:#7F1D1D;border-color:#EF4444;color:#FCA5A5}");
        sb.AppendLine(".debug-fail-count{color:#FCA5A5;font-size:12px}");
        sb.AppendLine(".pass{background:#10B981} .fail{background:#EF4444} .warn{background:#F59E0B}");
        sb.AppendLine("h1{color:#89B4FA;margin:0 0 4px} h3{color:#94A3B8;margin:16px 0 8px;font-size:13px}");
        sb.AppendLine("table{width:100%;border-collapse:collapse} td,th{padding:6px 10px;border-bottom:1px solid #1E293B;font-size:12px;text-align:left}");
        sb.AppendLine("th{color:#64748B;font-size:11px;text-transform:uppercase}");
        sb.AppendLine(".step-pass{color:#10B981} .step-fail{color:#EF4444}");
        sb.AppendLine("</style></head><body>");

        var outcomeClass = ctx.Outcome == "Failed" ? "fail" : ctx.Outcome == "Passed" ? "pass" : "warn";
        sb.AppendLine($"<h1>{Enc(ctx.TestName)}</h1>");
        sb.AppendLine($"<span class='badge {outcomeClass}'>{Enc(ctx.Outcome)}</span>");
        sb.AppendLine($"<span style='color:#64748B;margin-left:12px'>{Enc(ctx.UseCaseName)} &middot; {Enc(ctx.BuildNumber)} &middot; {ctx.Duration}</span>");

        if (ctx.ExecutionSteps.Count > 0)
        {
            sb.AppendLine("<div class='card'><h3>EXECUTION STEPS</h3>");
            sb.AppendLine("<table><tr><th></th><th>Step</th><th>Duration</th></tr>");
            foreach (var step in ctx.ExecutionSteps)
            {
                var cls = step.Outcome == "Passed" ? "step-pass" : "step-fail";
                sb.AppendLine($"<tr><td class='{cls}'>{Enc(step.OutcomeIcon)}</td><td>{Enc(step.StepName)}</td><td>{step.DurationText}</td></tr>");
            }
            sb.AppendLine("</table></div>");
        }

        AppendOptionalSection(sb, "ERROR MESSAGE", ctx.ErrorMessage, "(no error message)", "color:#EF4444");
        AppendOptionalSection(sb, "STACK TRACE", ctx.StackTrace, "(no stack trace)", null);
        AppendDebugTraceSection(sb, ctx.DebugTrace);
        AppendOptionalSection(sb, "STDOUT", ctx.StdOut, "(no stdout captured)", null);

        if (!string.IsNullOrEmpty(ctx.TrxFilePath))
            sb.AppendLine($"<div class='card'><h3>TRX FILE</h3><pre>{Enc(ctx.TrxFilePath)}</pre></div>");

        AppendFooter(sb);
        return sb.ToString();
    }

    private static readonly string[] FailureKeywords = ["fail", "error", "exception", "assert", "timeout", "abort"];

    private static void AppendDebugTraceSection(StringBuilder sb, string debugTrace)
    {
        if (string.IsNullOrWhiteSpace(debugTrace) || debugTrace == "(no debug trace)") return;

        var lines = debugTrace.Split('\n');
        int failCount = 0;
        var lineMarkup = new StringBuilder();
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var lower = line.ToLowerInvariant();
            bool isFail = Array.Exists(FailureKeywords, kw => lower.Contains(kw));
            if (isFail) failCount++;
            var cls = isFail ? "debug-line debug-line-fail" : "debug-line";
            var dataAttr = isFail ? " data-fail='1'" : "";
            lineMarkup.AppendLine($"<span class='{cls}'{dataAttr}>{Enc(line)}</span>");
        }

        sb.AppendLine("<div class='card'>");
        sb.AppendLine("<div class='debug-filter-bar'>");
        sb.AppendLine("<h3 style='margin:0'>DEBUG TRACE</h3>");
        if (failCount > 0)
        {
            sb.AppendLine($"<button class='debug-filter-btn' id='btnFilterFailed' onclick='toggleFailFilter()'>Show Failed Only</button>");
            sb.AppendLine($"<span class='debug-fail-count'>{failCount} failed line{(failCount == 1 ? "" : "s")}</span>");
            sb.AppendLine("<button class='debug-filter-btn' id='btnPrevFail' onclick='navigateFail(-1)'>&#9650; Prev</button>");
            sb.AppendLine("<button class='debug-filter-btn' id='btnNextFail' onclick='navigateFail(1)'>&#9660; Next</button>");
        }
        sb.AppendLine("</div>");
        sb.AppendLine($"<pre class='debug-trace-pre' id='debugTracePre'>{lineMarkup}</pre></div>");

        if (failCount > 0)
        {
            sb.AppendLine("<script>");
            sb.AppendLine(@"var failFilterActive=false, failIdx=-1;
function toggleFailFilter(){
  failFilterActive=!failFilterActive;
  var btn=document.getElementById('btnFilterFailed');
  var pre=document.getElementById('debugTracePre');
  var spans=pre.querySelectorAll('span.debug-line');
  btn.textContent=failFilterActive?'Show All':'Show Failed Only';
  if(failFilterActive){btn.classList.add('active')}else{btn.classList.remove('active')}
  for(var i=0;i<spans.length;i++){
    spans[i].style.display=failFilterActive&&!spans[i].dataset.fail?'none':'';
  }
  failIdx=-1;
}
function navigateFail(dir){
  var pre=document.getElementById('debugTracePre');
  var fails=pre.querySelectorAll('span[data-fail]');
  if(!fails.length)return;
  fails.forEach(function(f){f.style.outline='';});
  failIdx+=dir;
  if(failIdx<0)failIdx=fails.length-1;
  if(failIdx>=fails.length)failIdx=0;
  fails[failIdx].scrollIntoView({behavior:'smooth',block:'center'});
  fails[failIdx].style.outline='2px solid #EF4444';
}
");
            sb.AppendLine("</script>");
        }
    }

    private static void AppendOptionalSection(StringBuilder sb, string title, string content, string placeholder, string? style)
    {
        if (string.IsNullOrWhiteSpace(content) || content == placeholder) return;
        var styleAttr = style != null ? $" style='{style}'" : "";
        sb.AppendLine($"<div class='card'><h3>{title}</h3>");
        sb.AppendLine($"<pre{styleAttr}>{Enc(content)}</pre></div>");
    }

    // ???????????????????????????????????????????????????????????????
    // Single Build HTML Report
    // ???????????????????????????????????????????????????????????????

    public string GenerateSingleBuildHtml(BuildNode node)
    {
        var healthBg = HealthToColor(node.Health);

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'/>");
        sb.AppendLine("<style>");
        sb.AppendLine(DarkThemeBase());
        sb.AppendLine(StatsCss());
        sb.AppendLine(KpiCss());
        sb.AppendLine(DetailTableCss());
        sb.AppendLine(".health-bar { border-radius: 6px; height: 36px; display: flex; align-items: center; padding: 0 16px; font-weight: bold; font-size: 16px; color: #fff; }");
        sb.AppendLine(".badge-crit { display:inline-block; background:#7F1D1D; color:#FCA5A5; font-size:10px; font-weight:bold; padding:2px 8px; border-radius:10px; margin-left:6px; }");
        sb.AppendLine("</style></head><body>");

        sb.AppendLine($"<h2>Build Results: {node.BuildNumber}</h2>");
        sb.AppendLine($"<div class='health-bar' style='background:{healthBg}'>{node.PassRate:F1}% � {node.Health}</div>");

        AppendStatCards(sb, node.TotalTests, node.PassedTests, node.FailedTests, node.TimeoutTests);

        // Use Case Breakdown
        sb.AppendLine("<div class='card'><h3>Use Case Breakdown</h3>");
        sb.AppendLine("<table><tr><th>Use Case</th><th>Duration</th><th>Total</th><th>Passed</th><th>Failed</th><th>Timeout</th><th>Pass Rate</th><th>Failed Tests</th></tr>");
        foreach (var uc in node.UseCases)
        {
            var rateClass = GetRateClass(uc.PassRate);
            var failedNames = string.Join(", ", uc.FailedTests.Select(t => t.TestName));
            sb.AppendLine($"<tr><td>{uc.UseCaseName}</td><td>{uc.Duration:hh\\:mm\\:ss}</td><td>{uc.Total}</td><td class='pass'>{uc.Passed}</td><td class='{(uc.Failed > 0 ? "fail" : "")}'>{uc.Failed}</td><td>{uc.Timeout}</td><td class='{rateClass}'>{uc.PassRate:F1}%</td><td class='fail' style='font-size:11px'>{failedNames}</td></tr>");
        }
        sb.AppendLine("</table></div>");

        // Failed Tests Detail
        if (node.AllFailedTests.Count > 0)
        {
            sb.AppendLine("<div class='card'><h3>Failed Tests Detail</h3>");
            sb.AppendLine("<table><tr><th>Test Name</th><th>TRX File</th><th>Error Message</th></tr>");
            foreach (var t in node.AllFailedTests)
            {
                sb.AppendLine($"<tr><td class='fail'>{t.TestName}</td><td>{t.TrxFileName}</td><td style='font-size:11px'>{Enc(TruncateError(t.ErrorMessage))}</td></tr>");
            }
            sb.AppendLine("</table></div>");
        }

        AppendFooter(sb);
        return sb.ToString();
    }

    // ???????????????????????????????????????????????????????????????
    // Multi-Build Summary HTML Report
    // ???????????????????????????????????????????????????????????????

    public string GenerateMultiBuildHtml(IReadOnlyList<BuildNode> builds, int statTotal, int statPassed, int statFailed, int statTimeout)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'/>");
        sb.AppendLine("<style>");
        sb.AppendLine(DarkThemeBase());
        sb.AppendLine(StatsCss());
        sb.AppendLine(KpiCss());
        sb.AppendLine(DetailTableCss());
        sb.AppendLine("h2 { color: #60A5FA; } h3 { color: #94A3B8; margin: 0 0 8px; font-size: 14px; }");
        sb.AppendLine(".good-bg { background: #10B981; } .warn-bg { background: #F59E0B; } .bad-bg { background: #EF4444; }");
        sb.AppendLine(".health-pill { display: inline-block; padding: 2px 10px; border-radius: 10px; color: #fff; font-size: 11px; font-weight: bold; }");
        sb.AppendLine("</style></head><body>");

        sb.AppendLine($"<h2>All Builds Summary ({builds.Count} builds)</h2>");
        AppendStatCards(sb, statTotal, statPassed, statFailed, statTimeout);

        sb.AppendLine("<div class='card'><h3>Build Breakdown</h3>");
        sb.AppendLine("<table><tr><th>Build</th><th>Total</th><th>Passed</th><th>Failed</th><th>Pass Rate</th><th>Health</th></tr>");
        foreach (var b in builds)
        {
            var rateClass = GetRateClass(b.PassRate);
            var healthBg = HealthToCssClass(b.Health);
            sb.AppendLine($"<tr><td><strong>{Enc(b.BuildNumber)}</strong></td>" +
                           $"<td>{b.TotalTests}</td>" +
                           $"<td class='pass'>{b.PassedTests}</td>" +
                           $"<td class='{(b.FailedTests > 0 ? "fail" : "")}'>{b.FailedTests}</td>" +
                           $"<td class='{rateClass}'>{b.PassRate:F1}%</td>" +
                           $"<td><span class='health-pill {healthBg}'>{b.Health}</span></td></tr>");
        }
        sb.AppendLine("</table></div>");

        AppendFooter(sb);
        return sb.ToString();
    }

    // ???????????????????????????????????????????????????????????????
    // Trend Report HTML
    // ???????????????????????????????????????????????????????????????

    public string GenerateTrendHtml(TrendReport trend)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'/>");
        sb.AppendLine($"<title>Trend Report � {trend.Builds.Count} Builds</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(DarkThemeBase());
        sb.AppendLine(KpiCss());
        sb.AppendLine("h2 { color: #89B4FA; margin-bottom: 4px; }");
        sb.AppendLine("h3 { color: #94A3B8; margin: 24px 0 8px; font-size: 14px; text-transform: uppercase; letter-spacing: 1px; }");
        sb.AppendLine(".sub { color: #64748B; font-size: 12px; margin-bottom: 16px; }");
        sb.AppendLine(".good-bg { background: #10B981; } .warn-bg { background: #F59E0B; } .bad-bg { background: #EF4444; }");
        sb.AppendLine(".health-pill { display: inline-block; padding: 2px 10px; border-radius: 10px; color: #fff; font-size: 11px; font-weight: bold; }");
        sb.AppendLine(".chart-container { background: #1A2238; border-radius: 8px; padding: 20px; margin: 12px 0; }");
        sb.AppendLine("</style></head><body>");

        sb.AppendLine("<h2>Trend Report</h2>");
        sb.AppendLine($"<p class='sub'>{trend.Builds.Count} build(s) analyzed &middot; Generated {Timestamp()}</p>");

        if (trend.Builds.Count == 0)
        {
            sb.AppendLine("<div class='card'><p>No build data available for trend analysis.</p></div>");
            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        var totalFailed = trend.Builds.Sum(b => b.FailedTests);
        var avgPassRate = trend.Builds.Average(b => b.PassRate);
        var bestBuild = trend.Builds.MaxBy(b => b.PassRate);
        var worstBuild = trend.Builds.MinBy(b => b.PassRate);

        // KPIs
        sb.AppendLine("<div class='kpi-row'>");
        sb.AppendLine($"<div class='kpi-card'><div class='kpi-value' style='color:#60A5FA'>{trend.Builds.Count}</div><div class='kpi-label'>Builds</div></div>");
        sb.AppendLine($"<div class='kpi-card'><div class='kpi-value' style='color:#E2E8F0'>{trend.Builds.Sum(b => b.TotalTests):N0}</div><div class='kpi-label'>Total Tests</div></div>");
        sb.AppendLine($"<div class='kpi-card'><div class='kpi-value' style='color:#10B981'>{avgPassRate:F1}%</div><div class='kpi-label'>Avg Pass Rate</div></div>");
        sb.AppendLine($"<div class='kpi-card'><div class='kpi-value' style='color:#EF4444'>{totalFailed:N0}</div><div class='kpi-label'>Total Failures</div></div>");
        sb.AppendLine("</div>");

        // SVG Chart
        if (trend.Builds.Count >= 2)
            AppendPassRateChart(sb, trend.Builds);

        // Per-Build Table
        sb.AppendLine("<div class='card'><h3 style='margin-top:0'>Build Breakdown</h3>");
        sb.AppendLine("<table><tr><th>Build</th><th>Date</th><th>Total</th><th>Passed</th><th>Failed</th><th>Timeout</th><th>Pass Rate</th><th>Health</th></tr>");
        foreach (var b in trend.Builds)
        {
            var rateClass = GetRateClass(b.PassRate);
            var healthBg = HealthToCssClass(b.Health);
            sb.AppendLine($"<tr><td><strong>{Enc(b.BuildNumber)}</strong></td>" +
                           $"<td style='color:#64748B'>{b.Date:yyyy-MM-dd HH:mm}</td>" +
                           $"<td>{b.TotalTests}</td>" +
                           $"<td class='pass'>{b.PassedTests}</td>" +
                           $"<td class='{(b.FailedTests > 0 ? "fail" : "")}'>{b.FailedTests}</td>" +
                           $"<td>{b.TimeoutTests}</td>" +
                           $"<td class='{rateClass}'>{b.PassRate:F1}%</td>" +
                           $"<td><span class='health-pill {healthBg}'>{b.Health}</span></td></tr>");
        }
        sb.AppendLine("</table></div>");

        // Best / Worst
        if (bestBuild is not null && worstBuild is not null)
        {
            sb.AppendLine("<div class='kpi-row'>");
            sb.AppendLine($"<div class='kpi-card'><div class='kpi-value' style='color:#10B981'>{bestBuild.PassRate:F1}%</div><div class='kpi-label'>Best Build</div><div class='kpi-sub'>{Enc(bestBuild.BuildNumber)} ({bestBuild.Date:yyyy-MM-dd})</div></div>");
            sb.AppendLine($"<div class='kpi-card'><div class='kpi-value' style='color:#EF4444'>{worstBuild.PassRate:F1}%</div><div class='kpi-label'>Worst Build</div><div class='kpi-sub'>{Enc(worstBuild.BuildNumber)} ({worstBuild.Date:yyyy-MM-dd})</div></div>");
            sb.AppendLine("</div>");
        }

        AppendPeriodTable(sb, "Weekly Summary", "Week", trend.WeeklySummaries);
        AppendPeriodTable(sb, "Monthly Summary", "Month", trend.MonthlySummaries);

        sb.AppendLine($"<p style='font-size:11px;color:#64748B;margin-top:24px'>Generated {Timestamp()} by TestController</p>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private void AppendPassRateChart(StringBuilder sb, List<BuildTrendEntry> builds)
    {
        const int chartW = 800, chartH = 200, padL = 50, padR = 20, padT = 20, padB = 30;
        var plotW = chartW - padL - padR;
        var plotH = chartH - padT - padB;
        var count = builds.Count;

        sb.AppendLine("<div class='chart-container'>");
        sb.AppendLine("<h3 style='margin-top:0'>Pass Rate Over Time</h3>");
        sb.AppendLine($"<svg width='{chartW}' height='{chartH}' viewBox='0 0 {chartW} {chartH}'>");

        // Y-axis gridlines
        for (int pct = 0; pct <= 100; pct += 25)
        {
            var y = padT + plotH - (plotH * pct / 100.0);
            sb.AppendLine($"<line x1='{padL}' y1='{y:F1}' x2='{padL + plotW}' y2='{y:F1}' stroke='#334155' stroke-width='1' />");
            sb.AppendLine($"<text x='{padL - 6}' y='{y + 4:F1}' fill='#64748B' font-size='10' text-anchor='end'>{pct}%</text>");
        }

        // Threshold lines
        var goodY = padT + plotH - (plotH * GoodThreshold / 100.0);
        var warnY = padT + plotH - (plotH * WarningThreshold / 100.0);
        sb.AppendLine($"<line x1='{padL}' y1='{goodY:F1}' x2='{padL + plotW}' y2='{goodY:F1}' stroke='#10B981' stroke-width='1' stroke-dasharray='6,4' opacity='0.5' />");
        sb.AppendLine($"<line x1='{padL}' y1='{warnY:F1}' x2='{padL + plotW}' y2='{warnY:F1}' stroke='#F59E0B' stroke-width='1' stroke-dasharray='6,4' opacity='0.5' />");

        // Polyline
        var points = new List<string>();
        for (int i = 0; i < count; i++)
        {
            var (x, y) = PlotPoint(builds[i].PassRate, i, count, padL, padT, plotW, plotH);
            points.Add($"{x:F1},{y:F1}");
        }
        sb.AppendLine($"<polyline points='{string.Join(" ", points)}' fill='none' stroke='#89B4FA' stroke-width='2.5' stroke-linejoin='round' />");

        // Dots & X-axis labels
        for (int i = 0; i < count; i++)
        {
            var b = builds[i];
            var (x, y) = PlotPoint(b.PassRate, i, count, padL, padT, plotW, plotH);
            var dotColor = b.PassRate > GoodThreshold ? "#10B981" : b.PassRate >= WarningThreshold ? "#F59E0B" : "#EF4444";
            sb.AppendLine($"<circle cx='{x:F1}' cy='{y:F1}' r='4' fill='{dotColor}' />");

            if (count <= 15 || i % Math.Max(1, count / 10) == 0 || i == count - 1)
            {
                var label = b.BuildNumber.Length > 12 ? b.BuildNumber[..12] : b.BuildNumber;
                sb.AppendLine($"<text x='{x:F1}' y='{chartH - 4}' fill='#64748B' font-size='9' text-anchor='middle'>{Enc(label)}</text>");
            }
        }

        sb.AppendLine("</svg></div>");
    }

    private static (double x, double y) PlotPoint(double passRate, int index, int count, int padL, int padT, int plotW, int plotH)
    {
        var x = padL + (count == 1 ? plotW / 2.0 : (double)index / (count - 1) * plotW);
        var y = padT + plotH - (plotH * Math.Clamp(passRate, 0, 100) / 100.0);
        return (x, y);
    }

    private void AppendPeriodTable(StringBuilder sb, string title, string periodLabel, List<PeriodSummary> summaries)
    {
        if (summaries.Count == 0) return;

        sb.AppendLine($"<div class='card'><h3 style='margin-top:0'>{title}</h3>");
        sb.AppendLine($"<table><tr><th>{periodLabel}</th><th>Builds</th><th>Total Tests</th><th>Passed</th><th>Failed</th><th>Avg Pass Rate</th></tr>");
        foreach (var s in summaries)
        {
            var rateClass = GetRateClass(s.AvgPassRate);
            sb.AppendLine($"<tr><td>{s.Period}</td><td>{s.BuildCount}</td><td>{s.TotalTests}</td>" +
                           $"<td class='pass'>{s.TotalPassed}</td><td class='{(s.TotalFailed > 0 ? "fail" : "")}'>{s.TotalFailed}</td>" +
                           $"<td class='{rateClass}'>{s.AvgPassRate:F1}%</td></tr>");
        }
        sb.AppendLine("</table></div>");
    }

    // ???????????????????????????????????????????????????????????????
    // Alert Email HTML
    // ???????????????????????????????????????????????????????????????

    public string GenerateAlertEmailHtml(IReadOnlyList<ConsecutiveFailureAlert> alerts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'/>");
        sb.AppendLine("<style>");
        sb.AppendLine("body { font-family: 'Segoe UI', Arial, sans-serif; background: #fff; color: #1a1a2e; padding: 20px; }");
        sb.AppendLine("h2 { color: #EF4444; }");
        sb.AppendLine("table { width: 100%; border-collapse: collapse; margin: 12px 0; }");
        sb.AppendLine("th { background: #f1f5f9; padding: 8px 12px; font-size: 11px; text-transform: uppercase; text-align: left; }");
        sb.AppendLine("td { padding: 8px 12px; border-bottom: 1px solid #e2e8f0; font-size: 13px; }");
        sb.AppendLine(".critical { color: #dc2626; font-weight: bold; }");
        sb.AppendLine(".high { color: #ea580c; font-weight: bold; }");
        sb.AppendLine(".medium { color: #d97706; font-weight: bold; }");
        sb.AppendLine("</style></head><body>");
        sb.AppendLine($"<h2>\u26A0 Priority Investigation Required</h2>");
        sb.AppendLine($"<p>{alerts.Count} test(s) are failing across consecutive builds and require investigation.</p>");
        sb.AppendLine("<table><tr><th>Priority</th><th>Test Name</th><th>Use Case</th><th>Consecutive Fails</th><th>Failed In Builds</th><th>Last Error</th></tr>");
        foreach (var a in alerts)
        {
            var cls = a.Priority.ToLowerInvariant();
            var builds = string.Join(", ", a.FailedInBuilds);
            var err = Enc(TruncateError(a.LastError));
            sb.AppendLine($"<tr><td class='{cls}'>{a.Priority}</td><td>{a.TestName}</td><td>{a.UseCaseName}</td><td>{a.ConsecutiveFailCount}</td><td style='font-size:11px'>{builds}</td><td style='font-size:11px'>{err}</td></tr>");
        }
        sb.AppendLine("</table>");
        sb.AppendLine($"<p style='font-size:11px;color:#94a3b8'>Generated {Timestamp()} by TestController</p>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    // ???????????????????????????????????????????????????????????????
    // Email Report HTML (Outlook-compatible, table-based layout)
    // ???????????????????????????????????????????????????????????????

    /// <summary>
    /// Generates an Outlook-compatible HTML email report for a build's results.
    /// Redesigned: 600px container, emerald theme, compact metrics strip,
    /// Agent column, progress bars, failure classification pills (Critical/New/Flaky).
    /// </summary>
    public string GenerateEmailHtml(BuildNode build, string product, string controllerNode,
        string buildPath, string reportLink = "", string component = "", string testType = "")
    {
        var sb = new StringBuilder();
        var passRate = build.PassRate;
        var isPassing = build.FailedTests == 0;
        var statusText = isPassing ? "PASSED" : "FAILED";
        var statusColor = isPassing ? "#15803d" : "#b91c1c";
        var passRateColor = isPassing ? "#15803d" : "#b91c1c";

        // Derive component/testType defaults
        if (string.IsNullOrWhiteSpace(component)) component = "TestAgent CI";
        if (string.IsNullOrWhiteSpace(testType)) testType = "Functional Test";
        var title = $"{Enc(product)} \u2014 {Enc(testType)}";
        var runDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm");

        var allUseCases = build.UseCases
            .OrderByDescending(u => u.Failed)
            .ThenByDescending(u => u.Total)
            .ThenBy(u => u.UseCaseName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Preheader text (hidden)
        var preheader = $"{Enc(product)} \u2014 {Enc(testType)} \u00B7 {passRate:F0}% pass \u00B7 {build.FailedTests} failure{(build.FailedTests == 1 ? "" : "s")} \u00B7 Build {Enc(build.BuildNumber)}";

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\" xmlns:v=\"urn:schemas-microsoft-com:vml\" xmlns:o=\"urn:schemas-microsoft-com:office:office\">");
        sb.AppendLine("<head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta name=\"x-apple-disable-message-reformatting\">");
        sb.AppendLine($"<title>{Enc(component)} CI Results</title>");
        sb.AppendLine("<!--[if mso]><style>table, td, div, h1, h2, p, span { font-family:'Segoe UI',Calibri,Arial,sans-serif !important; }</style><![endif]-->");
        sb.AppendLine("</head>");
        sb.AppendLine("<body style=\"margin:0;padding:0;background-color:#eef0f3;font-family:'Segoe UI',Calibri,Arial,sans-serif;\">");

        // Hidden preheader
        sb.AppendLine($"<div style=\"display:none;max-height:0;overflow:hidden;mso-hide:all;\">{preheader}</div>");

        // Outer wrapper
        sb.AppendLine("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"background-color:#eef0f3;\"><tr><td align=\"center\" style=\"padding:20px 12px;\">");

        // MSO width fix
        sb.AppendLine("<!--[if mso]><table role=\"presentation\" width=\"600\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\"><tr><td><![endif]-->");

        // Main 600px container
        sb.AppendLine("<table role=\"presentation\" width=\"600\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"width:600px;max-width:600px;background-color:#ffffff;border:1px solid #d8dce1;\">");

        // === HEADER (deep emerald band) ===
        sb.AppendLine("<tr><td bgcolor=\"#0F5132\" style=\"background-color:#0F5132;padding:22px 28px;border-bottom:3px solid #3FB950;\">");
        sb.AppendLine("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\">");
        // Eyebrow + Status pill
        sb.AppendLine("<tr>");
        sb.AppendLine($"<td style=\"font-size:12px;font-weight:700;letter-spacing:1.5px;color:#86EFAC;text-transform:uppercase;\">{Enc(component)}</td>");
        sb.AppendLine($"<td align=\"right\"><span style=\"background-color:{statusColor};color:#ffffff;font-size:12px;font-weight:700;padding:3px 12px;letter-spacing:0.5px;\">{statusText}</span></td>");
        sb.AppendLine("</tr>");
        // Title
        sb.AppendLine($"<tr><td colspan=\"2\" style=\"padding-top:10px;font-size:21px;font-weight:700;color:#ffffff;line-height:1.25;\">{title}</td></tr>");
        // Meta line
        sb.AppendLine("<tr><td colspan=\"2\" style=\"padding-top:8px;font-size:12px;color:#b7e4c7;line-height:1.5;\">");
        sb.AppendLine($"Build <strong style=\"color:#ffffff;\">{Enc(build.BuildNumber)}</strong>");
        sb.AppendLine($"&nbsp;&middot;&nbsp; Controller <strong style=\"color:#ffffff;\">{Enc(controllerNode)}</strong>");
        sb.AppendLine($"&nbsp;&middot;&nbsp; {runDate}");
        sb.AppendLine("</td></tr>");
        sb.AppendLine("</table></td></tr>");

        // === COMPACT METRICS ROW ===
        sb.AppendLine("<tr><td style=\"padding:18px 28px 4px 28px;\">");
        sb.AppendLine("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" bgcolor=\"#f8f9fb\" style=\"background-color:#f8f9fb;border:1px solid #e5e7eb;\">");
        sb.AppendLine("<tr>");
        // Pass Rate
        sb.AppendLine($"<td align=\"center\" style=\"padding:14px 4px;border-right:1px solid #e5e7eb;\"><div style=\"font-size:22px;font-weight:700;color:{passRateColor};line-height:1;\">{passRate:F0}%</div><div style=\"font-size:10px;font-weight:600;color:#6b7280;text-transform:uppercase;letter-spacing:0.5px;padding-top:5px;\">Pass Rate</div></td>");
        // Total
        sb.AppendLine($"<td align=\"center\" style=\"padding:14px 4px;border-right:1px solid #e5e7eb;\"><div style=\"font-size:22px;font-weight:700;color:#1f2937;line-height:1;\">{build.TotalTests}</div><div style=\"font-size:10px;font-weight:600;color:#6b7280;text-transform:uppercase;letter-spacing:0.5px;padding-top:5px;\">Total</div></td>");
        // Passed
        sb.AppendLine($"<td align=\"center\" style=\"padding:14px 4px;border-right:1px solid #e5e7eb;\"><div style=\"font-size:22px;font-weight:700;color:#15803d;line-height:1;\">{build.PassedTests}</div><div style=\"font-size:10px;font-weight:600;color:#6b7280;text-transform:uppercase;letter-spacing:0.5px;padding-top:5px;\">Passed</div></td>");
        // Failed
        sb.AppendLine($"<td align=\"center\" style=\"padding:14px 4px;border-right:1px solid #e5e7eb;\"><div style=\"font-size:22px;font-weight:700;color:#b91c1c;line-height:1;\">{build.FailedTests}</div><div style=\"font-size:10px;font-weight:600;color:#6b7280;text-transform:uppercase;letter-spacing:0.5px;padding-top:5px;\">Failed</div></td>");
        // Duration
        sb.AppendLine($"<td align=\"center\" style=\"padding:14px 4px;\"><div style=\"font-size:16px;font-weight:700;color:#1f2937;line-height:1;padding-top:4px;\">{build.TotalDuration:hh\\:mm\\:ss}</div><div style=\"font-size:10px;font-weight:600;color:#6b7280;text-transform:uppercase;letter-spacing:0.5px;padding-top:6px;\">Duration</div></td>");
        sb.AppendLine("</tr></table></td></tr>");

        // === USE CASE RESULTS TABLE (emerald header, Agent first + Progress column) ===
        sb.AppendLine("<tr><td style=\"padding:18px 28px 0 28px;\">");
        sb.AppendLine("<div style=\"font-size:13px;font-weight:700;color:#14532d;text-transform:uppercase;letter-spacing:0.6px;padding-bottom:8px;\">Use Case Results</div>");
        sb.AppendLine("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"border:1px solid #cbe9d3;\">");
        // Table header
        sb.AppendLine("<tr bgcolor=\"#0F5132\">");
        sb.AppendLine("<td style=\"padding:8px 10px;font-size:10px;font-weight:700;color:#d1fae5;text-transform:uppercase;letter-spacing:0.4px;\">Agent</td>");
        sb.AppendLine("<td style=\"padding:8px 6px;font-size:10px;font-weight:700;color:#d1fae5;text-transform:uppercase;letter-spacing:0.4px;\">Use Case</td>");
        sb.AppendLine("<td align=\"center\" style=\"padding:8px 4px;font-size:10px;font-weight:700;color:#d1fae5;text-transform:uppercase;\">Pass %</td>");
        sb.AppendLine("<td style=\"padding:8px 6px;font-size:10px;font-weight:700;color:#d1fae5;text-transform:uppercase;\">Progress</td>");
        sb.AppendLine("<td align=\"center\" style=\"padding:8px 4px;font-size:10px;font-weight:700;color:#d1fae5;text-transform:uppercase;\">Pass</td>");
        sb.AppendLine("<td align=\"center\" style=\"padding:8px 4px;font-size:10px;font-weight:700;color:#d1fae5;text-transform:uppercase;\">Fail</td>");
        sb.AppendLine("<td align=\"center\" style=\"padding:8px 4px;font-size:10px;font-weight:700;color:#d1fae5;text-transform:uppercase;\">N/E</td>");
        sb.AppendLine("<td align=\"right\" style=\"padding:8px 10px;font-size:10px;font-weight:700;color:#d1fae5;text-transform:uppercase;\">Duration</td>");
        sb.AppendLine("</tr>");

        int rowIdx = 0;
        foreach (var uc in allUseCases)
        {
            var rowBg = rowIdx % 2 == 1 ? " bgcolor=\"#fbfcfd\"" : "";
            var rateColor = uc.PassRate >= 100.0 ? "#15803d" : uc.PassRate >= GoodThreshold ? "#15803d" : uc.PassRate >= WarningThreshold ? "#b45309" : "#b91c1c";
            var passWidth = uc.Total > 0 ? (int)Math.Round(uc.PassRate) : 0;
            var failWidth = 100 - passWidth;
            var agentName = !string.IsNullOrWhiteSpace(uc.Agent) ? uc.Agent : controllerNode;
            var failColor = uc.Failed > 0 ? "font-weight:700;color:#b91c1c;" : "color:#1f2937;";

            sb.AppendLine($"<tr{rowBg}>");
            sb.AppendLine($"<td style=\"padding:9px 10px;font-size:12px;font-weight:600;color:#374151;border-top:1px solid #eef0f3;\">{Enc(agentName)}</td>");
            sb.AppendLine($"<td style=\"padding:9px 6px;font-size:12px;color:#1f2937;border-top:1px solid #eef0f3;\">{Enc(uc.UseCaseName)}</td>");
            sb.AppendLine($"<td align=\"center\" style=\"padding:9px 4px;font-size:12px;font-weight:700;color:{rateColor};border-top:1px solid #eef0f3;\">{uc.PassRate:F1}%</td>");
            // Progress bar (nested table with bgcolor for Outlook)
            sb.AppendLine("<td style=\"padding:9px 6px;border-top:1px solid #eef0f3;\">");
            sb.AppendLine("<table role=\"presentation\" width=\"60\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\"><tr>");
            if (passWidth > 0)
                sb.AppendLine($"<td width=\"{passWidth}%\" bgcolor=\"#22c55e\" style=\"background-color:#22c55e;height:7px;line-height:7px;font-size:0;\">&nbsp;</td>");
            if (failWidth > 0)
                sb.AppendLine($"<td width=\"{failWidth}%\" bgcolor=\"#ef4444\" style=\"background-color:#ef4444;height:7px;line-height:7px;font-size:0;\">&nbsp;</td>");
            sb.AppendLine("</tr></table></td>");
            sb.AppendLine($"<td align=\"center\" style=\"padding:9px 4px;font-size:12px;color:#1f2937;border-top:1px solid #eef0f3;\">{uc.Passed}</td>");
            sb.AppendLine($"<td align=\"center\" style=\"padding:9px 4px;font-size:12px;{failColor}border-top:1px solid #eef0f3;\">{uc.Failed}</td>");
            sb.AppendLine($"<td align=\"center\" style=\"padding:9px 4px;font-size:12px;color:#9ca3af;border-top:1px solid #eef0f3;\">{uc.NotExecuted}</td>");
            sb.AppendLine($"<td align=\"right\" style=\"padding:9px 10px;font-size:11px;color:#6b7280;border-top:1px solid #eef0f3;\">{uc.Duration:hh\\:mm\\:ss}</td>");
            sb.AppendLine("</tr>");
            rowIdx++;
        }
        sb.AppendLine("</table></td></tr>");

        // === FAILED TESTS TABLE (red theme + Type classification) ===
        if (build.AllFailedTests.Count > 0)
        {
            sb.AppendLine("<tr><td style=\"padding:18px 28px 0 28px;\">");
            sb.AppendLine($"<div style=\"font-size:13px;font-weight:700;color:#b91c1c;text-transform:uppercase;letter-spacing:0.6px;padding-bottom:8px;\">Failed Tests ({build.AllFailedTests.Count})</div>");
            sb.AppendLine("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"border:1px solid #f0c4c4;\">");
            sb.AppendLine("<tr bgcolor=\"#7f1d1d\">");
            sb.AppendLine("<td style=\"padding:8px 12px;font-size:11px;font-weight:700;color:#fecaca;text-transform:uppercase;letter-spacing:0.5px;\">Test</td>");
            sb.AppendLine("<td align=\"center\" style=\"padding:8px 6px;font-size:11px;font-weight:700;color:#fecaca;text-transform:uppercase;\">Set</td>");
            sb.AppendLine("<td align=\"center\" style=\"padding:8px 6px;font-size:11px;font-weight:700;color:#fecaca;text-transform:uppercase;\">Type</td>");
            sb.AppendLine("<td style=\"padding:8px 12px;font-size:11px;font-weight:700;color:#fecaca;text-transform:uppercase;letter-spacing:0.5px;\">Error</td>");
            sb.AppendLine("</tr>");

            foreach (var t in build.AllFailedTests.Take(30))
            {
                var errText = TruncateError(t.ErrorMessage);
                var patternLabel = GetPatternLabel(t.TestName);
                var (classification, pillBg) = GetEmailClassification(patternLabel);

                sb.AppendLine("<tr bgcolor=\"#fef6f6\">");
                sb.AppendLine($"<td style=\"padding:8px 12px;font-size:12px;font-weight:600;color:#991b1b;border-top:1px solid #f3d1d1;\">{Enc(t.TestName)}</td>");
                sb.AppendLine($"<td align=\"center\" style=\"padding:8px 6px;font-size:12px;color:#6b7280;border-top:1px solid #f3d1d1;\">{Enc(t.UseCaseName)}</td>");
                sb.AppendLine($"<td align=\"center\" style=\"padding:8px 6px;border-top:1px solid #f3d1d1;\"><span style=\"background-color:{pillBg};color:#ffffff;font-size:10px;font-weight:700;padding:2px 8px;text-transform:uppercase;letter-spacing:0.4px;\">{classification}</span></td>");
                sb.AppendLine($"<td style=\"padding:8px 12px;font-size:12px;color:#991b1b;border-top:1px solid #f3d1d1;\">{Enc(errText)}</td>");
                sb.AppendLine("</tr>");
            }

            if (build.AllFailedTests.Count > 30)
            {
                sb.AppendLine($"<tr><td colspan=\"4\" style=\"padding:10px 12px;font-size:12px;color:#64748b;text-align:center;border-top:1px solid #f3d1d1;\">... and {build.AllFailedTests.Count - 30} more failed tests</td></tr>");
            }
            sb.AppendLine("</table></td></tr>");

            // === FAILURE CLASSIFICATION LEGEND ===
            sb.AppendLine("<tr><td style=\"padding:16px 28px 0 28px;\">");
            sb.AppendLine("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" bgcolor=\"#f8f9fb\" style=\"background-color:#f8f9fb;border:1px solid #e5e7eb;\">");
            sb.AppendLine("<tr><td style=\"padding:14px 16px;\">");
            sb.AppendLine("<div style=\"font-size:11px;font-weight:700;color:#374151;text-transform:uppercase;letter-spacing:0.6px;padding-bottom:10px;\">Failure Classification</div>");
            sb.AppendLine("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\">");
            // Critical
            sb.AppendLine("<tr><td width=\"16\" valign=\"top\" style=\"padding:0 0 8px 0;\"><span style=\"display:inline-block;width:10px;height:10px;background-color:#b91c1c;\"></span></td>");
            sb.AppendLine("<td style=\"padding:0 0 8px 0;font-size:12px;color:#374151;line-height:1.5;\"><strong style=\"color:#b91c1c;\">Critical</strong> &mdash; fails consistently across runs; a confirmed product defect that blocks the use case.</td></tr>");
            // New
            sb.AppendLine("<tr><td width=\"16\" valign=\"top\" style=\"padding:0 0 8px 0;\"><span style=\"display:inline-block;width:10px;height:10px;background-color:#b45309;\"></span></td>");
            sb.AppendLine("<td style=\"padding:0 0 8px 0;font-size:12px;color:#374151;line-height:1.5;\"><strong style=\"color:#b45309;\">New</strong> &mdash; passed in the previous build but failed in this one; a likely regression to triage first.</td></tr>");
            // Flaky
            sb.AppendLine("<tr><td width=\"16\" valign=\"top\" style=\"padding:0;\"><span style=\"display:inline-block;width:10px;height:10px;background-color:#6d28d9;\"></span></td>");
            sb.AppendLine("<td style=\"padding:0;font-size:12px;color:#374151;line-height:1.5;\"><strong style=\"color:#6d28d9;\">Flaky</strong> &mdash; passes and fails intermittently with no code change; unstable test or timing-sensitive.</td></tr>");
            sb.AppendLine("</table></td></tr></table></td></tr>");
        }

        // === FOOTER ===
        sb.AppendLine("<tr><td style=\"padding:18px 28px 22px 28px;\">");
        sb.AppendLine("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"border-top:1px solid #e5e7eb;\">");
        sb.AppendLine("<tr><td style=\"padding-top:14px;font-size:12px;color:#6b7280;line-height:1.6;\">");
        sb.AppendLine($"Results path: <span style=\"font-family:Consolas,'Courier New',monospace;font-size:11px;color:#374151;\">{Enc(buildPath)}</span><br>");
        sb.AppendLine($"Automated notification &middot; {Enc(product)} QA");
        sb.AppendLine("</td></tr></table></td></tr>");

        // Close main container + MSO fix + outer wrapper
        sb.AppendLine("</table>");
        sb.AppendLine("<!--[if mso]></td></tr></table><![endif]-->");
        sb.AppendLine("</td></tr></table></body></html>");

        return sb.ToString();
    }

    private static void AppendEmailKpiCard(StringBuilder sb, string value, string label,
        string bgColor, string borderColor, string valueColor, string labelColor)
    {
        sb.AppendLine($"<td width=\"25%\" style=\"padding:0 6px;\">");
        sb.AppendLine($"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background-color:{bgColor};border:1px solid {borderColor};border-radius:8px;\">");
        sb.AppendLine($"<tr><td align=\"center\" style=\"padding:16px 8px;\">");
        sb.AppendLine($"<div style=\"font-size:28px;font-weight:800;color:{valueColor};line-height:1;\">{value}</div>");
        sb.AppendLine($"<div style=\"font-size:11px;font-weight:600;color:{labelColor};margin-top:4px;text-transform:uppercase;letter-spacing:0.5px;\">{label}</div>");
        sb.AppendLine("</td></tr></table></td>");
    }

    // ???????????????????????????????????????????????????????????????
    // CSV Export
    // ???????????????????????????????????????????????????????????????

    public string GenerateCsvContent(BuildNode node)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Build,{node.BuildNumber}");
        sb.AppendLine($"Total,{node.TotalTests}");
        sb.AppendLine($"Passed,{node.PassedTests}");
        sb.AppendLine($"Failed,{node.FailedTests}");
        sb.AppendLine($"Timeout,{node.TimeoutTests}");
        sb.AppendLine($"PassRate,{node.PassRate:F1}%");
        sb.AppendLine();
        sb.AppendLine("UseCase,Duration,Total,Passed,Failed,Timeout,PassRate,FailedTests");
        foreach (var uc in node.UseCases)
        {
            var failedNames = string.Join("; ", uc.FailedTests.Select(t => t.TestName));
            sb.AppendLine($"\"{uc.UseCaseName}\",\"{uc.Duration:hh\\:mm\\:ss}\",{uc.Total},{uc.Passed},{uc.Failed},{uc.Timeout},{uc.PassRate:F1}%,\"{failedNames}\"");
        }
        return sb.ToString();
    }
}
