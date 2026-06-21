using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using TestController.Api.Contracts;
using TestControllerGrpc.Locking;

namespace TestController.Api.Hubs;

/// <summary>
/// Subscribes to ILockRegistry.OnLockEvent and broadcasts lock state changes
/// to all connected SignalR clients via ControllerHub.
/// Hosted service — registered in any host that owns an ILockRegistry (WPF controller
/// and the standalone WebApi). Must be activated (StartAsync) for the subscription to
/// take effect; registering it as a plain singleton leaves it dormant.
/// Per Pipeline_Lock_Coordination_Spec.md §4.6.
/// </summary>
public sealed class LockBroadcaster : IHostedService, IDisposable
{
    private readonly IHubContext<ControllerHub>? _hub;
    private readonly ILockRegistry _lockRegistry;

    public LockBroadcaster(ILockRegistry lockRegistry, IHubContext<ControllerHub>? hub = null)
    {
        _hub = hub;
        _lockRegistry = lockRegistry;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lockRegistry.OnLockEvent += OnLockEvent;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _lockRegistry.OnLockEvent -= OnLockEvent;
        return Task.CompletedTask;
    }

    private void OnLockEvent(LockEvent evt)
    {
        if (_hub is null) return;
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
