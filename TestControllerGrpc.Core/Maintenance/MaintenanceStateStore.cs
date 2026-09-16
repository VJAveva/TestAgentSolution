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
    // Serialises writers only; Get stays lock-free because the dispatch gate reads it on every dispatch.
    private readonly object _writeGate = new();

    public event EventHandler<NodeMaintenanceStateChanged>? Changed;

    public MaintenanceState Get(string nodeId)
        => _states.TryGetValue(nodeId, out var state) ? state : MaintenanceState.None;

    public void Set(string nodeId, MaintenanceState state)
    {
        MaintenanceState previous;

        // Read-modify-write must be atomic: this is the authority the dispatch gate consults, so racing
        // writers could otherwise both observe the old state and both act on it.
        lock (_writeGate)
        {
            previous = Get(nodeId);
            if (previous == state) return;

            if (state == MaintenanceState.None)
                _states.TryRemove(nodeId, out _);
            else
                _states[nodeId] = state;
        }

        // Raised outside the lock — handlers re-enter the store (the coordinator writes state from Changed).
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
