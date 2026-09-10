using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TestControllerGrpc.Core.Impact.Index;

/// <summary>
/// Hosted background service (registered in the WebApi host only) that keeps the retrieval index fresh
/// (P09). On an interval it compares the index build time recorded in metadata against
/// <see cref="ImpactMappingOptions.IndexOptions.MaxAge"/> and triggers an incremental refresh when the
/// index is stale or has never been built. Refresh failures are logged and retried on the next tick —
/// index staleness must never bring the host down. Uses <see cref="ILogger{T}"/> per the hosted-service
/// logging convention rather than the app's <c>IAppLogger</c>.
/// </summary>
public sealed class IndexMaintenanceService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);

    private readonly RetrievalIndexBuilder _builder;
    private readonly IDbContextFactory<ImpactIndexDbContext> _contextFactory;
    private readonly ImpactMappingOptions _options;
    private readonly ILogger<IndexMaintenanceService> _logger;

    /// <summary>Creates the maintenance service.</summary>
    public IndexMaintenanceService(
        RetrievalIndexBuilder builder,
        IDbContextFactory<ImpactIndexDbContext> contextFactory,
        IOptions<ImpactMappingOptions> options,
        ILogger<IndexMaintenanceService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Let host startup complete before a first-time build can start.
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await IsStaleAsync(stoppingToken).ConfigureAwait(false))
                {
                    _logger.LogInformation("Impact index is stale; starting refresh.");
                    IndexBuildResult result = await _builder
                        .BuildAsync(fullRebuild: false, progress: null, stoppingToken)
                        .ConfigureAwait(false);
                    _logger.LogInformation(
                        "Impact index refresh complete: indexed {Indexed}, skipped {Skipped}, elapsed {Elapsed}.",
                        result.DocumentsIndexed, result.DocumentsSkipped, result.Elapsed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Impact index refresh failed; will retry on the next interval.");
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<bool> IsStaleAsync(CancellationToken ct)
    {
        await using ImpactIndexDbContext ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await ImpactIndexInitializer.EnsureCreatedAsync(ctx, _options.Index.RebuildOnSchemaChange, ct).ConfigureAwait(false);

        IndexMetadata? built = await ctx.Metadata
            .FirstOrDefaultAsync(m => m.Key == "BuiltUtc", ct)
            .ConfigureAwait(false);

        if (built is null
            || !DateTimeOffset.TryParse(built.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset builtUtc))
        {
            return true; // never built
        }

        return DateTimeOffset.UtcNow - builtUtc >= _options.Index.MaxAge;
    }
}
