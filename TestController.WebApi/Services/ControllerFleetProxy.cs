using TestController.Api.Services;

namespace TestController.WebApi.Services;

/// <summary>
/// WebApi-host implementation of <see cref="IControllerFleetProxy"/>. Forwards the
/// fleet request to the co-located WPF controller's identical <c>/api/agents/fleet</c>
/// endpoint via the existing <see cref="ControllerProxyService"/>, so the WebClient
/// fleet reflects the controller's authoritative agent-lock state.
/// </summary>
public sealed class ControllerFleetProxy : IControllerFleetProxy
{
    private readonly ControllerProxyService _proxy;

    public ControllerFleetProxy(ControllerProxyService proxy) => _proxy = proxy;

    public bool IsConfigured => _proxy.IsConfigured;

    public async Task<string?> GetFleetJsonAsync(string? authorizationHeader, CancellationToken ct = default)
    {
        using var response = await _proxy.ForwardGetAsync("/api/agents/fleet", authorizationHeader);
        if (response is null || !response.IsSuccessStatusCode)
            return null;
        return await response.Content.ReadAsStringAsync(ct);
    }
}
