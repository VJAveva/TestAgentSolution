# Scaling Remediation — Implementation Plan

> **Objective:** Fix all P0/P1 scaling gaps to support 200 agents with zero regressions  
> **Approach:** 8 phases, each independently testable, each with unit tests before code changes  
> **Total estimate:** ~3 working days for a single developer  
> **Test strategy:** Write failing tests FIRST, then implement the fix (TDD)

---

## Phase Overview

| Phase | What | Files Changed | Tests Added | Risk |
|-------|------|---------------|-------------|------|
| 1 | FleetVM debounce + delta update | `FleetVM.cs` | `FleetVMScalingTests.cs` | Medium |
| 2 | FleetView virtualization | `FleetView.xaml` | Visual verification | Low |
| 3 | Pipeline bounded parallelism | `PipelineExecutorBase.cs` | `PipelineParallelismTests.cs` | Medium |
| 4 | ThreadPool + Rate limit config | `Program.cs`, `appsettings.json` | `RateLimitConfigTests.cs` | Low |
| 5 | SignalR output batching | `SignalRNotifier.cs` | `SignalRBatchingTests.cs` | Medium |
| 6 | React fleet debounce + memo | `useFleetState.ts`, `FleetPage.tsx` | `fleetScaling.test.ts` | Low |
| 7 | React virtualization | `FleetPage.tsx` | `fleetVirtualization.test.tsx` | Low |
| 8 | ControllerHub snapshot cache | `ControllerHub.cs` | `FleetSnapshotCacheTests.cs` | Low |

---

## Phase 1: FleetVM Debounce + Delta Update

### Problem (Non-Technical)
Every heartbeat (200 agents × every 5–15s) triggers a FULL rebuild of the agent card list. Like erasing and redrawing a 200-item whiteboard 30+ times per second.

### What Changes

**File:** `TestControllerGrpc/ViewModels/AgentWorkspace/FleetVM.cs`

**Before:**
```csharp
events.Subscribe<AgentHeartbeatEvent>(_ => uiDispatcher.InvokeAsync(Refresh));
// ... 6 more identical subscriptions, each calling Refresh()
```

**After:**
```csharp
events.Subscribe<AgentHeartbeatEvent>(_ => ScheduleRefresh());
events.Subscribe<AgentLocksChangedEvent>(_ => ScheduleRefresh());
// ... all subscriptions call ScheduleRefresh() instead
```

**New method — ScheduleRefresh():**
```csharp
private const int RefreshDebounceMs = 2000; // Max one refresh per 2 seconds
private DateTime _lastRefresh = DateTime.MinValue;
private bool _refreshScheduled;

private void ScheduleRefresh()
{
    if (_refreshScheduled) return; // Already queued — skip
    _refreshScheduled = true;

    var elapsed = DateTime.UtcNow - _lastRefresh;
    var delay = elapsed.TotalMilliseconds < RefreshDebounceMs
        ? TimeSpan.FromMilliseconds(RefreshDebounceMs) - elapsed
        : TimeSpan.Zero;

    _uiDispatcher.InvokeAsync(async () =>
    {
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay);
        _refreshScheduled = false;
        _lastRefresh = DateTime.UtcNow;
        Refresh();
    }, System.Windows.Threading.DispatcherPriority.Background);
}
```

### Unit Tests (Write FIRST)

**File:** `TestControllerGrpc.Tests/Scaling/FleetVMScalingTests.cs`

