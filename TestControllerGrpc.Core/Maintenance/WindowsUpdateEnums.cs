namespace TestControllerGrpc.Core.Maintenance;

// Windows Update detection vocabulary (Part 2). WindowsUpdateState is a fact about the machine; MaintenanceState is
// a scheduling decision. The policy (UpdatePolicy) is the configurable mapping between them. (Spec: FleetRevert §14.)

/// <summary>What the agent reports about its Windows Update posture.</summary>
public enum WindowsUpdateState
{
    /// <summary>Agent has not reported yet.</summary>
    Unknown = 0,
    UpToDate,
    /// <summary>Downloaded, not installed.</summary>
    UpdatePending,
    UpdateInstalling,
    RebootRequired,
    /// <summary>Inside the post-revert grace window — events recorded but not alarmed.</summary>
    Suppressed,
}

/// <summary>What triggered an agent maintenance event (mirrors the proto enum).</summary>
public enum MaintenanceEventKind
{
    Unspecified = 0,
    UpdatePending,
    UpdateInstalling,
    UpdateInstalled,
    UpdateFailed,
    RebootRequired,
    RebootCleared,
}

/// <summary>Which detection path produced an event (mirrors the proto enum).</summary>
public enum MaintenanceEventSource
{
    Unspecified = 0,
    EventLog,
    RegistryPoll,
    StartupSnapshot,
}
