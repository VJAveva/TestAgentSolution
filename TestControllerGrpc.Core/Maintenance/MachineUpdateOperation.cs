using System.Diagnostics;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Maintenance;

/// <summary>
/// The update engine: Precheck → Quarantine → Search → Install → RebootWait → Verify.
/// <para>
/// This exists because the Fleet UI used to call <see cref="INodeUpdateInstaller"/> directly, which meant a patch
/// run created no operation row, set no maintenance state and took no lock — so the node stayed dispatchable
/// mid-install and a controller crash left no trace. Routing installs through an operation puts them behind the
/// same guards as reboot and revert.
/// </para>
/// <para>
/// Nothing here is irreversible: no snapshot is taken or replaced. The reboot is issued inline rather than by
/// delegating to <see cref="IMachineRebootOperation"/>, because that would need a second operation on a node this
/// one already owns and <c>FleetMaintenanceService</c> would reject it as a duplicate.
/// </para>
/// </summary>
public sealed class MachineUpdateOperation : IMachineUpdateOperation
{
    private const string LogCategory = "Maintenance.Update";
    private const int PhaseCount = 6;

    private readonly INodeUpdateInstaller _installer;
    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly INodeReadinessProbe _readinessProbe;
    private readonly INodeUpdateStatusStore _statusStore;
    private readonly AgentLockManager _lockManager;
    private readonly IMaintenanceStateStore _stateStore;
    private readonly IMaintenanceOperationStore _operationStore;
    private readonly IAppLogger _logger;

    public MachineUpdateOperation(
        INodeUpdateInstaller installer,
        IAgentGrpcDispatcher dispatcher,
        INodeReadinessProbe readinessProbe,
        INodeUpdateStatusStore statusStore,
        AgentLockManager lockManager,
        IMaintenanceStateStore stateStore,
        IMaintenanceOperationStore operationStore,
        IAppLogger logger)
    {
        _installer = installer;
        _dispatcher = dispatcher;
        _readinessProbe = readinessProbe;
        _statusStore = statusStore;
        _lockManager = lockManager;
        _stateStore = stateStore;
        _operationStore = operationStore;
        _logger = logger;
    }

    /// <summary>How often the Verify phase re-reads the posture store while waiting for a fresh report.</summary>
    public TimeSpan PosturePollInterval { get; init; } = TimeSpan.FromSeconds(5);

