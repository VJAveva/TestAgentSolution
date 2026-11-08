# Scaling Gap Analysis: 10 → 200 Agents

> **Author:** Performance Architecture Review  
> **Date:** 2026-05-28  
> **Scope:** TestAgentSolution — all projects  
> **Current scale:** 4–10 agents, ~296 tests, 2+ hour runs  
> **Target scale:** 200 agents simultaneously, live output, 2-second telemetry, multi-user UI

---

## 1. EXECUTIVE SUMMARY

### The 5 Most Likely Things to Break First (in order)

| # | Failure | Layer | Breaks at ~N |
|---|---------|-------|--------------|
| 1 | **WPF FleetVM.Refresh() hammered by every heartbeat** — full ObservableCollection rebuild 200×/min with no virtualization | WPF UI (Layer 8) | ~30 agents |
| 2 | **React useFleetState refetches entire fleet on every SignalR event** — 40K+ API calls/day, 200 FleetCard re-renders per event | WebClient (Layer 10) | ~40 agents |
| 3 | **EventAggregator ThreadPool saturation** — every Publish() queues N×subscribers work items; at 200 agents × 6+ event types × multiple subscribers = thousands of queued items/second | Threading (Layer 12) | ~80 agents |
| 4 | **Pipeline executor unbounded parallelism** — `Task.WhenAll(children)` with no MaxDegreeOfParallelism; 200 parallel nodes can starve ThreadPool | Threading (Layer 12) | ~100 agents |
| 5 | **Rate limiter rejects telemetry polling** — 60 req/min per user with QueueLimit=0; one browser polling 200 agents at 2s = 6000 req/min | REST API (Layer 5) | ~10 agents with aggressive polling |

### The Single Biggest Architectural Risk

**The WPF FleetVM subscribes to `AgentHeartbeatEvent` and calls `Refresh()` (a full teardown-rebuild of all ObservableCollections + LINQ GroupBy) on EVERY heartbeat.** At 200 agents heartbeating every 5–15 seconds, this means 13–40 full UI rebuilds per second on the Dispatcher thread — with no virtualization in the ItemsControl. The UI thread will be saturated and the app will appear frozen.

### Go/No-Go Assessment

**GO — with targeted fixes.** The architecture is fundamentally sound:
- gRPC channel management is correct (one reusable channel per agent, HTTP/2 multiplexing)
- ConcurrentDictionary services are lock-free for reads
- SignalR heartbeat coalescing already reduces 200 events/sec → 1 batch/sec
- No sync-over-async patterns in production code
- Polly resilience pipelines handle agent failures well

The system needs ~8 targeted fixes (mostly in the UI and event-dispatch layers) but zero fundamental redesigns to reach 200.

---

## 2. LAYER-BY-LAYER FINDINGS

---

### LAYER 1: gRPC Channel Management

#### What works today at 10 agents
- `AgentGrpcClientManager` stores one `GrpcChannel` per agent in `ConcurrentDictionary<string, GrpcChannel>` with `GetOrAdd()` — lock-free, lazy creation
- `EnableMultipleHttp2Connections = true` allows multiple HTTP/2 streams per TCP connection
- `KeepAlivePingDelay = 60s`, `KeepAlivePingTimeout = 30s`, `PooledConnectionIdleTimeout = 5min`
- Channel reset guarded by `IsAgentExecuting()` check

#### What breaks at 200 agents

| Issue | Failure Mode | Breaks at | Priority |
|-------|-------------|-----------|----------|
| 200 keepalive pings every 60s = 3.3 pings/sec outbound | Network noise, minor CPU | Never breaks (negligible) | P2 |
| 200 simultaneous channel creations on startup storm | TCP connect timeout cascades (30s × 200 = thread exhaustion during connect) | ~100 agents registering within 5s | P1 |
| No max-channel limit; socket handle exhaustion | OS-level port exhaustion (`EMFILE`) | Unlikely on Windows (64K ports) but possible with proxy | P2 |

#### The Fix

```csharp
// In AgentGrpcClientManager — add connection-establishment throttle
private readonly SemaphoreSlim _connectThrottle = new(50, 50); // Max 50 concurrent new connections

public TestAgentService.TestAgentServiceClient GetClient(string address)
{
    var channel = _channels.GetOrAdd(address, addr =>
    {
        _connectThrottle.Wait(TimeSpan.FromSeconds(30)); // Block if too many concurrent connects
        try { return CreateChannel(addr); }
        finally { _connectThrottle.Release(); }
    });
    return new TestAgentService.TestAgentServiceClient(channel);
}
```

#### How to Measure
- **Metric:** `GrpcChannel` count in `_channels.Count`; TCP connection count via `netstat -an | findstr 5200`
- **Tool:** .NET `EventCounter` for `System.Net.Http` connections; Prometheus gauge
- **Target:** < 250 TCP connections at steady state; < 5s for all 200 channels to establish

---

### LAYER 2: gRPC Streaming (Live Agent Output)

#### What works today at 10 agents
- `RemoteCommandStreamRunner.StreamAsync()` uses proper `await foreach` over `call.ResponseStream.ReadAllAsync(ct)` — fully async, no thread blocked per stream
- Stderr ring buffer capped at 20 lines (~4KB max per agent)
- Progress tick callback fires every 5 minutes (configurable)
- Agent-side `CommandExecutor` uses binary `SemaphoreSlim(1,1)` — one command at a time per agent

#### What breaks at 200 agents

