using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using TestController.Api.Hubs;
using TestControllerGrpc.Core.Maintenance;

namespace TestController.Api.Services;

/// <summary>
/// Projects the maintenance engine's events onto the SignalR hub as <c>maintenanceStarted</c> /
/// <c>maintenanceProgress</c> / <c>maintenanceCompleted</c>, so the WebClient can watch reverts and reboots live.
/// This is the SignalR counterpart to the WPF host's dispatcher-based projection. No-ops when the maintenance stack
/// is absent (the standalone proxy host). (Spec: FleetRevert §7, Prompt 12.)
/// </summary>
public sealed class MaintenanceHubBridge : IHostedService
{
    private readonly IHubContext<ControllerHub> _hub;
    private readonly IFleetMaintenanceService? _service;
    private readonly ConcurrentDictionary<Guid, byte> _announced = new();

    public MaintenanceHubBridge(IHubContext<ControllerHub> hub, IFleetMaintenanceService? service = null)
    {
        _hub = hub;
        _service = service;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_service is not null)
        {
            _service.ProgressChanged += OnProgress;
            _service.OperationCompleted += OnCompleted;
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_service is not null)
        {
            _service.ProgressChanged -= OnProgress;
            _service.OperationCompleted -= OnCompleted;
        }
        return Task.CompletedTask;
    }

    private void OnProgress(object? sender, MaintenanceProgress p)
    {
        // First progress for an operation doubles as its "started" signal.
        if (_announced.TryAdd(p.OperationId, 0))
            _ = _hub.Clients.All.SendAsync("maintenanceStarted", new { operationId = p.OperationId, nodeId = p.NodeId });

        _ = _hub.Clients.All.SendAsync("maintenanceProgress", new
        {
            operationId = p.OperationId,
            nodeId = p.NodeId,
            phase = p.Phase.ToString(),
            stepNumber = p.StepNumber,
            stepCount = p.StepCount,
            message = p.Message,
            elapsedSeconds = p.Elapsed.TotalSeconds,
        });
    }

    private void OnCompleted(object? sender, MaintenanceOperation op)
    {
        _announced.TryRemove(op.Id, out _);
        _ = _hub.Clients.All.SendAsync("maintenanceCompleted", new
        {
            operationId = op.Id,
            nodeId = op.NodeId,
            kind = op.Kind.ToString(),
            state = op.State.ToString(),
            failurePhase = op.FailurePhase?.ToString(),
        });
    }
}
