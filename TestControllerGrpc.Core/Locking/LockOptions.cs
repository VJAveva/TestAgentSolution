namespace TestControllerGrpc.Locking;

/// <summary>
/// Configuration for the pipeline lock subsystem.
/// Bound from "Locks" section in appsettings.json.
/// </summary>
public sealed class LockOptions
{
    public const string SectionName = "Locks";

    /// <summary>Seconds after last heartbeat before a lock expires. Default 30.</summary>
    public int HeartbeatTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Seconds between TTL renewals while a run is live. Must be comfortably less than
    /// <see cref="HeartbeatTimeoutSeconds"/> so the expiry sweeper never reaps a lock
    /// mid-run (Impediment #1). Default 10.
    /// </summary>
    public int RenewalIntervalSeconds { get; set; } = 10;
}
