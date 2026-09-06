using System.Diagnostics;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Maintenance;

/// <summary>
/// The revert engine: an 8-phase state machine (Precheck → Quarantine → SnapshotRevert → PowerOn → PingWait →
/// AgentWait → PostPrep → Verify) shared by every front door. Runs on the controller because the agent is destroyed
/// mid-revert; success is confirmed by the agent reconnecting, not by the script exit code. Any failure past
/// Quarantine leaves the node quarantined — a half-reverted machine is never returned to rotation.
/// (Spec: FleetRevert §2–§6, Prompt 4.)
/// </summary>
public sealed class MachineRevertOperation : IMachineRevertOperation
{
    private const string LogCategory = "Maintenance.Revert";
    private const int PhaseCount = 8;

    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly IPowerShellScriptRunner _scriptRunner;
    private readonly INodeReadinessProbe _readinessProbe;
    private readonly AgentLockManager _lockManager;
    private readonly ExecutionSessionManager _sessionManager;
    private readonly IMaintenanceStateStore _stateStore;
    private readonly IMaintenanceOperationStore _operationStore;
    private readonly MaintenanceOptions _options;
    private readonly IAppLogger _logger;

    public MachineRevertOperation(
        IAgentGrpcDispatcher dispatcher,
        IPowerShellScriptRunner scriptRunner,
        INodeReadinessProbe readinessProbe,
        AgentLockManager lockManager,
        ExecutionSessionManager sessionManager,
        IMaintenanceStateStore stateStore,
        IMaintenanceOperationStore operationStore,
        MaintenanceOptions options,
        IAppLogger logger)
    {
        _dispatcher = dispatcher;
        _scriptRunner = scriptRunner;
        _readinessProbe = readinessProbe;
        _lockManager = lockManager;
        _sessionManager = sessionManager;
        _stateStore = stateStore;
        _operationStore = operationStore;
        _options = options;
        _logger = logger;
    }

