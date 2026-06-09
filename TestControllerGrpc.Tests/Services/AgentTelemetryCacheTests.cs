using TestAgentGrpc;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Tests for <see cref="AgentTelemetryCacheService"/> — validates cache update,
/// retrieval, staleness detection, and remove operations.
/// </summary>
public class AgentTelemetryCacheTests
{
    private readonly AgentTelemetryCacheService _cache = new();

    [Fact]
    public void GetCachedSnapshot_ReturnsNull_WhenNeverUpdated()
    {
        Assert.Null(_cache.GetCachedSnapshot("Agent1"));
    }

    [Fact]
    public void GetCachedMetrics_ReturnsNull_WhenNeverUpdated()
    {
        Assert.Null(_cache.GetCachedMetrics("Agent1"));
    }

    [Fact]
    public void Update_StoresSnapshotAndMetrics()
    {
        var snapshot = new AgentSnapshot { AgentName = "Agent1", State = AgentState.Ready };
        var metrics = new ResourceMetrics { CpuUsagePct = 42.5, MemoryUsedMb = 1024 };

        _cache.Update("Agent1", snapshot, metrics);

        Assert.Equal(snapshot, _cache.GetCachedSnapshot("Agent1"));
        Assert.Equal(metrics, _cache.GetCachedMetrics("Agent1"));
    }

    [Fact]
    public void Update_OverwritesPreviousEntry()
    {
        var old = new AgentSnapshot { AgentName = "Agent1", State = AgentState.Ready };
        var updated = new AgentSnapshot { AgentName = "Agent1", State = AgentState.Running };

        _cache.Update("Agent1", old, null);
        _cache.Update("Agent1", updated, null);

        Assert.Equal(AgentState.Running, _cache.GetCachedSnapshot("Agent1")!.State);
    }

    [Fact]
    public void IsCaseInsensitive()
    {
        var snapshot = new AgentSnapshot { AgentName = "TestNode", State = AgentState.Ready };
        _cache.Update("TestNode", snapshot, null);

        Assert.NotNull(_cache.GetCachedSnapshot("testnode"));
        Assert.NotNull(_cache.GetCachedSnapshot("TESTNODE"));
    }

    [Fact]
    public void IsStale_ReturnsTrue_WhenNeverUpdated()
    {
        Assert.True(_cache.IsStale("Unknown", TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void IsStale_ReturnsFalse_WhenRecentlyUpdated()
    {
        var snapshot = new AgentSnapshot { AgentName = "Agent1", State = AgentState.Ready };
        _cache.Update("Agent1", snapshot, null);

        Assert.False(_cache.IsStale("Agent1", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void GetLastUpdatedUtc_ReturnsTimestamp_AfterUpdate()
    {
        var before = DateTime.UtcNow;
        var snapshot = new AgentSnapshot { AgentName = "Agent1", State = AgentState.Ready };
        _cache.Update("Agent1", snapshot, null);
        var after = DateTime.UtcNow;

        var ts = _cache.GetLastUpdatedUtc("Agent1");
        Assert.NotNull(ts);
        Assert.InRange(ts.Value, before, after);
    }

    [Fact]
    public void GetLastUpdatedUtc_ReturnsNull_WhenNeverUpdated()
    {
        Assert.Null(_cache.GetLastUpdatedUtc("Ghost"));
    }

    [Fact]
    public void Remove_ClearsEntry()
    {
        var snapshot = new AgentSnapshot { AgentName = "Agent1", State = AgentState.Ready };
        _cache.Update("Agent1", snapshot, null);

        _cache.Remove("Agent1");

        Assert.Null(_cache.GetCachedSnapshot("Agent1"));
        Assert.True(_cache.IsStale("Agent1", TimeSpan.Zero));
    }

    [Fact]
    public void Remove_DoesNotThrow_WhenNotPresent()
    {
        _cache.Remove("NonExistent"); // Should not throw
    }

    [Fact]
    public void MultipleAgents_Isolated()
    {
        var snap1 = new AgentSnapshot { AgentName = "A1", State = AgentState.Ready };
        var snap2 = new AgentSnapshot { AgentName = "A2", State = AgentState.Running };

        _cache.Update("A1", snap1, null);
        _cache.Update("A2", snap2, null);

        Assert.Equal(AgentState.Ready, _cache.GetCachedSnapshot("A1")!.State);
        Assert.Equal(AgentState.Running, _cache.GetCachedSnapshot("A2")!.State);

        _cache.Remove("A1");
        Assert.Null(_cache.GetCachedSnapshot("A1"));
        Assert.NotNull(_cache.GetCachedSnapshot("A2"));
    }
}
