namespace TestController.Api.Services;

/// <summary>
/// Configuration options for <see cref="LockRecoveryService"/>.
/// Supports configurable orphan check interval with exponential backoff
/// when consecutive checks find no orphans.
/// </summary>
public sealed class LockRecoveryOptions
{
    /// <summary>Base interval between orphan detection runs (default: 5 minutes).</summary>
    public TimeSpan OrphanCheckInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Maximum interval after consecutive no-op checks (default: 30 minutes).</summary>
    public TimeSpan MaxBackoffInterval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Multiplier applied after each no-op check (default: 2.0).</summary>
    public double BackoffMultiplier { get; set; } = 2.0;

    /// <summary>Startup delay before first lock validation (default: 10 seconds).</summary>
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(10);
}
