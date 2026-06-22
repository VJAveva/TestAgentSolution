namespace TestControllerGrpc.Services;

/// <summary>
/// Single source of truth for interpreting agent status strings across hosts.
///
/// Several layers produce status text from different origins — the gRPC agent
/// state enum (e.g. <c>AgentStateReady</c>), the event-relay connectivity probe
/// (<c>Connected</c> / <c>Unreachable</c>), and the circuit-breaker health model.
/// This helper collapses all of them into one yes/no "is this agent online?"
/// decision so the WPF fleet, the REST fleet endpoint, and the React client all
/// agree. Mirrors the patterns in <c>src/lib/agentStatus.ts</c>.
/// </summary>
public static class AgentStatusVocabulary
{
    private static readonly string[] OnlineMarkers =
    {
        "connected", "online", "healthy", "ready", "running", "executing",
        "rebooting", "waiting", "busy", "free", "idle", "active",
    };

    private static readonly string[] OfflineMarkers =
    {
        "offline", "unhealthy", "disconnected", "unreachable", "error",
        "circuitopen", "circuit-open", "down", "dead",
    };

    /// <summary>
    /// Returns true when the status string denotes a reachable agent. Offline
    /// markers win over online markers (so "Disconnected" is offline even though
    /// it contains "connected"). Unknown/empty is treated as NOT online.
    /// </summary>
    public static bool IsOnline(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return false;
        var s = status.Trim().ToLowerInvariant();

        foreach (var off in OfflineMarkers)
            if (s.Contains(off)) return false;

        foreach (var on in OnlineMarkers)
            if (s.Contains(on)) return true;

        return false;
    }

    /// <summary>
    /// Canonical display status used by the fleet endpoint: one of
    /// <c>Online</c>, <c>Offline</c>, or <c>Unknown</c>.
    /// </summary>
    public static string Canonical(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return "Unknown";
        return IsOnline(status) ? "Online" : "Offline";
    }
}
