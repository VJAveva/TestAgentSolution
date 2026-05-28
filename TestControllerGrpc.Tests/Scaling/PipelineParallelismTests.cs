using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Scaling;

/// <summary>
/// Validates bounded parallelism in PipelineExecutorBase to prevent ThreadPool
/// starvation when 200 agents run simultaneously.
/// Regression: Without SemaphoreSlim(50), a 200-child parallel group started 200
/// tasks at once, exhausting the ThreadPool and causing cascading timeouts.
/// </summary>
public class PipelineParallelismTests
{
    /// <summary>
    /// Concrete test executor that tracks concurrency via Interlocked counters.
    /// </summary>
    private sealed class TestableExecutor : PipelineExecutorBase
    {
        private int _currentConcurrency;
        public int PeakConcurrency;
        public int CompletedCount;
        private readonly TimeSpan _actionDuration;

        public TestableExecutor(TimeSpan actionDuration)
            : base(new ExecutionSessionManager(), NullLogger.Instance)
        {
            _actionDuration = actionDuration;
        }

        protected override async Task<bool> ExecuteActionAsync(
            ActionConfig action, PipelineExecutionContext ctx, CancellationToken ct)
        {
            var current = Interlocked.Increment(ref _currentConcurrency);

            // Track peak concurrency (lock-free)
            int peak;
            do { peak = PeakConcurrency; }
            while (current > peak && Interlocked.CompareExchange(ref PeakConcurrency, current, peak) != peak);

            await Task.Delay(_actionDuration, ct);

            Interlocked.Decrement(ref _currentConcurrency);
            Interlocked.Increment(ref CompletedCount);
            return true;
        }

        public Task<bool> InvokeExecuteChildrenAsync(
            List<IActionNode> children, ExecutionMode mode, CancellationToken ct)
        {
            return ExecuteChildrenAsync(children, mode, true, new PipelineExecutionContext(), ct);
        }
    }

    [Fact]
    public async Task Parallel_BoundedConcurrency_NeverExceeds50()
    {
        // Simulate 100 children (like 100 agents getting a command simultaneously)
        var executor = new TestableExecutor(TimeSpan.FromMilliseconds(50));
        var children = Enumerable.Range(0, 100)
            .Select(_ => (IActionNode)new ActionConfig { Command = "test" })
            .ToList();

        var result = await executor.InvokeExecuteChildrenAsync(
            children, ExecutionMode.Parallel, CancellationToken.None);

        Assert.True(result, "All actions should succeed");
        Assert.Equal(100, executor.CompletedCount);
        Assert.True(executor.PeakConcurrency <= 50,
            $"Peak concurrency was {executor.PeakConcurrency}, should be <= 50");
        Assert.True(executor.PeakConcurrency > 1,
            $"Peak concurrency was {executor.PeakConcurrency}, should be > 1 (actually parallel)");
    }

    [Fact]
    public async Task Parallel_AllChildrenComplete_EvenWhenBounded()
    {
        // Ensure bounding doesn't lose any work items
        var executor = new TestableExecutor(TimeSpan.FromMilliseconds(10));
        var children = Enumerable.Range(0, 200)
            .Select(_ => (IActionNode)new ActionConfig { Command = "test" })
            .ToList();

        var result = await executor.InvokeExecuteChildrenAsync(
            children, ExecutionMode.Parallel, CancellationToken.None);

        Assert.True(result);
        Assert.Equal(200, executor.CompletedCount);
    }

    [Fact]
    public async Task Parallel_CancellationRespected_WhenWaitingForSemaphore()
    {
        // Use long action duration so tasks queue up waiting for the semaphore
        var executor = new TestableExecutor(TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();

        var children = Enumerable.Range(0, 100)
            .Select(_ => (IActionNode)new ActionConfig { Command = "test" })
            .ToList();

        var task = executor.InvokeExecuteChildrenAsync(
            children, ExecutionMode.Parallel, cts.Token);

        // Let some start, then cancel
        await Task.Delay(100);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Sequential_NotAffected_ByBoundedParallelism()
    {
        // Sequential mode should still work one-at-a-time as before
        var executor = new TestableExecutor(TimeSpan.FromMilliseconds(5));
        var children = Enumerable.Range(0, 10)
            .Select(_ => (IActionNode)new ActionConfig { Command = "test" })
            .ToList();

        var result = await executor.InvokeExecuteChildrenAsync(
            children, ExecutionMode.Sequential, CancellationToken.None);

        Assert.True(result);
        Assert.Equal(10, executor.CompletedCount);
        Assert.Equal(1, executor.PeakConcurrency); // Sequential = 1 at a time
    }
}
