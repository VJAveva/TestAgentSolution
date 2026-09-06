using Microsoft.Extensions.Hosting;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Maintenance;

/// <summary>
/// Host-agnostic wiring for <see cref="UpdatePostureCoordinator"/>: starts posture evaluation, drains nodes to their
/// blocking state when their last run finishes, and opens the suppression window after a maintenance operation.
/// Registered by both the WPF controller and the standalone WebApi (spec §14–§16, Prompt 16/17).
/// </summary>
public sealed class UpdatePostureHostedService : IHostedService, IDisposable
{
    private readonly UpdatePostureCoordinator _coordinator;
    private readonly IMaintenanceStateStore _maintenance;
    private readonly IEventAggregator _events;
    private readonly IFleetMaintenanceService? _fleetMaintenance;
    private IDisposable? _lockSubscription;

    public UpdatePostureHostedService(
        UpdatePostureCoordinator coordinator,
        IMaintenanceStateStore maintenance,
        IEventAggregator events,
        IFleetMaintenanceService? fleetMaintenance = null)
    {
        _coordinator = coordinator;
        _maintenance = maintenance;
        _events = events;
        _fleetMaintenance = fleetMaintenance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _coordinator.FleetMaintenance = _fleetMaintenance;
        _coordinator.Start();
        _lockSubscription = _events.Subscribe<AgentLocksChangedEvent>(OnLocksChanged);
        if (_fleetMaintenance is not null)
            _fleetMaintenance.OperationCompleted += OnMaintenanceCompleted;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _lockSubscription?.Dispose();
        _lockSubscription = null;
        if (_fleetMaintenance is not null)
            _fleetMaintenance.OperationCompleted -= OnMaintenanceCompleted;
        _coordinator.Dispose();
    }

    // A draining node is "idle" once it no longer holds a lock — that is the last run finishing.
    private void OnLocksChanged(AgentLocksChangedEvent e)
    {
        var locked = e.Locks.Select(l => l.AgentName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (nodeId, state) in _maintenance.Snapshot())
        {
            if (state == MaintenanceState.Draining && !locked.Contains(nodeId))
                _coordinator.OnNodeIdle(nodeId);
        }
    }

    private void OnMaintenanceCompleted(object? sender, MaintenanceOperation op)
        => _coordinator.OnMaintenanceCompleted(op.NodeId);
}
