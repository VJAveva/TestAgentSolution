using Microsoft.AspNetCore.SignalR;

namespace TestController.Api.Hubs;

/// <summary>
/// Broadcasts SystemModeChanged events to all connected SignalR clients.
/// Singleton — injected into controllers and gRPC services that perform mode transitions.
/// </summary>
public sealed class SystemModeBroadcaster
{
    private readonly IHubContext<ControllerHub>? _hub;

    public SystemModeBroadcaster(IHubContext<ControllerHub>? hub = null)
    {
        _hub = hub;
    }

    /// <summary>
    /// Notify all clients that the system mode has changed.
    /// Clients should reload their shell or navigate to the appropriate view.
    /// </summary>
    public async Task BroadcastModeChangedAsync(string newMode)
    {
        if (_hub is null) return;
        await _hub.Clients.All.SendAsync("SystemModeChanged", new { mode = newMode });
    }
}
