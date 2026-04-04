using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Tests for the architectural gap fixes:
///   GAP 10: Thread-safe ActionResults (ConcurrentBag)
///   GAP 6:  Async EventAggregator dispatch
///   GAP 5:  O(1) agent lookup (covered by existing functional tests)
/// </summary>
public class ScalabilityFixTests
{
    // ???????????????????????????????????????????????????????????????????
    // GAP 10: ExecutionSession.AddResult is thread-safe
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void AddResult_Should_NotLoseItems_When_CalledFromMultipleThreads()
    {
        // Simulates parallel action groups recording results concurrently
        var session = new ExecutionSession { WatchItemTag = "ParallelTest" };
        const int threadCount = 50;
        const int itemsPerThread = 100;

        var barrier = new Barrier(threadCount);
        var threads = Enumerable.Range(0, threadCount).Select(t => Task.Run(() =>
        {
            barrier.SignalAndWait(); // Synchronize start for maximum contention
            for (var i = 0; i < itemsPerThread; i++)
            {
                session.AddResult(new ActionExecutionResult
                {
                    ActionTag = $"t{t}-{i}",
                    Outcome = i % 2 == 0 ? ActionOutcome.Success : ActionOutcome.Failed,
                });
            }
        })).ToArray();

        Task.WaitAll(threads);

        Assert.Equal(threadCount * itemsPerThread, session.TotalActions);
    }

    [Fact]
    public void ActionResults_Should_BeReadableWhileWriting_When_EnumeratedConcurrently()
    {
        // Verifies that iterating ActionResults while another thread adds
        // does not throw (ConcurrentBag takes a snapshot for enumeration)
        var session = new ExecutionSession { WatchItemTag = "ReadWriteTest" };
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Writer thread
        var writer = Task.Run(() =>
        {
            var i = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                session.AddResult(new ActionExecutionResult
                {
                    ActionTag = $"item-{i++}",
                    Outcome = ActionOutcome.Success,
                });
            }
        }, cts.Token);

        // Reader thread — should never throw
        var reader = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                // These enumerate ActionResults internally
                _ = session.TotalActions;
                _ = session.SucceededCount;
                _ = session.FailedCount;
                _ = session.FailedActions.ToList();
            }
        }, cts.Token);

        // Should complete without exceptions
        Assert.True(Task.WaitAll([writer, reader], TimeSpan.FromSeconds(5)));
        Assert.True(session.TotalActions > 0);
    }

    [Fact]
    public void RecordResult_Should_BeThreadSafe_When_CalledViaSessionManager()
    {
        var mgr = new ExecutionSessionManager();
        var session = mgr.BeginSession("ConcurrentItem", "Renamed",
            new Dictionary<string, string>(), []);

        const int count = 500;
        var tasks = Enumerable.Range(0, count).Select(i => Task.Run(() =>
        {
            mgr.RecordResult(session.SessionId, new ActionExecutionResult
            {
                ActionTag = $"action-{i}",
                Outcome = i % 3 == 0 ? ActionOutcome.Failed : ActionOutcome.Success,
            });
        })).ToArray();

        Task.WaitAll(tasks);

        Assert.Equal(count, session.TotalActions);
        mgr.CompleteSession(session.SessionId);
        Assert.NotNull(session.CompletedUtc);
    }

    // ???????????????????????????????????????????????????????????????????
    // GAP 6: EventAggregator publishes asynchronously
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public async Task Publish_Should_NotBlockCaller_When_HandlerIsSlow()
    {
        var aggregator = new EventAggregator();
        var handlerStarted = new ManualResetEventSlim(false);
        var handlerCompleted = new ManualResetEventSlim(false);

        aggregator.Subscribe<string>(msg =>
        {
            handlerStarted.Set();
            Thread.Sleep(500); // Simulate slow handler
            handlerCompleted.Set();
        });

        // Publish should return immediately (handler runs on ThreadPool)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        aggregator.Publish("test-event");
        sw.Stop();

        // Publish should return in well under 100ms (handler takes 500ms)
        Assert.True(sw.ElapsedMilliseconds < 100,
            $"Publish took {sw.ElapsedMilliseconds}ms — should be nearly instant");

        // Handler should still eventually complete
        Assert.True(handlerStarted.Wait(TimeSpan.FromSeconds(2)), "Handler was never started");
        Assert.True(handlerCompleted.Wait(TimeSpan.FromSeconds(2)), "Handler never completed");
    }

    [Fact]
    public async Task Publish_Should_DeliverToAllSubscribers_When_MultipleSubscribed()
    {
        var aggregator = new EventAggregator();
        var received = new System.Collections.Concurrent.ConcurrentBag<int>();

        for (var i = 0; i < 10; i++)
        {
            var id = i;
            aggregator.Subscribe<string>(_ => received.Add(id));
        }

        aggregator.Publish("broadcast");

        // Wait for all handlers to complete on the ThreadPool
        await Task.Delay(500);

        Assert.Equal(10, received.Count);
        Assert.Equal(Enumerable.Range(0, 10).ToHashSet(), received.ToHashSet());
    }

    [Fact]
    public void Publish_Should_NotCrash_When_HandlerThrows()
    {
        var aggregator = new EventAggregator();
        var secondHandlerCalled = new ManualResetEventSlim(false);

        aggregator.Subscribe<string>(_ => throw new InvalidOperationException("boom"));
        aggregator.Subscribe<string>(_ => secondHandlerCalled.Set());

        // Should not throw even though first handler throws
        aggregator.Publish("test");

        Assert.True(secondHandlerCalled.Wait(TimeSpan.FromSeconds(2)),
            "Second handler should still be called despite first handler throwing");
    }

    [Fact]
    public void Unsubscribe_Should_StopDelivery_When_TokenDisposed()
    {
        var aggregator = new EventAggregator();
        var callCount = 0;

        var token = aggregator.Subscribe<string>(_ => Interlocked.Increment(ref callCount));

        aggregator.Publish("first");
        Thread.Sleep(200); // Wait for async dispatch
        Assert.Equal(1, callCount);

        token.Dispose();

        aggregator.Publish("second");
        Thread.Sleep(200);
        Assert.Equal(1, callCount); // Should not have been called again
    }

    // ???????????????????????????????????????????????????????????????????
    // GAP 10: ExecutionSession computed properties with ConcurrentBag
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void ExecutionSession_Should_ComputeCorrectCounts_When_UsingAddResult()
    {
        var session = new ExecutionSession { WatchItemTag = "CountTest" };
        session.AddResult(new ActionExecutionResult { Outcome = ActionOutcome.Success });
        session.AddResult(new ActionExecutionResult { Outcome = ActionOutcome.Success });
        session.AddResult(new ActionExecutionResult { Outcome = ActionOutcome.Failed });
        session.AddResult(new ActionExecutionResult { Outcome = ActionOutcome.TimedOut });

        Assert.Equal(4, session.TotalActions);
        Assert.Equal(2, session.SucceededCount);
        Assert.Equal(2, session.FailedCount);
        Assert.Equal(2, session.FailedActions.Count());
    }

    [Fact]
    public void ExecutionSession_Should_ExposeReadOnlyResults_When_Queried()
    {
        var session = new ExecutionSession { WatchItemTag = "ReadOnly" };
        session.AddResult(new ActionExecutionResult
        {
            ActionTag = "test",
            Outcome = ActionOutcome.Success,
        });

        // ActionResults should be IReadOnlyCollection — no Add method exposed
        IReadOnlyCollection<ActionExecutionResult> results = session.ActionResults;
        Assert.Single(results);
    }
}
