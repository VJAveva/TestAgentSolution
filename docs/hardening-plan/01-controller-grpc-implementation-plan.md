# TestControllerGrpc Hardening — Systematic Implementation Plan

**Created:** 2026-05-21  
**Source:** [01-controller-grpc-hardening.md](01-controller-grpc-hardening.md)  
**Target Project:** `TestControllerGrpc`  
**Primary File:** `TestControllerGrpc/Services/AgentGrpcDispatcher.cs`

---

## Implementation Overview

| Step | Work Item | Priority | Risk Addressed | Estimated Complexity |
|---:|---|:---:|---|---|
| 1 | Guard `DiagnoseAgentAsync` during active execution | P1 | CTRL-002 | Low |
| 2 | Add "metrics paused" warning to Monitor UI | P1 | CTRL-001 | Low |
| 3 | Extract dispatcher-level metric cache service | P1 | CTRL-001/005 | Medium |
| 4 | Add channel reset/recycle method | P1 | CTRL-004 | Medium |
| 5 | Centralize timeout constants into typed options | P2 | CTRL-006 | Medium |
| 6 | Add embedded host startup validation | P2 | CTRL-007 | Low |
| 7 | Extract telemetry formatting from MonitorVM | P2 | CTRL-005 | Medium |
| 8 | Add all missing tests | P1/P2 | TEST coverage | Medium |

---

## Step 1 — Guard `DiagnoseAgentAsync` During Active Execution

**Risk:** CTRL-002 — Diagnostics makes 2 gRPC calls (`GetState`, `GetAgentSnapshot`) that can interfere with in-flight command streams.

**File:** `TestControllerGrpc/Services/AgentGrpcDispatcher.cs` (Lines 162–287)

### Current Behavior

`DiagnoseAgentAsync` runs a 7-step diagnostic check with no execution guard. Steps 6–7 make gRPC calls that risk triggering HTTP/2 RST_STREAM on the same channel.

### Implementation

Add `IsAgentExecuting` guard at the top of `DiagnoseAgentAsync`. When active, return synthetic diagnostic results for the gRPC steps and skip actual network calls:

```csharp
public async Task<List<DiagnosticResult>> DiagnoseAgentAsync(string agentName, CancellationToken ct = default)
{
    var results = new List<DiagnosticResult>();

    // ── SAFEGUARD: Do not make gRPC calls during active execution ──
    if (IsAgentExecuting(agentName))
    {
        results.Add(new DiagnosticResult("Registration", true, "Agent registered"));
        results.Add(new DiagnosticResult("Execution Guard", true,
            "Diagnostics skipped — agent is currently executing. gRPC calls suppressed to protect command stream."));
        return results;
    }

    // ... existing 7-step diagnostics ...
}
```

### Caller Updates

| Caller | File | Action |
|--------|------|--------|
| `MonitorVM.RunDiagnosticsAsync` | `ViewModels/AgentWorkspace/MonitorVM.cs` (L548) | Add UI feedback: "Diagnostics unavailable during active execution" |
| `RegistryVM` | `ViewModels/AgentWorkspace/RegistryVM.cs` (L262) | Disable diagnose button or show notice |
| `MainViewModel.Agents` | `ViewModels/MainViewModel.Agents.cs` (L133) | Same pattern |

### Tests to Add

```
DiagnoseAgent_ReturnsSkipResult_WhenAgentIsExecuting
DiagnoseAgent_RunsFullDiagnostics_WhenAgentIsIdle
```

### Acceptance Criteria

- [ ] `DiagnoseAgentAsync` returns immediately with guard message when `IsAgentExecuting` is true.
- [ ] No gRPC calls occur during active execution.
- [ ] UI callers show informative message to operator.
- [ ] Test verifies synthetic path completes in < 50ms.

---

## Step 2 — Add "Metrics Paused" Warning to Monitor UI

**Risk:** CTRL-001 — Operator may not know why live metrics are stale during execution.

**File:** `TestControllerGrpc/ViewModels/AgentWorkspace/MonitorVM.cs` (Lines 258–280)

### Current Behavior

