using System.Net;
using System.Net.Mail;
using System.Text;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Endpoints;

public static class ResultsEndpoints
{
    public static RouteGroupBuilder MapResultsEndpoints(this RouteGroupBuilder group)
    {
        // These endpoints extend the shared ResultsController with standalone-specific features.
        // Common endpoints (builds, builds/{buildNumber}, trends, flaky, alerts) are provided by the shared library.
        group.MapGet("/export/{buildNumber}", ExportBuildReport);
        group.MapPost("/send-report", SendReport);
        return group;
    }

    /// <summary>GET /api/results/builds — list available builds.</summary>
    private static IResult ListBuilds(
        TrxResultsParser parser,
        BuildResultsAggregator aggregator,
        BuildResultsConfig config)
    {
        var builds = parser.DiscoverBuilds(config.ResultsRootPath);
        var results = builds.Select(b =>
        {
            try
            {
                var node = parser.ParseBuildFolder(b.Path);
                node = aggregator.EvaluateBuildHealth(node);
                return new
                {
                    b.BuildNumber,
                    b.Modified,
                    node.TotalTests,
                    node.PassedTests,
                    node.FailedTests,
                    node.TimeoutTests,
                    node.PassRate,
                    Health = node.Health.ToString()
                };
            }
            catch
            {
                return new
                {
                    b.BuildNumber,
                    b.Modified,
                    TotalTests = 0,
                    PassedTests = 0,
                    FailedTests = 0,
                    TimeoutTests = 0,
                    PassRate = 0.0,
                    Health = "Unknown"
                };
            }
        });

        return Results.Ok(results);
    }

    /// <summary>GET /api/results/builds/{buildNumber} — get detailed build results.</summary>
    private static IResult GetBuildResults(
        string buildNumber,
        TrxResultsParser parser,
        BuildResultsAggregator aggregator,
        BuildResultsConfig config)
    {
        var builds = parser.DiscoverBuilds(config.ResultsRootPath);
        var match = builds.FirstOrDefault(b =>
            string.Equals(b.BuildNumber, buildNumber, StringComparison.OrdinalIgnoreCase));

        if (match == default)
            return Results.NotFound($"Build '{buildNumber}' not found.");

        try
        {
            var node = parser.ParseBuildFolder(match.Path);
            node = aggregator.EvaluateBuildHealth(node);
            return Results.Ok(node);
        }
        catch (Exception ex)
        {
            return Results.Problem($"Failed to parse build: {ex.Message}", statusCode: 500);
        }
    }

    /// <summary>GET /api/results/trends — trend analysis across builds.</summary>
    private static IResult GetTrends(
        TrxResultsParser parser,
        BuildTrendAnalyzer trendAnalyzer,
        BuildResultsConfig config,
        IAppLogger logger)
    {
        try
        {
            var report = trendAnalyzer.AnalyzeTrends(config.ResultsRootPath, parser);
            return Results.Ok(report);
        }
        catch (Exception ex)
        {
            logger.Error("Results", "Failed to analyze trends", ex);
            return Results.Problem($"Failed to analyze trends: {ex.Message}", statusCode: 500);
        }
    }

    /// <summary>GET /api/results/alerts — consecutive failure alerts.</summary>
    private static IResult GetAlerts(
        TrxResultsParser parser,
        ConsecutiveFailureDetector detector,
        BuildResultsConfig config,
        IAppLogger logger)
    {
        try
        {
            var alerts = detector.Detect(config.ResultsRootPath, parser, config.ConsecutiveFailThreshold);
            return Results.Ok(alerts);
        }
        catch (Exception ex)
        {
            logger.Error("Results", "Failed to detect consecutive failures", ex);
            return Results.Problem($"Failed to detect alerts: {ex.Message}", statusCode: 500);
        }
    }

    /// <summary>GET /api/results/export/{buildNumber}?format=html|csv — download report.</summary>
    private static IResult ExportBuildReport(
        string buildNumber,
        HttpContext context,
        TrxResultsParser parser,
        BuildResultsAggregator aggregator,
        BuildReportHtmlGenerator htmlGenerator,
        BuildResultsConfig config)
    {
        var format = context.Request.Query["format"].ToString().ToLowerInvariant();
        if (string.IsNullOrEmpty(format)) format = "html";

        var builds = parser.DiscoverBuilds(config.ResultsRootPath);
        var match = builds.FirstOrDefault(b =>
            string.Equals(b.BuildNumber, buildNumber, StringComparison.OrdinalIgnoreCase));

        if (match == default)
            return Results.NotFound($"Build '{buildNumber}' not found.");

        try
        {
            var node = parser.ParseBuildFolder(match.Path);
            node = aggregator.EvaluateBuildHealth(node);

            if (format == "csv")
            {
                var csv = htmlGenerator.GenerateCsvContent(node);
                return Results.File(
                    Encoding.UTF8.GetBytes(csv),
                    "text/csv",
                    $"BuildResults_{buildNumber}.csv");
            }
            else
            {
                var html = htmlGenerator.GenerateSingleBuildHtml(node);
                return Results.File(
                    Encoding.UTF8.GetBytes(html),
                    "text/html",
                    $"BuildResults_{buildNumber}.html");
            }
        }
        catch (Exception ex)
        {
            return Results.Problem($"Failed to generate report: {ex.Message}", statusCode: 500);
        }
    }

    /// <summary>POST /api/results/send-report — email report to recipients.</summary>
    private static async Task<IResult> SendReport(
        SendReportRequest request,
        TrxResultsParser parser,
        BuildResultsAggregator aggregator,
        BuildReportHtmlGenerator htmlGenerator,
        BuildResultsConfig config)
    {
        var builds = parser.DiscoverBuilds(config.ResultsRootPath);
        var match = builds.FirstOrDefault(b =>
            string.Equals(b.BuildNumber, request.BuildNumber, StringComparison.OrdinalIgnoreCase));

        if (match == default)
            return Results.NotFound($"Build '{request.BuildNumber}' not found.");

        var recipients = !string.IsNullOrWhiteSpace(request.Recipients)
            ? request.Recipients
            : config.ReportRecipients;

        if (string.IsNullOrWhiteSpace(recipients))
            return Results.BadRequest("No recipients specified.");

        try
        {
            var node = parser.ParseBuildFolder(match.Path);
            node = aggregator.EvaluateBuildHealth(node);
            var html = htmlGenerator.GenerateSingleBuildHtml(node);

            using var smtp = new SmtpClient(config.SmtpServer, config.SmtpPort);
            using var message = new MailMessage
            {
                From = new MailAddress(
                    !string.IsNullOrWhiteSpace(config.FromAddress)
                        ? config.FromAddress
                        : "testcontroller@noreply.local"),
                Subject = $"Build Results: {request.BuildNumber} — {node.PassRate:F1}% pass rate",
                Body = html,
                IsBodyHtml = true
            };

            foreach (var addr in recipients.Split(';', ',')
                .Select(a => a.Trim())
                .Where(a => !string.IsNullOrEmpty(a)))
            {
                message.To.Add(addr);
            }

            await smtp.SendMailAsync(message);

            return Results.Ok(new
            {
                message = $"Report sent for build '{request.BuildNumber}'.",
                recipients = message.To.Count
            });
        }
        catch (Exception ex)
        {
            return Results.Problem($"Failed to send report: {ex.Message}", statusCode: 500);
        }
    }
}

public record SendReportRequest(string BuildNumber, string? Recipients);