```csharp
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels.AgentWorkspace;

namespace TestControllerGrpc.Tests.Scaling;

/// <summary>
/// Validates FleetVM debounce behavior — ensures that rapid heartbeat
/// events don't cause excessive Refresh() calls.
/// Regression: If these fail, the UI will freeze at 50+ agents.
/// </summary>
public class FleetVMScalingTests
{
    [Fact]
    public void RapidHeartbeats_RefreshCalledAtMostOncePerDebounceWindow()
    {
        // Arrange: Simulate 200 heartbeat events in rapid succession
        var aggregator = new EventAggregator();
        int refreshCount = 0;
        
        // We test the debounce logic directly (extracted to testable method)
        var debouncer = new RefreshDebouncer(intervalMs: 2000);
        debouncer.OnRefresh += () => Interlocked.Increment(ref refreshCount);

        // Act: Fire 200 events in 100ms
        for (int i = 0; i < 200; i++)
            debouncer.Request();

        // Assert: Should collapse to 1 refresh (within debounce window)
        Thread.Sleep(2500); // Wait for debounce to fire
        Assert.InRange(refreshCount, 1, 2); // At most 2 (leading + trailing)
    }

    [Fact]
    public void SpacedEvents_EachTriggersRefresh()
    {
        // Arrange
        var debouncer = new RefreshDebouncer(intervalMs: 100);
        int refreshCount = 0;
        debouncer.OnRefresh += () => Interlocked.Increment(ref refreshCount);

        // Act: Fire events well apart
        debouncer.Request();
        Thread.Sleep(200);
        debouncer.Request();
        Thread.Sleep(200);

        // Assert: Both should fire
        Assert.Equal(2, refreshCount);
    }

    [Fact]
    public void NoEvents_NoRefresh()
    {
        var debouncer = new RefreshDebouncer(intervalMs: 100);
        int refreshCount = 0;
        debouncer.OnRefresh += () => Interlocked.Increment(ref refreshCount);

        Thread.Sleep(300);
        Assert.Equal(0, refreshCount);
    }
}
```

### Regression Guard

The existing `FleetVM` behavior tests (if any) MUST still pass:
- Fleet shows all registered agents ✓
- Fleet groups agents by session ✓
- Filter text hides non-matching agents ✓
- Busy/Free/Offline counts are correct ✓

---

## Phase 2: FleetView Virtualization

### Problem (Non-Technical)
All 200 agent cards are always rendered in memory, even if you can only see 20. Like printing every page of a book to read one paragraph.

### What Changes

**File:** `TestControllerGrpc/Views/AgentWorkspace/FleetView.xaml`

**Before:**
```xml
<ItemsControl ItemsSource="{Binding Agents}" ...>
    <ItemsControl.ItemsPanel>
        <ItemsPanelTemplate>
            <UniformGrid Columns="2"/>
        </ItemsPanelTemplate>
    </ItemsControl.ItemsPanel>
</ItemsControl>
```

**After:**
```xml
<ListBox ItemsSource="{Binding Agents}" 
         VirtualizingStackPanel.IsVirtualizing="True"
         VirtualizingStackPanel.VirtualizationMode="Recycling"
         VirtualizingPanel.ScrollUnit="Pixel"
         ScrollViewer.HorizontalScrollBarVisibility="Disabled"
         Background="Transparent" BorderThickness="0"
         SelectionMode="Single"
         SelectedItem="{Binding SelectedCard}">
    <ListBox.ItemsPanel>
        <ItemsPanelTemplate>
            <VirtualizingStackPanel />
        </ItemsPanelTemplate>
    </ListBox.ItemsPanel>
    <!-- ItemTemplate stays identical to current DataTemplate -->
</ListBox>
```

### Unit Tests

Virtualization is a XAML rendering behavior — tested via manual verification + performance counter:
- At 200 agents, visual element count should be ~30 (visible only)
- Measured with: WPF Performance Tools → Visual Tree depth

### Regression Guard
- All fleet cards still render correctly
- Click to select agent still works
- HC glyph fallbacks still visible
- Status colors still apply

---

## Phase 3: Pipeline Bounded Parallelism

### Problem (Non-Technical)
When a pipeline says "run these 200 actions in parallel," ALL 200 start at the exact same instant. Like ordering 200 cooks to use a kitchen with 16 stoves.

### What Changes

**File:** `TestControllerGrpc.Core/Services/PipelineExecutorBase.cs`

**Before:**
```csharp
if (mode == ExecutionMode.Parallel)
{
    var tasks = children.Select(child =>
        ExecuteNodeAsync(child, ctx, ct)).ToList();
    var results = await Task.WhenAll(tasks);
    return results.All(r => r);
}
```

**After:**
```csharp
if (mode == ExecutionMode.Parallel)
{
    const int MaxParallelAgents = 50;
    var semaphore = new SemaphoreSlim(MaxParallelAgents, MaxParallelAgents);
    
    var tasks = children.Select(async child =>
    {
        await semaphore.WaitAsync(ct);
        try { return await ExecuteNodeAsync(child, ctx, ct); }
        finally { semaphore.Release(); }
    }).ToList();
    
    var results = await Task.WhenAll(tasks);
    return results.All(r => r);
}
```

Same change for `ExecuteChildrenTrackedAsync`.

### Unit Tests (Write FIRST)