    public async Task<MaintenanceOperation> ExecuteAsync(
        MaintenanceOperation operation,
        InstallUpdatesRequest request,
        IProgress<MaintenanceProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var op = operation with { Kind = MaintenanceKind.InstallUpdates };
        var node = request.NodeId;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // ── 1: Precheck — nothing has changed yet, so a failure here must NOT quarantine ──
            op = op with { State = MaintenanceOperationState.Running, Phase = RevertPhase.Precheck };
            await SaveAsync(op);
            Report(progress, op, 1, "Prechecking node.", stopwatch);

            if (!IsRegistered(node))
                return await FailPrecheckAsync(op, $"Node '{node}' is not registered.", stopwatch, progress);

            var currentState = _stateStore.Get(node);
            if (IsHeldByAnotherOperation(currentState))
                return await FailPrecheckAsync(op, $"Node '{node}' is already in maintenance ({currentState}).", stopwatch, progress);

            // No force path: a patch run must never pre-empt a pipeline.
            if (_lockManager.GetLock(node) is { } busy)
                return await FailPrecheckAsync(op, $"Node is busy running WatchItem '{busy.WatchItemTag}'.", stopwatch, progress);

            if (!await _installer.IsInstallSupportedAsync(node, cancellationToken).ConfigureAwait(false))
                return await FailPrecheckAsync(op,
                    $"Node '{node}' cannot install updates (the agent is not running elevated).", stopwatch, progress);

            // ── 2: Quarantine — out of rotation BEFORE anything is installed ──
            op = op with { Phase = RevertPhase.Quarantine };
            _stateStore.Set(node, MaintenanceState.Updating);
            await SaveAsync(op);
            Report(progress, op, 2, "Taking node out of rotation.", stopwatch);

            // ── 3: Search (online) ──
            op = op with { Phase = RevertPhase.SearchUpdates };
            await SaveAsync(op);
            Report(progress, op, 3, "Searching for updates.", stopwatch);

            var search = await _installer.SearchAsync(node, cancellationToken).ConfigureAwait(false);
            if (!search.Ok)
                return await QuarantineAsync(op, RevertPhase.SearchUpdates, search.Error ?? "Update search failed.", stopwatch, progress);

            if (search.AvailableCount == 0)
            {
                _logger.Info(LogCategory, $"'{node}' has no updates available; returning to rotation.");
                return await SucceedAsync(op, node, "No updates available.", stopwatch, progress);
            }

            // ── 4: Install ──
            op = op with { Phase = RevertPhase.InstallUpdates };
            await SaveAsync(op);
            Report(progress, op, 4, $"Installing {search.AvailableCount} update(s).", stopwatch);

            var install = await _installer.InstallAsync(node, cancellationToken).ConfigureAwait(false);
            if (!install.Ok)
                return await QuarantineAsync(op, RevertPhase.InstallUpdates, install.Error ?? "Update install failed.", stopwatch, progress);

            // A partial install is reported, not quarantined: some updates failing is normal and retryable.
            if (install.FailedCount > 0)
                _logger.Warn(LogCategory,
                    $"'{node}': {install.InstalledCount} update(s) installed, {install.FailedCount} failed.");

            // ── 5: Reboot, only when the installer says one is needed ──
            if (install.RebootRequired)
            {
                op = op with { Phase = RevertPhase.RebootWait };
                await SaveAsync(op);
                Report(progress, op, 5, "Rebooting after install.", stopwatch);

                var rebooted = await RebootAsync(node, request, op).ConfigureAwait(false);
                if (rebooted is not null)
                    return await QuarantineAsync(op, RevertPhase.RebootWait, rebooted, stopwatch, progress);

                var back = await _readinessProbe
                    .WaitForAgentAsync(node, request.AgentWaitTimeout, cancellationToken).ConfigureAwait(false);
                if (!back.Succeeded)
                    return await QuarantineAsync(op, RevertPhase.RebootWait,
                        back.FailureReason ?? "Agent did not return after the reboot.", stopwatch, progress);
            }

            // ── 6: Verify — back online AND able to scan. A node that answers but cannot scan is not verified ──
            op = op with { Phase = RevertPhase.Verify };
            await SaveAsync(op);
            Report(progress, op, 6, "Verifying the patched node.", stopwatch);

            var agent = await _readinessProbe
                .WaitForAgentAsync(node, request.AgentWaitTimeout, cancellationToken).ConfigureAwait(false);
            if (!agent.Succeeded)
                return await QuarantineAsync(op, RevertPhase.Verify,
                    agent.FailureReason ?? "Agent did not reconnect.", stopwatch, progress);

            var posture = await WaitForFreshPostureAsync(node, request.PostureTimeout, cancellationToken).ConfigureAwait(false);
            if (posture is null)
                return await QuarantineAsync(op, RevertPhase.Verify,
                    $"No posture report within {request.PostureTimeout.TotalMinutes:0} minute(s) of the node returning.",
                    stopwatch, progress);

            if (posture.ScanStatus != UpdateScanStatus.Ok)
                return await QuarantineAsync(op, RevertPhase.Verify,
                    $"Node returned but its update scan reported {posture.ScanStatus}"
                    + (string.IsNullOrWhiteSpace(posture.ScanError) ? "." : $": {posture.ScanError}"),
                    stopwatch, progress);

            var summary = install.FailedCount > 0
                ? $"Installed {install.InstalledCount} update(s); {install.FailedCount} failed."
                : $"Installed {install.InstalledCount} update(s).";
            return await SucceedAsync(op, node, summary, stopwatch, progress);
        }
        catch (Exception ex)
        {
            _logger.Error(LogCategory, $"Update of '{node}' threw during phase {op.Phase}.", ex);
            if (op.Phase != RevertPhase.Precheck)
                _stateStore.Set(node, MaintenanceState.Quarantined);
            var faulted = op with
            {
                State = MaintenanceOperationState.Failed,
                FailurePhase = op.Phase,
                CompletedUtc = DateTimeOffset.UtcNow,
            };
            await SaveAsync(faulted);
            Report(progress, faulted, StepOf(op.Phase), $"Failed: {ex.Message}", stopwatch);
            return faulted;
        }
    }

    /// <summary>Returns null on success, or the failure reason.</summary>
    private async Task<string?> RebootAsync(string node, InstallUpdatesRequest request, MaintenanceOperation op)
    {
        var action = new ActionConfig
        {
            Tag = "wu-reboot",
            Type = ActionType.RunRemoteCommand,
            Command = "shutdown",
            Parameters = $"/r /t {Math.Max(0, request.RebootDelaySeconds)}",
            AgentName = node,
            IsReboot = true,
        };
        var ctx = new PipelineExecutionContext { SessionId = $"wu-reboot-{op.Id:N}" };

        try
        {
            var result = await _dispatcher.ExecuteRemoteCommandAsync(action, ctx, CancellationToken.None).ConfigureAwait(false);
            return result.Success ? null : $"Reboot command failed: {result.ErrorMessage}";
        }
        catch (Exception ex)
        {
            return $"Reboot command threw: {ex.Message}";
        }
    }

    /// <summary>
    /// Waits for a posture report produced AFTER this call started. Reading the store once would accept the
    /// pre-install report still sitting there, which proves nothing about the patched node.
    /// </summary>
    private async Task<NodeUpdateStatus?> WaitForFreshPostureAsync(string node, TimeSpan timeout, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow;
        var deadline = since + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_statusStore.Get(node) is { } status && status.LastReportUtc >= since)
                return status;

            try { await Task.Delay(PosturePollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        }

        return null;
    }

    // Draining/Updating from posture are not another operation; only engine-owned states block a new one.
    private static bool IsHeldByAnotherOperation(MaintenanceState state)
        => state is MaintenanceState.Reverting or MaintenanceState.Rebooting or MaintenanceState.Quarantined;

    private bool IsRegistered(string nodeId)
        => _dispatcher.RegisteredAgents.Any(a => string.Equals(a, nodeId, StringComparison.OrdinalIgnoreCase));

    private async Task<MaintenanceOperation> SucceedAsync(
        MaintenanceOperation op, string node, string message, Stopwatch stopwatch, IProgress<MaintenanceProgress> progress)
    {
        _stateStore.Set(node, MaintenanceState.None);
        var done = op with
        {
            State = MaintenanceOperationState.Succeeded,
            Phase = RevertPhase.Verify,
            CompletedUtc = DateTimeOffset.UtcNow,
        };
        await SaveAsync(done);
        Report(progress, done, PhaseCount, message, stopwatch);
        _logger.Info(LogCategory, $"Update of '{node}' completed in {stopwatch.Elapsed}. {message}");
        return done;
    }

    private async Task<MaintenanceOperation> FailPrecheckAsync(
        MaintenanceOperation op, string reason, Stopwatch stopwatch, IProgress<MaintenanceProgress> progress)
    {
        _logger.Warn(LogCategory, $"Update of '{op.NodeId}' rejected at precheck: {reason}");
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
        _logger.Warn(LogCategory, $"Update of '{op.NodeId}' quarantined at {phase}: {reason}");
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

    private async Task SaveAsync(MaintenanceOperation op)
    {
        try { await _operationStore.SaveAsync(op, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.Error(LogCategory, $"Failed to persist operation {op.Id}.", ex); }
    }

    private static void Report(
        IProgress<MaintenanceProgress> progress, MaintenanceOperation op, int step, string message, Stopwatch stopwatch)
        => progress.Report(new MaintenanceProgress(op.Id, op.NodeId, op.Phase, step, PhaseCount, message, stopwatch.Elapsed));

    private static int StepOf(RevertPhase phase) => phase switch
    {
        RevertPhase.Precheck => 1,
        RevertPhase.Quarantine => 2,
        RevertPhase.SearchUpdates => 3,
        RevertPhase.InstallUpdates => 4,
        RevertPhase.RebootWait => 5,
        _ => PhaseCount,
    };
}
