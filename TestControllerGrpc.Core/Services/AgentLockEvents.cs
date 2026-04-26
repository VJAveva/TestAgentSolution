namespace TestControllerGrpc.Services;

/// <summary>
/// Published via IEventAggregator when agent locks change.
/// SignalRNotifier broadcasts this to all connected clients.
/// </summary>
public sealed record AgentLocksChangedEvent
{
    public IReadOnlyList<AgentLockInfo> Locks { get; init; }
        = Array.Empty<AgentLockInfo>();
    public string Reason { get; init; } = "";
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>Serializable lock info for API/SignalR responses.</summary>
public sealed record AgentLockInfo
{
    public string AgentName { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string WatchItemTag { get; init; } = "";
    public string UserId { get; init; } = "";
    public string Source { get; init; } = "";
    public DateTime LockedAtUtc { get; init; }
    public string Duration { get; init; } = "";
}
