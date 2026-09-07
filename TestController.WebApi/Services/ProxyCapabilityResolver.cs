using System.Text.Json;
using TestController.Api.Security;

namespace TestController.WebApi.Services;

/// <summary>
/// Resolves the caller's capabilities by forwarding <c>GET /api/auth/me</c> to the WPF controller, which owns
/// the session store. Without this the WebApi cannot identify a bearer token at all, so its locally-hosted
/// permission-gated endpoints (Code Churn, Report Card) would reject every caller in Secured mode.
/// </summary>
public sealed class ProxyCapabilityResolver : IRemoteCapabilityResolver
{
    private readonly ControllerProxyService _proxy;
    private readonly ILogger<ProxyCapabilityResolver> _logger;

    public ProxyCapabilityResolver(ControllerProxyService proxy, ILogger<ProxyCapabilityResolver> logger)
    {
        _proxy = proxy;
        _logger = logger;
    }

    public async Task<IReadOnlySet<string>?> GetCapabilitiesAsync(string? authorizationHeader, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
            return null;

        try
        {
            using HttpResponseMessage? response = await _proxy.ForwardGetAsync("/api/auth/me", authorizationHeader);
            if (response is null || !response.IsSuccessStatusCode)
                return null;

            await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("capabilities", out JsonElement caps) ||
                caps.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement capability in caps.EnumerateArray())
            {
                if (capability.GetString() is { Length: > 0 } name)
                    set.Add(name);
            }
            return set;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deny rather than fail open when the primary host cannot be reached.
            _logger.LogWarning(ex, "Capability lookup via the controller failed.");
            return null;
        }
    }
}
