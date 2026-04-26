namespace TestControllerGrpc.Services;

/// <summary>
/// Published via IEventAggregator when agent locks change.
/// SignalRNotifier broadcasts this to all connected clients.
/// </summary>
public sealed record AgentLocksChangedEvent
{
    /// <summary>The current set of active agent locks at the time of the event.</summary>
    public IReadOnlyList<AgentLockInfo> Locks { get; init; }
        = Array.Empty<AgentLockInfo>();

    /// <summary>Human-readable reason describing why the lock state changed (e.g. "Acquired", "Released", "Expired").</summary>
    public string Reason { get; init; } = "";

    /// <summary>UTC timestamp at which the change was observed.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>Serializable lock info for API/SignalR responses.</summary>
public sealed record AgentLockInfo
{
    /// <summary>Logical name of the locked agent.</summary>
    public string AgentName { get; init; } = "";

    /// <summary>Identifier of the execution session that owns the lock.</summary>
    public string SessionId { get; init; } = "";

    /// <summary>Tag of the watch item whose pipeline acquired the lock.</summary>
    public string WatchItemTag { get; init; } = "";

    /// <summary>Identifier of the user (or service) that initiated the locking pipeline.</summary>
    public string UserId { get; init; } = "";

    /// <summary>Source/origin of the lock request (e.g. "WPF", "WebApi").</summary>
    public string Source { get; init; } = "";

    /// <summary>UTC timestamp at which the lock was acquired.</summary>
    public DateTime LockedAtUtc { get; init; }

    /// <summary>Human-readable duration the lock has been held.</summary>
    public string Duration { get; init; } = "";
}
