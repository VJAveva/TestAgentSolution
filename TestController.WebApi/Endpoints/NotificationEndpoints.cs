using TestController.WebApi.Services;

namespace TestController.WebApi.Endpoints;

/// <summary>
/// REST proxy endpoints for notification mute management. The standalone WebApi has no database,
/// so <c>MuteService</c> is only registered on the primary host — the shared
/// <c>NotificationsController</c> is excluded here (see <see cref="ProxiedControllerExclusionProvider"/>)
/// because it cannot be activated without it.
/// </summary>
public static class NotificationEndpoints
{
    public static RouteGroupBuilder MapNotificationEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/mutes", ListMutes);
        group.MapPost("/mutes", Mute);
        group.MapDelete("/mutes/{id:long}", Unmute);
        return group;
    }

    private static async Task<IResult> ListMutes(ControllerProxyService proxy, HttpContext httpContext)
    {
        if (!proxy.IsConfigured)
            return Results.Json(Array.Empty<object>());

        var response = await proxy.ForwardGetAsync("/api/notifications/mutes", GetAuthHeader(httpContext));
        return await Relay(response);
    }

    private static async Task<IResult> Mute(ControllerProxyService proxy, HttpContext httpContext)
    {
        if (!proxy.IsConfigured)
            return Results.StatusCode(502);

        using var reader = new StreamReader(httpContext.Request.Body);
        var body = await reader.ReadToEndAsync();

        var response = await proxy.ForwardWithBodyAsync(
            HttpMethod.Post, "/api/notifications/mutes", GetAuthHeader(httpContext), body, "application/json");
        return await Relay(response);
    }

    private static async Task<IResult> Unmute(long id, ControllerProxyService proxy, HttpContext httpContext)
    {
        if (!proxy.IsConfigured)
            return Results.StatusCode(502);

        var response = await proxy.ForwardDeleteAsync($"/api/notifications/mutes/{id}", GetAuthHeader(httpContext));
        return await Relay(response);
    }

    private static async Task<IResult> Relay(HttpResponseMessage? response)
    {
        if (response is null)
            return Results.StatusCode(502);

        var content = await response.Content.ReadAsStringAsync();
        return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
    }

    private static string? GetAuthHeader(HttpContext context) =>
        context.Request.Headers["Authorization"].FirstOrDefault();
}
