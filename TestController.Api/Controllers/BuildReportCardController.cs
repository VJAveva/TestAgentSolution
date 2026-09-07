using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using TestController.Api.Security;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.Api.Controllers;

/// <summary>
/// Exposes the Build Report Card (the same <see cref="BuildReportAggregator"/>
/// the WPF host uses) to the React WebClient. Durations are projected to
/// friendly labels so the client needs no TimeSpan parsing; enums serialize as
/// strings via the host's camelCase + string-enum JSON options.
/// </summary>
[ApiController]
[Route("api/reportcard")]
[Authorize(Policy = SecurityPolicies.User)]
public class BuildReportCardController : ControllerBase
{
    private readonly BuildReportAggregator _aggregator;
    private readonly IAppLogger _appLogger;

    public BuildReportCardController(BuildReportAggregator aggregator, IAppLogger appLogger)
    {
        _aggregator = aggregator;
        _appLogger = appLogger;
    }

    private string Corr => HttpContext.Items["CorrelationId"] as string ?? "";

    /// <summary>GET /api/reportcard/builds — list available build folders (newest first).</summary>
    [HttpGet("builds")]
    [RequirePermission(Permission.ReportCard_View)]
    public IActionResult GetBuilds()
    {
        try
        {
            var builds = _aggregator.ListBuilds();
            return Ok(new { items = builds });
        }
        catch (Exception ex)
        {
            _appLogger.Log(LogLevel.Error, "BuildReportCardController", "GetBuilds failed", Corr, 0, ex);
            return StatusCode(500, new { error = "Failed to list builds", detail = ex.Message });
        }
    }

    /// <summary>GET /api/reportcard?build={buildNumber} — aggregate the report card.</summary>
    [HttpGet]
    [RequirePermission(Permission.ReportCard_View)]
    public async Task<IActionResult> GetReportCard([FromQuery] string? build, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var card = await _aggregator.AggregateAsync(build, ct);
            sw.Stop();
            _appLogger.Log(LogLevel.Information, "BuildReportCardController",
                $"GetReportCard build='{build ?? "(latest)"}' returned {card.Agents.Count} agents", Corr, sw.ElapsedMilliseconds);
            return Ok(Project(card));
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499, new { error = "Request cancelled" });
        }
        catch (Exception ex)
        {
            _appLogger.Log(LogLevel.Error, "BuildReportCardController", "GetReportCard failed", Corr, sw.ElapsedMilliseconds, ex);
            return StatusCode(500, new { error = "Failed to aggregate report card", detail = ex.Message });
        }
    }

    private static object Project(BuildReportCard c) => new
    {
        buildNumber = c.BuildNumber,
        generatedUtc = c.GeneratedUtc,
        triggeredBy = c.TriggeredBy,
        startedUtc = c.StartedUtc,
        completedUtc = c.CompletedUtc,
        durationLabel = FriendlyDuration(c.Duration),
        hasData = c.HasData,
        grade = new
        {
            letter = c.Grade.Letter,
            score = c.Grade.Score,
            basePassRate = c.Grade.BasePassRate,
            verdict = c.Grade.Verdict,
            breakdownLines = c.Grade.BreakdownLines,
            severity = c.Grade.Severity,
        },
        totalTests = c.TotalTests,
        passedTests = c.PassedTests,
        failedTests = c.FailedTests,
        skippedTests = c.SkippedTests,
        passRate = c.PassRate,
        regressionCount = c.RegressionCount,
        flakyCount = c.FlakyCount,
        psrErrorCount = c.PsrErrorCount,
        deltaVsLast = c.DeltaVsLast,
        psrPassCount = c.PsrPassCount,
        psrTotalCount = c.PsrTotalCount,
        cis = c.Cis.Select(ci => new
        {
            name = ci.Name,
            total = ci.Total,
            passed = ci.Passed,
            failed = ci.Failed,
            skipped = ci.Skipped,
            passRate = ci.PassRate,
            severity = ci.Severity,
        }),
        agents = c.Agents.Select(a => new
        {
            useCase = a.UseCase,
            agentName = a.AgentName,
            ci = a.Ci,
            passed = a.Passed,
            failed = a.Failed,
            skipped = a.Skipped,
            total = a.Total,
            durationLabel = FriendlyDuration(a.Duration),
            passRate = a.PassRate,
            severity = a.Severity,
        }),
        psrs = c.Psrs.Select(p => new
        {
            name = p.Name,
            scenario = p.Scenario,
            outcome = p.Outcome,
            durationLabel = FriendlyDuration(p.Duration),
            tags = p.Tags,
            throughput = p.Throughput,
            errors = p.Errors,
            warnings = p.Warnings,
            errorDetail = p.ErrorDetail,
        }),
        failures = c.Failures.Select(f => new
        {
            testName = f.TestName,
            ci = f.Ci,
            failedOnAgents = f.FailedOnAgents,
            pattern = f.Pattern,
            patternLabel = f.PatternLabel,
            owner = f.Owner,
            firstSeen = f.FirstSeen,
        }),
        trend = c.Trend.Select(t => new
        {
            label = t.Label,
            passRate = t.PassRate,
            isCurrent = t.IsCurrent,
        }),
    };

    private static string FriendlyDuration(TimeSpan d)
    {
        if (d <= TimeSpan.Zero) return "—";
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours}h {d.Minutes}m";
        if (d.TotalMinutes >= 1) return $"{d.Minutes}m";
        return $"{d.Seconds}s";
    }
}
