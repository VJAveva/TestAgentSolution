using System.Diagnostics;
using System.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using TestController.Api.Security;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.Api.Controllers;

[ApiController]
[Route("api/results")]
[Authorize(Policy = SecurityPolicies.User)]
public class ResultsController : ControllerBase
{
    private readonly CachedBuildResultsProvider _buildResults;
    private readonly TrxResultsParser _parser;
    private readonly BuildResultsAggregator _aggregator;
    private readonly BuildResultsConfig _config;
    private readonly IAppLogger _appLogger;
    private readonly FailurePatternAnalyzer _patternAnalyzer;
    private readonly ExecutionLogCorrelator _logCorrelator;

    public ResultsController(
        CachedBuildResultsProvider buildResults,
        TrxResultsParser parser,
        BuildResultsAggregator aggregator,
        BuildResultsConfig config,
        IAppLogger appLogger,
        FailurePatternAnalyzer patternAnalyzer,
        ExecutionLogCorrelator logCorrelator)
    {
        _buildResults = buildResults;
        _parser = parser;
        _aggregator = aggregator;
        _config = config;
        _appLogger = appLogger;
        _patternAnalyzer = patternAnalyzer;
        _logCorrelator = logCorrelator;
    }

    private string Corr => HttpContext.Items["CorrelationId"] as string ?? "";

    /// <summary>
    /// GET /api/results/builds � list available builds.
    /// </summary>
    [HttpGet("builds")]
    public IActionResult GetBuilds([FromQuery] int? limit, [FromQuery] string? health)
    {
        var corr = Corr;
        try
        {
            var builds = _buildResults.GetAllBuilds();
            IEnumerable<BuildNode> filtered = builds;

            if (!string.IsNullOrEmpty(health))
                filtered = filtered.Where(b =>
                    string.Equals(b.Health.ToString(), health, StringComparison.OrdinalIgnoreCase));
            if (limit is > 0)
                filtered = filtered.Take(limit.Value);

            var result = filtered.Select(node => new
            {
                buildNumber = node.BuildNumber,
                modified = node.LatestRun ?? node.EarliestRun,
                totalTests = node.TotalTests,
                passedTests = node.PassedTests,
                failedTests = node.FailedTests,
                timeoutTests = node.TimeoutTests,
                passRate = node.PassRate,
                health = node.Health.ToString(),
            }).ToList();

            _appLogger.Log(LogLevel.Information, "ResultsController",
                $"GetBuilds returned {result.Count} builds", corr);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _appLogger.Log(LogLevel.Error, "ResultsController",
                $"GetBuilds FAILED: {ex.Message}", corr, ex: ex);
            return StatusCode(500, new { error = "Failed to load builds", detail = ex.Message, correlationId = corr });
        }
    }

    /// <summary>
    /// GET /api/results/builds/{buildNumber} � parsed build results.
    /// </summary>
    [HttpGet("builds/{buildNumber}")]
    public IActionResult GetBuild(string buildNumber)
    {
        var corr = Corr;
        _appLogger.Log(LogLevel.Information, "ResultsController",
            $"GetBuild requested: {buildNumber}", corr);
        try
        {
            var node = _buildResults.GetBuild(buildNumber);
            if (node == null)
            {
                _appLogger.Log(LogLevel.Warning, "ResultsController",
                    $"Build not found: {buildNumber}", corr);
                return NotFound(new { error = $"Build '{buildNumber}' not found", correlationId = corr });
            }

            _appLogger.Log(LogLevel.Information, "ResultsController",
                $"GetBuild returned: {node.TotalTests} tests, {node.PassRate:F1}% pass", corr);
            return Ok(ToBuildDto(node));
        }
        catch (Exception ex)
        {
            _appLogger.Log(LogLevel.Error, "ResultsController",
                $"GetBuild FAILED for '{buildNumber}': {ex.Message}", corr, ex: ex);
            return StatusCode(500, new { error = $"Failed to load build '{buildNumber}'", detail = ex.Message, correlationId = corr });
        }
    }

