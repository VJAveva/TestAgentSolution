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

    private static string TruncateError(string? msg) =>
        msg is null ? "" : msg.Length > 120 ? msg[..120] + "�" : msg;
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
    /// Uses table-based layout (no CSS grid/flexbox) for maximum email client compatibility.
    /// </summary>
    public string GenerateEmailHtml(BuildNode build, string product, string machineName,
        string buildPath, string reportLink = "")
    {
        var sb = new StringBuilder();
        var passRate = build.PassRate;
        var isPassing = passRate >= GoodThreshold;
        var badgeColor = isPassing ? "#2ecc71" : passRate >= WarningThreshold ? "#f59e0b" : "#e74c3c";
        var badgeText = isPassing ? "&#x2714; Passed" : "&#x2718; Failed";
        var passBarWidth = (int)Math.Round(passRate);
        var failBarWidth = 100 - passBarWidth;

        var failedUseCases = build.UseCases
            .Where(u => u.Failed > 0)
            .OrderBy(u => u.PassRate)
            .ToList();

        var allUseCases = build.UseCases
            .OrderByDescending(u => u.Failed)
            .ThenBy(u => u.UseCaseName)
            .ToList();

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\"><head><meta charset=\"UTF-8\"></head>");
        sb.AppendLine("<body style=\"margin:0;padding:0;background-color:#f0f2f5;font-family:'Segoe UI',Tahoma,Geneva,Verdana,sans-serif;\">");

        // Outer wrapper
        sb.AppendLine("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background-color:#f0f2f5;padding:24px 0;\"><tr><td align=\"center\">");

        // Main card
        sb.AppendLine("<table role=\"presentation\" width=\"920\" cellpadding=\"0\" cellspacing=\"0\" style=\"background-color:#ffffff;border-radius:12px;box-shadow:0 2px 12px rgba(0,0,0,0.08);overflow:hidden;\">");

        // === HEADER BANNER ===
        sb.AppendLine("<tr><td style=\"background:linear-gradient(135deg,#1a1a2e 0%,#16213e 50%,#0f3460 100%);padding:0;\">");
        sb.AppendLine("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\">");
        sb.AppendLine("<tr><td style=\"padding:28px 36px 12px 36px;\">");
        sb.AppendLine("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr>");
        sb.AppendLine("<td><span style=\"font-size:13px;font-weight:600;letter-spacing:2px;color:#64ffda;text-transform:uppercase;\">TestAgent CI</span></td>");
        sb.AppendLine($"<td align=\"right\"><span style=\"display:inline-block;background-color:{badgeColor};color:#fff;font-size:13px;font-weight:700;padding:5px 16px;border-radius:20px;letter-spacing:1px;text-transform:uppercase;\">{badgeText}</span></td>");
        sb.AppendLine("</tr></table></td></tr>");
        sb.AppendLine("<tr><td style=\"padding:4px 36px 24px 36px;\">");
        sb.AppendLine("<h1 style=\"margin:0;font-size:26px;font-weight:700;color:#ffffff;line-height:1.3;\">Use Case Execution Results</h1>");
        sb.AppendLine("<p style=\"margin:6px 0 0 0;font-size:14px;color:#a0aec0;\">Functional test run completed &bull; Results summary below</p>");
        sb.AppendLine("</td></tr></table></td></tr>");

        // === RUN METADATA ===
        sb.AppendLine("<tr><td style=\"padding:24px 36px 0 36px;\">");
        sb.AppendLine("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background-color:#f8f9fb;border-radius:8px;border:1px solid #e8ecf1;\">");
        sb.AppendLine("<tr><td style=\"padding:16px 20px;\">");
        sb.AppendLine("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr>");
        sb.AppendLine("<td width=\"50%\" style=\"vertical-align:top;\">");
        sb.AppendLine("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\">");
        sb.AppendLine($"<tr><td style=\"padding-bottom:10px;\"><span style=\"font-size:11px;font-weight:600;color:#8895a7;text-transform:uppercase;letter-spacing:0.8px;\">Test Run</span><br/><span style=\"font-size:14px;font-weight:600;color:#2d3748;\">{Enc(machineName)}</span></td></tr>");
        sb.AppendLine($"<tr><td><span style=\"font-size:11px;font-weight:600;color:#8895a7;text-transform:uppercase;letter-spacing:0.8px;\">Product</span><br/><span style=\"font-size:14px;font-weight:600;color:#2d3748;\">{Enc(product)}</span></td></tr>");
        sb.AppendLine("</table></td>");
        sb.AppendLine("<td width=\"50%\" style=\"vertical-align:top;\">");
        sb.AppendLine("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\">");
        sb.AppendLine($"<tr><td style=\"padding-bottom:10px;\"><span style=\"font-size:11px;font-weight:600;color:#8895a7;text-transform:uppercase;letter-spacing:0.8px;\">Build Info</span><br/><span style=\"font-size:14px;font-weight:600;color:#2d3748;\">{Enc(build.BuildNumber)}</span></td></tr>");
        sb.AppendLine($"<tr><td><span style=\"font-size:11px;font-weight:600;color:#8895a7;text-transform:uppercase;letter-spacing:0.8px;\">Run Date</span><br/><span style=\"font-size:14px;font-weight:600;color:#2d3748;\">{DateTime.Now:yyyy-MM-dd  HH:mm:ss}</span></td></tr>");
        sb.AppendLine("</table></td>");
        sb.AppendLine("</tr></table></td></tr></table></td></tr>");

        // === OVERALL SUMMARY KPI ===
        sb.AppendLine("<tr><td style=\"padding:24px 36px 0 36px;\">");
        sb.AppendLine("<h2 style=\"margin:0 0 14px 0;font-size:16px;font-weight:700;color:#1a202c;border-bottom:2px solid #e2e8f0;padding-bottom:8px;\">Overall Summary</h2>");
        sb.AppendLine("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr>");
        AppendEmailKpiCard(sb, $"{passRate:F0}%", "Pass Rate", passRate >= GoodThreshold ? "#f0fdf4" : "#fef2f2",
            passRate >= GoodThreshold ? "#bbf7d0" : "#fecaca", passRate >= GoodThreshold ? "#16a34a" : "#dc2626",
            passRate >= GoodThreshold ? "#4ade80" : "#f87171");
        AppendEmailKpiCard(sb, build.TotalTests.ToString(), "Total Tests", "#eff6ff", "#bfdbfe", "#2563eb", "#60a5fa");
        AppendEmailKpiCard(sb, build.PassedTests.ToString(), "Passed", "#f0fdf4", "#bbf7d0", "#16a34a", "#4ade80");
        AppendEmailKpiCard(sb, build.FailedTests.ToString(), "Failed", "#fef2f2", "#fecaca", "#dc2626", "#f87171");
        sb.AppendLine("</tr></table>");

        // Progress bar
        sb.AppendLine("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin-top:14px;\"><tr><td>");
        sb.AppendLine("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"border-radius:6px;overflow:hidden;background-color:#fee2e2;\"><tr>");
        sb.AppendLine($"<td width=\"{passBarWidth}%\" style=\"background-color:#22c55e;height:10px;border-radius:6px 0 0 6px;\"></td>");
        sb.AppendLine($"<td width=\"{failBarWidth}%\" style=\"background-color:#ef4444;height:10px;border-radius:0 6px 6px 0;\"></td>");
        sb.AppendLine("</tr></table></td></tr>");
        sb.AppendLine("<tr><td style=\"padding-top:4px;\"><table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr>");
        sb.AppendLine($"<td style=\"font-size:11px;color:#6b7280;\">Timeout: <strong>{build.TimeoutTests}</strong></td>");
        sb.AppendLine($"<td align=\"right\" style=\"font-size:11px;color:#6b7280;\">Duration: <strong>{build.TotalDuration:hh\\:mm\\:ss}</strong></td>");
        sb.AppendLine("</tr></table></td></tr></table></td></tr>");

        // === USE CASE RESULTS TABLE ===
        sb.AppendLine("<tr><td style=\"padding:28px 36px 0 36px;\">");
        var tableTitle = failedUseCases.Count > 0
            ? $"Use Case Results ({failedUseCases.Count} with failures)"
            : "Use Case Results (All Passed)";
        sb.AppendLine($"<h2 style=\"margin:0 0 14px 0;font-size:16px;font-weight:700;color:#1a202c;border-bottom:2px solid #e2e8f0;padding-bottom:8px;\">{tableTitle}</h2>");

        sb.AppendLine("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"border:1px solid #e2e8f0;border-radius:8px;overflow:hidden;\">");
        sb.AppendLine("<tr>");
        sb.AppendLine("<td style=\"background-color:#1e293b;padding:10px 14px;font-size:12px;font-weight:700;color:#e2e8f0;text-transform:uppercase;letter-spacing:0.8px;\">Use Case</td>");
        sb.AppendLine("<td width=\"70\" align=\"center\" style=\"background-color:#1e293b;padding:10px 8px;font-size:12px;font-weight:700;color:#e2e8f0;text-transform:uppercase;\">Pass %</td>");
        sb.AppendLine("<td width=\"120\" style=\"background-color:#1e293b;padding:10px 8px;font-size:12px;font-weight:700;color:#e2e8f0;text-transform:uppercase;\">Progress</td>");
        sb.AppendLine("<td width=\"50\" align=\"center\" style=\"background-color:#1e293b;padding:10px 8px;font-size:12px;font-weight:700;color:#e2e8f0;text-transform:uppercase;\">Pass</td>");
        sb.AppendLine("<td width=\"50\" align=\"center\" style=\"background-color:#1e293b;padding:10px 8px;font-size:12px;font-weight:700;color:#e2e8f0;text-transform:uppercase;\">Fail</td>");
        sb.AppendLine("<td width=\"50\" align=\"center\" style=\"background-color:#1e293b;padding:10px 8px;font-size:12px;font-weight:700;color:#e2e8f0;text-transform:uppercase;\">N/E</td>");
        sb.AppendLine("<td width=\"80\" align=\"center\" style=\"background-color:#1e293b;padding:10px 8px;font-size:12px;font-weight:700;color:#e2e8f0;text-transform:uppercase;\">Duration</td>");
        sb.AppendLine("</tr>");

        foreach (var uc in allUseCases)
        {
            var rowBg = uc.Failed > 0 && uc.PassRate < 50 ? "background-color:#fef2f2;" : "";
            var rateColor = uc.PassRate >= GoodThreshold ? "#16a34a" : uc.PassRate >= WarningThreshold ? "#f59e0b" : "#dc2626";
            var ucPassWidth = uc.Total > 0 ? (int)Math.Round(uc.PassRate) : 0;
            var ucFailWidth = 100 - ucPassWidth;

            sb.AppendLine($"<tr style=\"{rowBg}\">");
            sb.AppendLine($"<td style=\"padding:10px 14px;font-size:13px;color:#334155;border-bottom:1px solid #f1f5f9;\">{Enc(uc.UseCaseName)}</td>");
            sb.AppendLine($"<td align=\"center\" style=\"padding:10px 8px;font-size:13px;font-weight:600;color:{rateColor};border-bottom:1px solid #f1f5f9;\">{uc.PassRate:F1}%</td>");
            sb.AppendLine("<td style=\"padding:10px 8px;border-bottom:1px solid #f1f5f9;\">");
            sb.AppendLine("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"border-radius:4px;overflow:hidden;\"><tr>");
            sb.AppendLine($"<td width=\"{ucPassWidth}%\" style=\"background-color:#22c55e;height:8px;\"></td>");
            sb.AppendLine($"<td width=\"{ucFailWidth}%\" style=\"background-color:#ef4444;height:8px;\"></td>");
            sb.AppendLine("</tr></table></td>");
            sb.AppendLine($"<td align=\"center\" style=\"padding:10px 8px;font-size:13px;font-weight:600;color:#334155;border-bottom:1px solid #f1f5f9;\">{uc.Passed}</td>");
            var failColor = uc.Failed > 0 ? "#dc2626" : "#334155";
            var failWeight = uc.Failed > 0 ? "700" : "400";
            sb.AppendLine($"<td align=\"center\" style=\"padding:10px 8px;font-size:13px;font-weight:{failWeight};color:{failColor};border-bottom:1px solid #f1f5f9;\">{uc.Failed}</td>");
            sb.AppendLine($"<td align=\"center\" style=\"padding:10px 8px;font-size:13px;color:#64748b;border-bottom:1px solid #f1f5f9;\">{uc.NotExecuted}</td>");
            sb.AppendLine($"<td align=\"center\" style=\"padding:10px 8px;font-size:12px;color:#64748b;font-family:'Courier New',monospace;border-bottom:1px solid #f1f5f9;\">{uc.Duration:hh\\:mm\\:ss}</td>");
            sb.AppendLine("</tr>");
        }
        sb.AppendLine("</table></td></tr>");

        // === FAILED TESTS DETAIL ===
        if (build.AllFailedTests.Count > 0)
        {
            sb.AppendLine("<tr><td style=\"padding:28px 36px 0 36px;\">");
            sb.AppendLine($"<h2 style=\"margin:0 0 14px 0;font-size:16px;font-weight:700;color:#dc2626;border-bottom:2px solid #fecaca;padding-bottom:8px;\">Failed Tests ({build.AllFailedTests.Count})</h2>");
            sb.AppendLine("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"border:1px solid #fecaca;border-radius:8px;overflow:hidden;\">");
            sb.AppendLine("<tr>");
            sb.AppendLine("<td style=\"background-color:#7f1d1d;padding:10px 14px;font-size:12px;font-weight:700;color:#fecaca;text-transform:uppercase;\">Test Name</td>");
            sb.AppendLine("<td style=\"background-color:#7f1d1d;padding:10px 14px;font-size:12px;font-weight:700;color:#fecaca;text-transform:uppercase;\">Use Case</td>");
            sb.AppendLine("<td style=\"background-color:#7f1d1d;padding:10px 14px;font-size:12px;font-weight:700;color:#fecaca;text-transform:uppercase;\">Pattern</td>");
            sb.AppendLine("<td style=\"background-color:#7f1d1d;padding:10px 14px;font-size:12px;font-weight:700;color:#fecaca;text-transform:uppercase;\">Error</td>");
            sb.AppendLine("</tr>");

            foreach (var t in build.AllFailedTests.Take(30))
            {
                var errText = TruncateError(t.ErrorMessage);
                var patternLabel = GetPatternLabel(t.TestName);
                var (patBg, patColor) = GetPatternBadgeColors(patternLabel);
                sb.AppendLine("<tr style=\"background-color:#fef2f2;\">");
                sb.AppendLine($"<td style=\"padding:8px 14px;font-size:12px;font-weight:600;color:#991b1b;border-bottom:1px solid #fecaca;\">{Enc(t.TestName)}</td>");
                sb.AppendLine($"<td style=\"padding:8px 14px;font-size:12px;color:#64748b;border-bottom:1px solid #fecaca;\">{Enc(t.UseCaseName)}</td>");
                sb.AppendLine($"<td style=\"padding:8px 14px;font-size:11px;font-weight:700;text-align:center;border-bottom:1px solid #fecaca;background:{patBg};color:{patColor};\">{Enc(patternLabel)}</td>");
                sb.AppendLine($"<td style=\"padding:8px 14px;font-size:11px;color:#991b1b;font-family:'Courier New',monospace;border-bottom:1px solid #fecaca;\">{Enc(errText)}</td>");
                sb.AppendLine("</tr>");
            }

            if (build.AllFailedTests.Count > 30)
            {
                sb.AppendLine($"<tr><td colspan=\"4\" style=\"padding:10px 14px;font-size:12px;color:#64748b;text-align:center;\">... and {build.AllFailedTests.Count - 30} more failed tests</td></tr>");
            }
            sb.AppendLine("</table></td></tr>");
        }

        // === FOOTER ===
        sb.AppendLine("<tr><td style=\"padding:28px 36px 0 36px;\">");
        sb.AppendLine("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background-color:#f8f9fb;border-radius:8px;border:1px solid #e8ecf1;\"><tr><td style=\"padding:16px 20px;\">");
        sb.AppendLine("<p style=\"margin:0 0 6px 0;font-size:13px;font-weight:700;color:#475569;\">TestAgent CI Functional Test Results Completed</p>");
        sb.AppendLine($"<p style=\"margin:0 0 10px 0;font-size:13px;color:#64748b;line-height:1.6;\">Product = <strong>{Enc(product)}</strong> | Build = <strong>{Enc(build.BuildNumber)}</strong><br/>Path = <span style=\"font-family:'Courier New',monospace;font-size:12px;color:#475569;\">{Enc(buildPath)}</span></p>");

        if (!string.IsNullOrEmpty(reportLink))
        {
            sb.AppendLine("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\"><tr><td>");
            sb.AppendLine($"<a href=\"{Enc(reportLink)}\" style=\"display:inline-block;background-color:#2563eb;color:#ffffff;font-size:13px;font-weight:600;padding:8px 20px;border-radius:6px;text-decoration:none;\">&#128196; View Full Report</a>");
            sb.AppendLine("</td></tr></table>");
        }
        sb.AppendLine("</td></tr></table></td></tr>");

        // Copyright
        sb.AppendLine("<tr><td style=\"padding:28px 36px;\"><table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"border-top:1px solid #e2e8f0;\"><tr><td style=\"padding-top:16px;\">");
        sb.AppendLine("<p style=\"margin:0;font-size:12px;color:#94a3b8;line-height:1.5;\">This is an automated notification from <strong style=\"color:#64748b;\">TestAgent CI</strong>.</p>");
        sb.AppendLine($"<p style=\"margin:10px 0 0 0;font-size:11px;color:#cbd5e1;\">&copy; {DateTime.Now.Year} TestAgent &bull; AVEVA System Platform QA</p>");
        sb.AppendLine("</td></tr></table></td></tr>");

        // Close tables
        sb.AppendLine("</table></td></tr></table></body></html>");

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
