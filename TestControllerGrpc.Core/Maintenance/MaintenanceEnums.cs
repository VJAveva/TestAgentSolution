namespace TestControllerGrpc.Core.Maintenance;

// Shared vocabulary for fleet maintenance (revert / reboot / prep / build install). These enums are the
// single source of truth the dispatch gate, the operation state machine, and every UI surface render from —
// never derive a UI chip or an eligibility decision by parsing log text. (Spec: FleetRevert §3–§5.)

/// <summary>Scheduling decision for a node — a fact the dispatch predicate reads, not a UI string.</summary>
public enum MaintenanceState
{
    /// <summary>Normal; eligible for dispatch.</summary>
    None = 0,
    /// <summary>Keeps its current run to completion but accepts no new work (Part 2).</summary>
    Draining,
    /// <summary>Snapshot revert in flight.</summary>
    Reverting,
    /// <summary>Reboot requested / awaiting power-on.</summary>
    Rebooting,
    /// <summary>Windows Update activity is holding the node out (Part 2).</summary>
    Updating,
    /// <summary>Failed maintenance; held out of rotation until an operator clears it.</summary>
    Quarantined,
}

/// <summary>
/// Phases of a maintenance operation, in execution order. UI phase chips render this directly.
/// The first eight are the revert sequence; the remainder extend it for a golden-image refresh, which reuses
/// Precheck/Quarantine/SnapshotRevert/PowerOn/PingWait/AgentWait/Verify and adds its own steps.
/// APPEND ONLY — this enum is persisted in the operation history.
/// </summary>
public enum RevertPhase
{
    Precheck,
    Quarantine,
    SnapshotRevert,
    PowerOn,
    PingWait,
    AgentWait,
    PostPrep,
    Verify,

    /// <summary>Online search for applicable updates (the detector's cached search is not enough).</summary>
    SearchUpdates,
    InstallUpdates,
    /// <summary>Reboot after installing, then wait for the node to come back.</summary>
    RebootWait,
    /// <summary>Power off so the new baseline is captured from a quiescent disk.</summary>
    PowerOff,
    /// <summary>The irreversible step: replace the golden-image snapshot.</summary>
    ReplaceBaseline,
    /// <summary>Power back on and confirm the node returns to rotation.</summary>
    FinalPowerOn,
}

/// <summary>Lifecycle state of a maintenance operation row.</summary>
public enum MaintenanceOperationState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>Which front door started the operation — the only thing that differs between callers, and exactly
/// what the history table needs to distinguish a panel revert from a plan-triggered one.</summary>
public enum MaintenanceTriggerSource
{
    FleetPanel,
    PlanAction,
    Autopilot,
    WebApi,
}

/// <summary>The kind of maintenance an operation performs. APPEND ONLY — persisted in the history table.</summary>
public enum MaintenanceKind
{
    Revert,
    Reboot,
    Prep,
    InstallBuild,

    /// <summary>Install pending Windows updates on a node, without touching its snapshot.</summary>
    InstallUpdates,

    /// <summary>Revert to baseline, patch, verify, then replace the baseline snapshot.</summary>
    GoldenImageRefresh,
}

/// <summary>Which stream a captured script output line came from.</summary>
public enum ScriptStream
{
    Stdout,
    Stderr,
}
