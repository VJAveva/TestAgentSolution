namespace TestController.WebApi.Tests;

/// <summary>
/// Validates the fleet snapshot caching pattern used by ControllerHub.
/// At scale, multiple browser clients (re)connecting simultaneously all
/// request the fleet snapshot, causing O(agents × clients) rebuilds.
/// With a 2-second cache, only 1 rebuild occurs per window.
/// Regression: Without cache, 10 browser tabs × 200 agents = 2000 LINQ traversals on connect.
/// </summary>
public class FleetSnapshotCacheTests
{
    [Fact]
    public void Cache_WithinTTL_ReturnsSameInstance()
    {
        var cache = new SnapshotCache<string>(TimeSpan.FromSeconds(2));

        var first = cache.GetOrBuild(() => "snapshot-" + Guid.NewGuid());
        var second = cache.GetOrBuild(() => "snapshot-" + Guid.NewGuid());

        Assert.Same(first, second); // Same instance = cache hit
    }

    [Fact]
    public void Cache_AfterExpiry_Rebuilds()
    {
        var cache = new SnapshotCache<string>(TimeSpan.FromMilliseconds(50));

        var first = cache.GetOrBuild(() => "v1");
        Thread.Sleep(100); // Wait past TTL
        var second = cache.GetOrBuild(() => "v2");

        Assert.Equal("v1", first);
        Assert.Equal("v2", second); // Rebuilt with new value
    }

    [Fact]
    public void Cache_ConcurrentAccess_OnlyBuildsOnce()
    {
        var cache = new SnapshotCache<string>(TimeSpan.FromSeconds(5));
        int buildCount = 0;

        // 20 concurrent callers all requesting at same time
        var results = new string[20];
        Parallel.For(0, 20, i =>
        {
            results[i] = cache.GetOrBuild(() =>
            {
                Interlocked.Increment(ref buildCount);
                Thread.Sleep(10); // Simulate build cost
                return "cached-value";
            });
        });

        // All got the same value, builder ran at most once
        Assert.All(results, r => Assert.Equal("cached-value", r));
        Assert.Equal(1, buildCount);
    }

    /// <summary>
    /// Minimal cache implementation mirroring ControllerHub's static cache pattern.
    /// </summary>
    private sealed class SnapshotCache<T> where T : class
    {
        private readonly object _lock = new();
        private T? _cached;
        private DateTime _expiry = DateTime.MinValue;
        private readonly TimeSpan _ttl;

        public SnapshotCache(TimeSpan ttl) => _ttl = ttl;

        public T GetOrBuild(Func<T> builder)
        {
            lock (_lock)
            {
                if (_cached != null && DateTime.UtcNow < _expiry)
                    return _cached;

                _cached = builder();
                _expiry = DateTime.UtcNow + _ttl;
                return _cached;
            }
        }
    }
}