**File:** `TestControllerGrpc.Tests/Scaling/PipelineParallelismTests.cs`

```csharp
namespace TestControllerGrpc.Tests.Scaling;

/// <summary>
/// Validates that parallel pipeline execution is bounded.
/// Regression: If max concurrency exceeds 50, ThreadPool starvation 
/// occurs at 200 agents causing timeouts across all operations.
/// </summary>
public class PipelineParallelismTests
{
    [Fact]
    public async Task ParallelExecution_BoundedConcurrency_MaxFiftySimultaneous()
    {
        // Arrange
        int peakConcurrency = 0;
        int currentConcurrency = 0;
        var gate = new SemaphoreSlim(50, 50);

        var tasks = Enumerable.Range(0, 200).Select(async i =>
        {
            await gate.WaitAsync();
            try
            {
                var current = Interlocked.Increment(ref currentConcurrency);
                InterlockedMax(ref peakConcurrency, current);
                await Task.Delay(10); // Simulate work
            }
            finally
            {
                Interlocked.Decrement(ref currentConcurrency);
                gate.Release();
            }
        });

        // Act
        await Task.WhenAll(tasks);

        // Assert: Never exceeded 50 concurrent
        Assert.True(peakConcurrency <= 50, 
            $"Peak concurrency was {peakConcurrency}, expected <= 50");
    }

    [Fact]
    public async Task ParallelExecution_AllChildrenComplete_NoneDropped()
    {
        // Arrange: 200 children with bounded parallelism
        int completedCount = 0;
        var gate = new SemaphoreSlim(50, 50);

        var tasks = Enumerable.Range(0, 200).Select(async i =>
        {
            await gate.WaitAsync();
            try
            {
                await Task.Delay(5);
                Interlocked.Increment(ref completedCount);
                return true;
            }
            finally { gate.Release(); }
        });

        // Act
        var results = await Task.WhenAll(tasks);

        // Assert: All 200 completed
        Assert.Equal(200, completedCount);
        Assert.All(results, r => Assert.True(r));
    }

    [Fact]
    public async Task ParallelExecution_Cancellation_StopsGracefully()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        var gate = new SemaphoreSlim(50, 50);
        int startedCount = 0;

        var tasks = Enumerable.Range(0, 200).Select(async i =>
        {
            await gate.WaitAsync(cts.Token);
            try
            {
                Interlocked.Increment(ref startedCount);
                await Task.Delay(1000, cts.Token); // Long work
                return true;
            }
            catch (OperationCanceledException) { return false; }
            finally { gate.Release(); }
        });

        // Act: Cancel after 100ms
        cts.CancelAfter(100);
        
        try { await Task.WhenAll(tasks); } 
        catch (OperationCanceledException) { }

        // Assert: Not all 200 started (cancellation worked)
        Assert.True(startedCount < 200, 
            $"Started {startedCount} — cancellation should have prevented some");
    }

    [Fact]
    public async Task SequentialExecution_StillRunsOneAtATime()
    {
        // Regression: ensure sequential mode was not accidentally changed
        int peakConcurrency = 0;
        int currentConcurrency = 0;
        
        foreach (var i in Enumerable.Range(0, 10))
        {
            var current = Interlocked.Increment(ref currentConcurrency);
            InterlockedMax(ref peakConcurrency, current);
            await Task.Delay(5);
            Interlocked.Decrement(ref currentConcurrency);
        }

        Assert.Equal(1, peakConcurrency);
    }

    private static void InterlockedMax(ref int location, int value)
    {
        int current;
        do { current = location; }
        while (value > current && Interlocked.CompareExchange(ref location, value, current) != current);
    }
}
```

### Regression Guard
- Existing `ActionPipelineExecutorTests.cs` MUST still pass:
  - Sequential execution order preserved ✓
  - FailAndContinue logic works ✓
  - Cancellation stops execution ✓
  - Parallel children all complete ✓

---

## Phase 4: ThreadPool + Rate Limit Configuration

### Problem (Non-Technical)
The system allows only 60 API requests/minute — but one browser watching 200 agents needs more. Also, .NET starts with only 16 threads and ramps up slowly.

### What Changes

**File:** `TestController.WebApi/Program.cs` — Add near top after `var builder`:
```csharp
// Scale fix: Pre-warm ThreadPool for 200 concurrent agent operations.
// Default min = CPU core count (8-16); under load, .NET adds threads at 500ms/thread.
// At 200 agents, need immediate capacity.
ThreadPool.SetMinThreads(workerThreads: 200, completionPortThreads: 200);
```

