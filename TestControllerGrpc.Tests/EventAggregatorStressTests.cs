using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests;

/// <summary>
/// CORE-005: Stress tests for <see cref="EventAggregator"/> to document
/// behavior under high load and validate bounded semantics.
/// </summary>
public class EventAggregatorStressTests
{
    private record TestEvent(int Value);
    private record AnotherEvent(string Message);

    [Fact]
    public void Publish_SingleSubscriber_ReceivesAllEvents()
    {
        var aggregator = new EventAggregator();
        var received = new System.Collections.Concurrent.ConcurrentBag<int>();
        using var sub = aggregator.Subscribe<TestEvent>(e => received.Add(e.Value));

        for (int i = 0; i < 100; i++)
            aggregator.Publish(new TestEvent(i));

        // Wait for ThreadPool dispatch
        SpinWait.SpinUntil(() => received.Count >= 100, TimeSpan.FromSeconds(5));

        Assert.Equal(100, received.Count);
        Assert.Equal(Enumerable.Range(0, 100), received.OrderBy(x => x));
    }

    [Fact]
    public void Publish_MultipleSubscribers_AllReceiveEvents()
    {
        var aggregator = new EventAggregator();
        var sub1Received = new System.Collections.Concurrent.ConcurrentBag<int>();
        var sub2Received = new System.Collections.Concurrent.ConcurrentBag<int>();

        using var s1 = aggregator.Subscribe<TestEvent>(e => sub1Received.Add(e.Value));
        using var s2 = aggregator.Subscribe<TestEvent>(e => sub2Received.Add(e.Value));

        for (int i = 0; i < 50; i++)
            aggregator.Publish(new TestEvent(i));

        SpinWait.SpinUntil(() => sub1Received.Count >= 50 && sub2Received.Count >= 50,
            TimeSpan.FromSeconds(5));

        Assert.Equal(50, sub1Received.Count);
        Assert.Equal(50, sub2Received.Count);
    }

    [Fact]
    public void Publish_SubscriberThrows_DoesNotAffectOtherSubscribers()
    {
        var aggregator = new EventAggregator();
        var received = new System.Collections.Concurrent.ConcurrentBag<int>();

        using var badSub = aggregator.Subscribe<TestEvent>(_ => throw new InvalidOperationException("Boom"));
        using var goodSub = aggregator.Subscribe<TestEvent>(e => received.Add(e.Value));

        aggregator.Publish(new TestEvent(42));

        SpinWait.SpinUntil(() => received.Count >= 1, TimeSpan.FromSeconds(5));

        Assert.Single(received);
        Assert.Contains(42, received);
    }

    [Fact]
    public void Unsubscribe_StopsReceivingEvents()
    {
        var aggregator = new EventAggregator();
        var received = new System.Collections.Concurrent.ConcurrentBag<int>();

        var sub = aggregator.Subscribe<TestEvent>(e => received.Add(e.Value));
        aggregator.Publish(new TestEvent(1));
        SpinWait.SpinUntil(() => received.Count >= 1, TimeSpan.FromSeconds(2));

        sub.Dispose();
        Thread.Sleep(100); // Allow time for disposal to propagate

        aggregator.Publish(new TestEvent(2));
        Thread.Sleep(500); // Allow time for any late delivery

        Assert.DoesNotContain(2, received);
    }

    [Fact]
    public void Publish_DifferentEventTypes_DoNotInterfere()
    {
        var aggregator = new EventAggregator();
        var testEvents = new System.Collections.Concurrent.ConcurrentBag<int>();
        var anotherEvents = new System.Collections.Concurrent.ConcurrentBag<string>();

        using var s1 = aggregator.Subscribe<TestEvent>(e => testEvents.Add(e.Value));
        using var s2 = aggregator.Subscribe<AnotherEvent>(e => anotherEvents.Add(e.Message));

        aggregator.Publish(new TestEvent(99));
        aggregator.Publish(new AnotherEvent("hello"));

        SpinWait.SpinUntil(() => testEvents.Count >= 1 && anotherEvents.Count >= 1,
            TimeSpan.FromSeconds(5));

        Assert.Single(testEvents);
        Assert.Single(anotherEvents);
    }

    [Fact]
    public async Task HighConcurrency_PublishFromMultipleThreads_NoDataLoss()
    {
        var aggregator = new EventAggregator();
        var received = new System.Collections.Concurrent.ConcurrentBag<int>();
        const int threadCount = 10;
        const int eventsPerThread = 100;
        const int totalExpected = threadCount * eventsPerThread;

        using var sub = aggregator.Subscribe<TestEvent>(e => received.Add(e.Value));

        var threads = Enumerable.Range(0, threadCount).Select(t =>
            Task.Run(() =>
            {
                for (int i = 0; i < eventsPerThread; i++)
                    aggregator.Publish(new TestEvent(t * eventsPerThread + i));
            })).ToArray();

        await Task.WhenAll(threads);

        SpinWait.SpinUntil(() => received.Count >= totalExpected, TimeSpan.FromSeconds(10));

        Assert.Equal(totalExpected, received.Count);
    }

    [Fact]
    public async Task SubscribeAndUnsubscribeDuringPublish_NoExceptions()
    {
        var aggregator = new EventAggregator();
        var received = new System.Collections.Concurrent.ConcurrentBag<int>();
        var subs = new System.Collections.Concurrent.ConcurrentBag<IDisposable>();

        // Continuously subscribe and unsubscribe while publishing
        var publishTask = Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
                aggregator.Publish(new TestEvent(i));
        });

        var subTask = Task.Run(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                var sub = aggregator.Subscribe<TestEvent>(e => received.Add(e.Value));
                Thread.Sleep(1);
                sub.Dispose();
            }
        });

        // Should complete without deadlocks or exceptions
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(publishTask, subTask).WaitAsync(cts.Token);
    }
}