`PollTelemetryAsync` correctly uses `_lastMetrics` cache during execution, but the UI shows status as "BUSY" with no indication that metrics are frozen/cached.

### Implementation

Add a visible indicator that metrics are cached, not live:

```csharp
// In the IsAgentExecuting branch of PollTelemetryAsync:
if (_dispatcher.IsAgentExecuting(AgentName))
{
    RefreshSessionInfo();
    StatusKind = "Busy";
    StatusText = $"BUSY · {SessionPipeline}";
    MetricsFrozenNotice = "Live metrics paused during execution to protect command stream";
    IsOnline = true;
    if (_lastMetrics != null)
        ApplyMetrics(_lastMetrics);
    return;
}
else
{
    MetricsFrozenNotice = null; // Clear when execution ends
}
```

### UI Changes

In `MonitorView.xaml`, add a subtle notice bar (e.g., amber info panel) bound to `MetricsFrozenNotice`:

```xml
<TextBlock Text="{Binding MetricsFrozenNotice}"
           Visibility="{Binding MetricsFrozenNotice, Converter={StaticResource NullToCollapsedConverter}}"
           Foreground="#F59E0B" FontStyle="Italic" Margin="0,4,0,0"/>
```

### Acceptance Criteria

- [ ] During active execution, UI shows "Live metrics paused…" notice.
- [ ] Notice disappears immediately when execution completes.
- [ ] Existing cached metrics still display in gauges.

---

## Step 3 — Extract Dispatcher-Level Metric Cache Service

**Risk:** CTRL-005 / Consistency — `MonitorVM` caches metrics locally, but `WebApi` and other consumers can't share that cache.

**File (new):** `TestControllerGrpc/Services/AgentTelemetryCacheService.cs`

### Design

```csharp
public interface IAgentTelemetryCache
{
    AgentSnapshot? GetCachedSnapshot(string agentName);
    ResourceMetrics? GetCachedMetrics(string agentName);
    void Update(string agentName, AgentSnapshot snapshot, ResourceMetrics? metrics);
    bool IsStale(string agentName, TimeSpan maxAge);
}

public class AgentTelemetryCacheService : IAgentTelemetryCache
{
    private readonly ConcurrentDictionary<string, CachedTelemetry> _cache = new(StringComparer.OrdinalIgnoreCase);

    private record CachedTelemetry(AgentSnapshot Snapshot, ResourceMetrics? Metrics, DateTime CachedUtc);

    public AgentSnapshot? GetCachedSnapshot(string agentName)
        => _cache.TryGetValue(agentName, out var c) ? c.Snapshot : null;

    public ResourceMetrics? GetCachedMetrics(string agentName)
        => _cache.TryGetValue(agentName, out var c) ? c.Metrics : null;

    public void Update(string agentName, AgentSnapshot snapshot, ResourceMetrics? metrics)
        => _cache[agentName] = new(snapshot, metrics, DateTime.UtcNow);

    public bool IsStale(string agentName, TimeSpan maxAge)
        => !_cache.TryGetValue(agentName, out var c) || (DateTime.UtcNow - c.CachedUtc) > maxAge;
}
```

### Integration Points

1. **AgentGrpcDispatcher** — After each successful `TestConnectionAsync`, update cache.
2. **MonitorVM** — Replace local `_lastMetrics` with injected `IAgentTelemetryCache`.
3. **Embedded WebApi** — Inject same cache instance to serve `/api/agents/{name}/telemetry` without independent gRPC calls during execution.

### Acceptance Criteria

- [ ] Cache populated on every successful telemetry poll.
- [ ] Cache queryable without gRPC by WebApi or other UI consumers.
- [ ] `IsStale()` correctly reports age.
- [ ] Tests: `AgentTelemetryCacheService_UpdateAndRetrieve`, `AgentTelemetryCacheService_IsStale`.

---

## Step 4 — Add Channel Reset/Recycle Method

**Risk:** CTRL-004 — Failed/stale gRPC channels remain until agent is unregistered. No way to recover a broken channel without unregister+re-register.