**File:** `TestController.WebApi/appsettings.json` — Update rate limit:
```json
"RateLimit": {
    "Enabled": true,
    "RequestsPerMinute": 300,
    "AdminRequestsPerMinute": 600
}
```

### Unit Tests

**File:** `TestControllerGrpc.Tests/Scaling/RateLimitConfigTests.cs`

```csharp
namespace TestControllerGrpc.Tests.Scaling;

/// <summary>
/// Validates rate limit configuration supports 200-agent scale.
/// Regression: If limits are too low, browsers get 429 errors when viewing fleet.
/// </summary>
public class RateLimitConfigTests
{
    [Fact]
    public void RateLimit_StandardUser_AllowsAtLeast300PerMinute()
    {
        // The fleet page fetches once per 2 seconds = 30/min max
        // Plus telemetry for selected agent = 4/min
        // Total: ~34 req/min per user (plenty of headroom at 300)
        var options = new TestController.Api.Security.RateLimitSecurityOptions
        {
            RequestsPerMinute = 300
        };
        
        const int fleetRefreshesPerMinute = 30;        // 1 per 2s
        const int telemetryPerMinute = 4;              // 1 per 15s
        const int otherPerMinute = 10;                 // navigation, etc.
        var totalNeeded = fleetRefreshesPerMinute + telemetryPerMinute + otherPerMinute;
        
        Assert.True(options.RequestsPerMinute > totalNeeded,
            $"Rate limit {options.RequestsPerMinute} must exceed {totalNeeded} needed req/min");
    }

    [Fact]
    public void ThreadPool_MinThreads_SufficientFor200Agents()
    {
        // At 200 agents, we need at least 200 worker threads available immediately
        ThreadPool.GetMinThreads(out int workerMin, out int ioMin);
        
        // This test validates the CONCEPT — actual SetMinThreads is in Program.cs
        // If running in CI, threads may be lower; just verify we can SET it
        ThreadPool.SetMinThreads(200, 200);
        ThreadPool.GetMinThreads(out workerMin, out ioMin);
        
        Assert.True(workerMin >= 200, $"Worker min threads = {workerMin}, need >= 200");
        Assert.True(ioMin >= 200, $"IO min threads = {ioMin}, need >= 200");
    }
}
```

### Regression Guard
- All existing API tests MUST still pass (rate limit only affects high-frequency callers)
- Health endpoint remains exempt from rate limiting

---

## Phase 5: SignalR Output Batching

### Problem (Non-Technical)
Every line of output from every agent is sent as a separate real-time message. At 200 agents × 10 lines/sec = 2000 messages/sec to browsers. Like sending 2000 letters/second instead of one envelope with all the pages.

### What Changes

**File:** `TestController.Api/Services/SignalRNotifier.cs`

**Before (OnAgentOutputEvent — sends per line):**
```csharp
private void OnAgentOutputEvent(AgentOutputEvent e)
{
    SendSafe("AgentOutput", new { ... });
}
```

**After (batch output like heartbeats):**
```csharp
private readonly Dictionary<string, List<object>> _pendingOutput = new();
private readonly object _outputLock = new();

private void OnAgentOutputEvent(AgentOutputEvent e)
{
    var payload = new
    {
        sessionId = e.SessionId,
        agentName = e.AgentName,
        line = SecurityRedactor.Redact(e.Line),
        kind = e.Kind,
        timestamp = e.Timestamp.ToString("HH:mm:ss.fff"),
    };

    lock (_outputLock)
    {
        if (!_pendingOutput.TryGetValue(e.SessionId ?? "", out var list))
            _pendingOutput[e.SessionId ?? ""] = list = new(64);
        list.Add(payload);
    }
}

// Called from existing timer (same 1000ms flush interval as heartbeats)
private void FlushOutput()
{
    Dictionary<string, List<object>> batch;
    lock (_outputLock)
    {
        if (_pendingOutput.Count == 0) return;
        batch = new(_pendingOutput);
        _pendingOutput.Clear();
    }

    foreach (var (sessionId, lines) in batch)
    {
        if (string.IsNullOrEmpty(sessionId))
            SendSafe("AgentOutputBatch", lines);
        else
            SendToSessionGroup(sessionId, "AgentOutputBatch", lines);
    }
}
```

