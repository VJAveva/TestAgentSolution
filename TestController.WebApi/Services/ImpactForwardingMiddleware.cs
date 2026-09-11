using TestControllerGrpc.Services;

namespace TestController.WebApi.Services;

/// <summary>
/// Serves Code Churn reads from the WPF controller instead of this host's own Azure DevOps credential.
///
/// In a co-located deployment the controller is signed in interactively (delegated Entra) and can reach ADO,
/// while this host depends on a PAT or service principal. When that credential is unusable - an expired PAT, or
/// an organisation that blocks token-based auth outright - every Code Churn read fails here but succeeds two
/// ports away. Forwarding lets the web client work off the controller's session instead of needing its own.
///
/// Opt-in via <c>Ado:ForwardImpactToController</c>, because it makes Code Churn depend on the controller being
/// up. Health and the SPA auth config are never forwarded: they must describe THIS host, or diagnostics would
/// report the controller's state while the web tier silently fails.
/// </summary>
public sealed class ImpactForwardingMiddleware
{
    private const string Prefix = "/api/impact/";

    // Must answer for the local host: health is the deploy sanity check, ado-auth-config drives the SPA's own
    // sign-in and carries this connection's secureTransport flag.
    private static readonly string[] NeverForward =
    [
        "/api/impact/health",
        "/api/impact/ado-auth-config",
    ];

    private readonly RequestDelegate _next;

    public ImpactForwardingMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ControllerProxyService proxy, IAppLogger logger)
    {
        if (!proxy.IsConfigured || !ShouldForward(context.Request))
        {
            await _next(context);
            return;
        }

        string pathAndQuery = context.Request.Path + context.Request.QueryString;
        string? authHeader = context.Request.Headers.Authorization.FirstOrDefault();

        // The controller re-authorises with the same bearer token, so CodeChurn_View is still enforced there.
        using HttpResponseMessage? response = await proxy.ForwardGetAsync(pathAndQuery, authHeader);

        if (response is null)
        {
            logger.Warn("ImpactForward", $"Controller unreachable forwarding GET {pathAndQuery}.");
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                "{\"error\":\"controller-unreachable\",\"detail\":\"Code Churn is served by the controller node, which is not reachable. Start TestControllerGrpc.exe on this machine.\"}");
            return;
        }

        context.Response.StatusCode = (int)response.StatusCode;
        string? contentType = response.Content.Headers.ContentType?.ToString();
        if (!string.IsNullOrEmpty(contentType))
            context.Response.ContentType = contentType;

        byte[] payload = await response.Content.ReadAsByteArrayAsync();
        if (payload.Length > 0)
            await context.Response.Body.WriteAsync(payload);
    }

    internal static bool ShouldForward(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method))
            return false;

        string path = request.Path.Value ?? string.Empty;
        return path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            && !NeverForward.Contains(path, StringComparer.OrdinalIgnoreCase);
    }
}
