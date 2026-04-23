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
    };
}
