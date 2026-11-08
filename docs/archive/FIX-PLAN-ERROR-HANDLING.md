# Error Handling & Resilience Fix Plan

> **Created:** 2025-05-23 | **Scope:** P1–P2 error handling gaps from GAP-ANALYSIS.md §1

---

## Fix 1 — P1: Add Global `IHubFilter` to `ControllerHub`

**File:** `TestController.Api/Hubs/ControllerHub.cs`

**Problem:** `RequestFleetSnapshot()` (and future hub methods) can throw unhandled exceptions — from `_dispatcher.RegisteredAgents`, `GetAllAgentHealth()`, LINQ operations, etc. SignalR treats unhandled exceptions as fatal for the invoking connection: the client receives an error message and the connection may drop. If the hub method is invoked in a group broadcast context, all clients in the group lose their connection.

**Fix:** Add a global `IHubFilter` that wraps every hub method invocation in a try/catch, logs the error, and returns a safe error response instead of propagating the exception.

```csharp
// New file: TestController.Api/Hubs/HubExceptionFilter.cs
using Microsoft.AspNetCore.SignalR;

namespace TestController.Api.Hubs;

/// <summary>
/// Global hub filter that catches unhandled exceptions in all hub methods,
/// logs them, and prevents connection drops from unexpected failures.
/// </summary>
public sealed class HubExceptionFilter : IHubFilter
{
    private readonly ILogger<HubExceptionFilter> _logger;

    public HubExceptionFilter(ILogger<HubExceptionFilter> logger)
    {
        _logger = logger;
    }

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        try
        {
            return await next(invocationContext);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[SignalR] Unhandled exception in hub method {Method} from connection {ConnectionId}",
                invocationContext.HubMethodName,
                invocationContext.Context.ConnectionId);

            // Return a HubException with a safe message (no stack trace to client)
            throw new HubException($"Server error in {invocationContext.HubMethodName}. Please retry.");
        }
    }
}
```

**Registration** — in the SignalR service setup (wherever `AddSignalR()` is called):

```csharp
builder.Services.AddSignalR(options =>
{
    options.AddFilter<HubExceptionFilter>();
});

// OR if AddSignalR() is already configured:
builder.Services.AddSignalR().AddHubOptions<ControllerHub>(options =>
{
    options.AddFilter<HubExceptionFilter>();
});
```

**Why `IHubFilter` over per-method try/catch:**
- Covers all hub methods automatically (current and future)
- Single place for error logging policy
- Consistent error shape for clients
- `HubException` sends message to client without dropping the connection

**Risk:** None — this is purely additive. Worst case is clients see "Server error" messages instead of silent disconnects (which is the desired behavior).

**Validation:** Integration test — invoke `RequestFleetSnapshot` when dispatcher throws → verify connection stays alive and error message is returned.

---

## Fix 2 — P1: Add Polly Resilience to `StandaloneAgentDispatcher`

**File:** `TestController.WebApi/Services/StandaloneAgentDispatcher.cs`

**Problem:** The WPF dispatcher (`AgentGrpcDispatcher`) wraps every gRPC call in a `ResiliencePipeline` (retry 3× with exponential backoff + circuit breaker + 24h outer timeout). The standalone WebApi dispatcher has **no** such wrapper — a single transient gRPC failure (network blip, container restart, DNS flap) causes an immediate hard failure with no recovery.

**Fix:** Add a `ResiliencePipeline` to `StandaloneAgentDispatcher` that mirrors the WPF dispatcher's policy. The pipeline should wrap the `RemoteCommandStreamRunner.StreamAsync` call.