| Issue | Failure Mode | Breaks at | Priority |
|-------|-------------|-----------|----------|
| 200 concurrent `await foreach` loops on ThreadPool | ThreadPool saturation if combined with other parallel work | ~150 (compound with Layer 12 pipeline parallelism) | P1 |
| Output callback fires per-line → EventAggregator → ThreadPool | Cascade amplification (see Layer 4) | ~80 agents streaming simultaneously | P1 |
| No backpressure on agent stdout volume | If agent dumps 10MB binary to stdout, gRPC buffers grow | Per-agent issue, not scale-dependent | P2 |

#### The Fix

```csharp
// In RemoteCommandStreamRunner — add output throttling
// Instead of firing AgentOutputEvent per line, batch lines per 100ms
private static readonly Channel<(string agent, string line, string stream)> _outputChannel 
    = Channel.CreateBounded<(string, string, string)>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest });
```

The existing `LogBufferService` (100ms flush, max 200 items/batch, 10K cap) already solves this for the WPF UI. Ensure `SignalRNotifier` uses similar batching for `AgentOutputEvent` (currently it does NOT batch output events — only heartbeats).

#### How to Measure
- **Metric:** ThreadPool work items queued (`ThreadPool.PendingWorkItemCount`); gRPC stream count
- **Tool:** `dotnet-counters monitor --counters System.Runtime`
- **Target:** `ThreadPool.PendingWorkItemCount` < 100 at steady state with 200 streams

---

### LAYER 3: Telemetry Polling

#### What works today at 10 agents
- `MainViewModel.StartPeriodicHealthCheck()` runs every 30s, polls all agents in parallel via `Task.WhenAll()`
- `TestConnectionAsync()` has 5s timeout; returns synthetic snapshot if agent is executing (no real gRPC call)
- `MonitorVM` polls individual agent every 2s (only for the actively-viewed agent)
- `RegistryVM` polls sequentially with `_isPolling` overlap guard

#### What breaks at 200 agents

| Issue | Failure Mode | Breaks at | Priority |
|-------|-------------|-----------|----------|
| `Task.WhenAll(200 parallel TestConnectionAsync)` — 200 concurrent gRPC calls every 30s | If 50 agents are offline (5s timeout each), 50 threads blocked for 5s = ThreadPool starvation | ~100 agents with 20% offline | P1 |
| WebClient `useAgentTelemetry` polls at 2s per selected agent | Only active for viewed agent — not a fleet-wide issue | Not a scaling issue | P2 |
| No jitter in polling interval — all 200 polls fire at exact same ms | CPU spike every 30s | ~100 agents | P2 |

#### The Fix

```csharp
// Add MaxDegreeOfParallelism to health check
public async Task PollHealthAsync(CancellationToken ct)
{
    var agents = _dispatcher.RegisteredAgents.ToList();
    
    // Throttle: max 50 concurrent health checks
    await Parallel.ForEachAsync(agents, 
        new ParallelOptions { MaxDegreeOfParallelism = 50, CancellationToken = ct },
        async (agent, token) =>
        {
            var (snapshot, _) = await _dispatcher.TestConnectionAsync(agent.Name, token);
            // Update UI...
        });
}
```

Also add per-agent jitter:
```csharp
await Task.Delay(Random.Shared.Next(0, 2000), ct); // 0-2s jitter per agent
```

#### How to Measure
- **Metric:** P95 latency of `TestConnectionAsync`; % of polls that timeout
- **Tool:** Custom `Stopwatch` + AppLogger per poll cycle
- **Target:** Full 200-agent poll cycle < 10s (including timeouts); < 5% timeout rate

---

### LAYER 4: IEventAggregator (In-Process Pub/Sub)

#### What works today at 10 agents
- `ConcurrentDictionary<Type, List<Delegate>>` for subscribers
- Snapshot-before-dispatch pattern (clone handler list under lock)
- Each handler dispatched via `ThreadPool.QueueUserWorkItem()` — publisher never blocks
- `IDisposable` tokens for unsubscription

#### What breaks at 200 agents

| Issue | Failure Mode | Breaks at | Priority |
|-------|-------------|-----------|----------|
| **ThreadPool flood:** 200 heartbeats/5s × 6 subscribers = 240 work items/sec; add 200 output streams × 3 subscribers = 600/sec → 840 ThreadPool items/sec | GC pressure from closures + ThreadPool queue growth | ~80 agents (compound) | P0 |
| Subscription lock contention: `lock (handlers)` during snapshot in Publish() | Brief stalls under extremely high publish rate | ~200 (minor) | P2 |
| Exception swallowing: `catch { }` in handler dispatch | Silent bugs — a broken subscriber never surfaces | Always (quality issue) | P2 |
| No subscription leak detection | ViewModels that don't dispose subscriptions accumulate handlers | Long sessions | P1 |

#### The Fix

```csharp
// Replace ThreadPool.QueueUserWorkItem with Channel-based batched dispatch
public sealed class EventAggregator : IEventAggregator
{
    private readonly ConcurrentDictionary<Type, List<Delegate>> _subs = new();
    private readonly Channel<Action> _dispatchChannel = 
        Channel.CreateBounded<Action>(new BoundedChannelOptions(5000) 
        { 
            FullMode = BoundedChannelFullMode.DropOldest 
        });

    public EventAggregator()
    {
        // Single dedicated consumer thread — avoids ThreadPool flooding
        Task.Factory.StartNew(async () =>
        {
            await foreach (var action in _dispatchChannel.Reader.ReadAllAsync())
            {
                try { action(); } catch { /* log */ }
            }
        }, TaskCreationOptions.LongRunning);
    }

    public void Publish<TEvent>(TEvent evt)
    {
        if (_subs.TryGetValue(typeof(TEvent), out var handlers))
        {
            Delegate[] snapshot;
            lock (handlers) { snapshot = [.. handlers]; }
            foreach (var h in snapshot)
            {
                var handler = (Action<TEvent>)h;
                _dispatchChannel.Writer.TryWrite(() => handler(evt));
            }
        }
    }
}
```

