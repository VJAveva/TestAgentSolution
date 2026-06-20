using System.Security.Claims;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Services;

/// <summary>
/// Makes the WPF controller the single pipeline-execution engine for web-triggered runs.
/// When a controller proxy URL is configured (co-located deployment), this middleware
/// intercepts execution-write requests (trigger / cancel / RBAC retry) and forwards them
/// to the controller's embedded API instead of running them in the WebApi process. The
/// controller then executes on the controller node — local PowerShell/bat, rCloud revert
/// and SendMail run under the controller identity, exactly like a WPF-triggered run, and the
/// authoritative single-run pipeline lock is enforced there.
///
/// When no proxy is configured (standalone WebApi-only deployment) this middleware is never
/// registered, so the WebApi keeps executing locally and the WPF path is untouched.
/// </summary>
public sealed class ControllerExecutionForwardingMiddleware
{
    private readonly RequestDelegate _next;

    public ControllerExecutionForwardingMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ControllerProxyService proxy, IAppLogger logger)
    {
        if (!proxy.IsConfigured || !ShouldForward(context.Request))
        {
            await _next(context);
            return;
        }

        // Triggering/cancelling/retrying requires an authenticated user. Fine-grained
        // authorization (role + pipeline assignment) is enforced by the controller, mirroring
        // the existing auth/lock proxy pattern.
        if (context.User?.Identity?.IsAuthenticated != true)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var pathAndQuery = context.Request.Path + context.Request.QueryString;
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        var userId = context.Request.Headers["X-User-Id"].FirstOrDefault() ?? ResolveUserId(context.User);
        var source = context.Request.Headers["X-Source"].FirstOrDefault() ?? "WebClient";

        string? body = null;
        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            context.Request.EnableBuffering();
            using var reader = new StreamReader(
                context.Request.Body, leaveOpen: true);
            body = await reader.ReadToEndAsync();
        }

        var contentType = context.Request.ContentType ?? "application/json";

        using var response = await proxy.ForwardExecutionAsync(
            new HttpMethod(context.Request.Method), pathAndQuery, authHeader, userId, source, body, contentType);

        if (response is null)
        {
            logger.Error("ExecutionForward",
                $"Controller unreachable forwarding {context.Request.Method} {pathAndQuery}; pipeline NOT started");
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                "{\"error\":\"controller-unreachable\",\"message\":\"The controller node is not reachable. The pipeline was not started.\"}");
            return;
        }

        logger.Info("ExecutionForward",
            $"Forwarded {context.Request.Method} {pathAndQuery} to controller (user={userId}) -> {(int)response.StatusCode}");

        context.Response.StatusCode = (int)response.StatusCode;
        var responseContentType = response.Content.Headers.ContentType?.ToString();
        if (!string.IsNullOrEmpty(responseContentType))
            context.Response.ContentType = responseContentType;

        var payload = await response.Content.ReadAsByteArrayAsync();
        if (payload.Length > 0)
            await context.Response.Body.WriteAsync(payload);
    }

    /// <summary>
    /// True for POST execution-write paths that must run on the controller node:
    /// <c>/api/execution/trigger/{tag}</c>, <c>/api/execution/cancel</c>,
    /// <c>/api/execution/{sessionId}/cancel</c>, and the RBAC retry gate
    /// <c>/api/pipelines/{tag}/retry</c>. The extended <c>trigger-all</c>/<c>trigger-event</c>
    /// stubs are intentionally excluded (no slash after "trigger").
    /// </summary>
    private static bool ShouldForward(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method)) return false;

        var path = request.Path.Value ?? string.Empty;

        if (path.StartsWith("/api/execution/trigger/", StringComparison.OrdinalIgnoreCase))
            return true;

        if (path.StartsWith("/api/execution/", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith("/cancel", StringComparison.OrdinalIgnoreCase))
            return true;

        if (path.StartsWith("/api/pipelines/", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith("/retry", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static string? ResolveUserId(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? user.FindFirstValue("sub")
        ?? user.Identity?.Name;
}