**File:** `TestControllerGrpc/Services/AgentGrpcDispatcher.cs` (AgentEndpoint class, Lines 930–1050)

### Current Behavior

- `PooledConnectionLifetime = Timeout.InfiniteTimeSpan` — channels never auto-recycle.
- The only way to reset is `UnregisterAgent()` → `RegisterAgent()`.

### Implementation

Add a `ResetChannelAsync` method to `AgentGrpcDispatcher`:

```csharp
public async Task<bool> ResetChannelAsync(string agentName)
{
    if (IsAgentExecuting(agentName))
        return false; // Never reset during active execution

    if (!_agents.TryGetValue(agentName.ToUpperInvariant(), out var endpoint))
        return false;

    var address = endpoint.Address;

    // Dispose old channel
    endpoint.Dispose();

    // Recreate with same address
    var newEndpoint = new AgentEndpoint(address);
    _agents[agentName.ToUpperInvariant()] = newEndpoint;

    // Reset health state
    if (_healthStates.TryGetValue(agentName.ToUpperInvariant(), out var health))
    {
        health.ConsecutiveFailures = 0;
        health.IsHealthy = true;
        health.CircuitOpenedUtc = null;
    }

    // Verify new channel works
    return await PingAsync(agentName);
}
```

### Auto-Reset Policy

Add automatic channel reset when health state reaches critical failure threshold:

```csharp
// In RecordFailure():
if (health.ConsecutiveFailures >= 10 && !IsAgentExecuting(agentName))
{
    _ = Task.Run(() => ResetChannelAsync(agentName)); // Background reset
}
```

### UI Integration

- Add "Reset Connection" button in Agent Workspace / Registry context menu.
- Log channel reset events with `[Channel]` category.

### Tests to Add

```
ResetChannel_RecreatesEndpoint_WhenAgentIdle
ResetChannel_ReturnsFalse_WhenAgentIsExecuting
ResetChannel_ResetsHealthState
AutoReset_TriggersAfterConsecutiveFailures
```

### Acceptance Criteria

- [ ] Channel reset is safe (blocked during execution).
- [ ] After reset, agent can be pinged and polled normally.
- [ ] Health state resets to healthy after channel recreation.
- [ ] UI exposes manual reset action.
- [ ] Automatic reset after 10 consecutive failures (configurable).

---

## Step 5 — Centralize Timeout Constants into Typed Options

**Risk:** CTRL-006 — 15+ timeout constants scattered across dispatcher and MonitorVM make tuning and deployment-specific configuration impossible.

**File (new):** `TestControllerGrpc/Services/ControllerTimeoutOptions.cs`

### Design

```csharp
public class ControllerTimeoutOptions
{
    public const string SectionName = "Controller:Timeouts";

    // Dispatcher
    public int TestConnectionTimeoutSeconds { get; set; } = 5;
    public int PingTimeoutSeconds { get; set; } = 3;
    public int DiagnosticsTcpTimeoutSeconds { get; set; } = 3;
    public int DiagnosticsHttpTimeoutSeconds { get; set; } = 3;
    public int DiagnosticsGrpcTimeoutSeconds { get; set; } = 5;
    public int WaitForAgentFreeMaxSeconds { get; set; } = 30;
    public int BusyRecoveryMaxSeconds { get; set; } = 120;
    public int BusyRecoveryIntervalSeconds { get; set; } = 10;

    // Channel
    public int ChannelConnectTimeoutSeconds { get; set; } = 30;
    public int KeepAlivePingDelaySeconds { get; set; } = 60;
    public int KeepAlivePingTimeoutSeconds { get; set; } = 30;
    public int PooledConnectionIdleMinutes { get; set; } = 5;
    public int CircuitBreakerBreakSeconds { get; set; } = 15;
    public int AutoResetFailureThreshold { get; set; } = 10;

    // Monitor polling
    public int TelemetryPollIntervalMs { get; set; } = 2000;
    public int SessionElapsedTimerMs { get; set; } = 1000;
}
```

### Registration

