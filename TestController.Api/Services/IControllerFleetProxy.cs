namespace TestController.Api.Services;

/// <summary>
/// Optional seam that lets the shared <c>AgentsController</c> defer the fleet
/// snapshot to a co-located WPF controller. Implemented and registered only by
/// the standalone WebApi host (which proxies execution to the controller); the
/// WPF controller host does not register it, so the controller builds the fleet
/// from its own authoritative registry + agent-lock state.
///
/// This keeps the WebClient fleet view consistent with the WPF fleet: when the
/// WebApi forwards runs to the controller, the controller holds the real agent
/// locks, so the fleet must come from the controller too.
/// </summary>
public interface IControllerFleetProxy
{
    /// <summary>True when a controller proxy URL is configured (co-located host).</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Fetches the controller's <c>/api/agents/fleet</c> JSON verbatim, forwarding
    /// the caller's Authorization header. Returns null when not configured or the
    /// controller is unreachable, so the caller can fall back to a local snapshot.
    /// </summary>
    Task<string?> GetFleetJsonAsync(string? authorizationHeader, CancellationToken ct = default);
}
