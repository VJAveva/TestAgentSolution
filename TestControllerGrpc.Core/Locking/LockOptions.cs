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
}
