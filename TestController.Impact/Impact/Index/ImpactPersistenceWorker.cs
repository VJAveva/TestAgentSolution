using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Impact.Index;

/// <summary>
/// Restores the learning store on startup when the local copy is gone (the state after a VM snapshot revert)
/// and mirrors it to the network share on an interval.
/// </summary>
/// <remarks>
/// The interval is the real protection: a snapshot revert is unannounced, so a backup taken only on graceful
/// shutdown would never run. A final backup on shutdown is still attempted for the ordinary case.
/// </remarks>
public sealed class ImpactPersistenceWorker : BackgroundService
{
    private readonly IImpactPersistenceService _persistence;
    private readonly ImpactPersistenceOptions _options;
    private readonly IAppLogger _logger;

    private const string LogCategory = "ImpactPersistence";

    public ImpactPersistenceWorker(
        IImpactPersistenceService persistence,
        IOptions<ImpactPersistenceOptions> options,
        IAppLogger logger)
    {
        _persistence = persistence;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.NetworkPath))
        {
            _logger.Info(LogCategory, "Impact persistence disabled; learning store is local-only.");
            return;
        }

        _logger.Info(LogCategory, $"Impact persistence active: '{_options.NetworkPath}', every {_options.BackupInterval}.");

        // Restore first: if this host came back from a revert, the local store is missing and the run
        // outcomes only exist on the share.
        PersistenceResult restore = await _persistence.RestoreIfMissingAsync(stoppingToken).ConfigureAwait(false);
        if (!restore.Succeeded) _logger.Warn(LogCategory, restore.Message);

        try { await Task.Delay(_options.StartupDelay, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            // Backup first so this host's newest rows are on the share before the union runs.
            try { await _persistence.BackupAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.Warn(LogCategory, $"Scheduled backup failed: {ex.Message}"); }

            try
            {
                MergeResult merge = await _persistence.MergeAsync(stoppingToken).ConfigureAwait(false);
                if (!merge.Succeeded) _logger.Warn(LogCategory, merge.Message);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.Warn(LogCategory, $"Scheduled merge failed: {ex.Message}"); }

            try { await Task.Delay(_options.BackupInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_options.Enabled && !string.IsNullOrWhiteSpace(_options.NetworkPath))
        {
            // Bounded: shutdown must not hang on an unreachable share.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            try { await _persistence.BackupAsync(cts.Token).ConfigureAwait(false); }
            catch (Exception ex) { _logger.Warn(LogCategory, $"Shutdown backup skipped: {ex.Message}"); }
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
