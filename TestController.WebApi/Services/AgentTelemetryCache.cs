using System.Collections.Concurrent;

namespace TestController.WebApi.Services;

/// <summary>
/// Caches the last-known telemetry response for each agent.
/// Used to serve telemetry when an agent is locked/executing
/// without disturbing the running gRPC stream.
/// </summary>
public sealed class AgentTelemetryCache
{
    private readonly ConcurrentDictionary<string, CachedTelemetry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public void Update(string agentName, AgentTelemetryResponse telemetry)
    {
        _cache[agentName] = new CachedTelemetry(telemetry, DateTime.UtcNow);
    }

    public CachedTelemetry? Get(string agentName)
    {
        _cache.TryGetValue(agentName, out var cached);
        return cached;
    }
}

public sealed record CachedTelemetry(AgentTelemetryResponse Response, DateTime CachedAtUtc);

public sealed class AgentTelemetryResponse
{
    public string AgentName { get; init; } = "";
    public string State { get; init; } = "";
    public string CurrentActivity { get; init; } = "";
    public string CurrentCommand { get; init; } = "";
    public int ExecutionsCompleted { get; init; }
    public int ExecutionsFailed { get; init; }
    public double CpuUsagePct { get; init; }
    public double MemoryUsedMb { get; init; }
    public double MemoryTotalMb { get; init; }
    public double DiskFreeGb { get; init; }
    public int ActiveProcessCount { get; init; }
    public DateTime Timestamp { get; init; }
    public bool IsOffline { get; init; }
    public string? Error { get; init; }
    public DateTime? LastSeenUtc { get; init; }
}