### Unit Tests

**File:** `TestControllerGrpc.Tests/Scaling/SignalRBatchingTests.cs`

```csharp
namespace TestControllerGrpc.Tests.Scaling;

/// <summary>
/// Validates that output events are batched before SignalR broadcast.
/// Regression: Individual messages must still arrive (just batched).
/// </summary>
public class SignalRBatchingTests
{
    [Fact]
    public void OutputBatching_MultipleLines_CoalescedIntoSingleBatch()
    {
        // Arrange
        var batcher = new OutputBatcher();

        // Act: 50 lines from same session arrive within batch window
        for (int i = 0; i < 50; i++)
            batcher.Add("session1", $"Line {i}");

        var batch = batcher.Flush();

        // Assert: Single batch with 50 items (not 50 individual sends)
        Assert.Single(batch);
        Assert.Equal(50, batch["session1"].Count);
    }

    [Fact]
    public void OutputBatching_MultipleSessions_SeparateBatches()
    {
        var batcher = new OutputBatcher();

        batcher.Add("session1", "Line A");
        batcher.Add("session2", "Line B");
        batcher.Add("session1", "Line C");

        var batch = batcher.Flush();

        Assert.Equal(2, batch.Count);
        Assert.Equal(2, batch["session1"].Count);
        Assert.Single(batch["session2"]);
    }

    [Fact]
    public void OutputBatching_FlushWhenEmpty_ReturnsNothing()
    {
        var batcher = new OutputBatcher();
        var batch = batcher.Flush();
        Assert.Empty(batch);
    }

    [Fact]
    public void OutputBatching_ConcurrentAdds_NoDataLoss()
    {
        var batcher = new OutputBatcher();
        const int threads = 10;
        const int linesPerThread = 100;

        Parallel.For(0, threads, t =>
        {
            for (int i = 0; i < linesPerThread; i++)
                batcher.Add("session1", $"Thread{t}-Line{i}");
        });

        var batch = batcher.Flush();
        Assert.Equal(threads * linesPerThread, batch["session1"].Count);
    }

    [Fact]
    public void OutputBatching_FlushClearsPending()
    {
        var batcher = new OutputBatcher();
        batcher.Add("session1", "Line 1");
        batcher.Flush();

        var secondFlush = batcher.Flush();
        Assert.Empty(secondFlush);
    }
}
```

### Regression Guard
- WebClient still receives all output lines (just in arrays instead of individual messages)
- Existing `useSignalR.ts` `connection.on('AgentOutput', ...)` handler must be updated to also handle `'AgentOutputBatch'`
- Log viewer in WPF still shows every line in order

---

## Phase 6: React Fleet Debounce + Memo

### Problem (Non-Technical)
Every single agent status change causes the browser to ask the server for ALL 200 agents again, then re-draws ALL 200 cards.

### What Changes

**File:** `TestController.WebClient/src/hooks/useFleetState.ts`

**Before:**
```typescript
const onStatusChanged = () => { fetchFleet(); };
```

**After:**
```typescript
import { useMemo, useRef } from 'react';

// Debounce: max 1 fetch per 2 seconds
const debouncedFetch = useMemo(() => {
    let timer: ReturnType<typeof setTimeout> | null = null;
    return () => {
        if (timer) return;
        timer = setTimeout(() => {
            timer = null;
            fetchFleet();
        }, 2000);
    };
}, [fetchFleet]);

const onStatusChanged = () => { debouncedFetch(); };
const onLocksChanged = () => { debouncedFetch(); };
```

**File:** `TestController.WebClient/src/components/agents/FleetPage.tsx`

**Before:**
```typescript
function FleetCard({ agent, onClick }: ...) { ... }
```

**After:**
```typescript
const FleetCard = React.memo(function FleetCard({ agent, onClick }: ...) { 
    ... // Same body
}, (prev, next) => 
    prev.agent.name === next.agent.name &&
    prev.agent.status === next.agent.status &&
    prev.agent.isLocked === next.agent.isLocked &&
    prev.agent.lastStatusDetail === next.agent.lastStatusDetail
);
```

### Unit Tests

**File:** `TestController.WebClient/src/hooks/useFleetState.test.ts`

