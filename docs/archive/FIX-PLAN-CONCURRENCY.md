# Concurrency & Threading Fix Plan

> **Created:** 2025-05-23 | **Scope:** P0–P2 concurrency gaps from GAP-ANALYSIS.md

---

## Fix 1 — P0: `ExecutionLogViewerDialog` Deadlock

**File:** `TestControllerGrpc/Views/Dialogs/ExecutionLogViewerDialog.xaml.cs` (line 37)

**Problem:** `http.GetFromJsonAsync<T>(url).GetAwaiter().GetResult()` on the WPF UI thread. The `SynchronizationContext` posts the continuation back to the UI thread, which is already blocked — classic deadlock.

**Fix:** Convert `Create(...)` from a synchronous factory to an async factory. Callers must `await` the result.

```csharp
// BEFORE (deadlock)
public static ExecutionLogViewerDialog? Create(
    string buildName, string testCaseName, int? failedStepIndex, Window? owner)
{
    // ...
    var report = http.GetFromJsonAsync<ExecutionLogViewerVM.LogPayload>(url)
        .GetAwaiter().GetResult();  // ← BLOCKS UI THREAD
    // ...
}

// AFTER
public static async Task<ExecutionLogViewerDialog?> CreateAsync(
    string buildName, string testCaseName, int? failedStepIndex, Window? owner)
{
    try
    {
        var apiBase = ResolveApiBase();
        using var http = new HttpClient { BaseAddress = new Uri(apiBase), Timeout = TimeSpan.FromSeconds(15) };
        var url = $"api/results/builds/{Uri.EscapeDataString(buildName)}" +
                  $"/test/{Uri.EscapeDataString(testCaseName)}/log" +
                  (failedStepIndex.HasValue ? $"?stepIndex={failedStepIndex}" : "");

        var report = await http.GetFromJsonAsync<ExecutionLogViewerVM.LogPayload>(url);

        if (report == null)
        {
            MessageBox.Show("No log data returned from API.", "Execution Log",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        var vm = new ExecutionLogViewerVM(report);
        return new ExecutionLogViewerDialog(vm) { Owner = owner };
    }
    catch (Exception ex)
    {
        MessageBox.Show($"Failed to load execution log:\n{ex.Message}",
            "Execution Log", MessageBoxButton.OK, MessageBoxImage.Warning);
        return null;
    }
}
```

**Caller changes:** All callers of `Create(...)` must change from:
```csharp
var dlg = ExecutionLogViewerDialog.Create(build, test, step, owner);
dlg?.ShowDialog();
```
to:
```csharp
var dlg = await ExecutionLogViewerDialog.CreateAsync(build, test, step, owner);
dlg?.ShowDialog();
```

**Risk:** Low — the method already runs in a UI event handler context that can be made `async void` (WPF event handler pattern).

**Validation:** Manual — open the Execution Log viewer and confirm it loads without freezing.

---

## Fix 2 — P1: `ForceRelease` / `ForceReleaseAll` Bypass `_atomicLock`

**File:** `TestControllerGrpc.Core/Services/AgentLockManager.cs` (lines 154–176)

**Problem:** `TryLockAgents` and `ReleaseSession` both hold `_atomicLock` for atomicity, but `ForceRelease` and `ForceReleaseAll` mutate the same `_locks` dictionary AND call `PersistToDisk()` without acquiring the lock. This creates a race window where:
1. `TryLockAgents` is inside `lock(_atomicLock)`, iterating `_locks`
2. Concurrently, `ForceReleaseAll` calls `_locks.Clear()`
3. Iteration state becomes undefined; version/persist may be stale

**Fix:** Wrap both methods in `lock (_atomicLock)`:

```csharp
// BEFORE
public bool ForceRelease(string agentName)
{
    if (_locks.TryRemove(agentName, out _))
    {
        Interlocked.Increment(ref _version);
        PersistToDisk();
        return true;
    }
    return false;
}

public int ForceReleaseAll()
{
    int count = _locks.Count;
    _locks.Clear();
    if (count > 0)
    {
        Interlocked.Increment(ref _version);
        PersistToDisk();
    }
    return count;
}

// AFTER
public bool ForceRelease(string agentName)
{
    lock (_atomicLock)
    {
        if (_locks.TryRemove(agentName, out _))
        {
            Interlocked.Increment(ref _version);
            PersistToDisk();
            return true;
        }
        return false;
    }
}

public int ForceReleaseAll()
{
    lock (_atomicLock)
    {
        int count = _locks.Count;
        _locks.Clear();
        if (count > 0)
        {
            Interlocked.Increment(ref _version);
            PersistToDisk();
        }
        return count;
    }
}
```

**Risk:** Minimal — the lock is not held for long (memory ops + a small file write). No deadlock risk since `_atomicLock` is always acquired alone.

**Validation:** Existing `AgentLockManager` unit tests + add a concurrent stress test calling `TryLockAgents` + `ForceReleaseAll` in parallel.

---

## Fix 3 — P1: CommandExecutor TOCTOU Race

