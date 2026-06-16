using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Services;

namespace TestController.Persistence.Audit;

/// <summary>
/// Background service that enforces audit log retention policy by deleting
/// AuditEntries older than the configured retention period (default 365 days).
/// Runs once daily. Primary host only.
/// </summary>
public sealed class AuditRetentionWorker : BackgroundService
{
    private readonly IDbContextFactory<OrchestratorDbContext> _dbFactory;
    private readonly IAppLogger _logger;
    private readonly TimeSpan _retentionPeriod;
    private static readonly TimeSpan RunInterval = TimeSpan.FromHours(24);

    public AuditRetentionWorker(
        IDbContextFactory<OrchestratorDbContext> dbFactory,
        IAppLogger logger,
        IOptions<AuditRetentionOptions> options)
    {
        _dbFactory = dbFactory;
        _logger = logger;
        _retentionPeriod = TimeSpan.FromDays(options.Value.RetentionDays);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait a bit on startup to let DB initialization settle
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cutoff = DateTime.UtcNow - _retentionPeriod;
                await using var db = await _dbFactory.CreateDbContextAsync(stoppingToken);

                var deleted = await db.AuditEntries
                    .Where(a => a.TimestampUtc < cutoff)
                    .ExecuteDeleteAsync(stoppingToken);

                if (deleted > 0)
                    _logger.Info("AuditRetention", $"Purged {deleted} audit entries older than {cutoff:u}");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error("AuditRetention", "Retention purge failed", ex);
            }

            await Task.Delay(RunInterval, stoppingToken);
        }
    }
}

/// <summary>Configurable audit retention settings. Bind from "Audit" config section.</summary>
public sealed class AuditRetentionOptions
{
    public int RetentionDays { get; set; } = 365;
}
