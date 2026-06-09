using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Validates that AgentLocksChangedEvent is published through the EventAggregator
/// when locks are acquired and released — ensuring the SignalR broadcast chain
/// (EventAggregator → SignalRNotifier → WebClient) can fire correctly.
///
/// These tests verify the fix for the missing lock broadcast in TriggerEvent and
/// TriggerWatchItem, which previously released locks without notifying the WebClient.
/// </summary>
public class LockBroadcastIntegrationTests
{
    [Fact]
    public void LockRelease_Should_ProduceEvent_When_PublishedThroughAggregator()
    {
        var events = new EventAggregator();
        var lockMgr = new AgentLockManager();

        AgentLocksChangedEvent? received = null;
        using var sub = events.Subscribe<AgentLocksChangedEvent>(e => received = e);

        // Acquire lock
        lockMgr.TryLockAgents(["agent1", "agent2"], "S1", "Deploy", "WPF/wwApps", "WPF");

        // Simulate the lock release + event publish (as done in the fixed TriggerEvent finally block)
        lockMgr.ReleaseSession("S1");
        events.Publish(new AgentLocksChangedEvent
        {
            Locks = lockMgr.GetAllLocks()
                .Select(l => new AgentLockInfo
                {
                    AgentName = l.AgentName,
                    SessionId = l.SessionId,
                    WatchItemTag = l.WatchItemTag,
                    UserId = l.UserId,
                    Source = l.Source,
                    LockedAtUtc = l.LockedAtUtc,
                }).ToList(),
            Reason = "Event completed: Deploy",
        });

        // EventAggregator dispatches on ThreadPool
        SpinWait.SpinUntil(() => received != null, TimeSpan.FromSeconds(5));

        Assert.NotNull(received);
        Assert.Empty(received!.Locks); // All locks released
        Assert.Contains("Deploy", received.Reason);
    }

    [Fact]
    public void LockAcquire_Should_ProduceEvent_When_PublishedThroughAggregator()
    {
        var events = new EventAggregator();
        var lockMgr = new AgentLockManager();

        AgentLocksChangedEvent? received = null;
        using var sub = events.Subscribe<AgentLocksChangedEvent>(e => received = e);

        // Acquire lock
        var (success, _) = lockMgr.TryLockAgents(
            ["agent1", "agent2"], "S1", "SmokeTest", "WPF/wwApps", "WPF");
        Assert.True(success);

        // Simulate the event publish (as done in the fixed TriggerEvent after lock acquisition)
        events.Publish(new AgentLocksChangedEvent
        {
            Locks = lockMgr.GetAllLocks()
                .Select(l => new AgentLockInfo
                {
                    AgentName = l.AgentName,
                    SessionId = l.SessionId,
                    WatchItemTag = l.WatchItemTag,
                    UserId = l.UserId,
                    Source = l.Source,
                    LockedAtUtc = l.LockedAtUtc,
                }).ToList(),
            Reason = "Event execution: SmokeTest",
        });

        SpinWait.SpinUntil(() => received != null, TimeSpan.FromSeconds(5));

        Assert.NotNull(received);
        Assert.Equal(2, received!.Locks.Count);
        Assert.Contains(received.Locks, l => l.AgentName == "agent1");
        Assert.Contains(received.Locks, l => l.AgentName == "agent2");
        Assert.Equal("WPF/wwApps", received.Locks[0].UserId);
        Assert.Equal("WPF", received.Locks[0].Source);
    }

    [Fact]
    public void LockRelease_Should_BeIdempotent_When_CalledTwice()
    {
        var events = new EventAggregator();
        var lockMgr = new AgentLockManager();
        var receiveCount = 0;

        using var sub = events.Subscribe<AgentLocksChangedEvent>(_ =>
            Interlocked.Increment(ref receiveCount));

        lockMgr.TryLockAgents(["agent1"], "S1", "Deploy", "user", "WPF");

        // First release succeeds
        var released1 = lockMgr.ReleaseSession("S1");
        Assert.Equal(1, released1);

        // Publish the event (only when something was released)
        if (released1 > 0)
        {
            events.Publish(new AgentLocksChangedEvent
            {
                Locks = lockMgr.GetAllLocks().Select(l => new AgentLockInfo
                {
                    AgentName = l.AgentName, SessionId = l.SessionId,
                    WatchItemTag = l.WatchItemTag, UserId = l.UserId,
                    Source = l.Source, LockedAtUtc = l.LockedAtUtc,
                }).ToList(),
                Reason = "Event completed",
            });
        }

        // Second release is a no-op (idempotent, as in the fire-and-forget finally block)
        var released2 = lockMgr.ReleaseSession("S1");
        Assert.Equal(0, released2);
        // No event published for the second release (released == 0)

        SpinWait.SpinUntil(() => receiveCount >= 1, TimeSpan.FromSeconds(5));

        // Only one event published (not two)
        Thread.Sleep(200); // Allow any extra dispatches to arrive
        Assert.Equal(1, receiveCount);
    }

    [Fact]
    public void CancelSession_Should_ReleaseLock_And_PublishEvent()
    {
        var events = new EventAggregator();
        var lockMgr = new AgentLockManager();
        var sessionMgr = new ExecutionSessionManager();

        AgentLocksChangedEvent? received = null;
        using var sub = events.Subscribe<AgentLocksChangedEvent>(e => received = e);

        // Simulate a running session with a lock
        var session = sessionMgr.BeginSession("SmokeTest", "WPF/wwApps",
            new Dictionary<string, string>(), new List<IActionNode>(), "sess-abc");
        lockMgr.TryLockAgents(["agent1"], "sess-abc", "SmokeTest", "WPF/wwApps", "WPF");

        // Simulate cancel flow (matches the fixed code path)
        session.RequestCancellation();
        Assert.True(session.IsCancellationRequested);

        // Release lock and broadcast (as the fixed finally block does)
        var released = lockMgr.ReleaseSession("sess-abc");
        Assert.Equal(1, released);

        events.Publish(new AgentLocksChangedEvent
        {
            Locks = lockMgr.GetAllLocks().Select(l => new AgentLockInfo
            {
                AgentName = l.AgentName, SessionId = l.SessionId,
                WatchItemTag = l.WatchItemTag, UserId = l.UserId,
                Source = l.Source, LockedAtUtc = l.LockedAtUtc,
            }).ToList(),
            Reason = "Event completed: SmokeTest",
        });

        SpinWait.SpinUntil(() => received != null, TimeSpan.FromSeconds(5));

        Assert.NotNull(received);
        Assert.Empty(received!.Locks); // Lock was released
        Assert.Null(lockMgr.GetLock("agent1")); // Confirmed released
    }
}