```csharp
// In App.xaml.cs or service registration:
services.Configure<ControllerTimeoutOptions>(
    configuration.GetSection(ControllerTimeoutOptions.SectionName));
```

### appsettings.json Section

```json
{
  "Controller": {
    "Timeouts": {
      "TestConnectionTimeoutSeconds": 5,
      "PingTimeoutSeconds": 3,
      "TelemetryPollIntervalMs": 2000
    }
  }
}
```

### Migration Strategy

1. Inject `IOptions<ControllerTimeoutOptions>` into `AgentGrpcDispatcher` constructor.
2. Replace all hardcoded `TimeSpan.FromSeconds(5)` etc. with `_options.Value.TestConnectionTimeoutSeconds`.
3. Inject into `MonitorVM` for polling intervals.
4. Add startup validation (all values > 0, reasonable bounds).

### Acceptance Criteria

- [ ] All timeouts configurable via `appsettings.json`.
- [ ] Defaults match current hardcoded values (zero behavioral change).
- [ ] Startup validation rejects invalid values with clear error messages.
- [ ] Tests verify default behavior unchanged.

---

## Step 6 — Add Embedded Host Startup Validation

**Risk:** CTRL-007 — Embedded web host DI bridge can fail silently if WPF services aren't wired.

**File:** `TestControllerGrpc/Services/` (startup/host configuration)

### Implementation

Add a health check that validates all required WPF-bridged services are resolvable:

```csharp
public class EmbeddedHostDependencyValidator : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<EmbeddedHostDependencyValidator> _logger;

    public Task StartAsync(CancellationToken ct)
    {
        var requiredServices = new[]
        {
            typeof(IAgentGrpcDispatcher),
            typeof(IAgentTelemetryCache),
            typeof(IExecutionSessionManager),
        };

        foreach (var svc in requiredServices)
        {
            var resolved = _services.GetService(svc);
            if (resolved == null)
            {
                _logger.LogCritical("Required WPF-bridge service not registered: {Service}", svc.Name);
                throw new InvalidOperationException($"Embedded host missing required service: {svc.Name}");
            }
        }

        _logger.LogInformation("Embedded host dependency validation passed");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
```

### Acceptance Criteria

- [ ] If bridge services are missing, host throws immediately at startup (fail-fast).
- [ ] If all present, logs success and starts normally.
- [ ] Health endpoint `/health` reflects embedded host readiness.

---

## Step 7 — Extract Telemetry Formatting from MonitorVM

**Risk:** CTRL-005 — `MonitorVM` mixes UI concerns (timers, bindings), telemetry polling, and metric formatting.

### Current State

`MonitorVM.cs` is ~650+ lines mixing:
- Polling logic (`PollTelemetryAsync`)
- Metric formatting (`ApplyMetrics`, CPU/Memory/Disk/Network formatting)
- Session state tracking
- Diagnostics invocation
- Timer management

### Target Architecture

```
MonitorVM (UI bindings, timers, commands)
    └── uses AgentTelemetryFormatter (pure formatting)
    └── uses IAgentTelemetryCache (cache, from Step 3)
    └── uses IAgentGrpcDispatcher (data source)
```

**Extract:** `AgentTelemetryFormatter.cs`

```csharp
public static class AgentTelemetryFormatter
{
    public static (string Text, string Kind) FormatCpuUsage(double cpuPercent) { ... }
    public static (string Text, string Kind) FormatMemory(long usedMB, long totalMB) { ... }
    public static (string Text, string Kind) FormatDisk(long freeGB, long totalGB) { ... }
    public static string FormatUptime(TimeSpan uptime) { ... }
    public static string FormatSessionElapsed(DateTime startUtc) { ... }
}
```

### Acceptance Criteria

- [ ] All metric formatting is pure (no VM dependencies).
- [ ] `MonitorVM` delegates formatting to extracted class.
- [ ] Unit tests cover all formatters with boundary values.
- [ ] No behavioral change in UI.

---

## Step 8 — Test Plan

### P1 Tests (Required for confidence)

