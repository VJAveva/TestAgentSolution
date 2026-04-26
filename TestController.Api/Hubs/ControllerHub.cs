using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace TestController.Api.Hubs;

/// <summary>
/// Shared SignalR hub for real-time communication with browser clients.
/// Used by both the WPF-hosted Kestrel server and the standalone WebApi.
/// Server pushes events; clients can join/leave session groups.
/// </summary>
public sealed class ControllerHub : Hub
{
    private readonly ILogger<ControllerHub> _logger;

    public ControllerHub(ILogger<ControllerHub> logger)
    {
        _logger = logger;
    }

    /// <summary>Subscribe to events for a specific session.</summary>
    public async Task JoinSession(string sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"session:{sessionId}");
        _logger.LogDebug("[SignalR] {ConnectionId} joined session {SessionId}", Context.ConnectionId, sessionId);
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

    /// <summary>Subscribe to events for a specific user across all their sessions.</summary>
    public async Task JoinAsUser(string userId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{userId}");
        _logger.LogDebug("[SignalR] {ConnectionId} joined user group {UserId}", Context.ConnectionId, userId);
    }

    /// <summary>Explicitly join the global group (auto-joined on connect, useful after reconnect).</summary>
    public async Task JoinGlobal()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "global");
    }

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "global");
        var remoteIp = Context.GetHttpContext()?.Connection.RemoteIpAddress;
        _logger.LogInformation("[SignalR] Client connected: {ConnectionId} from {RemoteIp}",
            Context.ConnectionId, remoteIp);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("[SignalR] Client disconnected: {ConnectionId}. Reason: {Reason}",
            Context.ConnectionId, exception?.Message ?? "clean");
        await base.OnDisconnectedAsync(exception);
    }
}
