using System.Net;
using System.Net.Mail;
using System.Text;
using TestController.Api.Interceptors;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Identity;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using IAuthorizationService = TestControllerGrpc.Authorization.IAuthorizationService;

namespace TestController.WebApi.Endpoints;

public static class ResultsEndpoints
{
    /// <summary>
    /// Maps standalone-only Results endpoints. Routes for builds, builds/{id}/detail,
    /// trends, flaky, alerts, cache-stats, invalidate, and health are provided by
    /// the shared <c>ResultsController</c> in TestController.Api.
    ///
    /// Only these two endpoints live here because they reference WebApi-only
    /// dependencies (HTML generator + SMTP) that the shared controller doesn't
    /// take a dependency on.
    /// </summary>
    public static RouteGroupBuilder MapResultsEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/export/{buildNumber}", ExportBuildReport);
        group.MapPost("/send-report", SendReport);
        return group;
    }

    /// <summary>GET /api/results/export/{buildNumber}?format=html|csv � download report.</summary>
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

    /// <summary>POST /api/results/send-report – email report to recipients. Gated by Report_Generate.</summary>
    private static async Task<IResult> SendReport(
        SendReportRequest request,
        HttpContext context,
        SessionAuthInterceptor authInterceptor,
        IAuthorizationService authorizationService,
        IAuditWriter auditWriter,
        TrxResultsParser parser,
        BuildResultsAggregator aggregator,
        BuildReportHtmlGenerator htmlGenerator,
        BuildResultsConfig config)
    {
        // ── Permission gate: Report_Generate (Admin + SrMgr only) ──
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        var user = await authInterceptor.ResolveUserAsync(authHeader, ClientKind.Web);
        if (user is null)
            return Results.Json(new { error = "Not authenticated" }, statusCode: 401);

        var decision = await authorizationService.CanAsync(user, Permission.Report_Generate);
        if (!decision.Allowed)
            return Results.Json(new { error = decision.HumanReadable ?? "Forbidden" }, statusCode: 403);

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
                Subject = $"Build Results: {request.BuildNumber} � {node.PassRate:F1}% pass rate",
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

            // Fire-and-forget audit
            auditWriter.Enqueue(new AuditEntry
            {
                UserId = user.UserId,
                GuestId = user.GuestId,
                RoleAtTime = Enum.TryParse<Role>(user.Roles.FirstOrDefault(), out var r) ? r : null,
                ActionName = "Report_Generate",
                ResourceId = request.BuildNumber,
                Allowed = true,
                ReasonCode = $"sent:recipients={message.To.Count}",
                TimestampUtc = DateTime.UtcNow,
                ClientKind = user.ClientKind,
            });

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
