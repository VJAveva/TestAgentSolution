using Microsoft.Extensions.Hosting;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Locking;

/// <summary>
/// Background service that sweeps expired locks every 5 seconds.
/// Per Pipeline_Lock_Coordination_Spec.md §4.4 and 01_System_Design.md §8.
/// </summary>
public sealed class LockExpirySweeper : IHostedService, IDisposable
{
    private readonly LockRegistry _registry;
    private readonly IAppLogger _logger;
    private Timer? _timer;

    public LockExpirySweeper(LockRegistry registry, IAppLogger logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(Sweep, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        _logger.Info("LockSweeper", "Lock expiry sweeper started (5s cadence, 30s default TTL)");
        return Task.CompletedTask;
    }

    private void Sweep(object? state)
    {
        try
        {
            var count = _registry.ExpireStale();
            if (count > 0)
                _logger.Info("LockSweeper", $"Expired {count} stale lock(s)");
        }
        catch (Exception ex)
        {
            _logger.Error("LockSweeper", "Error during lock expiry sweep", ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        _logger.Info("LockSweeper", "Lock expiry sweeper stopped");
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();
}
