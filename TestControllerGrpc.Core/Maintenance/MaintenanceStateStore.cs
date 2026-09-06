using System.Collections.Concurrent;

namespace TestControllerGrpc.Core.Maintenance;

/// <summary>
/// In-memory authority for each node's <see cref="MaintenanceState"/>. Singleton; the revert engine writes it and
/// everything that schedules work (dispatch gate, liveness monitor, UI) reads it. Absent nodes are
/// <see cref="MaintenanceState.None"/>; setting a node back to None removes it. (Spec: FleetRevert §3–§4.)
/// </summary>
public sealed class MaintenanceStateStore : IMaintenanceStateStore
{
    private readonly ConcurrentDictionary<string, MaintenanceState> _states = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<NodeMaintenanceStateChanged>? Changed;

    public MaintenanceState Get(string nodeId)
        => _states.TryGetValue(nodeId, out var state) ? state : MaintenanceState.None;

    public void Set(string nodeId, MaintenanceState state)
    {
        var previous = Get(nodeId);

        if (state == MaintenanceState.None)
            _states.TryRemove(nodeId, out _);
        else
            _states[nodeId] = state;

        if (previous != state)
            Changed?.Invoke(this, new NodeMaintenanceStateChanged(nodeId, previous, state));
    }

    public IReadOnlyDictionary<string, MaintenanceState> Snapshot()
        => new Dictionary<string, MaintenanceState>(_states, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Thrown when a second maintenance operation is requested for a node that already has one in flight.</summary>
public sealed class MaintenanceInProgressException : Exception
{
    public string NodeId { get; }
    public Guid ExistingOperationId { get; }

    public MaintenanceInProgressException(string nodeId, Guid existingOperationId)
        : base($"A maintenance operation ({existingOperationId}) is already in progress for node '{nodeId}'.")
    {
        NodeId = nodeId;
        ExistingOperationId = existingOperationId;
    }
}