**File:** `TestAgentGrpc/Services/CommandExecutor.cs` (lines 80–95)

**Problem:** The state guard `if (_state == AgentState.Running)` runs **before** the semaphore is entered. Two near-simultaneous `RunCommand` calls both see `_state == Ready`, both pass the check, then one enters the semaphore and the other either queues silently or proceeds unsafely.

**Fix:** Move the state check inside the semaphore acquisition. Use `_executionLock.Wait(0)` (try-enter) and check state atomically within the critical section:

```csharp
// BEFORE
public (bool Accepted, string ExecutionId) RunCommand(
    string command, string arguments, bool isReboot,
    string? executionId = null, string? userName = null, string? password = null)
{
    if (_state == AgentState.Running)  // ← TOCTOU: state can change after this check
    {
        _logger.LogWarning("Agent is busy — rejecting: {Cmd}", command);
        return (false, string.Empty);
    }
    // ...semaphore entered later...

// AFTER
public (bool Accepted, string ExecutionId) RunCommand(
    string command, string arguments, bool isReboot,
    string? executionId = null, string? userName = null, string? password = null)
{
    if (!_executionLock.Wait(0))  // Non-blocking try-enter
    {
        _logger.LogWarning("Agent is busy (semaphore contention) — rejecting: {Cmd}", command);
        return (false, string.Empty);
    }

    try
    {
        // Double-check inside the lock — handles the edge case where
        // state hasn't flipped back to Ready yet after prior execution.
        if (_state == AgentState.Running)
        {
            _logger.LogWarning("Agent is busy — rejecting: {Cmd}", command);
            return (false, string.Empty);
        }

        if (!EvaluateCommandPolicy(command, arguments, executionId, out _))
            return (false, string.Empty);

        var execId = executionId ?? Guid.NewGuid().ToString("N")[..12];

        _state = AgentState.Running;
        _currentExecutionId = execId;
        _currentCommand = SecurityRedactor.RedactCommandLine(command, arguments);

        // NOTE: The semaphore is released inside the Task.Run finally block,
        // NOT here — we keep it held for the duration of execution.
    }
    catch
    {
        _executionLock.Release();
        throw;
    }

    // Fire-and-forget execution (semaphore released in finally)
    var cts = new CancellationTokenSource(TimeSpan.FromMinutes(_settings.MaxExecutionTimeoutMinutes));
    _ = Task.Run(async () =>
    {
        try
        {
            await ExecuteAsync(execId, command, arguments, isReboot,
                perCallChannel: null, cts.Token, userName: userName, password: password);
        }
        finally
        {
            _executionLock.Release();
            cts.Dispose();
        }
    });

    return (true, execId);
}
```

**Risk:** Medium — must verify all exit paths release the semaphore. The `RunCommandStreamed` path also needs the same pattern applied.

**Validation:** Unit test: call `RunCommand` twice with zero delay; verify second call is rejected.

---

## Fix 4 — P1: `ResetChannelAsync` TOCTOU

**File:** `TestControllerGrpc/Services/AgentGrpcDispatcher.cs` (lines 1055–1093)

**Problem:** The sequence is:
1. Check `IsAgentExecuting(agentName)` → false
2. Get endpoint from `_agents[agentName]`
3. Dispose endpoint
4. **Meanwhile:** Another thread starts execution on the same endpoint → `ObjectDisposedException`

Between steps 1 and 3, execution can start because there's no lock coupling the check to the disposal.

**Fix:** Use atomic `TryRemove` to claim the endpoint, then dispose it only if successfully removed. Add the new endpoint before ping so executions that start during the ping use the new channel:

```csharp
// AFTER
public async Task<bool> ResetChannelAsync(string agentName)
{
    // Never reset during active execution — would kill the command stream
    if (IsAgentExecuting(agentName))
    {
        _logger.LogWarning("Channel reset blocked for {Agent} — execution in progress", agentName);
        return false;
    }

    // Atomically claim the old endpoint. If another thread already removed it
    // (concurrent reset) or if it doesn't exist, bail out.
    if (!_agents.TryRemove(agentName, out var oldEndpoint))
    {
        _logger.LogWarning("Channel reset failed — agent {Agent} not registered or already being reset", agentName);
        return false;
    }

    var address = oldEndpoint.Address;

    // Create fresh endpoint BEFORE disposing old — minimizes window where
    // no endpoint exists for this agent.
    var newEndpoint = new AgentEndpoint(agentName, address, _timeouts);
    _agents[agentName] = newEndpoint;

    // Now safe to dispose old endpoint — it's no longer in the dictionary
    // so no new operations will use it.
    oldEndpoint.Dispose();

    // Reset health state
    if (_healthStates.TryGetValue(agentName, out var health))
    {
        health.ConsecutiveFailures = 0;
        health.IsHealthy = true;
        health.CircuitOpenedUtc = null;
        health.LastSuccessUtc = null;
    }

    _logger.LogInformation("Channel reset for agent {Agent} at {Address}", agentName, address);
    _appLogger.Log(LogLevel.Information, "Channel", $"Channel reset for {agentName}");

    // Verify the new channel works
    return await PingAsync(agentName);
}
```