| Test Name | Validates | File |
|-----------|-----------|------|
| `DiagnoseAgent_ReturnsSkipResult_WhenAgentIsExecuting` | Step 1 guard | `ExecutionStreamSafeguardTests.cs` |
| `DiagnoseAgent_RunsFullSteps_WhenAgentIsIdle` | Step 1 no regression | `ExecutionStreamSafeguardTests.cs` |
| `MonitorTelemetryCache_UpdateAndRetrieve` | Step 3 cache | New: `AgentTelemetryCacheTests.cs` |
| `MonitorTelemetryCache_IsStale_ReportsCorrectly` | Step 3 staleness | `AgentTelemetryCacheTests.cs` |
| `ResetChannel_RecreatesEndpoint_WhenIdle` | Step 4 reset | `ChannelResetTests.cs` |
| `ResetChannel_BlockedDuringExecution` | Step 4 safety | `ChannelResetTests.cs` |
| `CancellationHandling_UserCancel_VsTimeout` | Existing gap | `CancellationHandlingTests.cs` |

### P2 Tests (Quality improvement)

| Test Name | Validates | File |
|-----------|-----------|------|
| `TelemetryFormatter_Cpu_BoundaryValues` | Step 7 | `TelemetryFormatterTests.cs` |
| `TelemetryFormatter_Memory_LargeValues` | Step 7 | `TelemetryFormatterTests.cs` |
| `TimeoutOptions_DefaultsMatchHardcoded` | Step 5 | `TimeoutOptionsTests.cs` |
| `TimeoutOptions_Validation_RejectsNegative` | Step 5 | `TimeoutOptionsTests.cs` |
| `EmbeddedHost_FailsFast_WhenServiceMissing` | Step 6 | `EmbeddedHostValidationTests.cs` |

---

## Implementation Order & Dependencies

```
Step 1 (Diagnose guard)          ← Independent, highest value, lowest risk
     ↓
Step 2 (Monitor UI warning)      ← Independent of Step 1, quick win
     ↓
Step 3 (Telemetry cache)         ← Foundation for WebApi integration
     ↓
Step 4 (Channel reset)           ← Uses health states, independent
     ↓
Step 5 (Timeout options)         ← Refactoring, touches many lines
     ↓
Step 6 (Host validation)         ← Quick, independent
     ↓
Step 7 (Extract formatter)       ← Refactoring, clean-up, lowest priority
     ↓
Step 8 (Tests)                   ← Parallel with each step above
```

### Recommended Execution Batches

| Batch | Steps | Theme |
|-------|-------|-------|
| **Batch A** (Safety) | 1 + 2 + tests | Protect execution stream from all known callers |
| **Batch B** (Architecture) | 3 + 4 + tests | Shared cache + channel lifecycle |
| **Batch C** (Configuration) | 5 + 6 + tests | Make system configurable and production-ready |
| **Batch D** (Cleanup) | 7 + remaining tests | Code quality and testability |

---

## Validation Gates

After each batch:

```powershell
# Build
dotnet build TestAgentSolution.sln -v q

# Run safeguard tests
dotnet test TestControllerGrpc.Tests\TestControllerGrpc.Tests.csproj --no-build

# Run WebApi tests (ensure no regression in shared services)
dotnet test TestController.WebApi.Tests\TestController.WebApi.Tests.csproj --no-build
```

### Manual Smoke Tests

- [ ] Open Monitor during long install → no gRPC exception, metrics show cached.
- [ ] Click Diagnostics during execution → immediate return with guard message.
- [ ] Kill agent process → channel marked unhealthy → Reset Connection → recovery.
- [ ] After execution → real metrics resume within one polling cycle (2s).
- [ ] ForceReady and TerminateExecution still work against old/new agents.

---

## Risk Mitigation

| Risk | Mitigation |
|------|------------|
| Step 5 (timeout refactor) breaks existing behavior | Keep defaults identical; add test verifying parity |
| Step 4 (channel reset) causes transient failures | Guard with `IsAgentExecuting`; log all resets |
| Step 3 (cache) serves stale data indefinitely | `IsStale()` check + configurable max age |
| Step 7 (extract) introduces formatting regression | Pure function tests with known inputs/outputs |
