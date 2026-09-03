using Microsoft.AspNetCore.SignalR;

namespace TestController.WebApi.Hubs;

/// <summary>
/// SignalR hub that streams <c>ImpactMappingProgress</c> to clients (P29). Progress is broadcast to a group
/// named by the client-supplied correlation id, so a caller sees only its own run's ticks. The engine's
/// progress type is a Core contract; no SignalR type ever reaches Core — the adapter lives here in the host.
/// </summary>
public sealed class ImpactProgressHub : Hub
{
    /// <summary>Subscribes the caller to a run's progress group.</summary>
    public Task JoinRun(string correlationId) => Groups.AddToGroupAsync(Context.ConnectionId, correlationId);

    /// <summary>Unsubscribes the caller from a run's progress group.</summary>
    public Task LeaveRun(string correlationId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, correlationId);
}
