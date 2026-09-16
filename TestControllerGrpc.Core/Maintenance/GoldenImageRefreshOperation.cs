using System.Diagnostics;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Maintenance;

/// <summary>
/// The golden-image refresh engine: revert to baseline, patch, verify, then replace the baseline.
///
/// Reverting FIRST is deliberate — the new image is built from the known-good baseline rather than a machine
/// that has drifted through weeks of test runs, so drift never compounds across refreshes.
///
/// The verification gate before <see cref="RevertPhase.ReplaceBaseline"/> is the safety property this type
/// exists for. Replacing a baseline is irreversible on a single-snapshot platform, so an unverified node must
/// never reach it: on failure the node is reverted back to its intact baseline and quarantined.
/// </summary>
public sealed class GoldenImageRefreshOperation : IGoldenImageRefreshOperation
{
    private const string LogCategory = "Maintenance.Refresh";
    private const int PhaseCount = 12;

    private readonly IVirtualizationProvider _provider;
    private readonly INodeUpdateInstaller _installer;
    private readonly INodeReadinessProbe _readinessProbe;
    private readonly BaselineReplacer _baselineReplacer;
    private readonly AgentLockManager _lockManager;
    private readonly ExecutionSessionManager _sessionManager;
    private readonly IMaintenanceStateStore _stateStore;
    private readonly IMaintenanceOperationStore _operationStore;
    private readonly MaintenanceOptions _options;
    private readonly IAppLogger _logger;

    public GoldenImageRefreshOperation(
        IVirtualizationProvider provider,
        INodeUpdateInstaller installer,
        INodeReadinessProbe readinessProbe,
        BaselineReplacer baselineReplacer,
        AgentLockManager lockManager,
        ExecutionSessionManager sessionManager,
        IMaintenanceStateStore stateStore,
        IMaintenanceOperationStore operationStore,
        MaintenanceOptions options,
        IAppLogger logger)
    {
        _provider = provider;
        _installer = installer;
        _readinessProbe = readinessProbe;
        _baselineReplacer = baselineReplacer;
        _lockManager = lockManager;
        _sessionManager = sessionManager;
        _stateStore = stateStore;
        _operationStore = operationStore;
        _options = options;
        _logger = logger;
    }

