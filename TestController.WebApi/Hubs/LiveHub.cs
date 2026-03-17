using Microsoft.AspNetCore.SignalR;

namespace TestController.WebApi.Hubs;

/// <summary>
/// SignalR hub that pushes real-time events to connected browser clients.
/// 
/// Client methods (browser receives):
///   "ExecutionLog"    — log entry from pipeline
///   "AgentStatus"     — agent state change  (agentName, status)
///   "NodeProgress"    — tree node status update (tag, status: Running/Success/Failed)
///   "ExecutionEvent"  — stdout/stderr line from agent
///   "TriggerFired"    — file trigger activated
/// </summary>
public sealed class LiveHub : Hub
{
    private readonly ILogger<LiveHub> _logger;

    public LiveHub(ILogger<LiveHub> logger) => _logger = logger;

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("SignalR client connected: {ConnectionId}", Context.ConnectionId);
        await Clients.Caller.SendAsync("Connected", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("SignalR client disconnected: {ConnectionId}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
