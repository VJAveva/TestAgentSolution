using Microsoft.Extensions.Logging.Abstractions;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Scaling;

/// <summary>
/// The dispatch cap used to be a per-call <c>SemaphoreSlim(50)</c> created inside each fan-out
/// loop, so it bounded ONE group: two concurrent 50-node stages put 100 actions in flight. These
/// pin the replacement - a process-wide cap taken around each action.
///
/// The permit is deliberately NOT taken around a group's fan-out loop: a global gate there
/// deadlocks on nested parallel groups, because every permit ends up held by an outer child that is
/// itself waiting for its own children to get one. <see cref="Nested_Should_NotDeadlock_When_ParallelGroupsAreNested"/>
/// is the guard for that, and it fails by TIMING OUT rather than asserting, which is why it carries
/// its own cancellation deadline.
/// </summary>
[Collection("PipelineConcurrency")]
public class PipelineConcurrencyTests : IDisposable
{
    public PipelineConcurrencyTests() => PipelineConcurrency.Configure(PipelineConcurrency.DefaultMaxConcurrentActions);

    public void Dispose()
    {
        PipelineConcurrency.Configure(PipelineConcurrency.DefaultMaxConcurrentActions);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Shared by every executor in a test, because summing two executors' individual peaks is NOT
    /// the peak of the sum - each can peak at a different instant and the total never be that high.
    /// </summary>
    private sealed class ConcurrencyMeter
    {
        private int _inFlight;
        public int Peak;
        public int Completed;

        public void Enter()
        {
            var current = Interlocked.Increment(ref _inFlight);
            int peak;
            do { peak = Peak; }
            while (current > peak && Interlocked.CompareExchange(ref Peak, current, peak) != peak);
        }

        public void Exit()
        {
            Interlocked.Decrement(ref _inFlight);
            Interlocked.Increment(ref Completed);
        }
    }

    private sealed class CountingExecutor(ConcurrencyMeter meter, TimeSpan actionDuration)
        : PipelineExecutorBase(new ExecutionSessionManager(), NullLogger.Instance)
    {
        protected override async Task<bool> ExecuteActionAsync(
            ActionConfig action, PipelineExecutionContext ctx, CancellationToken ct)
        {
            meter.Enter();
            try
            {
                await Task.Delay(actionDuration, ct);
                return true;
            }
            finally { meter.Exit(); }
        }

        public Task<bool> RunStage(List<IActionNode> children, CancellationToken ct) =>
            ExecuteChildrenAsync(children, ExecutionMode.Parallel, true, new PipelineExecutionContext(), ct);
    }

    private static List<IActionNode> Actions(int count) =>
        [.. Enumerable.Range(0, count).Select(i => (IActionNode)new ActionConfig { Command = $"cmd{i}" })];

    [Fact]
    public void Default_Should_Stay50_So_ExistingBehaviourIsUnchanged()
    {
        Assert.Equal(50, PipelineConcurrency.DefaultMaxConcurrentActions);
        Assert.Equal(50, PipelineConcurrency.MaxConcurrentActions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Configure_Should_ClampToOne_When_ValueIsNotPositive(int configured)
    {
        // A cap of 0 would wedge every pipeline in the process forever.
        PipelineConcurrency.Configure(configured);

        Assert.Equal(1, PipelineConcurrency.MaxConcurrentActions);
    }

    [Fact]
    public async Task TwoConcurrentStages_Should_NotExceedTheGlobalCap()
    {
        // THE regression: each stage used to get its own semaphore, so N stages meant N x cap.
        const int cap = 4;
        PipelineConcurrency.Configure(cap);

        var meter = new ConcurrencyMeter();
        var left = new CountingExecutor(meter, TimeSpan.FromMilliseconds(40));
        var right = new CountingExecutor(meter, TimeSpan.FromMilliseconds(40));

        await Task.WhenAll(
            left.RunStage(Actions(20), CancellationToken.None),
            right.RunStage(Actions(20), CancellationToken.None));

        Assert.Equal(40, meter.Completed);
        Assert.True(meter.Peak <= cap, $"peak across both stages was {meter.Peak}, cap is {cap}");
    }

    [Fact]
    public async Task SingleStage_Should_StillRunInParallel_When_Capped()
    {
        // Guards the opposite failure: a cap that accidentally serialises everything.
        PipelineConcurrency.Configure(4);
        var meter = new ConcurrencyMeter();
        var executor = new CountingExecutor(meter, TimeSpan.FromMilliseconds(40));

        await executor.RunStage(Actions(20), CancellationToken.None);

        Assert.True(meter.Peak > 1, $"peak was {meter.Peak} - the cap serialised the stage");
        Assert.True(meter.Peak <= 4, $"peak was {meter.Peak}, cap is 4");
    }

    [Fact]
    public async Task Nested_Should_NotDeadlock_When_ParallelGroupsAreNested()
    {
        // With the permit taken around the fan-out loop instead of the action, the outer children
        // would hold both permits while waiting for their own children, and this never returns.
        PipelineConcurrency.Configure(2);
        var meter = new ConcurrencyMeter();
        var executor = new CountingExecutor(meter, TimeSpan.FromMilliseconds(5));

        List<IActionNode> outer =
        [
            new ActionGroupConfig { Tag = "inner-a", ExecutionType = ExecutionMode.Parallel, Children = Actions(4) },
            new ActionGroupConfig { Tag = "inner-b", ExecutionType = ExecutionMode.Parallel, Children = Actions(4) },
            new ActionGroupConfig { Tag = "inner-c", ExecutionType = ExecutionMode.Parallel, Children = Actions(4) },
        ];

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ok = await executor.RunStage(outer, deadline.Token);

        Assert.True(ok);
        Assert.Equal(12, meter.Completed);
    }

    [Fact]
    public async Task Permit_Should_ReturnToItsOwnSemaphore_When_ReconfiguredWhileHeld()
    {
        // Releasing into the NEW semaphore would permanently inflate it past its own cap.
        PipelineConcurrency.Configure(1);
        var permit = await PipelineConcurrency.AcquireAsync(CancellationToken.None);

        PipelineConcurrency.Configure(2);
        permit.Dispose();

        var a = await PipelineConcurrency.AcquireAsync(CancellationToken.None);
        var b = await PipelineConcurrency.AcquireAsync(CancellationToken.None);
        using var third = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PipelineConcurrency.AcquireAsync(third.Token));

        a.Dispose();
        b.Dispose();
    }

    [Fact]
    public async Task Permit_Should_BeIdempotent_When_DisposedTwice()
    {
        // A double release would raise the ceiling above the configured cap for the process lifetime.
        PipelineConcurrency.Configure(1);

        var permit = await PipelineConcurrency.AcquireAsync(CancellationToken.None);
        permit.Dispose();
        permit.Dispose();

        var held = await PipelineConcurrency.AcquireAsync(CancellationToken.None);
        using var second = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PipelineConcurrency.AcquireAsync(second.Token));

        held.Dispose();
    }

    // ── permit lifecycle ────────────────────────────────────────────────

    [Fact]
    public async Task Permit_Should_NotBeConsumed_When_TheWaitIsCancelled()
    {
        // A cancelled WAIT must not take a slot, or every cancelled run would leak one permit and
        // the fleet would quietly throttle itself to a standstill.
        PipelineConcurrency.Configure(1);
        var held = await PipelineConcurrency.AcquireAsync(CancellationToken.None);

        using (var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => PipelineConcurrency.AcquireAsync(cancelled.Token));
        }

        held.Dispose();

        using var after = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reacquired = await PipelineConcurrency.AcquireAsync(after.Token);
        reacquired.Dispose();
    }

