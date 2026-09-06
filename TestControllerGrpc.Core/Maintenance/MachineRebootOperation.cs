using System.Diagnostics;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Maintenance;

/// <summary>
/// The reboot engine: Precheck → Quarantine → Reboot → AgentWait → Verify. The reboot itself is issued through the
/// agent dispatcher's existing out-of-band reboot path (<c>shutdown /r … /m \\host</c>), which already waits for the
/// machine to come back. Any failure past Quarantine leaves the node quarantined. (Spec: FleetRevert §14, Prompt 7.)
/// </summary>
public sealed class MachineRebootOperation : IMachineRebootOperation
{
    private const string LogCategory = "Maintenance.Reboot";
    private const int PhaseCount = 5;

    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly INodeReadinessProbe _readinessProbe;
    private readonly AgentLockManager _lockManager;
    private readonly ExecutionSessionManager _sessionManager;
    private readonly IMaintenanceStateStore _stateStore;
    private readonly IMaintenanceOperationStore _operationStore;
    private readonly IAppLogger _logger;

    public MachineRebootOperation(
        IAgentGrpcDispatcher dispatcher,
        INodeReadinessProbe readinessProbe,
        AgentLockManager lockManager,
        ExecutionSessionManager sessionManager,
        IMaintenanceStateStore stateStore,
        IMaintenanceOperationStore operationStore,
        IAppLogger logger)
    {
        _dispatcher = dispatcher;
        _readinessProbe = readinessProbe;
        _lockManager = lockManager;
        _sessionManager = sessionManager;
        _stateStore = stateStore;
        _operationStore = operationStore;
        _logger = logger;
    }

    public async Task<MaintenanceOperation> ExecuteAsync(
        MaintenanceOperation operation,
        RebootRequest request,
        IProgress<MaintenanceProgress> progress,
        CancellationToken cancellationToken)
    {
        var op = operation with { Kind = MaintenanceKind.Reboot };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            op = op with { State = MaintenanceOperationState.Running, Phase = RevertPhase.Precheck };
            await SaveAsync(op);
            Report(progress, op, 1, "Prechecking node.", stopwatch);

            // ── Phase 1: Precheck (failures here do NOT quarantine) ──
            if (!IsRegistered(request.NodeId))
                return await FailPrecheckAsync(op, $"Node '{request.NodeId}' is not registered.", stopwatch, progress);

            var currentState = _stateStore.Get(request.NodeId);
            if (currentState != MaintenanceState.None)
                return await FailPrecheckAsync(op, $"Node '{request.NodeId}' is already in maintenance ({currentState}).", stopwatch, progress);

            var existingLock = _lockManager.GetLock(request.NodeId);
            if (existingLock is not null && !request.ForceIfBusy)
                return await FailPrecheckAsync(op, $"Node is busy running WatchItem '{existingLock.WatchItemTag}'. Enable force to override.", stopwatch, progress);

            // ── Phase 2: Quarantine ──
            op = op with { Phase = RevertPhase.Quarantine };
            _stateStore.Set(request.NodeId, MaintenanceState.Rebooting);
            await SaveAsync(op);
            Report(progress, op, 2, "Taking node out of rotation.", stopwatch);

            if (request.ForceIfBusy && existingLock is not null)
            {
                _sessionManager.CancelSession(existingLock.SessionId);
                _lockManager.ReleaseSession(existingLock.SessionId);
                _logger.Warn(LogCategory, $"Force-aborted session '{existingLock.SessionId}' on node '{request.NodeId}' for reboot.");
            }

            // ── Phase 3: Reboot (point of no return; the dispatcher waits for recovery) ──
            op = op with { Phase = RevertPhase.PowerOn };
            await SaveAsync(op);
            Report(progress, op, 3, "Rebooting the machine.", stopwatch);

            var action = new ActionConfig
            {
                Tag = "reboot",
                Type = ActionType.RunRemoteCommand,
                Command = "shutdown",
                Parameters = $"/r /t {Math.Max(0, request.RebootDelaySeconds)}",
                AgentName = request.NodeId,
                IsReboot = true,
            };
            var ctx = new PipelineExecutionContext { SessionId = $"reboot-{op.Id:N}" };

            ActionResult result;
            try
            {
                result = await _dispatcher.ExecuteRemoteCommandAsync(action, ctx, CancellationToken.None);
            }
            catch (Exception ex)
            {
                return await QuarantineAsync(op, RevertPhase.PowerOn, $"Reboot command threw: {ex.Message}", stopwatch, progress);
            }
            if (!result.Success)
                return await QuarantineAsync(op, RevertPhase.PowerOn, $"Reboot command failed: {result.ErrorMessage}", stopwatch, progress);

            // ── Phase 4: AgentWait (guard confirm — the dispatcher already polled for recovery) ──
            if (cancellationToken.IsCancellationRequested)
                return await CancelAsync(op, RevertPhase.AgentWait, stopwatch, progress);
            op = op with { Phase = RevertPhase.AgentWait };
            await SaveAsync(op);
            Report(progress, op, 4, "Waiting for the agent to reconnect.", stopwatch);

            var agent = await _readinessProbe.WaitForAgentAsync(request.NodeId, request.AgentWaitTimeout, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return await CancelAsync(op, RevertPhase.AgentWait, stopwatch, progress);
            if (!agent.Succeeded)
                return await QuarantineAsync(op, RevertPhase.AgentWait, agent.FailureReason ?? "Agent did not reconnect.", stopwatch, progress);

            // ── Phase 5: Verify ──
            op = op with { Phase = RevertPhase.Verify };
            Report(progress, op, 5, "Verifying and returning to rotation.", stopwatch);
            _stateStore.Set(request.NodeId, MaintenanceState.None);
            var done = op with { State = MaintenanceOperationState.Succeeded, CompletedUtc = DateTimeOffset.UtcNow };
            await SaveAsync(done);
            Report(progress, done, 5, "Completed.", stopwatch);
            _logger.Info(LogCategory, $"Reboot of '{request.NodeId}' completed in {stopwatch.Elapsed}.");
            return done;
        }
        catch (Exception ex)
        {
            _logger.Error(LogCategory, $"Reboot of '{request.NodeId}' threw during phase {op.Phase}.", ex);
            if (op.Phase != RevertPhase.Precheck)
                _stateStore.Set(request.NodeId, MaintenanceState.Quarantined);
            var faulted = op with { State = MaintenanceOperationState.Failed, FailurePhase = op.Phase, CompletedUtc = DateTimeOffset.UtcNow };
            await SaveAsync(faulted);
            return faulted;
        }
    }

