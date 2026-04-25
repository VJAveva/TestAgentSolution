using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Services;

namespace TestController.Api.Middleware;

/// <summary>
/// Middleware that assigns a correlation ID (X-Request-Id) to every API request,
/// logs request start/end with timing, and catches unhandled exceptions to return
/// structured JSON errors instead of IIS error pages.
/// </summary>
public sealed class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IAppLogger _appLogger;

    public RequestLoggingMiddleware(RequestDelegate next, IAppLogger appLogger)
    {
        _next = next;
        _appLogger = appLogger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";

        // Skip non-API requests (static files, SignalR negotiate, etc.)
        if (!path.StartsWith("/api/"))
        {
            await _next(context);
            return;
        }

        var method = context.Request.Method;
        var query = context.Request.QueryString.Value ?? "";

        // Get or create correlation ID
        var correlationId = context.Request.Headers["X-Request-Id"].FirstOrDefault()
            ?? Guid.NewGuid().ToString("N")[..8];

        context.Response.Headers["X-Request-Id"] = correlationId;
        context.Items["CorrelationId"] = correlationId;

        var sw = Stopwatch.StartNew();
        _appLogger.Log(LogLevel.Information, "HTTP",
            $"BEGIN {method} {path}{query}", correlationId);

        try
        {
            await _next(context);
            sw.Stop();

            var status = context.Response.StatusCode;
            var level = status >= 500 ? LogLevel.Error
                : status >= 400 ? LogLevel.Warning
                : LogLevel.Information;

            _appLogger.Log(level, "HTTP",
                $"{method} {path}{query} ? {status}", correlationId, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _appLogger.Log(LogLevel.Error, "HTTP",
                $"{method} {path}{query} ? EXCEPTION: {ex.Message}", correlationId, sw.ElapsedMilliseconds, ex);

            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Internal Server Error",
                detail = ex.Message,
                correlationId,
                timestamp = DateTime.UtcNow,
            });
        }
    }
}