```csharp
// Add to StandaloneAgentDispatcher fields:
private readonly ResiliencePipeline _resilience;

// In constructor, build the pipeline:
public StandaloneAgentDispatcher(
    AgentGrpcClientManager clientManager,
    AgentRegistry registry,
    ILogger<StandaloneAgentDispatcher> logger)
{
    _clientManager = clientManager;
    _registry = registry;
    _logger = logger;
    _resilience = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromSeconds(1),
            BackoffType = DelayBackoffType.Exponential,
            ShouldHandle = new PredicateBuilder().Handle<RpcException>(ex =>
                ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded),
            OnRetry = args =>
            {
                _logger.LogWarning(
                    "Retry #{Attempt} for gRPC call after {Delay}: {Exception}",
                    args.AttemptNumber, args.RetryDelay, args.Outcome.Exception?.Message);
                return default;
            }
        })
        .AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(30),
            MinimumThroughput = 5,
            BreakDuration = TimeSpan.FromSeconds(30),
        })
        .AddTimeout(TimeSpan.FromHours(24))
        .Build();
}
```

**Wrap the execution call:**

```csharp
// In ExecuteRemoteCommandAsync, replace the direct StreamAsync call:
// BEFORE:
var streamResult = await RemoteCommandStreamRunner.StreamAsync(
    client, agentName, resolved, linked.Token,
    outputReceived: (a, l, k) => OutputReceived?.Invoke(a, l, k));

// AFTER:
var streamResult = await _resilience.ExecuteAsync(async resilienceCt =>
{
    // Re-fetch client on each attempt in case channel was reset
    var currentClient = _clientManager.GetClient(entry.Address);
    return await RemoteCommandStreamRunner.StreamAsync(
        currentClient, agentName, resolved, resilienceCt,
        outputReceived: (a, l, k) => OutputReceived?.Invoke(a, l, k));
}, linked.Token);
```

**Package dependency:** Add `Polly.Core` to `TestController.WebApi.csproj`:
```xml
<PackageReference Include="Polly.Core" Version="8.5.2" />
```

**Risk:** Low — the WPF dispatcher has been running this exact pattern in production. Only behavioral difference: transient failures now retry instead of immediately failing.

**Validation:** Unit test — mock gRPC client to throw `Unavailable` on first call, succeed on second → verify action succeeds.

---

## Fix 3 — P1: Atomic Parameter File Writes

**Files:**
- `TestControllerGrpc.Core/Services/ParameterResolver.cs:103` (`SaveParameterFile`)
- `TestController.Api/Controllers/ExecutionController.cs:405` (`WriteAllLinesAsync`)

**Problem:** Both locations write parameter files directly via `File.WriteAllLines` / `File.WriteAllLinesAsync`. If the process crashes mid-write (power loss, UIAutomationCore stack overflow, forced kill), the file is left in a partially-written state. On next launch, the pipeline reads corrupt parameters and cascading failures occur.

**Fix:** Use the temp-file + atomic rename pattern (same pattern already used by `ExecutionSessionManager.PersistToDisk`):

### ParameterResolver.cs

```csharp
// BEFORE
public static void SaveParameterFile(string filePath, IEnumerable<(string Key, string Value)> entries)
{
    var lines = entries.Select(e => $"{e.Key},{e.Value}").ToList();
    File.WriteAllLines(filePath, lines);
}

// AFTER
public static void SaveParameterFile(string filePath, IEnumerable<(string Key, string Value)> entries)
{
    var lines = entries.Select(e => $"{e.Key},{e.Value}").ToList();
    var tempPath = filePath + ".tmp";
    File.WriteAllLines(tempPath, lines);
    File.Move(tempPath, filePath, overwrite: true);
}
```

### ExecutionController.cs

```csharp
// BEFORE
await System.IO.File.WriteAllLinesAsync(paramFile, lines);

// AFTER
var tempFile = paramFile + ".tmp";
await System.IO.File.WriteAllLinesAsync(tempFile, lines);
System.IO.File.Move(tempFile, paramFile, overwrite: true);
```

**Why this works:** `File.Move` with `overwrite: true` on NTFS is atomic at the filesystem level — either the old file or the new file is visible, never a partial state. If power is lost before `Move`, the `.tmp` file is abandoned (harmless) and the original is untouched.

**Cleanup consideration:** On startup, if a `.tmp` file exists next to a parameter file, it means a write was interrupted. Optionally delete stale `.tmp` files, or let them be overwritten on next save (current behavior).

