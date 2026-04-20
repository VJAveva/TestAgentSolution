using Microsoft.AspNetCore.SignalR;

namespace TestControllerGrpc.Hubs;

/// <summary>
/// SignalR hub for real-time communication with the React WebClient.
/// Server pushes events; clients can join/leave session groups.
/// </summary>
public sealed class ControllerHub : Hub
{
    /// <summary>Subscribe to events for a specific session.</summary>
    public async Task JoinSession(string sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"session:{sessionId}");
    }

    /// <summary>Unsubscribe from a specific session.</summary>
    public async Task LeaveSession(string sessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"session:{sessionId}");
    }

    /// <summary>Subscribe to all execution events.</summary>
    public async Task JoinAllSessions()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "all-sessions");
    }

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "global");
        await base.OnConnectedAsync();
    }
}