    [Fact]
    public async Task Permit_Should_BeReleased_When_TheActionThrows()
    {
        PipelineConcurrency.Configure(1);
        var executor = new ThrowingExecutor();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.RunOne(new ActionConfig { Command = "boom" }));

        // The permit is returned by the using in DispatchActionAsync, so the next action can run.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var next = await PipelineConcurrency.AcquireAsync(deadline.Token);
        next.Dispose();
    }

    [Fact]
    public async Task Permit_Should_BeReleased_When_TheActionIsCancelled()
    {
        // The executor swallows cancellation and reports failure rather than throwing, so the thing
        // to assert is that the slot came back - not that an exception escaped.
        PipelineConcurrency.Configure(1);
        var executor = new CountingExecutor(new ConcurrencyMeter(), TimeSpan.FromSeconds(30));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        try { await executor.RunStage(Actions(1), cancel.Token); }
        catch (OperationCanceledException) { /* either outcome is fine; the permit is the point */ }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var next = await PipelineConcurrency.AcquireAsync(deadline.Token);
        next.Dispose();
    }

    [Fact]
    public async Task ALongAction_Should_HoldExactlyOnePermit_AndNotStarveOthers()
    {
        // A week-long action is legitimate here (MaxExecutionTimeoutMinutes is 20160), so it must
        // occupy ONE slot, not block the cap.
        const int cap = 3;
        PipelineConcurrency.Configure(cap);

        var meter = new ConcurrencyMeter();
        var slow = new CountingExecutor(meter, TimeSpan.FromMilliseconds(600));
        var quick = new CountingExecutor(meter, TimeSpan.FromMilliseconds(20));

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var slowRun = slow.RunStage(Actions(1), deadline.Token);
        var quickRun = quick.RunStage(Actions(12), deadline.Token);

        await Task.WhenAll(slowRun, quickRun);

        Assert.Equal(13, meter.Completed);
        Assert.True(meter.Peak <= cap, $"peak was {meter.Peak}, cap is {cap}");
        // cap - 1 slots stayed usable while the long action held its one.
        Assert.True(meter.Peak > 1, $"peak was {meter.Peak} - the long action starved the others");
    }

    private sealed class ThrowingExecutor()
        : PipelineExecutorBase(new ExecutionSessionManager(), NullLogger.Instance)
    {
        protected override Task<bool> ExecuteActionAsync(
            ActionConfig action, PipelineExecutionContext ctx, CancellationToken ct) =>
            throw new InvalidOperationException("boom");

        public Task<bool> RunOne(ActionConfig action) =>
            ExecuteSingleActionAsync(action, new PipelineExecutionContext(), CancellationToken.None);
    }
}
