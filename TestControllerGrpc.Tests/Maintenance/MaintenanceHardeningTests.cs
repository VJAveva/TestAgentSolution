using Moq;
using TestControllerGrpc.Core.Maintenance;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Maintenance;

/// <summary>
/// Regression cover for the hardening pass over the fleet maintenance core: the state store's read-modify-write
/// race, the readiness probe's two timing/cancellation faults, and the snooze that silently discarded its duration.
/// </summary>
public class MaintenanceHardeningTests
{
    // ── MaintenanceStateStore: it is the authority the dispatch gate reads, so a lost write lets work onto
    //    a node that is mid-operation. ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Set_Should_RaiseWithCorrectPrevious_When_StateTransitions()
    {
        var store = new MaintenanceStateStore();
        var events = new List<NodeMaintenanceStateChanged>();
        store.Changed += (_, e) => events.Add(e);

        store.Set("NodeA", MaintenanceState.Reverting);
        store.Set("NodeA", MaintenanceState.Quarantined);
        store.Set("NodeA", MaintenanceState.None);

        Assert.Equal(3, events.Count);
        Assert.Equal((MaintenanceState.None, MaintenanceState.Reverting), (events[0].Previous, events[0].Current));
        Assert.Equal((MaintenanceState.Reverting, MaintenanceState.Quarantined), (events[1].Previous, events[1].Current));
        Assert.Equal((MaintenanceState.Quarantined, MaintenanceState.None), (events[2].Previous, events[2].Current));
        Assert.Equal(MaintenanceState.None, store.Get("NodeA"));
    }

    [Fact]
    public void Set_Should_NotRaise_When_StateUnchanged()
    {
        var store = new MaintenanceStateStore();
        store.Set("NodeA", MaintenanceState.Quarantined);

        var events = 0;
        store.Changed += (_, _) => events++;
        store.Set("NodeA", MaintenanceState.Quarantined);
        store.Set("NodeA", MaintenanceState.Quarantined);

        Assert.Equal(0, events);
    }

    [Fact]
    public async Task Set_Should_NotLoseWrites_When_NodesWrittenConcurrently()
    {
        const int nodes = 200;
        var store = new MaintenanceStateStore();

        await Task.WhenAll(Enumerable.Range(0, nodes).Select(i => Task.Run(() =>
        {
            var node = $"NODE{i:000}";
            store.Set(node, MaintenanceState.Reverting);
            store.Set(node, MaintenanceState.Quarantined);
        })));

        var snapshot = store.Snapshot();
        Assert.Equal(nodes, snapshot.Count);
        Assert.All(snapshot.Values, s => Assert.Equal(MaintenanceState.Quarantined, s));
    }

    [Fact]
    public async Task Set_Should_RaiseOnce_When_ConcurrentWritersSetTheSameState()
    {
        const int rounds = 200;
        const int writers = 8;
        var store = new MaintenanceStateStore();
        var quarantineEvents = 0;
        store.Changed += (_, e) =>
        {
            if (e.Current == MaintenanceState.Quarantined) Interlocked.Increment(ref quarantineEvents);
        };

        // Each round is exactly one None -> Quarantined transition, however many writers race to make it.
        // Unsynchronised, every writer reads previous=None before any of them writes, so all of them raise —
        // the fleet then sees N "node quarantined" notifications for a single quarantine.
        for (var round = 0; round < rounds; round++)
        {
            store.Set("NodeA", MaintenanceState.None);

            using var barrier = new Barrier(writers);
            await Task.WhenAll(Enumerable.Range(0, writers).Select(_ => Task.Run(() =>
            {
                barrier.SignalAndWait();
                store.Set("NodeA", MaintenanceState.Quarantined);
            })));
        }

        Assert.Equal(rounds, quarantineEvents);
    }

    // ── NodeReadinessProbe: a gRPC deadline surfaces as OperationCanceledException and used to be mistaken
    //    for the operator cancelling, aborting the whole agent wait on the first blip. ─────────────────────

    private static NodeReadinessProbe ProbeWith(Mock<IAgentGrpcDispatcher> dispatcher) => new(dispatcher.Object);