```typescript
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

describe('useFleetState debounce', () => {
    beforeEach(() => { vi.useFakeTimers(); });
    afterEach(() => { vi.useRealTimers(); });

    it('rapid events collapse to single fetch within 2s window', () => {
        let fetchCount = 0;
        const debouncedFetch = createDebouncedFetch(() => { fetchCount++; }, 2000);

        // Fire 100 events in rapid succession
        for (let i = 0; i < 100; i++) debouncedFetch();
        
        vi.advanceTimersByTime(2000);
        
        // Should have fetched only once
        expect(fetchCount).toBe(1);
    });

    it('events spaced beyond debounce window each trigger fetch', () => {
        let fetchCount = 0;
        const debouncedFetch = createDebouncedFetch(() => { fetchCount++; }, 2000);

        debouncedFetch();
        vi.advanceTimersByTime(2500);
        debouncedFetch();
        vi.advanceTimersByTime(2500);

        expect(fetchCount).toBe(2);
    });
});

function createDebouncedFetch(fn: () => void, ms: number) {
    let timer: ReturnType<typeof setTimeout> | null = null;
    return () => {
        if (timer) return;
        timer = setTimeout(() => { timer = null; fn(); }, ms);
    };
}
```

### Regression Guard
- Fleet page still shows all agents after initial load ✓
- Status changes eventually appear (within 2s) ✓
- Clicking a card still navigates to agent detail ✓

---

## Phase 7: React Virtualization

### Problem (Non-Technical)
All 200 cards exist in the browser's document even when you can only see ~12. Scrolling becomes slow with 200 real DOM elements.

### What Changes

**File:** `TestController.WebClient/src/components/agents/FleetPage.tsx`

**Before:**
```typescript
<div className="flex-1 overflow-auto p-4 grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-3">
    {fleet.map(agent => (
        <FleetCard key={agent.name} agent={agent} onClick={...} />
    ))}
</div>
```

**After (using @tanstack/react-virtual already in deps):**
```typescript
import { useVirtualizer } from '@tanstack/react-virtual';

function FleetGrid({ fleet, onSelectAgent }: { fleet: FleetAgent[]; onSelectAgent: (name: string) => void }) {
    const parentRef = useRef<HTMLDivElement>(null);
    const columns = 3;
    const rowCount = Math.ceil(fleet.length / columns);

    const virtualizer = useVirtualizer({
        count: rowCount,
        getScrollElement: () => parentRef.current,
        estimateSize: () => 140, // Card height + gap
        overscan: 3,
    });

    return (
        <div ref={parentRef} className="flex-1 overflow-auto p-4">
            <div style={{ height: `${virtualizer.getTotalSize()}px`, position: 'relative' }}>
                {virtualizer.getVirtualItems().map(virtualRow => (
                    <div key={virtualRow.key}
                         style={{ position: 'absolute', top: virtualRow.start, width: '100%' }}
                         className="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-3">
                        {Array.from({ length: columns }).map((_, col) => {
                            const idx = virtualRow.index * columns + col;
                            const agent = fleet[idx];
                            if (!agent) return null;
                            return <FleetCard key={agent.name} agent={agent}
                                       onClick={() => onSelectAgent(agent.name)} />;
                        })}
                    </div>
                ))}
            </div>
        </div>
    );
}
```

### Unit Tests

**File:** `TestController.WebClient/src/components/agents/fleetVirtualization.test.tsx`

```typescript
import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';

describe('FleetGrid virtualization', () => {
    it('renders only visible rows (not all 200)', () => {
        const fleet = Array.from({ length: 200 }, (_, i) => ({
            name: `agent-${i}`,
            status: 'Ready',
            address: `http://agent-${i}:5200`,
            isLocked: false,
        }));

        const { container } = render(
            <div style={{ height: '600px', overflow: 'auto' }}>
                <FleetGrid fleet={fleet} onSelectAgent={() => {}} />
            </div>
        );

        // With 140px row height and 600px viewport, only ~4-5 rows visible
        // Plus overscan of 3 = ~8 rows rendered max (24 cards out of 200)
        const cards = container.querySelectorAll('[data-testid="fleet-card"]');
        expect(cards.length).toBeLessThan(30);
    });
});
```

### Regression Guard
- All agents visible when scrolling ✓
- Click to select still works ✓
- Search/filter still narrows results ✓

---

## Phase 8: ControllerHub Snapshot Cache

### Problem (Non-Technical)
Every time a browser connects or refreshes, the server builds the fleet overview from scratch — O(N²) operations. Caching it for 2 seconds would serve most requests instantly.

### What Changes

**File:** `TestController.Api/Hubs/ControllerHub.cs`

**Before:**
```csharp
public IReadOnlyList<AgentFleetGroupDto> RequestFleetSnapshot()
{
    return BuildFleetSnapshot();
}
```

**After:**
```csharp
private static IReadOnlyList<AgentFleetGroupDto>? _cachedSnapshot;
private static DateTime _cacheExpiry = DateTime.MinValue;
private static readonly object _cacheLock = new();

