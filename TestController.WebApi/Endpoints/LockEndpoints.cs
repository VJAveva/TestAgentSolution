using System.Net.Http.Json;
using System.Text.Json;
using TestController.Api.Contracts;
using TestController.WebApi.Services;

namespace TestController.WebApi.Endpoints;

/// <summary>
/// REST proxy endpoints for pipeline lock operations. The standalone WebApi does NOT
/// host a LockRegistry — all operations are forwarded to the WPF controller via ControllerProxyService.
/// Per phase-3a-context.md "Watch out for" #1.
/// </summary>
public static class LockEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static RouteGroupBuilder MapLockEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/locks", ListLocks);
        group.MapGet("/locks/{pipelineId}", GetLock);
        group.MapDelete("/locks/{pipelineId}", ReleaseLock);
        group.MapPost("/locks/{pipelineId}/force-release", ForceRelease);
        return group;
    }

    private static async Task<IResult> ListLocks(
        ControllerProxyService proxy,
        HttpContext httpContext)
    {
        if (!proxy.IsConfigured)
            return Results.Json(Array.Empty<PipelineLockDto>());

        var response = await proxy.ForwardGetAsync("/api/locks", GetAuthHeader(httpContext));
        if (response is null)
            return Results.StatusCode(502);

        var content = await response.Content.ReadAsStringAsync();
        return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
    }

    private static async Task<IResult> GetLock(
        string pipelineId,
        ControllerProxyService proxy,
        HttpContext httpContext)
    {
        if (!proxy.IsConfigured)
            return Results.NotFound();

        var response = await proxy.ForwardGetAsync(
            $"/api/locks/{Uri.EscapeDataString(pipelineId)}", GetAuthHeader(httpContext));
        if (response is null)
            return Results.StatusCode(502);

        if (!response.IsSuccessStatusCode)
            return Results.StatusCode((int)response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        return Results.Content(content, "application/json");
    }

    private static async Task<IResult> ReleaseLock(
        string pipelineId,
        ControllerProxyService proxy,
        HttpContext httpContext)
    {
        if (!proxy.IsConfigured)
            return Results.StatusCode(502);

        var response = await proxy.ForwardDeleteAsync(
            $"/api/locks/{Uri.EscapeDataString(pipelineId)}", GetAuthHeader(httpContext));
        if (response is null)
            return Results.StatusCode(502);

        var content = await response.Content.ReadAsStringAsync();
        return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
    }

    private static async Task<IResult> ForceRelease(
        string pipelineId,
        HttpContext httpContext,
        ControllerProxyService proxy)
    {
        if (!proxy.IsConfigured)
            return Results.StatusCode(502);

        var response = await proxy.ForwardPostAsync(
            $"/api/locks/{Uri.EscapeDataString(pipelineId)}/force-release",
            GetAuthHeader(httpContext));
        if (response is null)
            return Results.StatusCode(502);

        var content = await response.Content.ReadAsStringAsync();
        var statusCode = (int)response.StatusCode;

        // Mirror 409 conflict response from controller
        return Results.Content(content, "application/json", statusCode: statusCode);
    }

    private static string? GetAuthHeader(HttpContext context) =>
        context.Request.Headers["Authorization"].FirstOrDefault();
}
