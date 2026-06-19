using TestController.WebApi.Services;

namespace TestController.WebApi.Endpoints;

/// <summary>
/// REST proxy endpoints for authentication. The standalone WebApi has no database or
/// session store — all auth operations are forwarded to the WPF controller via
/// <see cref="ControllerProxyService"/>. The shared <c>AuthController</c> is excluded on
/// this host (see <see cref="ProxiedControllerExclusionProvider"/>) because its
/// <c>AuthService</c> resolves to NullSessionStore here and would 500.
/// </summary>
public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/auth/me", GetMe).AllowAnonymous();
        group.MapPost("/auth/login", Login).AllowAnonymous();
        group.MapPost("/auth/guest", LoginAsGuest).AllowAnonymous();
        group.MapPost("/auth/logout", Logout).AllowAnonymous();
        group.MapPost("/auth/change-password", ChangePassword).AllowAnonymous();
        return group;
    }

    private static Task<IResult> GetMe(ControllerProxyService proxy, HttpContext httpContext) =>
        ForwardGet(proxy, httpContext, "/api/auth/me");

    private static Task<IResult> Login(ControllerProxyService proxy, HttpContext httpContext) =>
        ForwardWithBody(proxy, httpContext, HttpMethod.Post, "/api/auth/login");

    private static Task<IResult> LoginAsGuest(ControllerProxyService proxy, HttpContext httpContext) =>
        ForwardWithBody(proxy, httpContext, HttpMethod.Post, "/api/auth/guest");

    private static Task<IResult> Logout(ControllerProxyService proxy, HttpContext httpContext) =>
        ForwardWithBody(proxy, httpContext, HttpMethod.Post, "/api/auth/logout");

    private static Task<IResult> ChangePassword(ControllerProxyService proxy, HttpContext httpContext) =>
        ForwardWithBody(proxy, httpContext, HttpMethod.Post, "/api/auth/change-password");

    private static async Task<IResult> ForwardGet(
        ControllerProxyService proxy, HttpContext httpContext, string path)
    {
        if (!proxy.IsConfigured)
            return Results.StatusCode(502);

        var response = await proxy.ForwardGetAsync(path, GetAuthHeader(httpContext));
        if (response is null)
            return Results.StatusCode(502);

        var content = await response.Content.ReadAsStringAsync();
        return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
    }

    private static async Task<IResult> ForwardWithBody(
        ControllerProxyService proxy, HttpContext httpContext, HttpMethod method, string path)
    {
        if (!proxy.IsConfigured)
            return Results.StatusCode(502);

        string? body = null;
        if (httpContext.Request.ContentLength is > 0 || httpContext.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            using var reader = new StreamReader(httpContext.Request.Body);
            body = await reader.ReadToEndAsync();
        }

        // StringContent rejects a media type that carries a charset; use the bare media type.
        var mediaType = httpContext.Request.ContentType?.Split(';')[0].Trim();
        if (string.IsNullOrEmpty(mediaType))
            mediaType = "application/json";

        var response = await proxy.ForwardWithBodyAsync(
            method, path, GetAuthHeader(httpContext), body, mediaType);
        if (response is null)
            return Results.StatusCode(502);

        var content = await response.Content.ReadAsStringAsync();
        return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
    }

    private static string? GetAuthHeader(HttpContext context) =>
        context.Request.Headers["Authorization"].FirstOrDefault();
}