    public async Task<MaintenanceOperation> ExecuteAsync(
        MaintenanceOperation operation,
        GoldenImageRefreshRequest request,
        IProgress<MaintenanceProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var op = operation with
        {
            Kind = MaintenanceKind.GoldenImageRefresh,
            LogPath = Path.Combine(_options.LogDirectory, $"{operation.Id:N}.log"),
        };

        using var log = new OperationLog(op.LogPath);
        var stopwatch = Stopwatch.StartNew();
        var node = request.NodeId;

        try
        {
            // ── 1: Precheck — nothing has changed yet, so a failure here must NOT quarantine ──
            op = Advance(op, RevertPhase.Precheck, 1, "Prechecking node.", progress, stopwatch);
            await SaveAsync(op);

            var currentState = _stateStore.Get(node);
            if (IsHeldByAnotherOperation(currentState))
                return await FailPrecheckAsync(op, $"Node '{node}' is already in maintenance ({currentState}).", log, stopwatch, progress);

            var existingLock = _lockManager.GetLock(node);
            if (existingLock is not null && !request.ForceIfBusy)
                return await FailPrecheckAsync(op, $"Node is busy running WatchItem '{existingLock.WatchItemTag}'. Enable force to override.", log, stopwatch, progress);

            if (!await _installer.IsInstallSupportedAsync(node, cancellationToken).ConfigureAwait(false))
                return await FailPrecheckAsync(op,
                    $"Node '{node}' cannot install updates (agents running under an interactive account are not supported).",
                    log, stopwatch, progress);

            var baselines = await _provider.ListSnapshotsAsync(node, cancellationToken).ConfigureAwait(false);
            if (baselines.Count == 0)
                return await FailPrecheckAsync(op,
                    $"Node '{node}' has no baseline snapshot to revert to or replace.", log, stopwatch, progress);

            // ── 2: Quarantine — out of rotation before anything destructive ──
            op = Advance(op, RevertPhase.Quarantine, 2, "Taking node out of rotation.", progress, stopwatch);
            _stateStore.Set(node, MaintenanceState.Updating);
            await SaveAsync(op);

            if (request.ForceIfBusy && existingLock is not null)
            {
                _sessionManager.CancelSession(existingLock.SessionId);
                _lockManager.ReleaseSession(existingLock.SessionId);
                log.Note($"Force-aborted session '{existingLock.SessionId}' (WatchItem '{existingLock.WatchItemTag}').");
            }

            // ── 3: Revert to baseline — start from known-good, never from a drifted machine ──
            op = Advance(op, RevertPhase.SnapshotRevert, 3, "Reverting to baseline.", progress, stopwatch);
            await SaveAsync(op);

            var revert = await _provider.RevertSnapshotAsync([node], request.BaselineSnapshotName, CancellationToken.None)
                .ConfigureAwait(false);
            if (revert.For(node) is not { Ok: true })
                return await QuarantineAsync(op, RevertPhase.SnapshotRevert, ErrorOf(revert, node, "Revert failed."), log, stopwatch, progress);

            // ── 4: Power on and wait for the agent ──
            op = Advance(op, RevertPhase.PowerOn, 4, "Powering on.", progress, stopwatch);
            await SaveAsync(op);
            var powerOn = await _provider.PowerOnAsync([node], CancellationToken.None).ConfigureAwait(false);
            if (powerOn.For(node) is not { Ok: true })
                return await QuarantineAsync(op, RevertPhase.PowerOn, ErrorOf(powerOn, node, "Power-on failed."), log, stopwatch, progress);

            op = Advance(op, RevertPhase.AgentWait, 5, "Waiting for the agent to reconnect.", progress, stopwatch);
            await SaveAsync(op);
            var ready = await _readinessProbe.WaitForAgentAsync(node, request.AgentWaitTimeout, cancellationToken).ConfigureAwait(false);
            if (!ready.Succeeded)
                return await QuarantineAsync(op, RevertPhase.AgentWait, ready.FailureReason ?? "Agent did not reconnect.", log, stopwatch, progress);

            // ── 5: Search (online) ──
            op = Advance(op, RevertPhase.SearchUpdates, 6, "Searching for updates.", progress, stopwatch);
            await SaveAsync(op);
            var search = await _installer.SearchAsync(node, cancellationToken).ConfigureAwait(false);
            if (!search.Ok)
                return await QuarantineAsync(op, RevertPhase.SearchUpdates, search.Error ?? "Update search failed.", log, stopwatch, progress);

            log.Note($"{search.AvailableCount} update(s) available.");

            // Nothing to install: the baseline is already current, so burning the rollback point buys nothing.
            if (search.AvailableCount == 0 && !request.RefreshWhenNoUpdates)
            {
                log.Note("No updates available; baseline left untouched.");
                return await CompleteAsync(op, node, "No updates available; baseline unchanged.", log, stopwatch, progress);
            }

            // ── 6: Install ──
            op = Advance(op, RevertPhase.InstallUpdates, 7, $"Installing {search.AvailableCount} update(s).", progress, stopwatch);
            await SaveAsync(op);
            var install = await _installer.InstallAsync(node, cancellationToken).ConfigureAwait(false);
            if (!install.Ok)
                return await QuarantineAsync(op, RevertPhase.InstallUpdates, install.Error ?? "Update install failed.", log, stopwatch, progress);

            // ── 7: Reboot and wait ──
            if (install.RebootRequired)
            {
                op = Advance(op, RevertPhase.RebootWait, 8, "Rebooting after install.", progress, stopwatch);
                await SaveAsync(op);

                var cycled = await CyclePowerAsync(node, log).ConfigureAwait(false);
                if (cycled is not null)
                    return await QuarantineAsync(op, RevertPhase.RebootWait, cycled, log, stopwatch, progress);

                var back = await _readinessProbe.WaitForAgentAsync(node, request.AgentWaitTimeout, cancellationToken).ConfigureAwait(false);
                if (!back.Succeeded)
                    return await QuarantineAsync(op, RevertPhase.RebootWait, back.FailureReason ?? "Agent did not return after reboot.", log, stopwatch, progress);
            }

            // ── 8: VERIFY — the gate. Nothing past here is reversible on a single-snapshot platform ──
            op = Advance(op, RevertPhase.Verify, 9, "Verifying the patched node.", progress, stopwatch);
            await SaveAsync(op);

            var verify = await _readinessProbe.WaitForAgentAsync(node, request.AgentWaitTimeout, cancellationToken).ConfigureAwait(false);
            if (!verify.Succeeded)
            {
                // Roll back rather than promote: the baseline is still intact at this point.
                log.Note("Verification FAILED; reverting to the intact baseline.");
                await TryRevertToBaselineAsync(node, request.BaselineSnapshotName, log).ConfigureAwait(false);
                return await QuarantineAsync(op, RevertPhase.Verify,
                    verify.FailureReason ?? "Verification failed; node reverted to its baseline.", log, stopwatch, progress);
            }

            // ── 9: Power off so the snapshot is taken from a quiescent disk ──
            op = Advance(op, RevertPhase.PowerOff, 10, "Powering off to capture the baseline.", progress, stopwatch);
            await SaveAsync(op);
            var off = await _provider.PowerOffAsync([node], graceful: true, CancellationToken.None).ConfigureAwait(false);
            if (off.For(node) is not { Ok: true })
                return await QuarantineAsync(op, RevertPhase.PowerOff, ErrorOf(off, node, "Power-off failed."), log, stopwatch, progress);

            // ── 10: Replace the baseline — irreversible ──
            op = Advance(op, RevertPhase.ReplaceBaseline, 11, "Replacing the baseline snapshot.", progress, stopwatch);
            await SaveAsync(op);

            var name = string.IsNullOrWhiteSpace(request.NewSnapshotName)
                ? $"baseline-{DateTimeOffset.UtcNow:yyyyMMdd-HHmm}"
                : request.NewSnapshotName!;

            var replaced = await _baselineReplacer
                .ReplaceAsync(node, name, $"Golden image after {install.InstalledCount} update(s).", CancellationToken.None)
                .ConfigureAwait(false);

            if (!replaced.Ok)
            {
                var detail = replaced.Status == BaselineReplacementStatus.FailedBaselineMissing
                    ? $"BASELINE MISSING — {replaced.Error}. This node cannot be reverted until a snapshot is taken."
                    : replaced.Error ?? "Baseline replacement failed.";
                return await QuarantineAsync(op, RevertPhase.ReplaceBaseline, detail, log, stopwatch, progress);
            }

            log.Note($"New baseline '{name}' created.");

            // ── 11: Power back on ──
            op = Advance(op, RevertPhase.FinalPowerOn, 12, "Powering back on.", progress, stopwatch);
            await SaveAsync(op);
            var on = await _provider.PowerOnAsync([node], CancellationToken.None).ConfigureAwait(false);
            if (on.For(node) is not { Ok: true })
                return await QuarantineAsync(op, RevertPhase.FinalPowerOn, ErrorOf(on, node, "Final power-on failed."), log, stopwatch, progress);

            var final = await _readinessProbe.WaitForAgentAsync(node, request.AgentWaitTimeout, cancellationToken).ConfigureAwait(false);
            if (!final.Succeeded)
                return await QuarantineAsync(op, RevertPhase.FinalPowerOn, final.FailureReason ?? "Agent did not return after the refresh.", log, stopwatch, progress);

            return await CompleteAsync(op, node,
                $"Refreshed with {install.InstalledCount} update(s); new baseline '{name}'.", log, stopwatch, progress);
        }
        catch (Exception ex)
        {
            _logger.Error(LogCategory, $"Refresh of '{node}' threw during phase {op.Phase}.", ex);
            log.Note($"Unhandled error during {op.Phase}: {ex.Message}");

            if (op.Phase != RevertPhase.Precheck)
                _stateStore.Set(node, MaintenanceState.Quarantined);

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

    private async Task<string?> CyclePowerAsync(string node, OperationLog log)
    {
        var off = await _provider.PowerOffAsync([node], graceful: true, CancellationToken.None).ConfigureAwait(false);
        if (off.For(node) is not { Ok: true })
            return ErrorOf(off, node, "Power-off for reboot failed.");

        var on = await _provider.PowerOnAsync([node], CancellationToken.None).ConfigureAwait(false);
        if (on.For(node) is not { Ok: true })
            return ErrorOf(on, node, "Power-on after reboot failed.");

        log.Note("Rebooted after installing updates.");
        return null;
    }

    // Best-effort rollback. A failure here is already the bad path; it must not mask the original reason.
    private async Task TryRevertToBaselineAsync(string node, string? snapshotName, OperationLog log)
    {
        try
        {
            var result = await _provider.RevertSnapshotAsync([node], snapshotName, CancellationToken.None).ConfigureAwait(false);
            if (result.For(node) is { Ok: true })
            {
                await _provider.PowerOnAsync([node], CancellationToken.None).ConfigureAwait(false);
                log.Note("Node reverted to its baseline after failed verification.");
            }
            else
            {
                log.Note($"Rollback to baseline FAILED: {ErrorOf(result, node, "unknown error")}");
            }
        }
        catch (Exception ex)
        {
            log.Note($"Rollback to baseline threw: {ex.Message}");
        }
    }

    private MaintenanceOperation Advance(
        MaintenanceOperation op, RevertPhase phase, int step, string message,
        IProgress<MaintenanceProgress> progress, Stopwatch stopwatch)
    {
        var next = op with { State = MaintenanceOperationState.Running, Phase = phase };
        progress.Report(new MaintenanceProgress(
            next.Id, next.NodeId, phase, step, PhaseCount, message, stopwatch.Elapsed));
        return next;
    }

    private async Task<MaintenanceOperation> CompleteAsync(
        MaintenanceOperation op, string node, string message,
        OperationLog log, Stopwatch stopwatch, IProgress<MaintenanceProgress> progress)
    {
        _stateStore.Set(node, MaintenanceState.None);
        var done = op with { State = MaintenanceOperationState.Succeeded, CompletedUtc = DateTimeOffset.UtcNow };
        log.Note(message);
        await SaveAsync(done);
        progress.Report(new MaintenanceProgress(done.Id, node, done.Phase, PhaseCount, PhaseCount, message, stopwatch.Elapsed));
        _logger.Info(LogCategory, $"Refresh of '{node}' completed in {stopwatch.Elapsed}: {message}");
        return done;
    }

    private async Task<MaintenanceOperation> QuarantineAsync(
        MaintenanceOperation op, RevertPhase phase, string reason,
        OperationLog log, Stopwatch stopwatch, IProgress<MaintenanceProgress> progress)
    {
        _stateStore.Set(op.NodeId, MaintenanceState.Quarantined);
        log.Note($"QUARANTINED at {phase}: {reason}");
        _logger.Warn(LogCategory, $"Refresh of '{op.NodeId}' quarantined at {phase}: {reason}");

        var failed = op with
        {
            State = MaintenanceOperationState.Failed,
            Phase = phase,
            FailurePhase = phase,
            CompletedUtc = DateTimeOffset.UtcNow,
        };
        await SaveAsync(failed);
        progress.Report(new MaintenanceProgress(failed.Id, failed.NodeId, phase, PhaseCount, PhaseCount, $"Failed: {reason}", stopwatch.Elapsed));
        return failed;
    }

    private async Task<MaintenanceOperation> FailPrecheckAsync(
        MaintenanceOperation op, string reason,
        OperationLog log, Stopwatch stopwatch, IProgress<MaintenanceProgress> progress)
    {
        log.Note($"Precheck failed: {reason}");
        _logger.Warn(LogCategory, $"Refresh of '{op.NodeId}' rejected at precheck: {reason}");

        var failed = op with
        {
            State = MaintenanceOperationState.Failed,
            Phase = RevertPhase.Precheck,
            FailurePhase = RevertPhase.Precheck,
            CompletedUtc = DateTimeOffset.UtcNow,
        };
        await SaveAsync(failed);
        progress.Report(new MaintenanceProgress(failed.Id, failed.NodeId, RevertPhase.Precheck, 1, PhaseCount, $"Precheck failed: {reason}", stopwatch.Elapsed));
        return failed;
    }

    private static bool IsHeldByAnotherOperation(MaintenanceState state)
        => state is MaintenanceState.Reverting or MaintenanceState.Rebooting or MaintenanceState.Quarantined;

    private static string ErrorOf(VmOpResult result, string node, string fallback)
        => result.For(node)?.Error ?? fallback;

    private async Task SaveAsync(MaintenanceOperation op)
    {
        try { await _operationStore.SaveAsync(op, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.Warn(LogCategory, $"Could not persist operation {op.Id}: {ex.Message}"); }
    }
}
