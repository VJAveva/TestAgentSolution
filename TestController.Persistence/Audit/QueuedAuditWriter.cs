using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Authorization;

namespace TestController.Persistence.Audit;

/// <summary>
/// Fire-and-forget audit writer. Bounded channel (10K capacity), async background drain.
/// Per 01_System_Design.md §11 — never await in the request path.
/// </summary>
public sealed class QueuedAuditWriter : IAuditWriter
{
    private readonly Channel<AuditEntry> _channel;

    public QueuedAuditWriter(int capacity = 10_000)
    {
        _channel = Channel.CreateBounded<AuditEntry>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
    }

    public void Enqueue(AuditEntry entry)
    {
        _channel.Writer.TryWrite(entry);
    }

    internal ChannelReader<AuditEntry> Reader => _channel.Reader;
}

/// <summary>
/// Background worker that drains the audit queue to SQLite.
/// Per 01_System_Design.md §11.
/// </summary>
public sealed class AuditDrainWorker : BackgroundService
{
    private readonly QueuedAuditWriter _writer;
    private readonly IDbContextFactory<OrchestratorDbContext> _dbFactory;
    private readonly ILogger<AuditDrainWorker> _logger;

    public AuditDrainWorker(
        QueuedAuditWriter writer,
        IDbContextFactory<OrchestratorDbContext> dbFactory,
        ILogger<AuditDrainWorker> logger)
    {
        _writer = writer;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<AuditEntry>(100);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for first item
                if (!await _writer.Reader.WaitToReadAsync(stoppingToken))
                    break;

                // Drain batch
                batch.Clear();
                while (batch.Count < 100 && _writer.Reader.TryRead(out var entry))
                {
                    batch.Add(entry);
                }

                if (batch.Count > 0)
                {
                    await using var db = await _dbFactory.CreateDbContextAsync(stoppingToken);
                    db.AuditEntries.AddRange(batch);
                    await db.SaveChangesAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AuditDrainWorker failed to flush {Count} entries", batch.Count);
                // Avoid tight spin on repeated failure
                await Task.Delay(1000, stoppingToken);
            }
        }

        // Final drain on shutdown
        await FlushRemainingAsync();
    }

    private async Task FlushRemainingAsync()
    {
        var remaining = new List<AuditEntry>();
        while (_writer.Reader.TryRead(out var entry))
            remaining.Add(entry);

        if (remaining.Count > 0)
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(CancellationToken.None);
                db.AuditEntries.AddRange(remaining);
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AuditDrainWorker failed final flush of {Count} entries", remaining.Count);
            }
        }
    }
}