    public async Task<MaintenanceOperation> ExecuteAsync(
        MaintenanceOperation operation,
        RevertRequest request,
        IProgress<MaintenanceProgress> progress,
        CancellationToken cancellationToken)
    {
        var revertScript = request.ScriptPath ?? _options.RevertScriptPath;
        var op = operation with
        {
            Kind = MaintenanceKind.Revert,
            ScriptPath = revertScript,
            LogPath = Path.Combine(_options.LogDirectory, $"{operation.Id:N}.log"),
        };

        using var log = new OperationLog(op.LogPath);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            op = op with { State = MaintenanceOperationState.Running, Phase = RevertPhase.Precheck };
            await SaveAsync(op);
            Report(progress, op, 1, "Prechecking node.", stopwatch);

            // ── Phase 1: Precheck (failures here do NOT quarantine — nothing was changed yet) ──
            if (!IsRegistered(request.NodeId))
                return await FailPrecheckAsync(op, $"Node '{request.NodeId}' is not registered.", log, stopwatch, progress);

            var currentState = _stateStore.Get(request.NodeId);
            if (currentState != MaintenanceState.None)
                return await FailPrecheckAsync(op, $"Node '{request.NodeId}' is already in maintenance ({currentState}).", log, stopwatch, progress);

            if (string.IsNullOrWhiteSpace(request.SnapshotName))
                return await FailPrecheckAsync(op, "Snapshot name is required.", log, stopwatch, progress);

            // A missing / misconfigured revert script must fail here (Precheck) — not at SnapshotRevert, which is
            // past the quarantine point — so a configuration mistake never strands a healthy node in quarantine.
            if (string.IsNullOrWhiteSpace(revertScript))
                return await FailPrecheckAsync(op,
                    "No revert script is configured. Set 'Maintenance:RevertScriptPath' to a script that accepts (vmName, snapshot).",
                    log, stopwatch, progress);
            if (!File.Exists(revertScript))
                return await FailPrecheckAsync(op,
                    $"Revert script not found at '{revertScript}'. Check 'Maintenance:RevertScriptPath'.",
                    log, stopwatch, progress);

            var existingLock = _lockManager.GetLock(request.NodeId);
            if (existingLock is not null && !request.ForceIfBusy)
                return await FailPrecheckAsync(op, $"Node is busy running WatchItem '{existingLock.WatchItemTag}'. Enable force to override.", log, stopwatch, progress);

            // ── Phase 2: Quarantine — take the node out of rotation before touching it ──
            op = op with { Phase = RevertPhase.Quarantine };
            // Setting Reverting both blocks dispatch and (read by the liveness monitor) suppresses the "agent lost" alarm.
            _stateStore.Set(request.NodeId, MaintenanceState.Reverting);
            await SaveAsync(op);
            Report(progress, op, 2, "Taking node out of rotation.", stopwatch);

            if (request.ForceIfBusy && existingLock is not null)
            {
                _sessionManager.CancelSession(existingLock.SessionId);
                _lockManager.ReleaseSession(existingLock.SessionId);
                log.Note($"Force-aborted session '{existingLock.SessionId}' (WatchItem '{existingLock.WatchItemTag}').");
                _logger.Warn(LogCategory, $"Force-aborted session '{existingLock.SessionId}' on node '{request.NodeId}' for revert.");
            }

            // ── Phase 3: SnapshotRevert — point of no return, cancellation deferred ──
            op = op with { Phase = RevertPhase.SnapshotRevert };
            await SaveAsync(op);
            Report(progress, op, 3, $"Reverting to snapshot '{request.SnapshotName}'.", stopwatch);
            if (cancellationToken.IsCancellationRequested)
                log.Note("Cancellation requested but deferred until after the snapshot revert.");

            // Script contract: (VmName, SnapshotName). vCloud credentials are resolved by the script itself
            // from configuration / Credential Manager — never injected here (spec §8).
            var revert = await _scriptRunner.RunAsync(
                new ScriptInvocation { ScriptPath = revertScript, Arguments = [request.NodeId, request.SnapshotName] },
                log,
                CancellationToken.None);
            op = op with { ExitCode = revert.ExitCode };
            if (revert.ExitCode != 0)
                return await QuarantineAsync(op, RevertPhase.SnapshotRevert, $"Revert script exited {revert.ExitCode}.", log, stopwatch, progress);

            // ── Phase 4: PowerOn — a zero exit means the snapshot applied and power-on was requested ──
            op = op with { Phase = RevertPhase.PowerOn };
            await SaveAsync(op);
            Report(progress, op, 4, "Snapshot applied; power-on requested.", stopwatch);

            if (!request.WaitForAgent)
                return await CompleteAsync(op, log, stopwatch, progress);  // fire-and-forget: no reconnect wait

            // ── Phase 5: PingWait ──
            if (cancellationToken.IsCancellationRequested)
                return await CancelAsync(op, RevertPhase.PingWait, log, stopwatch, progress);
            op = op with { Phase = RevertPhase.PingWait };
            await SaveAsync(op);
            Report(progress, op, 5, "Waiting for the machine to respond to ping.", stopwatch);

            var host = ResolveHost(request.NodeId);
            var ping = await _readinessProbe.WaitForPingAsync(host, _options.PingOptions, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return await CancelAsync(op, RevertPhase.PingWait, log, stopwatch, progress);
            if (!ping.Succeeded)
                return await QuarantineAsync(op, RevertPhase.PingWait, ping.FailureReason ?? "Ping wait failed.", log, stopwatch, progress);

            // ── Phase 6: AgentWait ──
            op = op with { Phase = RevertPhase.AgentWait };
            await SaveAsync(op);
            Report(progress, op, 6, "Waiting for the agent to reconnect.", stopwatch);

            var agent = await _readinessProbe.WaitForAgentAsync(request.NodeId, request.AgentWaitTimeout, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return await CancelAsync(op, RevertPhase.AgentWait, log, stopwatch, progress);
            if (!agent.Succeeded)
                return await QuarantineAsync(op, RevertPhase.AgentWait, agent.FailureReason ?? "Agent did not reconnect.", log, stopwatch, progress);

            // ── Phase 7: PostPrep ──
            if (cancellationToken.IsCancellationRequested)
                return await CancelAsync(op, RevertPhase.PostPrep, log, stopwatch, progress);
            op = op with { Phase = RevertPhase.PostPrep };
            await SaveAsync(op);
            Report(progress, op, 7, "Running post-revert preparation.", stopwatch);

            if (request.RunPrep && !string.IsNullOrWhiteSpace(_options.PrepScriptPath))
            {
                var prep = await _scriptRunner.RunAsync(
                    new ScriptInvocation { ScriptPath = _options.PrepScriptPath!, Arguments = [request.NodeId] }, log, cancellationToken);
                if (prep.Cancelled)
                    return await CancelAsync(op, RevertPhase.PostPrep, log, stopwatch, progress);
                if (prep.ExitCode != 0)
                    return await QuarantineAsync(op, RevertPhase.PostPrep, $"Prep script exited {prep.ExitCode}.", log, stopwatch, progress);
            }

            if (request.InstallBuild && !string.IsNullOrWhiteSpace(_options.InstallBuildPath))
            {
                var install = await _scriptRunner.RunAsync(
                    new ScriptInvocation { ScriptPath = _options.InstallBuildPath!, Arguments = [request.NodeId] }, log, cancellationToken);
                if (install.Cancelled)
                    return await CancelAsync(op, RevertPhase.PostPrep, log, stopwatch, progress);
                if (install.ExitCode != 0)
                    return await QuarantineAsync(op, RevertPhase.PostPrep, $"Install-build exited {install.ExitCode}.", log, stopwatch, progress);
            }

            // ── Phase 8: Verify ──
            return await CompleteAsync(op, log, stopwatch, progress);
        }
        catch (Exception ex)
        {
            _logger.Error(LogCategory, $"Revert of '{request.NodeId}' threw during phase {op.Phase}.", ex);
            log.Note($"Unhandled error during {op.Phase}: {ex.Message}");

            // Past precheck we have already quarantined the node; keep it out of rotation.
            if (op.Phase != RevertPhase.Precheck)
                _stateStore.Set(request.NodeId, MaintenanceState.Quarantined);

            var faulted = op with
            {
                State = MaintenanceOperationState.Failed,
                FailurePhase = op.Phase,
                CompletedUtc = DateTimeOffset.UtcNow,
            };
            await SaveAsync(faulted);
            return faulted;
        }
    }

    private async Task<MaintenanceOperation> CompleteAsync(
        MaintenanceOperation op, OperationLog log, Stopwatch stopwatch, IProgress<MaintenanceProgress> progress)
    {
        op = op with { Phase = RevertPhase.Verify };
        Report(progress, op, 8, "Verifying and returning to rotation.", stopwatch);
        _stateStore.Set(op.NodeId, MaintenanceState.None);

        var done = op with { State = MaintenanceOperationState.Succeeded, CompletedUtc = DateTimeOffset.UtcNow };
        log.Note("Revert completed successfully.");
        await SaveAsync(done);
        Report(progress, done, 8, "Completed.", stopwatch);
        _logger.Info(LogCategory, $"Revert of '{op.NodeId}' completed in {stopwatch.Elapsed}.");
        return done;
    }

    private async Task<MaintenanceOperation> QuarantineAsync(
        MaintenanceOperation op, RevertPhase phase, string reason,
        OperationLog log, Stopwatch stopwatch, IProgress<MaintenanceProgress> progress)
    {
        _stateStore.Set(op.NodeId, MaintenanceState.Quarantined);
        log.Note($"QUARANTINED at {phase}: {reason}");
        _logger.Warn(LogCategory, $"Revert of '{op.NodeId}' quarantined at {phase}: {reason}");

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
        MaintenanceOperation op, RevertPhase phase,
        OperationLog log, Stopwatch stopwatch, IProgress<MaintenanceProgress> progress)
    {
        // Cancel past Quarantine still leaves the node quarantined — it was reverted but not verified.
        _stateStore.Set(op.NodeId, MaintenanceState.Quarantined);
        log.Note($"CANCELLED at {phase}; node left quarantined.");
        _logger.Warn(LogCategory, $"Revert of '{op.NodeId}' cancelled at {phase}; node quarantined.");

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

    private async Task<MaintenanceOperation> FailPrecheckAsync(
        MaintenanceOperation op, string reason,
        OperationLog log, Stopwatch stopwatch, IProgress<MaintenanceProgress> progress)
    {
        log.Note($"Precheck failed: {reason}");
        _logger.Warn(LogCategory, $"Revert of '{op.NodeId}' rejected at precheck: {reason}");

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

    private bool IsRegistered(string nodeId)
        => _dispatcher.RegisteredAgents.Any(a => string.Equals(a, nodeId, StringComparison.OrdinalIgnoreCase));

    private string ResolveHost(string nodeId)
    {
        var address = _dispatcher.GetAgentAddress(nodeId);
        return !string.IsNullOrWhiteSpace(address) && Uri.TryCreate(address, UriKind.Absolute, out var uri)
            ? uri.Host
            : nodeId;
    }

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
        RevertPhase.SnapshotRevert => 3,
        RevertPhase.PowerOn => 4,
        RevertPhase.PingWait => 5,
        RevertPhase.AgentWait => 6,
        RevertPhase.PostPrep => 7,
        RevertPhase.Verify => 8,
        _ => 0,
    };

    /// <summary>Thread-safe accumulator for one operation's script output; flushed to <see cref="MaintenanceOperation.LogPath"/>.</summary>
    private sealed class OperationLog : IProgress<ScriptOutputLine>, IDisposable
    {
        private readonly string? _path;
        private readonly object _gate = new();
        private readonly List<string> _lines = [];

        public OperationLog(string? path) => _path = path;

        public void Report(ScriptOutputLine line)
        {
            lock (_gate)
                _lines.Add($"{line.TimestampUtc:o} [{line.Stream}] {line.Text}");
        }

        public void Note(string note)
        {
            lock (_gate)
                _lines.Add($"{DateTimeOffset.UtcNow:o} [Note] {note}");
        }

        public void Dispose()
        {
            if (string.IsNullOrWhiteSpace(_path))
                return;

            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                string[] snapshot;
                lock (_gate)
                    snapshot = _lines.ToArray();

                File.WriteAllLines(_path, snapshot);
            }
            catch
            {
                // Operation logs are best-effort; never fail a revert because a log could not be written.
            }
        }
    }
}