**Risk:** None — this is a strict safety improvement. Worst case is a stale `.tmp` file on disk.

**Validation:** Existing parameter resolution tests pass unchanged. Add test: write to a temp path, verify `.tmp` doesn't exist after successful save.

---

## Fix 4 — P2: Synchronous Flush on Session Complete

**File:** `TestControllerGrpc.Core/Services/ExecutionSessionManager.cs`

**Problem:** `PersistToDisk()` uses `ThreadPool.QueueUserWorkItem` — it's fire-and-forget with a 5-second throttle in `RecordResult`. If the process crashes within those 5 seconds, up to 5 seconds of action results are lost. The `CompleteSession` method also calls `PersistToDisk()` which queues to the thread pool — so even session completion is technically async.

**Current behavior:**
1. `RecordResult()` → throttled persist (at most once per 5s)
2. `CompleteSession()` → persist (but still via ThreadPool)
3. Process crash between action result and persist → data loss

**Fix:** Add a synchronous persist path specifically for session-terminal events (`CompleteSession`, `CancelSession`, `CancelAll`). Keep the throttled async persist for `RecordResult` (which fires rapidly during execution).

```csharp
// Add a new method for synchronous persistence:
private void PersistToDiskSync()
{
    if (string.IsNullOrEmpty(_persistPath)) return;

    lock (_persistLock)
    {
        try
        {
            var dir = Path.GetDirectoryName(_persistPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            List<PersistedSession> snapshot;
            lock (_historyLock)
            {
                snapshot = _history.Select(ToPersistedSession).ToList();
            }

            foreach (var active in _active.Values)
                snapshot.Add(ToPersistedSession(active));

            var json = JsonSerializer.Serialize(snapshot,
                new JsonSerializerOptions { WriteIndented = true });

            var tempPath = _persistPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _persistPath, overwrite: true);
        }
        catch
        {
            // Persistence failure is non-fatal
        }
    }
}
```

**Then update terminal methods:**

```csharp
// In CompleteSession():
public void CompleteSession(string sessionId)
{
    if (_active.TryRemove(sessionId, out var session))
    {
        // ... existing logic ...
        PersistToDiskSync();  // ← synchronous, guarantees flush before return
    }
}

// In CancelSession():
public bool CancelSession(string sessionId)
{
    if (!_active.TryRemove(sessionId, out var session))
        return false;
    // ... existing logic ...
    PersistToDiskSync();  // ← synchronous
    return true;
}

// In CancelAll():
public List<string> CancelAll()
{
    // ... existing logic ...
    if (cancelledTags.Count > 0)
        PersistToDiskSync();  // ← synchronous
    return cancelledTags;
}
```

**Leave `RecordResult` as-is** — the throttled async persist is fine for mid-execution saves (high frequency, low criticality). The guarantee is: by the time `CompleteSession` returns, all data is on disk.

**Risk:** Minimal — adds a synchronous file write on session completion (sub-millisecond for typical session data). The lock prevents contention with the threaded path.

**Validation:** Test: complete a session, kill the process immediately, restore from disk → verify all action results are present.

---

## Implementation Order

| Step | Fix | Dependencies |
|------|-----|--------------|
| 1 | Fix 3 (Atomic file writes) | None — pure safety improvement |
| 2 | Fix 1 (HubFilter) | None — new file + registration |
| 3 | Fix 4 (Sync flush) | None — internal refactor |
| 4 | Fix 2 (Polly resilience) | Requires adding `Polly.Core` package reference |

Steps 1–3 are independent and can be done in parallel. Step 4 requires a NuGet package addition.

---

## Testing Strategy

1. **Build verification:** Full solution compile with 0 errors.
2. **Existing tests:** All ~915 tests should pass unchanged.
3. **New tests:**
   - HubFilter: integration test confirming connection survival on hub method throw
   - Polly: unit test with mock gRPC client verifying retry on Unavailable
   - Atomic write: unit test verifying no `.tmp` file remains after successful save
   - Sync flush: unit test verifying data survives immediate post-complete access
