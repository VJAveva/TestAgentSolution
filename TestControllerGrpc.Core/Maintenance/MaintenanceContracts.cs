namespace TestControllerGrpc.Core.Maintenance;

// Seams for the maintenance engine. Contracts only — no implementations live in Core. The WPF host and the
// WebApi host each supply their own notifier / store / script-runner behind these interfaces, so the engine
// (the state machine in a later phase) stays host-agnostic and unit-testable. (Spec: FleetRevert §5, §7.)

/// <summary>Executes a single node revert as an 8-phase state machine, reporting progress as it goes.</summary>
public interface IMachineRevertOperation
{
    /// <summary>
    /// Runs the revert to completion (or failure). The caller owns <paramref name="operation"/> (so it holds the
    /// operation id before this returns); the engine advances it and returns the final state. SnapshotRevert is the
    /// point of no return; cancellation is honored only at phase boundaries and refused once the snapshot revert has
    /// started. Any failure past the Quarantine phase leaves the node <see cref="MaintenanceState.Quarantined"/>,
    /// never silently back in rotation.
    /// </summary>
    Task<MaintenanceOperation> ExecuteAsync(
        MaintenanceOperation operation,
        RevertRequest request,
        IProgress<MaintenanceProgress> progress,
        CancellationToken cancellationToken);
}

/// <summary>Executes a single node reboot: quarantine, out-of-band reboot, wait for reconnect, return to rotation.
/// Simpler than a revert (no snapshot), but the same quarantine-on-failure rule applies.</summary>
public interface IMachineRebootOperation
{
    Task<MaintenanceOperation> ExecuteAsync(
        MaintenanceOperation operation,
        RebootRequest request,
        IProgress<MaintenanceProgress> progress,
        CancellationToken cancellationToken);
}

/// <summary>The fleet-facing façade every front door calls. Owns the set of in-flight operations and raises
/// progress / completion so hosts can project it (WPF binding, SignalR broadcast).</summary>
public interface IFleetMaintenanceService
{
    /// <summary>Snapshot each node's dispatch eligibility before showing the confirm dialog.</summary>
    Task<PrecheckResult> PrecheckAsync(IReadOnlyList<string> nodeIds, CancellationToken cancellationToken);

    /// <summary>Queue a revert; returns the new operation id. Does not block on completion.</summary>
    Task<Guid> StartRevertAsync(RevertRequest request, CancellationToken cancellationToken);

    /// <summary>Queue a reboot; returns the new operation id. Does not block on completion.</summary>
    Task<Guid> StartRebootAsync(RebootRequest request, CancellationToken cancellationToken);

    /// <summary>Request cancellation at the next phase boundary. Returns false if the operation is unknown or
    /// already past the point of no return.</summary>
    Task<bool> RequestCancelAsync(Guid operationId);

    /// <summary>Return a quarantined node to rotation after an operator has verified it.</summary>
    Task ClearQuarantineAsync(string nodeId, string clearedBy);

    /// <summary>Operations currently queued or running.</summary>
    IReadOnlyCollection<MaintenanceOperation> ActiveOperations { get; }

    event EventHandler<MaintenanceProgress>? ProgressChanged;
    event EventHandler<MaintenanceOperation>? OperationCompleted;
}

/// <summary>Runs a PowerShell / batch script on the controller (the node being reverted is unreachable), streaming
/// output line-by-line. This is a net-new capability — the codebase only runs commands on agents today.</summary>
public interface IPowerShellScriptRunner
{
    Task<ScriptResult> RunAsync(
        ScriptInvocation invocation,
        IProgress<ScriptOutputLine> output,
        CancellationToken cancellationToken);
}

/// <summary>Waits for a node to come back — first ICMP reachability, then agent re-registration.</summary>
public interface INodeReadinessProbe
{
    Task<ReadinessResult> WaitForPingAsync(string host, PingOptions options, CancellationToken cancellationToken);

    Task<ReadinessResult> WaitForAgentAsync(string nodeId, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Host-specific projection of maintenance events (WPF dispatcher → view model, or SignalR broadcast).</summary>
public interface IMaintenanceNotifier
{
    void OperationStarted(MaintenanceOperation operation);
    void OperationProgress(MaintenanceProgress progress);
    void OperationCompleted(MaintenanceOperation operation);
}

/// <summary>Persistence seam for the maintenance-operation history (backed by EF Core / SQLite in a later phase).</summary>
public interface IMaintenanceOperationStore
{
    Task SaveAsync(MaintenanceOperation operation, CancellationToken cancellationToken);

    Task<MaintenanceOperation?> GetAsync(Guid operationId, CancellationToken cancellationToken);

    Task<IReadOnlyList<MaintenanceOperation>> GetHistoryAsync(
        string? nodeId,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Operations still in a non-terminal state (Queued or Running). After a controller restart these are
    /// necessarily orphaned: the engine's in-flight state is process-local, so nothing is still driving them.
    /// Default implementation returns none, so existing fakes and mocks keep compiling.
    /// </summary>
    Task<IReadOnlyList<MaintenanceOperation>> GetUnfinishedAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<MaintenanceOperation>>([]);
}

/// <summary>
/// Authoritative in-memory map of node -> <see cref="MaintenanceState"/>. This is the grounded stand-in for the
/// spec's "n.MaintenanceState" — the codebase has no unified agent-node model carrying it. The revert engine writes
/// it; the dispatch gate and the liveness monitor (later phases) read it; the UI binds to <see cref="Changed"/>.
/// </summary>
public interface IMaintenanceStateStore
{
    MaintenanceState Get(string nodeId);
    void Set(string nodeId, MaintenanceState state);
    IReadOnlyDictionary<string, MaintenanceState> Snapshot();
    event EventHandler<NodeMaintenanceStateChanged>? Changed;
}