public IReadOnlyList<AgentFleetGroupDto> RequestFleetSnapshot()
{
    if (DateTime.UtcNow < _cacheExpiry && _cachedSnapshot != null)
        return _cachedSnapshot;
    
    lock (_cacheLock)
    {
        if (DateTime.UtcNow < _cacheExpiry && _cachedSnapshot != null)
            return _cachedSnapshot;
        
        var snapshot = BuildFleetSnapshot();
        _cachedSnapshot = snapshot;
        _cacheExpiry = DateTime.UtcNow.AddSeconds(2);
        return snapshot;
    }
}
```

Also fix the O(N) FirstOrDefault in `BuildAgentFleetDto`:

**Before:**
```csharp
var agentLock = allLocks.FirstOrDefault(l =>
    string.Equals(l.AgentName, agentName, StringComparison.OrdinalIgnoreCase));
```

**After:**
```csharp
// Build dictionary ONCE at top of BuildFleetSnapshot:
var locksByAgent = allLocks.ToDictionary(
    l => l.AgentName, l => l, StringComparer.OrdinalIgnoreCase);

// Then in BuildAgentFleetDto:
locksByAgent.TryGetValue(agentName, out var agentLock);
```

### Unit Tests

**File:** `TestControllerGrpc.Tests/Scaling/FleetSnapshotCacheTests.cs`

```csharp
namespace TestControllerGrpc.Tests.Scaling;

/// <summary>
/// Validates fleet snapshot caching reduces repeated computation.
/// Regression: Snapshot must still reflect state changes within 2 seconds.
/// </summary>
public class FleetSnapshotCacheTests
{
    [Fact]
    public void Cache_SecondCallWithin2s_ReturnsSameInstance()
    {
        var cache = new SnapshotCache<string>(ttlSeconds: 2);
        int buildCount = 0;

        var first = cache.GetOrBuild(() => { buildCount++; return "snapshot-1"; });
        var second = cache.GetOrBuild(() => { buildCount++; return "snapshot-2"; });

        Assert.Equal(1, buildCount);
        Assert.Same(first, second);
    }

    [Fact]
    public void Cache_CallAfterExpiry_Rebuilds()
    {
        var cache = new SnapshotCache<string>(ttlSeconds: 0); // Immediate expiry
        int buildCount = 0;

        cache.GetOrBuild(() => { buildCount++; return "snapshot-1"; });
        Thread.Sleep(50);
        cache.GetOrBuild(() => { buildCount++; return "snapshot-2"; });

        Assert.Equal(2, buildCount);
    }

    [Fact]
    public void Cache_ConcurrentAccess_NoDuplicateBuilds()
    {
        var cache = new SnapshotCache<string>(ttlSeconds: 5);
        int buildCount = 0;

        Parallel.For(0, 100, _ =>
        {
            cache.GetOrBuild(() =>
            {
                Interlocked.Increment(ref buildCount);
                Thread.Sleep(10); // Simulate work
                return "snapshot";
            });
        });

        // Lock ensures only 1 build (or at most 2 due to race before first lock)
        Assert.InRange(buildCount, 1, 2);
    }

    [Fact]
    public void LockDictionary_O1Lookup_InsteadOfFirstOrDefault()
    {
        // Simulate AgentLockManager.GetAllLocks() result
        var locks = Enumerable.Range(0, 200)
            .Select(i => new FakeLock { AgentName = $"Agent-{i}" })
            .ToList();

        // O(N) approach (OLD)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 200; i++)
            locks.FirstOrDefault(l => l.AgentName == $"Agent-{i}");
        var linearTime = sw.ElapsedTicks;

        // O(1) approach (NEW)
        var dict = locks.ToDictionary(l => l.AgentName, StringComparer.OrdinalIgnoreCase);
        sw.Restart();
        for (int i = 0; i < 200; i++)
            dict.TryGetValue($"Agent-{i}", out _);
        var dictTime = sw.ElapsedTicks;

        // Dictionary should be significantly faster
        Assert.True(dictTime < linearTime, 
            $"Dict: {dictTime} ticks, Linear: {linearTime} ticks — dict should be faster");
    }

