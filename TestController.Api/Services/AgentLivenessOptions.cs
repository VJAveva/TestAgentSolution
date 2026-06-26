namespace TestController.Api.Services;

/// <summary>
/// Configuration options for <see cref="AgentLivenessMonitor"/>.
/// Controls how often idle agents are actively probed so the fleet screen
/// reflects a dead agent within seconds instead of waiting for a run to hang.
/// Bound from configuration section "Controller:AgentLiveness".
/// </summary>
public sealed class AgentLivenessOptions
{
    public const string SectionName = "Controller:AgentLiveness";

    /// <summary>Enable periodic liveness probing of idle agents (default: true).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds between liveness sweeps (default: 15).</summary>
    public int IntervalSeconds { get; set; } = 15;

    /// <summary>Startup delay before the first sweep (default: 10 seconds).</summary>
    public int StartupDelaySeconds { get; set; } = 10;

    /// <summary>Maximum concurrent agent probes per sweep (default: 8).</summary>
    public int MaxConcurrentProbes { get; set; } = 8;
}
