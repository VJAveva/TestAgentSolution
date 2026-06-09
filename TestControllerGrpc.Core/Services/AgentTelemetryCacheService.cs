using System.Collections.Concurrent;
using TestAgentGrpc;

namespace TestControllerGrpc.Services;

/// <summary>
/// Provides cached telemetry snapshots and metrics for agents so that
/// multiple consumers (WPF Monitor, embedded WebApi, WebClient endpoints)
/// can serve recent data without independent gRPC calls — especially
/// during active execution when polling is suppressed.
/// </summary>
public interface IAgentTelemetryCache
{
    /// <summary>Returns the last cached snapshot for the agent, or null if never polled.</summary>
    AgentSnapshot? GetCachedSnapshot(string agentName);

    /// <summary>Returns the last cached resource metrics for the agent, or null.</summary>
    ResourceMetrics? GetCachedMetrics(string agentName);

    /// <summary>Updates the cache with a fresh snapshot and optional metrics.</summary>
    void Update(string agentName, AgentSnapshot snapshot, ResourceMetrics? metrics);

    /// <summary>Returns true if the cached entry is older than <paramref name="maxAge"/> or missing.</summary>
    bool IsStale(string agentName, TimeSpan maxAge);

    /// <summary>Returns the UTC timestamp when the cache was last updated for the agent, or null.</summary>
    DateTime? GetLastUpdatedUtc(string agentName);

    /// <summary>Removes a cached entry (e.g. on agent unregister).</summary>
    void Remove(string agentName);
}

/// <summary>
/// Thread-safe in-memory telemetry cache backed by <see cref="ConcurrentDictionary{TKey, TValue}"/>.
/// </summary>
public sealed class AgentTelemetryCacheService : IAgentTelemetryCache
{
    private readonly ConcurrentDictionary<string, CachedTelemetry> _cache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record CachedTelemetry(AgentSnapshot Snapshot, ResourceMetrics? Metrics, DateTime CachedUtc);

    public AgentSnapshot? GetCachedSnapshot(string agentName)
        => _cache.TryGetValue(agentName, out var c) ? c.Snapshot : null;

    public ResourceMetrics? GetCachedMetrics(string agentName)
        => _cache.TryGetValue(agentName, out var c) ? c.Metrics : null;

    public void Update(string agentName, AgentSnapshot snapshot, ResourceMetrics? metrics)
        => _cache[agentName] = new CachedTelemetry(snapshot, metrics, DateTime.UtcNow);

    public bool IsStale(string agentName, TimeSpan maxAge)
        => !_cache.TryGetValue(agentName, out var c) || (DateTime.UtcNow - c.CachedUtc) > maxAge;

    public DateTime? GetLastUpdatedUtc(string agentName)
        => _cache.TryGetValue(agentName, out var c) ? c.CachedUtc : null;

    public void Remove(string agentName)
        => _cache.TryRemove(agentName, out _);
}
