using System.Collections.Concurrent;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Scaling;

/// <summary>
/// Validates that the debounce/coalesce pattern works correctly.
/// We test the concept (timer-based coalescing) without requiring WPF Dispatcher,
/// since FleetVM uses DispatcherTimer internally.
/// Regression: Without debounce, 200 heartbeats/sec cause 200 full Refresh() calls.
/// </summary>
public class FleetVMScalingTests
{
    /// <summary>
    /// Simulates the debounce pattern used by FleetVM.ScheduleRefresh() —
    /// a timer that resets on each request and fires once after quiescence.
    /// </summary>
    private sealed class TimerDebouncer : IDisposable
    {
        private readonly Timer _timer;
        private readonly int _intervalMs;
        private int _fireCount;

        public int FireCount => _fireCount;

        public TimerDebouncer(int intervalMs)
        {
            _intervalMs = intervalMs;
            _timer = new Timer(_ => Interlocked.Increment(ref _fireCount));
        }

        public void Request()
        {
            // Same pattern as ScheduleRefresh: restart timer each time
            _timer.Change(_intervalMs, Timeout.Infinite);
        }

        public void Dispose() => _timer.Dispose();
    }

    [Fact]
    public void RapidHeartbeats_CoalescesToSingleRefresh()
    {
        // Simulate 200 heartbeat events arriving within 50ms (burst)
        using var debouncer = new TimerDebouncer(intervalMs: 250);

        for (int i = 0; i < 200; i++)
        {
            debouncer.Request();
            Thread.Sleep(0); // Yield but don't wait
        }

        // Wait for debounce window to elapse
        Thread.Sleep(500);

        // All 200 events should coalesce to 1 refresh
        Assert.Equal(1, debouncer.FireCount);
    }

    [Fact]
    public void SpacedEvents_EachTriggersRefresh()
    {
        using var debouncer = new TimerDebouncer(intervalMs: 50);

        debouncer.Request();
        Thread.Sleep(150); // Well past debounce window
        debouncer.Request();
        Thread.Sleep(150);
        debouncer.Request();
        Thread.Sleep(150);

        Assert.Equal(3, debouncer.FireCount);
    }

    [Fact]
    public void NoEvents_NoRefresh()
    {
        using var debouncer = new TimerDebouncer(intervalMs: 100);
        Thread.Sleep(300);
        Assert.Equal(0, debouncer.FireCount);
    }

    [Fact]
    public void EventAggregator_CanHandle200Subscribers_WithoutException()
    {
        // Validates that EventAggregator supports the subscription pattern
        // FleetVM uses (one subscription per event type, many publishes)
        var aggregator = new EventAggregator();
        var received = new ConcurrentBag<int>();

        // Subscribe once (like FleetVM does)
        aggregator.Subscribe<TestScaleEvent>(_ => received.Add(1));

        // Fire 200 events in rapid succession (simulating 200 agents heartbeating)
        Parallel.For(0, 200, _ =>
        {
            aggregator.Publish(new TestScaleEvent(Environment.CurrentManagedThreadId));
        });

        // Allow ThreadPool dispatch to complete
        SpinWait.SpinUntil(() => received.Count >= 200, TimeSpan.FromSeconds(5));
        Assert.Equal(200, received.Count);
    }

    private sealed record TestScaleEvent(int Id);
}
