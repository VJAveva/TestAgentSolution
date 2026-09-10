using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Ado;
using TestControllerGrpc.Services;

namespace TestController.Api.Security;

/// <summary>
/// Lifts the caller's delegated Azure DevOps token off the request so the singleton ADO stack can act as the
/// signed-in web user for the duration of that request. The header is refused on a plaintext connection: it is
/// a bearer credential for the whole ADO organisation, and forwarding it over HTTP exposes it to anyone on the
/// network path.
/// </summary>
public sealed class AdoUserTokenMiddleware
{
    public const string HeaderName = "X-Ado-Token";

    private readonly RequestDelegate _next;
    private readonly AdoOptions _options;
    private readonly IAppLogger _logger;
    private int _insecureWarned;

    public AdoUserTokenMiddleware(RequestDelegate next, IOptions<AdoOptions> options, IAppLogger logger)
    {
        _next = next;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IAdoUserTokenAccessor accessor)
    {
        accessor.Token = Extract(context);
        try
        {
            await _next(context);
        }
        finally
        {
            accessor.Token = null;
        }
    }

    private string? Extract(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(HeaderName, out var values))
            return null;

        string token = values.ToString().Trim();
        if (token.Length == 0)
            return null;

        if (!context.Request.IsHttps && _options.RequireSecureUserToken)
        {
            if (Interlocked.Exchange(ref _insecureWarned, 1) == 0)
            {
                _logger.Warn("Ado",
                    $"Ignoring {HeaderName} on a plaintext connection - a delegated ADO token must not cross HTTP. " +
                    "Serve the site over HTTPS, or set Ado:RequireSecureUserToken=false to accept the risk.");
            }
            return null;
        }

        return token;
    }
}