    [Fact]
    public async Task WaitForAgentAsync_Should_Succeed_When_AgentAnswers()
    {
        var dispatcher = new Mock<IAgentGrpcDispatcher>();
        dispatcher.Setup(d => d.PingAsync("NodeA", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await ProbeWith(dispatcher).WaitForAgentAsync("NodeA", TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task WaitForAgentAsync_Should_KeepPolling_When_DispatcherReportsDeadlineExceeded()
    {
        var dispatcher = new Mock<IAgentGrpcDispatcher>();
        // A per-call gRPC deadline, NOT the caller's token — the probe must treat this as "not up yet".
        dispatcher.Setup(d => d.PingAsync("NodeA", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("deadline exceeded"));

        var result = await ProbeWith(dispatcher).WaitForAgentAsync("NodeA", TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Assert.False(result.Succeeded);
        // The distinguishing assertion: it timed out on its own terms rather than reporting operator cancellation.
        Assert.Contains("did not reconnect", result.FailureReason);
        Assert.DoesNotContain("Cancelled", result.FailureReason);
    }

    [Fact]
    public async Task WaitForAgentAsync_Should_ReportCancelled_When_CallerTokenIsCancelled()
    {
        var dispatcher = new Mock<IAgentGrpcDispatcher>();
        dispatcher.Setup(d => d.PingAsync("NodeA", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await ProbeWith(dispatcher).WaitForAgentAsync("NodeA", TimeSpan.FromSeconds(10), cts.Token);

        Assert.False(result.Succeeded);
        Assert.Contains("Cancelled", result.FailureReason);
    }

    // ── FleetNotificationService.Snooze took a duration and threw it away, permanently acknowledging instead.
    //    Two callers pass a real one: the 4-hour UI snooze and the API's configurable snooze. ──────────────

    private static FleetNotification Note(string nodeId) => new()
    {
        NodeId = nodeId,
        Kind = MaintenanceEventKind.RebootRequired,
        Title = $"{nodeId} needs a reboot",
    };

    [Fact]
    public void Snooze_Should_MuteWithoutAcknowledging_When_DurationIsPositive()
    {
        var sut = new FleetNotificationService();
        sut.Raise(Note("NodeA"));

        sut.Snooze("NodeA", TimeSpan.FromHours(4));

        var n = Assert.Single(sut.Notifications);
        Assert.False(n.Acknowledged);                     // a snooze is not an acknowledgement
        Assert.NotNull(n.SnoozedUntilUtc);
        Assert.True(n.IsMutedAt(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Snooze_Should_Resurface_When_WindowHasLapsed()
    {
        var sut = new FleetNotificationService();
        sut.Raise(Note("NodeA"));

        sut.Snooze("NodeA", TimeSpan.FromMinutes(30));

        var n = Assert.Single(sut.Notifications);
        Assert.True(n.IsMutedAt(DateTimeOffset.UtcNow));
        Assert.False(n.IsMutedAt(DateTimeOffset.UtcNow.AddHours(1)));   // the whole point of a timed snooze
    }

    [Fact]
    public void Snooze_Should_LeaveOtherNodesUntouched_When_OneNodeSnoozed()
    {
        var sut = new FleetNotificationService();
        sut.Raise(Note("NodeA"));
        sut.Raise(Note("NodeB"));

        sut.Snooze("NodeB", TimeSpan.FromHours(1));

        var now = DateTimeOffset.UtcNow;
        Assert.False(sut.Notifications.Single(n => n.NodeId == "NodeA").IsMutedAt(now));
        Assert.True(sut.Notifications.Single(n => n.NodeId == "NodeB").IsMutedAt(now));
    }

    [Fact]
    public void Acknowledge_Should_MutePermanently_When_Applied()
    {
        var sut = new FleetNotificationService();
        sut.Raise(Note("NodeA"));
        var id = sut.Notifications[0].Id;

        sut.Acknowledge(id);

        var n = Assert.Single(sut.Notifications);
        Assert.True(n.Acknowledged);
        Assert.True(n.IsMutedAt(DateTimeOffset.UtcNow.AddYears(1)));   // unlike a snooze, never lapses
    }
}
