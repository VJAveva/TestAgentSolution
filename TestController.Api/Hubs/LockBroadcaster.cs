using Microsoft.AspNetCore.SignalR;
using TestController.Api.Contracts;
using TestControllerGrpc.Locking;

namespace TestController.Api.Hubs;

/// <summary>
/// Subscribes to ILockRegistry.OnLockEvent and broadcasts lock state changes
/// to all connected SignalR clients via ControllerHub.
/// Singleton — registered only in the WPF controller host.
/// Per Pipeline_Lock_Coordination_Spec.md §4.6.
/// </summary>
public sealed class LockBroadcaster : IDisposable
{
    private readonly IHubContext<ControllerHub> _hub;
    private readonly ILockRegistry _lockRegistry;

    public LockBroadcaster(IHubContext<ControllerHub> hub, ILockRegistry lockRegistry)
    {
        _hub = hub;
        _lockRegistry = lockRegistry;
        _lockRegistry.OnLockEvent += OnLockEvent;
    }

    private void OnLockEvent(LockEvent evt)
    {
        var dto = LockMapper.ToDto(evt.Lock);
        var priorOwnerName = evt.PriorOwner?.DisplayName;

        // Fire-and-forget: broadcast to all connected clients
        _ = evt.EventKind switch
        {
            LockEventKind.Acquired => _hub.Clients.All.SendAsync("PipelineLockAcquired", dto),
            LockEventKind.Released => _hub.Clients.All.SendAsync("PipelineLockReleased", dto),
            LockEventKind.Expired => _hub.Clients.All.SendAsync("PipelineLockExpired", dto),
            LockEventKind.ForceReleased => _hub.Clients.All.SendAsync("PipelineLockForceReleased", new { Lock = dto, PriorOwnerDisplayName = priorOwnerName }),
            LockEventKind.Rewritten => _hub.Clients.All.SendAsync("PipelineLockRewritten", new { Lock = dto, PriorOwnerDisplayName = priorOwnerName }),
            _ => Task.CompletedTask,
        };
    }

    public void Dispose()
    {
        _lockRegistry.OnLockEvent -= OnLockEvent;
    }
}
