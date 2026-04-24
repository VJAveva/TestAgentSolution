using System.IO;
using Microsoft.AspNetCore.Mvc;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.Api.Controllers;

[ApiController]
[Route("api/results")]
public class ResultsController : ControllerBase
{
    private readonly CachedBuildResultsProvider _buildResults;
    private readonly TrxResultsParser _parser;
    private readonly BuildResultsAggregator _aggregator;
    private readonly BuildResultsConfig _config;

    public ResultsController(
        CachedBuildResultsProvider buildResults,
        TrxResultsParser parser,
        BuildResultsAggregator aggregator,
        BuildResultsConfig config)
    {
        _buildResults = buildResults;
        _parser = parser;
        _aggregator = aggregator;
        _config = config;
    }

    /// <summary>
    /// GET /api/results/builds — list available builds.
    /// Cached: first call parses TRX files, subsequent calls return from cache
    /// until the build folder's modification time changes.
    /// </summary>
    [HttpGet("builds")]
    public IActionResult GetBuilds([FromQuery] int? limit, [FromQuery] string? health)
    {
        var builds = _buildResults.GetAllBuilds();

        IEnumerable<BuildNode> filtered = builds;

        if (!string.IsNullOrEmpty(health))
        {
            filtered = filtered.Where(b =>
                string.Equals(b.Health.ToString(), health, StringComparison.OrdinalIgnoreCase));
        }

        if (limit is > 0)
        {
            filtered = filtered.Take(limit.Value);
        }

        return Ok(filtered.Select(node => new
        {
            buildNumber = node.BuildNumber,
            modified = node.LatestRun ?? node.EarliestRun,
            totalTests = node.TotalTests,
            passedTests = node.PassedTests,
            failedTests = node.FailedTests,
            timeoutTests = node.TimeoutTests,
            passRate = node.PassRate,
            health = node.Health.ToString(),
        }));
    }

    /// <summary>
    /// GET /api/results/builds/{buildNumber} — parsed build results.
    /// Returns from cache if available.
    /// </summary>
    [HttpGet("builds/{buildNumber}")]
    public IActionResult GetBuild(string buildNumber)
    {
        var node = _buildResults.GetBuild(buildNumber);
        if (node == null)
            return NotFound(new { error = $"Build '{buildNumber}' not found" });

        return Ok(ToBuildDto(node));
    }

    /// <summary>
    /// GET /api/results/builds/{buildNumber}/detail — full build detail with all test results.
    /// Includes individual test cases with error messages, stack traces, and debug output.
    /// Supports optional filtering by outcome, use case, and text search.
    /// </summary>
    [HttpGet("builds/{buildNumber}/detail")]
    public IActionResult GetBuildDetail(
        string buildNumber,
        [FromQuery] string? outcome,
        [FromQuery] string? useCase,
        [FromQuery] string? search)
    {
        var node = _buildResults.GetBuild(buildNumber);
        if (node == null)
            return NotFound(new { error = $"Build '{buildNumber}' not found" });

        // Flatten all test results across use cases
        var allTests = node.UseCases
            .SelectMany(uc => uc.TestResults)
            .ToList();

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
        });
    }

    /// <summary>GET /api/results/trends — pass rate trends.</summary>
    [HttpGet("trends")]
    public IActionResult GetTrends()
    {
        var analyzer = new BuildTrendAnalyzer(_aggregator, _config);
        var trend = analyzer.AnalyzeTrends(_config.ResultsRootPath, _parser);
        return Ok(trend);
    }

    /// <summary>GET /api/results/flaky?builds=5 — flaky test detection.</summary>
    [HttpGet("flaky")]
    public IActionResult GetFlakyTests([FromQuery] int builds = 5)
    {
        var detector = new FlakyTestDetector();
        var alerts = detector.DetectFlakyTests(_config.ResultsRootPath, _parser, builds);
        return Ok(alerts);
    }

    /// <summary>GET /api/results/alerts — consecutive failure alerts.</summary>
    [HttpGet("alerts")]
    public IActionResult GetAlerts()
    {
        var detector = new ConsecutiveFailureDetector();
        var alerts = detector.Detect(_config.ResultsRootPath, _parser);
        return Ok(alerts);
    }

    /// <summary>
    /// POST /api/results/invalidate/{buildNumber} — force re-parse of a specific build.
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
    /// POST /api/results/invalidate — clear the entire results cache.
    /// </summary>
    [HttpPost("invalidate")]
    public IActionResult InvalidateAll()
    {
        _buildResults.InvalidateAll();
        return Ok(new { message = "All results cache cleared" });
    }

    /// <summary>
    /// GET /api/results/cache-stats — cache diagnostics.
    /// </summary>
    [HttpGet("cache-stats")]
    public IActionResult GetCacheStats()
    {
        return Ok(_buildResults.GetStats());
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