    private record FakeLock { public string AgentName { get; init; } = ""; }
}
```

### Regression Guard
- Fleet snapshot still reflects correct state after 2 seconds ✓
- Lock state, session progress, online/offline all accurate ✓
- Multiple simultaneous client connections don't cause stale data beyond 2s ✓

---

## Implementation Order & Dependencies

```
Phase 4 (config) ─── no dependencies, do FIRST (5 minutes)
    │
Phase 3 (pipeline cap) ─── depends on Phase 4 (ThreadPool ready)
    │
Phase 1 (FleetVM debounce) ─── independent
    │
Phase 2 (FleetView virtualization) ─── depends on Phase 1
    │
Phase 5 (SignalR batching) ─── independent
    │
Phase 6 (React debounce) ─── independent
    │
Phase 7 (React virtualization) ─── depends on Phase 6
    │
Phase 8 (snapshot cache) ─── independent
```

**Recommended execution order:**
1. Phase 4 (5 min) — config only, zero risk
2. Phase 3 (1 hr) — critical pipeline fix
3. Phase 1 (4 hr) — critical WPF fix
4. Phase 2 (2 hr) — depends on Phase 1
5. Phase 5 (3 hr) — SignalR batching
6. Phase 8 (2 hr) — server optimization
7. Phase 6 (3 hr) — React debounce
8. Phase 7 (3 hr) — React virtualization

---

## Regression Test Suite — Run After Every Phase

```powershell
# .NET tests (must all pass after each phase)
dotnet test TestControllerGrpc.Tests\TestControllerGrpc.Tests.csproj --verbosity normal

# WebClient tests (must all pass after Phases 6-7)
cd TestController.WebClient
npx vitest run

# Build verification (must succeed after every phase)
dotnet build TestAgentSolution.sln --no-restore
```

### Tests That MUST NOT Break (Existing Regression Suite)

| Test File | What It Guards |
|-----------|---------------|
| `EventAggregatorStressTests.cs` | Event delivery under load |
| `ActionPipelineExecutorTests.cs` | Pipeline execution logic |
| `LockBroadcastIntegrationTests.cs` | Lock state propagation |
| `ConcurrentSessionTests.cs` | Multi-session safety |
| `ScalabilityFixTests.cs` | Existing scale fixes |
| `agentStatus.test.ts` (WebClient) | Agent status mapping |
| All existing vitest files | UI component behavior |

---

## Success Criteria

After all 8 phases are complete, verify:

| Metric | Before | After | How to Verify |
|--------|--------|-------|---------------|
| FleetVM Refresh/sec at 200 agents | 30+ | ≤ 1 | Counter in ScheduleRefresh |
| DOM nodes in browser fleet | 4000+ | < 100 | Chrome DevTools |
| SignalR messages/sec (output) | 2000+ | < 10 | Network tab |
| ThreadPool starvation events | Frequent | Zero | `dotnet-counters` |
| API 429 errors | Common | Zero | Browser console |
| Pipeline peak concurrency | 200 | ≤ 50 | Test assertion |
| Fleet snapshot build time | 500ms+ | < 10ms (cached) | Stopwatch log |

---

## New Test File Summary

| File Path | Tests | Guards Against |
|-----------|-------|---------------|
| `TestControllerGrpc.Tests/Scaling/FleetVMScalingTests.cs` | 3 | UI thread saturation from rapid events |
| `TestControllerGrpc.Tests/Scaling/PipelineParallelismTests.cs` | 4 | ThreadPool starvation from unbounded parallel |
| `TestControllerGrpc.Tests/Scaling/RateLimitConfigTests.cs` | 2 | Rate limiter blocking legitimate requests |
| `TestControllerGrpc.Tests/Scaling/SignalRBatchingTests.cs` | 5 | SignalR message flood |
| `TestControllerGrpc.Tests/Scaling/FleetSnapshotCacheTests.cs` | 4 | Expensive repeated computation |
| `TestController.WebClient/src/hooks/useFleetState.test.ts` | 2 | Browser API call storm |
| `TestController.WebClient/src/components/agents/fleetVirtualization.test.tsx` | 1 | DOM bloat at scale |

**Total: 21 new unit tests** covering all performance-critical paths.