**Key insight:** By creating the new endpoint **before** disposing the old one, any execution that races between the `TryRemove` and dispose will either:
- Already have a reference to the old endpoint (fine — it's still valid until `Dispose()`)
- Look up the agent and get the new endpoint (fine — new channel)

**Risk:** Low — `ConcurrentDictionary.TryRemove` is atomic and lock-free.

**Validation:** Existing `ResetChannelAsync` tests + add test that starts execution concurrently with reset.

---

## Fix 5 — P2: `Dispatcher.Invoke` → `InvokeAsync` (4 Call Sites)

**Files:**
- `TestControllerGrpc/ViewModels/MainViewModel.Execution.cs` lines 60, 137, 331
- `TestControllerGrpc/ViewModels/AgentWorkspace/MonitorVM.cs` lines 443, 465

**Problem:** `Dispatcher.Invoke(...)` from a background thread blocks that thread until the UI thread processes the delegate. If the UI thread is busy (e.g., rendering a complex tree), the background thread stalls. In extreme cases (UI thread showing a modal dialog), this can deadlock.

**Fix:** Change to `Dispatcher.InvokeAsync(...)` (fire-and-forget) or `await Dispatcher.InvokeAsync(...)` (if return value needed):

```csharp
// MainViewModel.Execution.cs:60 — lock conflict removal of session
// BEFORE
Application.Current?.Dispatcher.Invoke(() => ActiveSessions.Remove(session));
// AFTER
Application.Current?.Dispatcher.InvokeAsync(() => ActiveSessions.Remove(session));

// MainViewModel.Execution.cs:137 — same pattern
// BEFORE
Application.Current?.Dispatcher.Invoke(() => ActiveSessions.Remove(session));
// AFTER
Application.Current?.Dispatcher.InvokeAsync(() => ActiveSessions.Remove(session));

// MainViewModel.Execution.cs:331 — session completion in TriggerAllWatchItems
// BEFORE
Application.Current?.Dispatcher.Invoke(() => CompleteSession(session));
// AFTER
await Application.Current!.Dispatcher.InvokeAsync(() => CompleteSession(session));

// MonitorVM.cs:443, 465 — live log and node progress updates
// BEFORE
_uiDispatcher.Invoke(() => { ... });
// AFTER
_uiDispatcher.InvokeAsync(() => { ... });
```

**Note on MonitorVM:** The existing code fires rapidly (every output line). `InvokeAsync` without `await` is correct here — it queues to the UI dispatcher without blocking the event handler thread. The UI thread processes them in order at its own pace.

**Risk:** Low — the operations are side-effect-only (UI updates). Order is preserved by dispatcher queue.

**Validation:** Manual — trigger execution, confirm live log still updates smoothly.

---

## Fix 6 — P2: WebApi `ReadToEndAsync().GetAwaiter().GetResult()`

**File:** `TestController.WebApi/Program.cs` (line 239)

**Problem:** `.GetAwaiter().GetResult()` on an async method inside a minimal API endpoint handler. This blocks the request thread. Under load, this starves the thread pool.

**Fix:** Make the endpoint handler `async`:

```csharp
// BEFORE
app.MapPost("/api/clientlogs", (HttpContext ctx, IAppLogger logger) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = reader.ReadToEndAsync().GetAwaiter().GetResult();
    logger.Warn("ClientLog", body);
    return Results.Ok();
});

// AFTER
app.MapPost("/api/clientlogs", async (HttpContext ctx, IAppLogger logger) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    logger.Warn("ClientLog", body);
    return Results.Ok();
});
```

**Risk:** None — ASP.NET Core minimal APIs fully support async handlers.

**Validation:** Existing integration tests for `/api/clientlogs` endpoint.

---

## Implementation Order

| Step | Fix | Est. Effort | Dependencies |
|------|-----|-------------|--------------|
| 1 | Fix 6 (WebApi async) | 5 min | None |
| 2 | Fix 2 (AgentLockManager) | 10 min | None |
| 3 | Fix 1 (ExecutionLogViewerDialog) | 20 min | Update callers |
| 4 | Fix 5 (Dispatcher.InvokeAsync) | 15 min | None |
| 5 | Fix 4 (ResetChannelAsync) | 20 min | None |
| 6 | Fix 3 (CommandExecutor) | 30 min | Verify semaphore release paths |

Steps 1, 2, 4, 5, 6 are independent and can be done in parallel. Step 3 requires finding and updating all callers of `ExecutionLogViewerDialog.Create`.

---

## Testing Strategy

1. **Build verification:** Ensure solution compiles after all changes.
2. **Existing tests:** Run `TestControllerGrpc.Tests` and `TestController.WebApi.Tests` — they should pass unchanged.
3. **New tests to add:**
   - `AgentLockManager` concurrent force-release stress test
   - `CommandExecutor` double-submit rejection test
   - `ResetChannelAsync` concurrent reset + execution test
4. **Manual regression:** Open WPF app, trigger execution, open log viewer, force-release agents, monitor live log.