    private async Task<MaintenanceOperation> FailPrecheckAsync(
        MaintenanceOperation op, string reason, Stopwatch stopwatch, IProgress<MaintenanceProgress> progress)
    {
        _logger.Warn(LogCategory, $"Reboot of '{op.NodeId}' rejected at precheck: {reason}");
        var failed = op with
        {
            State = MaintenanceOperationState.Failed,
            Phase = RevertPhase.Precheck,
            FailurePhase = RevertPhase.Precheck,
            CompletedUtc = DateTimeOffset.UtcNow,
        };
        await SaveAsync(failed);
        Report(progress, failed, 1, $"Precheck failed: {reason}", stopwatch);
        return failed;
    }

    private async Task<MaintenanceOperation> QuarantineAsync(
        MaintenanceOperation op, RevertPhase phase, string reason, Stopwatch stopwatch, IProgress<MaintenanceProgress> progress)
    {
        _stateStore.Set(op.NodeId, MaintenanceState.Quarantined);
        _logger.Warn(LogCategory, $"Reboot of '{op.NodeId}' quarantined at {phase}: {reason}");
        var failed = op with
        {
            State = MaintenanceOperationState.Failed,
            Phase = phase,
            FailurePhase = phase,
            CompletedUtc = DateTimeOffset.UtcNow,
        };
        await SaveAsync(failed);
        Report(progress, failed, StepOf(phase), $"Failed: {reason}", stopwatch);
        return failed;
    }

    private async Task<MaintenanceOperation> CancelAsync(
        MaintenanceOperation op, RevertPhase phase, Stopwatch stopwatch, IProgress<MaintenanceProgress> progress)
    {
        _stateStore.Set(op.NodeId, MaintenanceState.Quarantined);
        _logger.Warn(LogCategory, $"Reboot of '{op.NodeId}' cancelled at {phase}; node quarantined.");
        var cancelled = op with
        {
            State = MaintenanceOperationState.Cancelled,
            Phase = phase,
            FailurePhase = phase,
            CompletedUtc = DateTimeOffset.UtcNow,
        };
        await SaveAsync(cancelled);
        Report(progress, cancelled, StepOf(phase), "Cancelled.", stopwatch);
        return cancelled;
    }

    private bool IsRegistered(string nodeId)
        => _dispatcher.RegisteredAgents.Any(a => string.Equals(a, nodeId, StringComparison.OrdinalIgnoreCase));

    private async Task SaveAsync(MaintenanceOperation op)
    {
        try { await _operationStore.SaveAsync(op, CancellationToken.None); }
        catch (Exception ex) { _logger.Error(LogCategory, $"Failed to persist operation {op.Id}.", ex); }
    }

    private static void Report(
        IProgress<MaintenanceProgress> progress, MaintenanceOperation op, int step, string message, Stopwatch stopwatch)
        => progress.Report(new MaintenanceProgress(op.Id, op.NodeId, op.Phase, step, PhaseCount, message, stopwatch.Elapsed));

    private static int StepOf(RevertPhase phase) => phase switch
    {
        RevertPhase.Precheck => 1,
        RevertPhase.Quarantine => 2,
        RevertPhase.PowerOn => 3,
        RevertPhase.AgentWait => 4,
        RevertPhase.Verify => 5,
        _ => 0,
    };
}
