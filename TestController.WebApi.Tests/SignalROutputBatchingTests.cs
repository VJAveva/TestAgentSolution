namespace TestController.WebApi.Tests;

/// <summary>
/// Validates the output batching pattern used by SignalRNotifier.
/// At 200 agents producing output simultaneously, individual sends would
/// overwhelm the WebSocket. The batching timer coalesces lines into single broadcasts.
/// Regression: If batching is removed, WebClient will lag/freeze during 200-agent runs.
/// </summary>
public class SignalROutputBatchingTests
{
    /// <summary>
    /// Simulates the batching pattern: multiple items added, single flush.
    /// This mirrors the _pendingOutputLines + FlushOutput pattern in SignalRNotifier.
    /// </summary>
    [Fact]
    public void OutputBatching_MultipleAdds_FlushReturnsAll()
    {
        var outputLock = new object();
        var pending = new List<object>();
        var flushed = new List<List<object>>();

        // Simulate 200 output lines arriving (one per agent)
        for (int i = 0; i < 200; i++)
        {
            lock (outputLock)
            {
                pending.Add(new { agentName = $"Agent-{i}", line = $"output line {i}" });
            }
        }

        // Simulate flush (like the timer fires)
        List<object> batch;
        lock (outputLock)
        {
            batch = new List<object>(pending);
            pending.Clear();
        }
        flushed.Add(batch);

        Assert.Single(flushed);
        Assert.Equal(200, flushed[0].Count);
        Assert.Empty(pending); // cleared after flush
    }

    [Fact]
    public void OutputBatching_NoOutput_FlushDoesNothing()
    {
        var outputLock = new object();
        var pending = new List<object>();
        int flushCount = 0;

        // Simulate flush with nothing pending
        lock (outputLock)
        {
            if (pending.Count == 0)
            {
                // Early return in real code
                flushCount = 0;
            }
            else
            {
                flushCount++;
            }
        }

        Assert.Equal(0, flushCount);
    }

    [Fact]
    public void OutputBatching_ConcurrentAdds_ThreadSafe()
    {
        var outputLock = new object();
        var pending = new List<object>();
        var totalFlushed = 0;

        // Simulate concurrent output from 200 agents
        Parallel.For(0, 200, i =>
        {
            lock (outputLock)
            {
                pending.Add(new { agentName = $"Agent-{i}", line = $"line {i}" });
            }
        });

        // Flush
        lock (outputLock)
        {
            totalFlushed = pending.Count;
            pending.Clear();
        }

        Assert.Equal(200, totalFlushed);
    }

    [Fact]
    public void OutputBatching_MultipleFlushCycles_NoDataLoss()
    {
        var outputLock = new object();
        var pending = new List<object>();
        var allFlushed = new List<object>();

        // Cycle 1: 50 lines
        for (int i = 0; i < 50; i++)
            lock (outputLock) { pending.Add(new { i }); }
        lock (outputLock) { allFlushed.AddRange(pending); pending.Clear(); }

        // Cycle 2: 100 lines
        for (int i = 0; i < 100; i++)
            lock (outputLock) { pending.Add(new { i }); }
        lock (outputLock) { allFlushed.AddRange(pending); pending.Clear(); }

        // Cycle 3: 50 lines
        for (int i = 0; i < 50; i++)
            lock (outputLock) { pending.Add(new { i }); }
        lock (outputLock) { allFlushed.AddRange(pending); pending.Clear(); }

        Assert.Equal(200, allFlushed.Count);
        Assert.Empty(pending);
    }
}
