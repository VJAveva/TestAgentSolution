using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Services;

namespace TestController.Api.Controllers;

/// <summary>
/// Ingests client-side (WebClient) errors and crashes into the shared
/// <see cref="IAppLogger"/> so a React error boundary / unhandled rejection
/// lands in the same <c>errors_{date}.log</c> + <c>app_{date}.jsonl</c> as the
/// server-side failure that triggered it. Tagged with component "WebClient"
/// and the run/correlation id so it correlates with the rest of the trace.
///
/// <para>
/// <see cref="AllowAnonymous"/> because crashes can occur before auth
/// completes. The endpoint is deliberately minimal and never throws — a
/// logging sink must not become a failure surface.
/// </para>
/// </summary>
[ApiController]
[Route("api")]
[AllowAnonymous]
public sealed class ClientLogsController : ControllerBase
{
    private const int MaxFieldLength = 4000;

    private readonly IAppLogger _appLogger;

    public ClientLogsController(IAppLogger appLogger) => _appLogger = appLogger;

    /// <summary>POST /api/clientlogs — record a single browser-side log entry.</summary>
    [HttpPost("clientlogs")]
    public IActionResult Post([FromBody] ClientLogDto? dto)
    {
        if (dto is null) return BadRequest();

        var scope = Trunc(dto.Scope) ?? "WebClient";
        var action = Trunc(dto.Action);
        var detail = Trunc(dto.Detail) ?? "(no detail)";
        var url = Trunc(dto.Url);
        var page = Trunc(dto.Page);
        var runId = Trunc(dto.CorrelationId);

        var level = (dto.Level ?? "error").ToLowerInvariant() switch
        {
            "warn" or "warning" => LogLevel.Warning,
            "info" => LogLevel.Information,
            "debug" => LogLevel.Debug,
            _ => LogLevel.Error,
        };

        var statusPart = dto.Status is > 0 ? $"{dto.Status} " : "";
        var urlPart = string.IsNullOrEmpty(url) ? "" : $"{url} ";
        var pagePart = string.IsNullOrEmpty(page) ? "" : $" (page {page})";
        var message = $"[{scope}] {statusPart}{urlPart}\u2014 {detail}{pagePart}";

        // Fire-and-forget into the shared sink; LogStructured itself never throws.
        _appLogger.LogStructured(
            level,
            category: "WebClient",
            message: message,
            agent: null,
            runId: runId,
            pipeline: null,
            action: action,
            elapsedMs: 0,
            ex: null);

        return Accepted();
    }

    private static string? Trunc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Length <= MaxFieldLength ? s : s[..MaxFieldLength];
    }

    /// <summary>Payload posted by the WebClient logger (<c>src/lib/logger.ts</c>).</summary>
    public sealed class ClientLogDto
    {
        public string? Level { get; set; }
        public string? Scope { get; set; }
        public string? Action { get; set; }
        public int? Status { get; set; }
        public string? Url { get; set; }
        public string? Detail { get; set; }
        public string? CorrelationId { get; set; }
        public string? Timestamp { get; set; }
        public string? UserAgent { get; set; }
        public string? Page { get; set; }
    }
}