#### How to Measure
- **Metric:** `ThreadPool.PendingWorkItemCount`; EventAggregator channel pending count
- **Tool:** `dotnet-counters`; add `Interlocked.Increment` counter on Publish()
- **Target:** Channel backlog < 500; ThreadPool pending < 50

---

### LAYER 5: SignalR Hub

#### What works today at 10 agents
- `ControllerHub` with group management (per-session, per-user, global)
- `RequestFleetSnapshot()` builds full fleet state on demand
- `HubExceptionFilter` catches unhandled exceptions
- SignalR configured via `appsettings.json` (MaxMessageSize, KeepAlive, ClientTimeout configurable)

#### What breaks at 200 agents

| Issue | Failure Mode | Breaks at | Priority |
|-------|-------------|-----------|----------|
| `BuildFleetSnapshot()` is O(agents × locks): iterates all agents, calls `allLocks.FirstOrDefault()` per agent | ~200 × 200 = 40K iterations per snapshot request; if 5 clients reconnect simultaneously = 200K iterations | ~200 (latency, not crash) | P1 |
| No message batching for non-heartbeat events (OutputReceived, StatusChanged) | At peak: 200 status changes in 5s = 200 individual SignalR sends | ~100 concurrent changes | P1 |
| Default SignalR `MaximumParallelInvocationsPerClient = 1` | Client-invoked methods serialize; slow client blocks itself | Not a server bottleneck | P2 |
| No backplane (Redis) configured | Cannot scale to multiple controller instances | Single-instance limit | P2 (for now) |

#### The Fix

```csharp
// Cache BuildFleetSnapshot with short TTL
private IReadOnlyList<AgentFleetGroupDto>? _cachedSnapshot;
private DateTime _cacheExpiry = DateTime.MinValue;
private readonly object _cacheLock = new();

private IReadOnlyList<AgentFleetGroupDto> BuildFleetSnapshot()
{
    if (DateTime.UtcNow < _cacheExpiry && _cachedSnapshot != null)
        return _cachedSnapshot;
        
    lock (_cacheLock)
    {
        if (DateTime.UtcNow < _cacheExpiry && _cachedSnapshot != null)
            return _cachedSnapshot;
            
        // ... existing build logic ...
        
        // Also: replace FirstOrDefault with dictionary lookup
        var locksByAgent = allLocks.ToDictionary(
            l => l.AgentName, l => l, StringComparer.OrdinalIgnoreCase);
        
        _cachedSnapshot = result;
        _cacheExpiry = DateTime.UtcNow.AddSeconds(2);
        return result;
    }
}
```

#### How to Measure
- **Metric:** `BuildFleetSnapshot` execution time (P50/P95); SignalR message rate out
- **Tool:** `Stopwatch` + structured logging; SignalR diagnostics
- **Target:** Snapshot build < 50ms; cache hit rate > 80%

---

### LAYER 6: SignalRBridge / SignalRNotifier

