namespace TestControllerGrpc.Core.Maintenance;

// Immutable data shapes passed across the maintenance seam. Records are init-only so a caller cannot mutate an
// in-flight operation; the engine advances state with `with` expressions. (Spec: FleetRevert §5–§6.)

/// <summary>Everything needed to revert one node. Built by whichever front door invokes the engine.</summary>
public sealed record RevertRequest
{
    /// <summary>Registry key of the node to revert (agent name).</summary>
    public required string NodeId { get; init; }

    /// <summary>Snapshot to roll the VM back to.</summary>
    public required string SnapshotName { get; init; }

    /// <summary>Override the configured revert script path; null uses the default.</summary>
    public string? ScriptPath { get; init; }

    /// <summary>Block until the agent re-registers after power-on.</summary>
    public bool WaitForAgent { get; init; } = true;

    /// <summary>Run the post-revert prep script (Prepare-AgentNode.ps1).</summary>
    public bool RunPrep { get; init; } = true;

    /// <summary>Install the current build after prep (Install-Build.bat).</summary>
    public bool InstallBuild { get; init; }

    /// <summary>Proceed even if the node currently holds an active session (operator override).</summary>
    public bool ForceIfBusy { get; init; }

    /// <summary>How long to wait for the agent to come back before failing the AgentWait phase.</summary>
    public TimeSpan AgentWaitTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Which front door started this (fleet panel, plan action, autopilot, web API).</summary>
    public required MaintenanceTriggerSource TriggerSource { get; init; }

    /// <summary>User identity that requested the revert, when known.</summary>
    public string? TriggeredBy { get; init; }

    /// <summary>Free-text reason for audit.</summary>
    public string? Reason { get; init; }

    /// <summary>Execution session this revert is linked to, when triggered by a plan action.</summary>
    public Guid? LinkedRunId { get; init; }
}

/// <summary>Everything needed to reboot one node. The reboot runs out-of-band via the agent dispatcher.</summary>
public sealed record RebootRequest
{
    public required string NodeId { get; init; }

    /// <summary>Seconds passed to <c>shutdown /r /t</c>.</summary>
    public int RebootDelaySeconds { get; init; } = 5;

    /// <summary>Proceed even if the node holds an active session (operator override).</summary>
    public bool ForceIfBusy { get; init; }

    /// <summary>How long to wait for the agent to reconnect before failing.</summary>
    public TimeSpan AgentWaitTimeout { get; init; } = TimeSpan.FromMinutes(15);

    public required MaintenanceTriggerSource TriggerSource { get; init; }
    public string? TriggeredBy { get; init; }
    public string? Reason { get; init; }
}

/// <summary>A single progress tick emitted through <see cref="IProgress{T}"/> during a revert.</summary>
public sealed record MaintenanceProgress(
    Guid OperationId,
    string NodeId,
    RevertPhase Phase,
    int StepNumber,
    int StepCount,
    string Message,
    TimeSpan Elapsed);

/// <summary>The tracked / persisted record of one maintenance operation. Advanced with `with` as it runs.</summary>
public sealed record MaintenanceOperation
{
    public required Guid Id { get; init; }
    public required string NodeId { get; init; }
    public required MaintenanceKind Kind { get; init; }
    public string? SnapshotName { get; init; }
    public string? ScriptPath { get; init; }
    public MaintenanceOperationState State { get; init; } = MaintenanceOperationState.Queued;
    public RevertPhase Phase { get; init; }
    public required MaintenanceTriggerSource TriggerSource { get; init; }
    public string? TriggeredBy { get; init; }
    public string? Reason { get; init; }
    public Guid? LinkedRunId { get; init; }
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedUtc { get; init; }
    public int? ExitCode { get; init; }

    /// <summary>Phase at which the operation failed, when <see cref="State"/> is <see cref="MaintenanceOperationState.Failed"/>.</summary>
    public RevertPhase? FailurePhase { get; init; }

    /// <summary>Path to the full operation log file; the row carries the path, not the text.</summary>
    public string? LogPath { get; init; }
}

/// <summary>Per-node eligibility snapshot returned by a precheck, driving the confirm dialog (spec R9).</summary>
public sealed record PrecheckResult(IReadOnlyList<NodePrecheck> Nodes);

/// <summary>Eligibility of a single node at precheck time.</summary>
public sealed record NodePrecheck(
    string NodeId,
    bool IsDispatchable,
    MaintenanceState MaintenanceState,
    string? RunningWatchItem,
    DispatchEligibility Eligibility = DispatchEligibility.Eligible);

/// <summary>Inputs for one controller-side PowerShell / batch invocation.</summary>
public sealed record ScriptInvocation
{
    public required string ScriptPath { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }

    /// <summary>Hard timeout for the process; null means no timeout.</summary>
    public TimeSpan? Timeout { get; init; }
}

/// <summary>Outcome of a script invocation.</summary>
public sealed record ScriptResult(int ExitCode, TimeSpan Duration, bool Cancelled);

/// <summary>One captured line of script output, tagged with its stream and capture time.</summary>
public sealed record ScriptOutputLine(DateTimeOffset TimestampUtc, ScriptStream Stream, string Text);

/// <summary>Result of waiting for a node to become reachable / re-register.</summary>
public sealed record ReadinessResult(bool Succeeded, TimeSpan Elapsed, string? FailureReason);

/// <summary>Tuning for the ping / boot-wait probe (spec §4 PingWait: boot delay then N consecutive replies).</summary>
public sealed record PingOptions
{
    /// <summary>Grace period after power-on before pinging starts.</summary>
    public TimeSpan BootDelay { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Delay between ping attempts.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Consecutive successful replies required before the node is considered up.</summary>
    public int RequiredConsecutiveReplies { get; init; } = 3;

    /// <summary>Overall timeout for the ping phase.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>Raised when a node's maintenance state transitions.</summary>
public sealed record NodeMaintenanceStateChanged(string NodeId, MaintenanceState Previous, MaintenanceState Current);

/// <summary>Deployment-configured defaults for the maintenance engine (script locations, log dir, ping tuning).</summary>
public sealed record MaintenanceOptions
{
    /// <summary>Default revert script used when <see cref="RevertRequest.ScriptPath"/> is null.</summary>
    public string RevertScriptPath { get; init; } = "";

    /// <summary>Optional post-revert prep script (Prepare-AgentNode.ps1).</summary>
    public string? PrepScriptPath { get; init; }

    /// <summary>Optional build-install script (Install-Build.bat).</summary>
    public string? InstallBuildPath { get; init; }

    /// <summary>Directory where per-operation log files are written.</summary>
    public string LogDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "maintenance-logs");

    /// <summary>Ping/boot-wait tuning for the PingWait phase.</summary>
    public PingOptions PingOptions { get; init; } = new();
}