    /// <summary>
    /// GET /api/results/builds/{buildNumber}/detail � full build detail with all test results.
    /// </summary>
    [HttpGet("builds/{buildNumber}/detail")]
    public IActionResult GetBuildDetail(
        string buildNumber,
        [FromQuery] string? outcome,
        [FromQuery] string? useCase,
        [FromQuery] string? search)
    {
        var corr = Corr;
        var sw = Stopwatch.StartNew();
        _appLogger.Log(LogLevel.Information, "ResultsController",
            $"GetBuildDetail requested: {buildNumber} (outcome={outcome}, useCase={useCase}, search={search})", corr);

        try
        {
            var node = _buildResults.GetBuild(buildNumber);
            if (node == null)
            {
                _appLogger.Log(LogLevel.Warning, "ResultsController",
                    $"Build not found for detail: {buildNumber} (root={_config.ResultsRootPath})", corr);
                return NotFound(new { error = $"Build '{buildNumber}' not found", correlationId = corr });
            }

            _appLogger.Log(LogLevel.Information, "ResultsController",
                $"Build loaded: {node.TotalTests} tests across {node.UseCases.Count} use cases", corr);

            // Flatten all test results across use cases
            var allTests = node.UseCases
                .SelectMany(uc => uc.TestResults)
                .ToList();

            _appLogger.Log(LogLevel.Information, "ResultsController",
                $"Flattened {allTests.Count} test results", corr);

            // Apply optional filters
            IEnumerable<TestResult> filtered = allTests;
            if (!string.IsNullOrEmpty(outcome))
                filtered = filtered.Where(t => string.Equals(t.Outcome, outcome, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(useCase))
                filtered = filtered.Where(t => string.Equals(t.UseCaseName, useCase, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(search))
                filtered = filtered.Where(t =>
                    t.TestName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    t.ClassName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    (t.ErrorMessage?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));

            var filteredList = filtered.ToList();

            sw.Stop();
            _appLogger.Log(LogLevel.Information, "ResultsController",
                $"GetBuildDetail completed: {filteredList.Count}/{allTests.Count} tests returned", corr, sw.ElapsedMilliseconds);

            return Ok(new
            {
                buildNumber = node.BuildNumber,
                earliestRun = node.EarliestRun,
                latestRun = node.LatestRun,
                totalDuration = node.TotalDuration,
                totalTests = node.TotalTests,
                passedTests = node.PassedTests,
                failedTests = node.FailedTests,
                timeoutTests = node.TimeoutTests,
                notExecutedTests = node.NotExecutedTests,
                passRate = node.PassRate,
                health = node.Health.ToString(),
                useCases = node.UseCases.Select(uc => new
                {
                    useCaseName = uc.UseCaseName,
                    total = uc.Total,
                    passed = uc.Passed,
                    failed = uc.Failed,
                    timeout = uc.Timeout,
                    notExecuted = uc.NotExecuted,
                    passRate = uc.PassRate,
                    duration = uc.Duration,
                }),
                filteredCount = filteredList.Count,
                tests = filteredList.Select(t => new
                {
                    testName = t.TestName,
                    className = t.ClassName,
                    useCase = t.UseCaseName,
                    outcome = t.Outcome,
                    duration = t.Duration,
                    durationText = FormatDuration(t.Duration),
                    errorMessage = t.ErrorMessage,
                    stackTrace = t.StackTrace,
                    debugTrace = t.DebugTrace,
                    stdOut = t.StdOut,
                    trxFile = t.TrxFileName,
                    steps = t.ExecutionSteps.Select(s => new
                    {
                        stepName = s.StepName,
                        outcome = s.Outcome,
                        duration = s.Duration,
                        stdOut = s.StdOut,
                        errorMessage = s.ErrorMessage,
                    }),
                }),
                filters = new { outcome, useCase, search },
                correlationId = corr,
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            _appLogger.Log(LogLevel.Error, "ResultsController",
                $"GetBuildDetail FAILED for '{buildNumber}': {ex.Message}", corr, sw.ElapsedMilliseconds, ex);
            return StatusCode(500, new
            {
                error = "Failed to parse build detail",
                detail = ex.Message,
                correlationId = corr,
                build = buildNumber,
            });
        }
    }

    /// <summary>GET /api/results/trends � pass rate trends.</summary>
    [HttpGet("trends")]
    public IActionResult GetTrends()
    {
        var analyzer = new BuildTrendAnalyzer(_aggregator, _config);
        var trend = analyzer.AnalyzeTrends(_config.ResultsRootPath, _parser);
        return Ok(trend);
    }

    /// <summary>GET /api/results/flaky?builds=5 � flaky test detection.</summary>
    [HttpGet("flaky")]
    public IActionResult GetFlakyTests([FromQuery] int builds = 5)
    {
        var detector = new FlakyTestDetector();
        var alerts = detector.DetectFlakyTests(_config.ResultsRootPath, _parser, builds);
        return Ok(alerts);
    }

    /// <summary>GET /api/results/alerts � consecutive failure alerts.</summary>
    [HttpGet("alerts")]
    public IActionResult GetAlerts()
    {
        var detector = new ConsecutiveFailureDetector();
        var alerts = detector.Detect(_config.ResultsRootPath, _parser);
        return Ok(alerts);
    }

    /// <summary>
    /// GET /api/results/analyze/{testName}?builds=10 � failure pattern analysis for a specific test.
    /// </summary>
    [HttpGet("analyze/{testName}")]
    public IActionResult AnalyzeTest(string testName, [FromQuery] int builds = 10)
    {
        var corr = Corr;
        try
        {
            var report = _patternAnalyzer.AnalyzeTest(testName, builds);

            _appLogger.Log(LogLevel.Information, "ResultsController",
                $"AnalyzeTest '{testName}': pattern={report.Pattern}, confidence={report.Confidence}%", corr);

            return Ok(new
            {
                testCaseName = report.TestCaseName,
                pattern = report.Pattern.ToString(),
                verdict = report.Verdict,
                confidence = report.Confidence,
                consecutiveFailures = report.ConsecutiveFailures,
                totalBuildsAnalyzed = report.TotalBuildsAnalyzed,
                totalFailures = report.TotalFailures,
                flakeRate = report.FlakeRate,
                lastPassBuild = report.LastPassBuild,
                firstFailBuild = report.FirstFailBuild,
                allSignaturesMatch = report.AllSignaturesMatch,
                suggestedAction = report.SuggestedAction,
                signatures = report.FailureSignatures.Select(s => new
                {
                    buildName = s.BuildName,
                    buildDate = s.BuildDate,
                    failedStepIndex = s.FailedStepIndex,
                    failedStepName = s.FailedStepName,
                    errorType = s.ErrorType,
                    normalizedMessage = s.NormalizedMessage,
                    topStackFrame = s.TopStackFrame,
                    agent = s.Agent,
                    duration = s.Duration,
                }),
                history = report.History.Select(h => new
                {
                    buildName = h.BuildName,
                    buildDate = h.BuildDate,
                    outcome = h.Outcome,
                    duration = h.Duration,
                    errorMessage = h.ErrorMessage.Length > 200
                        ? h.ErrorMessage[..200] + "�" : h.ErrorMessage,
                    agent = h.Agent,
                }),
                correlationId = corr,
            });
        }
        catch (Exception ex)
        {
            _appLogger.Log(LogLevel.Error, "ResultsController",
                $"AnalyzeTest FAILED for '{testName}': {ex.Message}", corr, ex: ex);
            return StatusCode(500, new { error = "Analysis failed", detail = ex.Message, correlationId = corr });
        }
    }

    /// <summary>
    /// GET /api/results/test/{testName}/compact-label � short pattern badge label
    /// (e.g. "REGRESSION (4x)") suitable for the QA email "Pattern" column.
    /// </summary>
    [HttpGet("test/{testName}/compact-label")]
    public IActionResult GetCompactLabel(string testName, [FromQuery] int builds = 10)
    {
        try
        {
            var label = _patternAnalyzer.GetCompactLabel(testName, builds);
            return Ok(new { testName, label });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "Compact-label failed", detail = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/results/builds/{build}/test/{testName}/log?stepIndex=N
    /// Returns merged TRX + Agent + Controller log filtered to the test's time window.
    /// </summary>
    [HttpGet("builds/{build}/test/{testName}/log")]
    public IActionResult GetTestLog(
        string build, string testName, [FromQuery] int? stepIndex = null)
    {
        var corr = Corr;
        try
        {
            var report = _logCorrelator.BuildReport(build, testName, stepIndex);
            if (!string.IsNullOrEmpty(report.Error))
                return NotFound(new { error = report.Error, correlationId = corr });

            return Ok(new
            {
                buildName = report.BuildName,
                testCaseName = report.TestCaseName,
                outcome = report.Outcome,
                agent = report.Agent,
                startTime = report.StartTime,
                endTime = report.EndTime,
                duration = report.Duration,
                failedStepIndex = report.FailedStepIndex,
                errorMessage = report.ErrorMessage,
                stackTrace = report.StackTrace,
                steps = report.Steps,
                agentLogLines = report.AgentLogLines,
                controllerLogLines = report.ControllerLogLines,
                mergedTimeline = report.MergedTimeline,
                correlationId = corr,
            });
        }
        catch (Exception ex)
        {
            _appLogger.Log(LogLevel.Error, "ResultsController",
                $"GetTestLog FAILED for build='{build}' test='{testName}': {ex.Message}", corr, ex: ex);
            return StatusCode(500, new { error = "Log retrieval failed", detail = ex.Message, correlationId = corr });
        }
    }

    /// <summary>
    /// POST /api/results/invalidate/{buildNumber} � force re-parse of a specific build.
    /// </summary>
    [HttpPost("invalidate/{buildNumber}")]
    public IActionResult InvalidateBuild(string buildNumber)
    {
        _buildResults.Invalidate(buildNumber);
        return Ok(new
        {
            message = $"Cache invalidated for '{buildNumber}'",
            build = _buildResults.GetBuild(buildNumber) is { } node ? new
            {
                buildNumber = node.BuildNumber,
                totalTests = node.TotalTests,
                passRate = node.PassRate,
                health = node.Health.ToString(),
            } : null,
        });
    }

    /// <summary>
    /// POST /api/results/invalidate � clear the entire results cache.
    /// </summary>
    [HttpPost("invalidate")]
    public IActionResult InvalidateAll()
    {
        _buildResults.InvalidateAll();
        return Ok(new { message = "All results cache cleared" });
    }

    /// <summary>
    /// GET /api/results/cache-stats � cache diagnostics.
    /// </summary>
    [HttpGet("cache-stats")]
    public IActionResult GetCacheStats()
    {
        return Ok(_buildResults.GetStats());
    }

    /// <summary>
    /// GET /api/results/health � diagnostic endpoint that confirms the
    /// Results subsystem is configured correctly. Use this from production
    /// to quickly tell the difference between "API not reachable",
    /// "wrong path configured", and "path empty/no builds".
    /// </summary>
    [HttpGet("health")]
    public IActionResult GetHealth()
    {
        var corr = Corr;
        var rootPath = _config.ResultsRootPath ?? "";
        var rootExists = !string.IsNullOrEmpty(rootPath) && Directory.Exists(rootPath);
        var buildCount = 0;
        string? rootError = null;

        if (rootExists)
        {
            try { buildCount = _parser.DiscoverBuilds(rootPath).Count; }
            catch (Exception ex) { rootError = ex.Message; }
        }
        else if (!string.IsNullOrEmpty(rootPath))
        {
            rootError = $"Path '{rootPath}' does not exist or is not accessible.";
        }
        else
        {
            rootError = "BuildResults:ResultsRootPath is not configured in appsettings.";
        }

        var ok = rootExists && rootError == null;
        var payload = new
        {
            ok,
            resultsRootPath = rootPath,
            rootExists,
            buildCount,
            error = rootError,
            cache = _buildResults.GetStats(),
            correlationId = corr,
        };

        _appLogger.Log(ok ? LogLevel.Information : LogLevel.Warning,
            "ResultsController",
            $"Health: ok={ok} root='{rootPath}' exists={rootExists} builds={buildCount} error={rootError}",
            corr);

        return ok ? Ok(payload) : StatusCode(503, payload);
    }

    private static object ToBuildDto(BuildNode node) => new
    {
        buildNumber = node.BuildNumber,
        earliestRun = node.EarliestRun,
        latestRun = node.LatestRun,
        totalDuration = node.TotalDuration,
        totalTests = node.TotalTests,
        passedTests = node.PassedTests,
        failedTests = node.FailedTests,
        timeoutTests = node.TimeoutTests,
        notExecutedTests = node.NotExecutedTests,
        passRate = node.PassRate,
        health = node.Health.ToString(),
        useCases = node.UseCases.Select(uc => new
        {
            useCaseName = uc.UseCaseName,
            total = uc.Total,
            passed = uc.Passed,
            failed = uc.Failed,
            timeout = uc.Timeout,
            notExecuted = uc.NotExecuted,
            passRate = uc.PassRate,
            duration = uc.Duration,
            failedTests = uc.FailedTests.Select(t => new
            {
                testName = t.TestName,
                outcome = t.Outcome,
                duration = t.Duration,
                errorMessage = t.ErrorMessage,
            }),
        }),
        allFailedTests = node.AllFailedTests.Select(t => new
        {
            testName = t.TestName,
            className = t.ClassName,
            outcome = t.Outcome,
            duration = t.Duration,
            errorMessage = t.ErrorMessage,
            stackTrace = t.StackTrace,
            useCaseName = t.UseCaseName,
            trxFileName = t.TrxFileName,
        }),
    };

    private static string FormatDuration(TimeSpan d)
    {
        if (d.TotalMinutes >= 1) return $"{d.Minutes}m {d.Seconds}s";
        if (d.TotalSeconds >= 1) return $"{d.TotalSeconds:F1}s";
        return $"{d.TotalMilliseconds:F0}ms";
    }
}
