using Microsoft.Extensions.Hosting;
using TestControllerGrpc.Core.Maintenance;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Services;

/// <summary>
/// Reconciles maintenance operations orphaned by a controller crash or restart, the maintenance-layer
/// counterpart to <c>LockRecoveryService</c>.
/// </summary>
/// <remarks>
/// The engine's in-flight state is process-local: <see cref="IMaintenanceStateStore"/> and
/// <c>FleetMaintenanceService._active</c> are both in memory. A crash mid-revert therefore leaves the node
/// reading as <see cref="MaintenanceState.None"/> - dispatchable - while its VM may still be reverting, and
/// leaves a row stuck at Running in history forever.
///
/// Dispatching a run to a half-reverted machine produces false failures that look like product bugs, so the
/// node is quarantined rather than silently returned to the pool. Whether the revert actually completed is
/// not knowable from here; an operator clears it with ClearQuarantineAsync once they have checked.
/// </remarks>
public sealed class MaintenanceRecoveryService : BackgroundService
{
    private readonly IMaintenanceOperationStore _store;
    private readonly IMaintenanceStateStore _stateStore;
    private readonly IAppLogger _logger;

    private const string LogCategory = "MaintenanceRecovery";

    public MaintenanceRecoveryService(
        IMaintenanceOperationStore store,
        IMaintenanceStateStore stateStore,
        IAppLogger logger)
    {
        _store = store;
        _stateStore = stateStore;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => RecoverAsync(stoppingToken);

    /// <summary>
    /// Runs one reconciliation pass. Separate from <see cref="ExecuteAsync"/> so it can be invoked directly:
    /// BackgroundService start/stop gives no guarantee the body has run by the time StartAsync returns.
    /// Idempotent - a pass interrupted by shutdown is simply redone on the next start.
    /// </summary>
    internal async Task RecoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<MaintenanceOperation> orphans = await _store.GetUnfinishedAsync(cancellationToken).ConfigureAwait(false);
            if (orphans.Count == 0)
            {
                _logger.Info(LogCategory, "No interrupted maintenance operations to recover.");
                return;
            }

            _logger.Warn(LogCategory,
                $"{orphans.Count} maintenance operation(s) were interrupted by a controller restart; quarantining their nodes.");

            foreach (MaintenanceOperation op in orphans)
            {
                MaintenanceOperation failed = op with
                {
                    State = MaintenanceOperationState.Failed,
                    FailurePhase = op.Phase,
                    CompletedUtc = DateTimeOffset.UtcNow,
                };

                try { await _store.SaveAsync(failed, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) { _logger.Error(LogCategory, $"Could not close operation {op.Id} for '{op.NodeId}'.", ex); }

                _stateStore.Set(op.NodeId, MaintenanceState.Quarantined);
                _logger.Warn(LogCategory,
                    $"'{op.NodeId}': {op.Kind} interrupted in phase {op.Phase}. Node quarantined - verify the machine, " +
                    "then clear the quarantine from the Fleet panel to return it to rotation.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown during startup recovery.
        }
        catch (Exception ex)
        {
            // Recovery is best-effort; a failure here must not stop the controller from starting.
            _logger.Error(LogCategory, "Maintenance recovery failed.", ex);
        }
    }
}
