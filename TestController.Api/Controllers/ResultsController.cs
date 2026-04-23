using System.IO;
using Microsoft.AspNetCore.Mvc;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.Api.Controllers;

[ApiController]
[Route("api/results")]
public class ResultsController : ControllerBase
{
    private readonly TrxResultsParser _parser;
    private readonly BuildResultsAggregator _aggregator;
    private readonly BuildResultsConfig _config;

    public ResultsController(
        TrxResultsParser parser,
        BuildResultsAggregator aggregator,
        BuildResultsConfig config)
    {
        _parser = parser;
        _aggregator = aggregator;
        _config = config;
    }

    /// <summary>GET /api/results/builds — list available builds.</summary>
    [HttpGet("builds")]
    public IActionResult GetBuilds()
    {
        var builds = _parser.DiscoverBuilds(_config.ResultsRootPath);
        return Ok(builds.Select(b => new
        {
            buildNumber = b.BuildNumber,
            modified = b.Modified,
        }));
    }

    /// <summary>GET /api/results/{buildNumber} — parsed build results.</summary>
    [HttpGet("{buildNumber}")]
    public IActionResult GetBuild(string buildNumber)
    {
        var buildPath = Path.Combine(_config.ResultsRootPath, buildNumber);
        if (!Directory.Exists(buildPath))
            return NotFound(new { error = $"Build '{buildNumber}' not found" });

        var node = _parser.ParseBuildFolder(buildPath);
        node = _aggregator.EvaluateBuildHealth(node);

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