#### What works today at 10 agents
- Heartbeat coalescing: accumulates pending heartbeats in `Dictionary<string, object>` under lock; flushes once per 1000ms as single `"AgentHeartbeats"` batch
- 9 subscriptions (4 C# events + 5 EventAggregator events)
- `SendSafe()` wrapper with 5s timeout prevents stalled broadcasts

#### What breaks at 200 agents

| Issue | Failure Mode | Breaks at | Priority |
|-------|-------------|-----------|----------|
| **Output events not batched** — `AgentOutputEvent` forwarded 1:1 to SignalR per line | 200 agents × 10 lines/sec = 2000 SignalR messages/sec | ~50 streaming agents | P0 |
| **StatusChanged events not batched** — each status change is an individual broadcast | 200 agents starting = 200 StatusChanged in 5s | ~100 agents (startup storm) | P1 |
| Timer runs during idle (1KB/sec wasted) | Negligible | Never | P2 |
| No subscription disposal on `OnStop()` — potential leak on restart | Memory leak over host lifetime | Long uptime | P1 |

#### The Fix

```csharp
// Add output batching similar to heartbeat coalescing
private readonly Dictionary<string, List<string>> _pendingOutput = new();
private readonly object _outputLock = new();

private void OnAgentOutput(AgentOutputEvent e)
{
    lock (_outputLock)
    {
        if (!_pendingOutput.TryGetValue(e.AgentName, out var lines))
            _pendingOutput[e.AgentName] = lines = new List<string>();
        lines.Add(e.Line);
    }
}

// Flush on same 1000ms timer alongside heartbeats
private void FlushOutput()
{
    List<(string Agent, List<string> Lines)> batch;
    lock (_outputLock)
    {
        if (_pendingOutput.Count == 0) return;
        batch = _pendingOutput.Select(kvp => (kvp.Key, kvp.Value)).ToList();
        _pendingOutput.Clear();
    }
    // Send as "AgentOutputBatch" — one SignalR message per second with all lines grouped by agent
    SendSafe("AgentOutputBatch", batch);
}
```

#### How to Measure
- **Metric:** SignalR messages/sec outbound; serialization time per flush
- **Tool:** ASP.NET Core SignalR diagnostics event source; `Stopwatch` in FlushHeartbeats
- **Target:** < 10 SignalR messages/sec at steady state (batches); serialization < 20ms

---

### LAYER 7: ConcurrentDictionary Services

#### What works today at 10 agents
- `AgentRegistry`: `ConcurrentDictionary<string, AgentEntry>` — lock-free reads via `TryGetValue`
- `AgentLockManager`: `ConcurrentDictionary<string, AgentLock>` with global `_atomicLock` for TryLockAgents (all-or-nothing semantics)
- `ExecutionSessionManager`: `ConcurrentDictionary<string, ExecutionSession>` for active sessions + `List<ExecutionSession>` history (max 50, under lock)

#### What breaks at 200 agents

| Issue | Failure Mode | Breaks at | Priority |
|-------|-------------|-----------|----------|
| `AgentLockManager._atomicLock` serializes ALL lock/release operations | If 10 sessions acquire/release simultaneously, each waits for previous lock to complete; O(agents) scan in each | ~50 concurrent lock operations | P1 |
| `GetAllLocks().FirstOrDefault()` in BuildFleetSnapshot — O(N) scan per agent | 200 × 200 = 40K iterations per snapshot | ~200 (latency) | P1 |
| `_history` FIFO limited to 50 | Not a scaling issue — intentional trim | N/A | N/A |
| Persistence via `ThreadPool.QueueUserWorkItem` in PersistToDisk | Under high lock churn, queues many persist ops; disk I/O becomes bottleneck | ~100 rapid lock/unlock cycles | P2 |

#### The Fix

```csharp
// AgentLockManager: Add indexed lookup for BuildFleetSnapshot
private readonly ConcurrentDictionary<string, AgentLock> _locksByAgent = new(...);

// Already exists — but callers should use dictionary lookup not FirstOrDefault:
public AgentLock? GetLock(string agentName) => _locks.TryGetValue(agentName, out var l) ? l : null;

// In ControllerHub.BuildAgentFleetDto — replace:
//   var agentLock = allLocks.FirstOrDefault(l => l.AgentName == agentName);
// With:
//   _lockManager.GetLock(agentName);
// This changes O(N) to O(1) per agent.
```

```csharp
// Debounce persistence: at most once per 2 seconds
private long _lastPersistTicks;
private void PersistToDisk()
{
    if (string.IsNullOrEmpty(_persistPath)) return;
    var now = DateTime.UtcNow.Ticks;
    if (now - Interlocked.Read(ref _lastPersistTicks) < TimeSpan.TicksPerSecond * 2)
        return;
    Interlocked.Exchange(ref _lastPersistTicks, now);
    // ... existing persist logic ...
}
```

#### How to Measure
- **Metric:** Lock contention time on `_atomicLock` (Stopwatch around lock acquisition); persist I/O frequency
- **Tool:** Logging before/after lock; FileSystemWatcher on persist file
- **Target:** Lock hold time < 5ms per operation; persist frequency < 1/sec

---

### LAYER 8: WPF UI Rendering

#### What works today at 10 agents
- `FleetVM` rebuilds `Groups` and `Cards` ObservableCollections on each `Refresh()`
- `UniformGrid Columns="2"` layout in `FleetView.xaml` — no virtualization
- `LogBufferService` properly batches log output (100ms interval, 200 items/batch, 10K cap, `RangeObservableCollection`)
- `MainViewModel` uses `Dictionary<string, AgentInfoViewModel>` for O(1) agent lookup

#### What breaks at 200 agents

| Issue | Failure Mode | Breaks at | Priority |
|-------|-------------|-----------|----------|
| **`FleetVM.Refresh()` on every `AgentHeartbeatEvent`** — full `Cards.Clear()` + rebuild + GroupBy + re-add | UI thread blocked 50-200ms × 13-40 times/sec = frozen UI | **~30 agents** | **P0** |
| **No virtualization in FleetView `ItemsControl`** — 200 Border elements always measured/rendered | Layout pass cost grows linearly; 200 cards × 50px = full visual tree always active | ~50 agents | P0 |
| `RefreshAgentStatusSummary()` called on EVERY heartbeat (scans all agents) | O(N) per heartbeat × 200/5s = 40 full scans/sec | ~30 agents | P0 |
| `Dispatcher.InvokeAsync()` per heartbeat event in `MainViewModel` | Dispatcher queue floods with 200 items every 5s | ~50 agents (jank) | P1 |
| `LiveLog` ObservableCollection in `MonitorVM` — not used at fleet scale | Only active for single-agent monitoring | N/A | N/A |

#### The Fix

```xml
<!-- FleetView.xaml: Add virtualization -->
<ListBox ItemsSource="{Binding Cards}"
         VirtualizingStackPanel.IsVirtualizing="True"
         VirtualizingStackPanel.VirtualizationMode="Recycling"
         VirtualizingPanel.ScrollUnit="Pixel"
         ScrollViewer.HorizontalScrollBarVisibility="Disabled">
    <ListBox.ItemsPanel>
        <ItemsPanelTemplate>
            <VirtualizingStackPanel />  <!-- Replace UniformGrid -->
        </ItemsPanelTemplate>
    </ListBox.ItemsPanel>
    <!-- ItemTemplate stays the same -->
</ListBox>
```

```csharp
// FleetVM: Debounce/throttle Refresh() — max once per 2 seconds
private DateTime _lastRefresh = DateTime.MinValue;
private bool _refreshPending;

private void ScheduleRefresh()
{
    if (_refreshPending) return;
    _refreshPending = true;
    
    var elapsed = DateTime.UtcNow - _lastRefresh;
    var delay = elapsed < TimeSpan.FromSeconds(2) 
        ? TimeSpan.FromSeconds(2) - elapsed 
        : TimeSpan.Zero;
    
    _uiDispatcher.InvokeAsync(async () =>
    {
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay);
        _refreshPending = false;
        _lastRefresh = DateTime.UtcNow;
        Refresh();
    }, DispatcherPriority.Background);
}

// Replace all event subscriptions from:
//   events.Subscribe<AgentHeartbeatEvent>(_ => uiDispatcher.InvokeAsync(Refresh));
// To:
//   events.Subscribe<AgentHeartbeatEvent>(_ => ScheduleRefresh());
```

```csharp
// FleetVM.Refresh(): Use delta updates instead of full rebuild
public void Refresh()
{
    var agents = _dispatcher.RegisteredAgents.ToList();
    var existing = Cards.ToDictionary(c => c.AgentName, StringComparer.OrdinalIgnoreCase);
    
    // Add new agents
    foreach (var name in agents.Where(n => !existing.ContainsKey(n)))
        Cards.Add(BuildCard(name));
    
    // Remove departed agents  
    foreach (var card in Cards.Where(c => !agents.Contains(c.AgentName, StringComparer.OrdinalIgnoreCase)).ToList())
        Cards.Remove(card);
    
    // Update existing (properties only — no collection churn)
    foreach (var card in Cards)
        UpdateCardProperties(card);
}
```

#### How to Measure
- **Metric:** Dispatcher queue depth; time spent in Refresh(); visual frame rate (FPS)
- **Tool:** WPF Performance Tools (PerfView + Visual Studio Diagnostic Tools); `Stopwatch` in Refresh()
- **Target:** < 1 Refresh/sec; each Refresh < 50ms; UI frame rate > 30 FPS

---

### LAYER 9: WPF Data Binding & Change Notification

#### What works today at 10 agents
- `[ObservableProperty]` (CommunityToolkit) generates `PropertyChanged` per property
- `RangeObservableCollection<T>` used in `LogBufferService` for batched notifications
- `INotifyPropertyChanged` on all ViewModels
- Bindings mostly OneWay (correct)

#### What breaks at 200 agents

| Issue | Failure Mode | Breaks at | Priority |
|-------|-------------|-----------|----------|
| 200 cards × 5 property changes per heartbeat × 2 times/sec (after debounce) = 2000 PropertyChanged/sec | Binding update storm causes visual tree invalidation | ~100 agents (jank) | P1 |
| `FleetCardVM` properties updated one-at-a-time (not batched) | Each PropertyChanged triggers measure/arrange on that card | ~50 agents | P1 |
| `ObservableCollection<FleetGroupVM>.Clear()` + re-add fires N+1 CollectionChanged events | Massive layout invalidation | ~30 agents (before debounce fix) | P0 (fixed by Layer 8 fix) |

#### The Fix

```csharp
// FleetCardVM: Batch property updates with single notification
public void UpdateFrom(AgentSnapshot snapshot)
{
    // Suppress individual notifications
    _suppressNotify = true;
    Status = snapshot.State;
    CpuUsage = $"{snapshot.CpuUsagePct:F0}%";
    MemoryUsage = $"{snapshot.MemoryUsedMb:F0}MB";
    StatusDetail = snapshot.CurrentActivity;
    _suppressNotify = false;
    
    // Fire single "all properties changed" notification
    OnPropertyChanged(string.Empty);
}
```

#### How to Measure
- **Metric:** PropertyChanged event count/sec (instrument with counter in base VM)
- **Tool:** WPF snoop tool; Visual Studio Live Visual Tree
- **Target:** < 500 PropertyChanged events/sec at steady state

---

### LAYER 10: WebClient (React) Performance

#### What works today at 10 agents
- `useFleetState` hook fetches fleet from `/api/agents/fleet` on SignalR events
- `FleetCard` component renders agent card with Tailwind CSS
- `useAgentTelemetry` polls individual agent at 2s (idle) / 15s (active)
- `errorThrottle` with exponential backoff for error deduplication
- Zustand state management (`useConnectionStore`, `useWatchListStore`)

#### What breaks at 200 agents

| Issue | Failure Mode | Breaks at | Priority |
|-------|-------------|-----------|----------|
| **`useFleetState` refetches on every `AgentStatusChanged` SignalR event** — full GET /api/agents/fleet | 200 status events → 200 API calls → 200 full re-renders of 200-card list | **~30 agents** | **P0** |
| **No `React.memo` on `FleetCard`** — parent re-render causes 200 child re-renders | React reconciliation: 200 components × diffing | ~50 agents | P0 |
| **No virtualization** — CSS grid renders all 200 cards in DOM | DOM node count: 200 × ~20 nodes = 4000 nodes always in viewport | ~100 agents (scroll jank) | P1 |
| **Rate limiter rejects polling** with `QueueLimit=0` — 60 req/min cap vs. fleet fetch storm | HTTP 429 errors in browser console; stale data | ~10 agents with multiple watchers | P0 |
| Memory growth in long sessions — no cleanup of old SignalR event data | Browser tab > 1GB after hours | ~200 running 4+ hours | P1 |

#### The Fix

```typescript
// 1. Debounce fleet refetch — max once per 2 seconds
const fetchFleet = useMemo(
    () => debounce(async () => {
        const { data } = await axios.get<FleetResponse>('/api/agents/fleet');
        setFleet(data.agents);
    }, 2000, { leading: true, trailing: true }),
    []
);

// 2. Wrap FleetCard in React.memo with custom comparator
const FleetCard = React.memo(function FleetCard({ agent, onClick }) {
    // ... existing render ...
}, (prev, next) => prev.agent.name === next.agent.name 
    && prev.agent.status === next.agent.status
    && prev.agent.progressPercent === next.agent.progressPercent);

// 3. Add react-window for virtualization
import { FixedSizeGrid } from 'react-window';

function FleetGrid({ fleet, onSelectAgent }) {
    const columns = 3;
    return (
        <FixedSizeGrid
            columnCount={columns}
            rowCount={Math.ceil(fleet.length / columns)}
            columnWidth={340}
            rowHeight={100}
            height={window.innerHeight - 200}
            width={window.innerWidth - 40}
        >
            {({ columnIndex, rowIndex, style }) => {
                const idx = rowIndex * columns + columnIndex;
                const agent = fleet[idx];
                if (!agent) return null;
                return <div style={style}><FleetCard agent={agent} onClick={() => onSelectAgent(agent)} /></div>;
            }}
        </FixedSizeGrid>
    );
}
```

```json
// 4. Increase rate limit for fleet endpoint
{
  "Security": {
    "RateLimit": {
      "RequestsPerMinute": 300,
      "AdminRequestsPerMinute": 600
    }
  }
}
```

#### How to Measure
- **Metric:** React render count (React DevTools Profiler); API call frequency; DOM node count
- **Tool:** Chrome Performance tab; React DevTools; Network tab
- **Target:** < 1 fleet fetch/2 sec; < 20 FleetCard re-renders per cycle; < 1000 DOM nodes visible

---

### LAYER 11: Memory & GC

#### What works today at 10 agents
- `LogBufferService`: bounded Channel(5000) + trim to 10K entries
- `AppLogger`: ring buffer 5000 entries + daily file rotation
- `ExecutionTracker` (agent-side): ring buffer configurable (`MaxExecutionHistoryCount = 200`)
- `ExecutionSessionManager` history: max 50 sessions
- `RemoteCommandStreamRunner` stderr: 20-line ring buffer

#### What breaks at 200 agents

| Issue | Failure Mode | Breaks at | Priority |
|-------|-------------|-----------|----------|
| Event object allocation: 200 heartbeats/5s × event record = 40 allocations/sec + closures | Gen0 GC pressure; brief pauses | ~200 (minor) | P2 |
| `AgentHealthState` per agent — unbounded history of connectivity events | Memory growth if not trimmed | ~200 long sessions | P2 |
| gRPC response deserialization buffers | Transient allocations; collected in Gen0 | Never (healthy) | N/A |
| `PersistedSession` JSON serialization of all active sessions | Large string allocation during persist | ~50 concurrent sessions with large result sets | P2 |

#### The Fix

```csharp
// Pool event objects to reduce GC pressure (optional — P2)
private static readonly ObjectPool<AgentHeartbeatEvent> _heartbeatPool = 
    ObjectPool.Create<AgentHeartbeatEvent>();

// Trim AgentHealthState connectivity history
public class AgentHealthState
{
    private const int MaxConnectivityHistory = 50;
    // Trim on each state change...
}
```

#### How to Measure
- **Metric:** Gen0/Gen1/Gen2 collection rate; heap size; LOH allocations
- **Tool:** `dotnet-counters`; `dotnet-gcdump`; JetBrains dotMemory
- **Target:** Gen2 collections < 1/min; heap < 500MB at 200 agents; no LOH growth

---

### LAYER 12: Threading & Async

#### What works today at 10 agents
- No `.Result` or `.Wait()` in production code ✅
- Proper async/await throughout
- Agent-side `SemaphoreSlim(1,1)` for command serialization
- `LogBufferService` uses dedicated `DispatcherTimer` (not ThreadPool)

#### What breaks at 200 agents

| Issue | Failure Mode | Breaks at | Priority |
|-------|-------------|-----------|----------|
| **`PipelineExecutorBase.ExecuteChildrenAsync` — unbounded `Task.WhenAll(children)`** | If pipeline has 200 parallel children, all compete for ThreadPool simultaneously; combined with 200 gRPC streams = starvation | ~100 agents with parallel pipelines | **P0** |
| `EventAggregator` queues `ThreadPool.QueueUserWorkItem` per handler per event | Flood risk (see Layer 4) | ~80 agents | P0 |
| Missing `ThreadPool.SetMinThreads()` configuration | .NET default min = CPU count (8-16); under load, ThreadPool ramps up slowly (500ms/thread) | ~50 concurrent tasks needing threads | P1 |
| `AgentHealthState.ConsecutiveFailures++` not atomic | Lost increments under high concurrency | ~200 (correctness issue) | P1 |

#### The Fix

```csharp
// 1. Pipeline executor: Add MaxDegreeOfParallelism
protected async Task<bool> ExecuteChildrenAsync(
    List<IActionNode> children, ExecutionMode mode, ...)
{
    if (mode == ExecutionMode.Parallel)
    {
        // Limit concurrent agent operations to prevent ThreadPool starvation
        var semaphore = new SemaphoreSlim(50, 50);
        var tasks = children.Select(async child =>
        {
            await semaphore.WaitAsync(ct);
            try { return await ExecuteNodeAsync(child, ctx, ct); }
            finally { semaphore.Release(); }
        });
        var results = await Task.WhenAll(tasks);
        return results.All(r => r);
    }
}

// 2. Set ThreadPool minimum at startup (Program.cs)
ThreadPool.SetMinThreads(workerThreads: 200, completionPortThreads: 200);

// 3. Fix atomic increment
Interlocked.Increment(ref state._consecutiveFailures);
```

#### How to Measure
- **Metric:** `ThreadPool.ThreadCount`; `ThreadPool.PendingWorkItemCount`; thread injection rate
- **Tool:** `dotnet-counters monitor --counters System.Runtime`
- **Target:** Pending work items < 100; thread count stable at ~200-250; no ThreadPool starvation events

---

## 3. BREAKING POINT ESTIMATE

| Layer | Component | Breaks at ~N Agents | Symptom |
|-------|-----------|--------------------:|---------|
| 8 | FleetVM.Refresh() per heartbeat | **~30** | WPF UI frozen, unresponsive to clicks |
| 10 | useFleetState refetch per event | **~30** | Browser tab sluggish, 200+ API calls/min |
| 8 | FleetView no virtualization | ~50 | Scroll jank, high CPU |
| 10 | FleetCard no React.memo | ~50 | React profiler shows 200 re-renders per update |
| 4 | EventAggregator ThreadPool flood | ~80 | Thread count grows; GC pauses |
| 12 | Pipeline unbounded parallelism | ~100 | ThreadPool starvation; timeouts on unrelated operations |
| 3 | Health poll Task.WhenAll(200) | ~100 (20% offline) | 50 threads blocked 5s; operations time out |
| 5 | BuildFleetSnapshot O(N²) | ~200 | Snapshot takes > 500ms; SignalR clients see stale data |
| 6 | Output events unbatched | ~50 streaming | SignalR message rate > 1000/sec; backpressure |
| 7 | _atomicLock serialization | ~50 concurrent lock ops | Lock acquisition latency > 100ms |
| 1 | Startup channel storm | ~100 within 5s | Connect timeouts cascade |
| 11 | Memory/GC | ~200 long session | Gen2 GC pauses 50-100ms |

---

## 4. PRIORITIZED REMEDIATION ROADMAP

### P0 — Required to Reach 200 (Blockers)

| # | Fix | Layer | Effort | Impact |
|---|-----|-------|--------|--------|
| 1 | **Debounce FleetVM.Refresh()** — max once/2s, delta update instead of rebuild | 8 | 4h | Eliminates UI freeze |
| 2 | **Add virtualization to FleetView.xaml** — VirtualizingStackPanel | 8 | 2h | 200 cards without layout thrashing |
| 3 | **Debounce useFleetState** — max 1 fetch/2s, use delta SignalR events | 10 | 3h | Eliminates API storm |
| 4 | **Add React.memo to FleetCard** | 10 | 30min | 200× fewer re-renders |
| 5 | **Batch output events in SignalRNotifier** — flush every 1s like heartbeats | 6 | 3h | 2000 msg/sec → 1 msg/sec |
| 6 | **Cap pipeline parallelism** — `SemaphoreSlim(50)` in ExecuteChildrenAsync | 12 | 1h | Prevents ThreadPool starvation |
| 7 | **Set `ThreadPool.SetMinThreads(200, 200)` in Program.cs** | 12 | 5min | Eliminates slow thread injection |
| 8 | **Increase rate limit to 300/min** for standard users (or exempt fleet endpoint) | API | 5min | Prevents 429 errors |

### P1 — Required to Preserve UX at 200

| # | Fix | Layer | Effort | Impact |
|---|-----|-------|--------|--------|
| 9 | Cache `BuildFleetSnapshot` with 2-5s TTL + O(1) lock lookup | 5,7 | 2h | Snapshot from 500ms → < 10ms |
| 10 | Throttle health poll parallelism — max 50 concurrent pings | 3 | 1h | Prevents timeout cascades |
| 11 | Add jitter to health poll interval | 3 | 30min | Spreads load uniformly |
| 12 | Add react-window virtualization to FleetPage grid | 10 | 3h | DOM nodes: 4000 → ~60 |
| 13 | Replace EventAggregator ThreadPool dispatch with bounded Channel | 4 | 3h | Controlled dispatch rate |
| 14 | Fix `ConsecutiveFailures++` → `Interlocked.Increment` | 12 | 10min | Correctness under concurrency |
| 15 | Throttle channel establishment — SemaphoreSlim(50) on GetClient | 1 | 1h | Prevents startup storm |
| 16 | Batch WPF property change notifications in FleetCardVM | 9 | 2h | 5× fewer binding updates |
| 17 | Add subscription leak detection / IDisposable verification | 4 | 2h | Prevents long-session memory growth |

### P2 — Optimizations

| # | Fix | Layer | Effort | Impact |
|---|-----|-------|--------|--------|
| 18 | Pool heartbeat event objects to reduce GC pressure | 11 | 2h | Minor GC improvement |
| 19 | Debounce AgentLockManager persistence (max 1/2s) | 7 | 30min | Reduces disk I/O |
| 20 | Consider push-based telemetry (agent pushes, controller aggregates) | 3 | 2d | Eliminates polling entirely |
| 21 | Add Redis backplane for multi-controller scale-out | 5 | 1d | Horizontal scaling (future) |
| 22 | Agent-side re-registration exponential backoff | 1 | 1h | Prevents thundering herd |

---

## 5. MEASUREMENT PLAN

### Instrumentation (Before Fixes)

| Metric | Where | How |
|--------|-------|-----|
| ThreadPool queue depth | Controller startup | `ThreadPool.PendingWorkItemCount` every 1s → AppLogger |
| FleetVM.Refresh() duration | FleetVM.cs | `Stopwatch` around Refresh(), log if > 50ms |
| SignalR messages/sec | SignalRNotifier | Increment counter per SendSafe, log every 10s |
| BuildFleetSnapshot latency | ControllerHub | `Stopwatch`, log P95 |
| API 429 error rate | WebApi middleware | Counter on rate-limit rejection |
| React render count | FleetPage | React DevTools Profiler in production build |
| GC collection counts | Process-wide | `dotnet-counters` Gen0/1/2 |
| WPF Dispatcher queue | App.xaml.cs | `Dispatcher.CurrentDispatcher.Hooks.OperationPosted` counter |

### Load Testing Approach

```yaml
# Simulate 200 agents without 200 real VMs:
# 
# Option A: gRPC Agent Simulator (.NET console app)
#   - Spawns N async loops, each:
#     1. Registers as unique agent
#     2. Sends heartbeats every 5s
#     3. Responds to RunCommand with simulated stdout (10 lines/sec)
#     4. Responds to GetAgentSnapshot with synthetic metrics
#   - Single machine can simulate 200 agents (just gRPC clients)
#
# Option B: k6 + grpcurl for API load testing
#   - Tests REST endpoints under load
#   - Simulates N browser clients consuming SignalR
#
# Option C: Integration test fixture
#   - TestControllerGrpc.Tests with mock IAgentGrpcDispatcher
#   - Injects 200 synthetic heartbeat events per second
#   - Measures SignalR output volume, UI refresh rate

# Recommended: Build Option A as a TestAgentSimulator project
# ~ 200 lines of code, reusable for CI/CD load gates
```

### Key Metrics & Target Thresholds

| Metric | Threshold (200 agents) | Alert if exceeded |
|--------|----------------------|-------------------|
| ThreadPool.PendingWorkItemCount | < 100 | > 200 |
| UI Frame Rate (WPF) | > 30 FPS | < 15 FPS |
| BuildFleetSnapshot P95 | < 50ms | > 200ms |
| SignalR messages/sec (outbound) | < 20 | > 100 |
| API 429 errors/min | 0 | > 5 |
| Controller memory (working set) | < 1GB | > 2GB |
| Gen2 GC collections/min | < 2 | > 5 |
| FleetVM.Refresh() duration | < 50ms | > 200ms |
| Health poll cycle time | < 15s | > 30s |
| gRPC channel count | ~200 | > 250 (leak) |

---

## 6. ARCHITECTURAL RECOMMENDATIONS

### Patterns That Should Change

| Current Pattern | Problem at Scale | Recommended Pattern |
|-----------------|-----------------|---------------------|
| Pull-based heartbeat → EventAggregator → every subscriber | N×M fan-out per heartbeat | Keep, but ensure all subscribers are debounced/batched |
| Full fleet snapshot on demand | O(N²) computation on every request | Maintain pre-computed snapshot, invalidate on state change (2s TTL cache) |
| Per-line output forwarding to SignalR | 2000+ messages/sec | Batch output per agent per second |
| Full ObservableCollection rebuild per event | O(N) UI-thread work per event | Delta updates: add/remove/update individual items |
| Event → full API refetch (React) | 200 API calls per second peak | Incremental SignalR events (push delta, not full state) |
| Unbounded Task.WhenAll for parallel pipeline | ThreadPool exhaustion | Bounded parallelism via SemaphoreSlim |

### Single-Controller Model Assessment

**The single-controller model survives 200 agents** based on:
- CPU: ~200 concurrent gRPC streams + SignalR is well within a modern 8-core server
- Memory: ~500KB for agent state + 10K log entries + channels = < 1GB total
- Network: 200 × (500 bytes heartbeat + 1KB output batch) = ~300KB/sec = negligible
- ThreadPool: With fixes (bounded parallelism, MinThreads=200), handles 200 concurrent ops

**Scale-out is NOT needed for 200 agents.** Consider only if:
- Target exceeds 500 agents
- High availability (active-passive) is required
- Multiple geographic regions need local controllers

### Summary Architecture Diagram (200-Agent Target)

```
┌─────────────────────────────────────────────────────────────────┐
│                        CONTROLLER HOST                           │
│                                                                  │
│  ┌─────────────┐     ┌──────────────────┐    ┌──────────────┐  │
│  │ AgentRegistry│     │ PipelineExecutor │    │  SignalR Hub  │  │
│  │ (200 entries)│     │  SemSlim(50)     │    │  (cached snap)│  │
│  └──────┬───────┘     └───────┬──────────┘    └───────┬───────┘  │
│         │                     │                       │          │
│  ┌──────▼───────┐     ┌──────▼──────────┐    ┌──────▼───────┐  │
│  │ LockManager  │     │ EventAggregator │    │SignalRNotifier│  │
│  │ (O(1) lookup)│     │ (Channel-based) │    │(batch output) │  │
│  └───────────────┘     └────────────────┘    └──────────────┘  │
│                                                                  │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │           AgentGrpcClientManager (200 channels)           │   │
│  │           SemSlim(50) connect throttle                    │   │
│  │           HTTP/2 multiplexing enabled                     │   │
│  └──────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
         │ gRPC (200 channels)           │ SignalR WebSocket
         ▼                               ▼
┌────────────────┐              ┌──────────────────┐
│  200 Agents    │              │  WPF + Browsers  │
│  (test VMs)    │              │  (< 10 clients)  │
│  5200/tcp each │              │  virtualized UI  │
└────────────────┘              └──────────────────┘
```

---

## APPENDIX: Quick Reference — Files to Modify

| File | Fix Summary |
|------|-------------|
| `TestControllerGrpc/ViewModels/AgentWorkspace/FleetVM.cs` | Debounce Refresh(); delta updates |
| `TestControllerGrpc/Views/AgentWorkspace/FleetView.xaml` | Add VirtualizingStackPanel |
| `TestControllerGrpc/ViewModels/MainViewModel.Agents.cs` | Debounce RefreshAgentStatusSummary() |
| `TestControllerGrpc.Core/Services/EventAggregator.cs` | Channel-based dispatch |
| `TestControllerGrpc.Core/Services/PipelineExecutorBase.cs` | SemaphoreSlim(50) on parallel children |
| `TestController.Api/Services/SignalRNotifier.cs` | Batch AgentOutputEvent |
| `TestController.Api/Hubs/ControllerHub.cs` | Cache BuildFleetSnapshot; O(1) lock lookup |
| `TestController.WebApi/Program.cs` | ThreadPool.SetMinThreads(200,200); increase rate limit |
| `TestController.WebApi/Services/AgentGrpcClientManager.cs` | SemaphoreSlim(50) connect throttle |
| `TestController.WebClient/src/hooks/useFleetState.ts` | Debounce fetchFleet (2s) |
| `TestController.WebClient/src/pages/FleetPage.tsx` | React.memo + react-window |
| `TestControllerGrpc.Core/Services/AgentLockManager.cs` | Debounce persist; fix atomic increment |
