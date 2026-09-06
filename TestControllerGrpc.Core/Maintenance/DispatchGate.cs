using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Maintenance;

/// <summary>Scheduling decision for a node.</summary>
public enum DispatchEligibility
{
    Eligible,
    Draining,
    Blocked,
}

/// <summary>One node that cannot take work, with the human-readable reason (Reverting / Quarantined / Busy / …).</summary>
public sealed record FleetBlock(string NodeId, string Reason);

/// <summary>
/// Returned when a dispatch request finds no dispatchable node — the typed alternative to a bare 409. Names each
/// blocking node and why, so the caller (and a future run queue) can act on it. (Spec: FleetRevert §3, Prompt 11.)
/// </summary>
public sealed record FleetUnavailableResult(IReadOnlyList<FleetBlock> BlockedNodes);

/// <summary>
/// The single source of the dispatch-eligibility rule. Every place that chooses a node for work evaluates it here —
/// no second copy of the condition. A node is dispatchable only when connected, not under maintenance, and not busy.
/// </summary>
public static class DispatchGate
{
    public static DispatchEligibility Evaluate(
        IMaintenanceStateStore maintenance, AgentLockManager locks, string nodeId, bool isConnected)
    {
        if (!isConnected)
            return DispatchEligibility.Blocked;

        var state = maintenance.Get(nodeId);
        if (state is MaintenanceState.Reverting or MaintenanceState.Rebooting
            or MaintenanceState.Updating or MaintenanceState.Quarantined)
            return DispatchEligibility.Blocked;

        // Draining keeps its current run but takes no new work.
        if (state == MaintenanceState.Draining)
            return DispatchEligibility.Draining;

        if (locks.GetLock(nodeId) is not null)
            return DispatchEligibility.Blocked;

        return DispatchEligibility.Eligible;
    }

    public static bool IsDispatchable(
        IMaintenanceStateStore maintenance, AgentLockManager locks, string nodeId, bool isConnected)
        => Evaluate(maintenance, locks, nodeId, isConnected) == DispatchEligibility.Eligible;

    /// <summary>
    /// The pre-lock gate: returns the required nodes currently held out for maintenance, or null when none are.
    /// Busy nodes are left to the lock manager's own conflict path; this covers the maintenance states that path
    /// cannot see (Reverting / Rebooting / Updating / Quarantined).
    /// </summary>
    public static FleetUnavailableResult? GetMaintenanceBlockers(
        IMaintenanceStateStore maintenance, IEnumerable<string> requiredAgents)
    {
        List<FleetBlock>? blocks = null;
        foreach (var node in requiredAgents)
        {
            var state = maintenance.Get(node);
            if (state != MaintenanceState.None)
                (blocks ??= []).Add(new FleetBlock(node, state.ToString()));
        }

        return blocks is null ? null : new FleetUnavailableResult(blocks);
    }
}
