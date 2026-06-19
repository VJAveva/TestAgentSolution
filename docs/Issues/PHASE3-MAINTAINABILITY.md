# Phase 3 — Maintainability (Implementation Plan)

> **Created:** 2026-06-19
> **Scope:** Reduce concentrated complexity — the largest, most duplicated code identified in the 2026-06-19 walkthrough.
> **Stack touched:** `TestControllerGrpc` (WPF) + `TestControllerGrpc.Core`. No WebClient changes.
> **Goal:** Make the dispatcher's retry logic single-sourced and split the biggest files along their existing seams — **with zero behavior change**. These are refactors, not features.

---

## Summary

| # | Item | Area | Impact | Effort | Risk |
|---|------|------|--------|--------|------|
| **C1** | De-duplicate `ExecuteRemoteCommandAsync` retry/stream orchestration (4× → 1 helper) | Dispatcher correctness/maint. | High | M | Med |
| **C3** | Split the largest VM / code-behind files into partials along existing seams | Navigability/maint. | Med | M (mechanical) | Low |

**Recommended order:** C3 first (pure mechanical moves, low risk, makes the code easier to read while doing C1), then C1.

**Hard rule for this phase:** no functional change. Every diff should be provably behavior-preserving — verified by the existing test suite plus the characterization tests added in C1.

---

## C1 — De-duplicate the dispatcher's retry/stream orchestration

### Problem
`ExecuteRemoteCommandAsync` is ~550 lines and the **"build timeout CTS → link → `RemoteCommandStreamRunner.StreamAsync` under `endpoint.Resilience`"** block is copy-pasted **four** times, plus a fifth variant in the reboot path. The raw streaming is already shared (`RemoteCommandStreamRunner`), but the *orchestration wrapper* is not — so a fix to one path (e.g. re-reading the endpoint after a channel reset, or a timeout tweak) can silently miss the other three.

### Evidence (`TestControllerGrpc/Services/AgentGrpcDispatcher.cs`)
- `:387` — initial attempt: `endpoint.Resilience.ExecuteAsync(...)` → builds `timeoutCts`/`linked` → `StreamAsync(... onProgressTick, correlationId)`.
- `:534` — **busy-retry**: re-calls `StreamAsync(...)` after force-ready recovery.
- `:653` — **UNAVAILABLE recovery-retry**: another `endpoint.Resilience.ExecuteAsync` + `timeoutCts`/`linked` + `StreamAsync`.
- `:762` — **post-reboot retry**: same block again on `retryEndpoint`.
- `:927` — `ExecuteRebootBypassingCircuitBreakerAsync` builds the same `timeoutCts`/`linked` + `StreamAsync` a fifth time.

The duplicated pieces are identical except for two knobs: whether `onProgressTick` is passed (initial only) and which endpoint reference is used (re-read after reset).

### Fix — extract two private helpers, keep policy in the orchestrator

**1. The inner unit — one streamed attempt with timeout linking + endpoint re-read:**
```csharp
private async Task<RemoteCommandStreamResult> StreamOnceAsync(
    string agentName, ActionConfig resolved, string correlationId,
    CancellationToken outerCt, Action<string, string, TimeSpan>? onProgressTick = null)
{
    // Re-read endpoint each attempt: a channel reset may have replaced it between retries.
    if (!_agents.TryGetValue(agentName, out var endpoint))
        return new RemoteCommandStreamResult(false, -1, $"Agent '{agentName}' not registered", []);
    var client = endpoint.GetClient();

    CancellationTokenSource? timeoutCts = resolved.Timeout > 0
        ? new CancellationTokenSource(TimeSpan.FromSeconds(resolved.Timeout)) : null;
    using var _t = timeoutCts;
    using var linked = timeoutCts is not null
        ? CancellationTokenSource.CreateLinkedTokenSource(outerCt, timeoutCts.Token)
        : CancellationTokenSource.CreateLinkedTokenSource(outerCt);

    return await RemoteCommandStreamRunner.StreamAsync(
        client, agentName, resolved, linked.Token,
        outputReceived: (a, l, k) => OutputReceived?.Invoke(a, l, k),
        onProgressTick: onProgressTick,
        correlationId: correlationId);
}
```

**2. The resilient wrapper — run an attempt under the agent's Polly pipeline:**
```csharp
private Task<RemoteCommandStreamResult> RunResilientStreamAsync(
    AgentEndpoint endpoint, string agentName, ActionConfig resolved,
    string correlationId, CancellationToken ct, Action<string, string, TimeSpan>? onProgressTick = null)
    => endpoint.Resilience.ExecuteAsync(
        async rct => await StreamOnceAsync(agentName, resolved, correlationId, rct, onProgressTick), ct)
        .AsTask();
```

**3. Collapse the call sites.** The orchestrator keeps all *policy* — busy detection + force-ready recovery, reboot wait, exit-code classification, health tracking, `_activeExecutions` guard — but each "do the stream" block becomes a one-liner:
```csharp
// initial (with progress ticks)
var streamResult = await RunResilientStreamAsync(endpoint, agentName, resolved, correlationId, ct, ProgressTick);
// busy-retry / recovery-retry / post-reboot retry (no progress ticks)
streamResult = await RunResilientStreamAsync(endpoint, agentName, resolved, correlationId, ct);
```
`ExecuteRebootBypassingCircuitBreakerAsync` uses `StreamOnceAsync` directly (it intentionally bypasses `Resilience`).

Net: ~5 duplicated blocks → 2 helpers + call sites; the method shrinks substantially and there's **one** place that knows how to build a timeout-linked streamed attempt.

### Do this test-first (characterization before refactor)
Refactoring a 550-line method safely requires a behavior net first. Existing coverage is adjacent but not exhaustive: `Services/SmartRetryTests.cs`, `ChannelResetTests.cs`, `MonitorStreamInterferenceTests.cs`, `ScalabilityFixTests.cs`.

Add **characterization tests** in `TestControllerGrpc.Tests/Services/` against a fake `TestAgentServiceClient` before touching the method:
- happy path (exit 0) → `ActionResult(true, 0, "")`;
- agent-busy reply → force-ready → retry → success;
- `RpcException(Unavailable)` → `WaitForAgentRecoveryAsync` recovers → retry succeeds (**this path has no test today** — was P1 in the old gap analysis);
- `Unavailable` with no recovery → `AutoRebootOnUnavailable` reboot path;
- `BrokenCircuitException` / `TimeoutRejectedException` → correct `ActionResult`.

Run them green, refactor, run them green again — the diff is correct iff they still pass unchanged.

### Files
`TestControllerGrpc/Services/AgentGrpcDispatcher.cs`; new tests in `TestControllerGrpc.Tests/Services/`.

### Caveats
- Preserve the **per-call differences**: only the initial attempt passes `onProgressTick`; retries don't. Don't accidentally add progress ticks to retries (changes log output) or drop them from the initial.
- Keep the endpoint **re-read** semantics (`_agents.TryGetValue` each attempt) — it's load-bearing for channel-reset-during-retry.
- Reboot path stays **outside** `Resilience` (circuit-breaker bypass is intentional — see `ExecuteRebootBypassingCircuitBreakerAsync`).
- `StreamResult` empty-stderr sentinel `[]` requires the `IReadOnlyList<string>` collection-expression target; match the existing `RemoteCommandStreamResult` ctor.

### Acceptance criteria
- All new characterization tests pass **before and after** the refactor, unchanged.
- Full `TestControllerGrpc.Tests` suite green.
- `ExecuteRemoteCommandAsync` no longer contains a repeated timeout-CTS/StreamAsync block; the logic lives in `StreamOnceAsync`/`RunResilientStreamAsync`.
- No change to log lines, status messages, or `ActionResult` values for the scenarios above.

---

## C3 — Split the largest files along existing seams

### Problem
A few files concentrate disproportionate size, hurting navigation, review, and merge-conflict rates:

| File | Lines | Nature |
|------|------:|--------|
| `ViewModels/BuildResultsViewModel.cs` | 1,365 | already `partial class`, many distinct concerns |
| `Views/MainWindow.xaml.cs` | 1,154 | code-behind; one ~500-line tree-interaction block |
| `ViewModels/Execution/ExecutionDashboardVM.cs` | 941 | candidate (see follow-ons) |
| `Controllers/ExecutionController.cs` | 939 | candidate (split by route group) |
| `ViewModels/MainViewModel.Execution.cs` | 1,027 | already a partial; can split further |

This is a **mechanical, behavior-preserving** refactor: move method/property groups into additional `partial class` files. No logic changes.

### Fix — `BuildResultsViewModel` → 5 partials (it's already `partial`)
Seams confirmed by structure (`[ObservableProperty]`/`[RelayCommand]` clusters):

| New file | Members (by current line) |
|----------|---------------------------|
| `BuildResultsViewModel.cs` (core) | fields, ctor, list+load+stats: `RefreshBuilds` (264), `LoadBuildResults` (277), `LoadAllBuilds` (327), `Stat*` props |
| `BuildResultsViewModel.Detail.cs` | detail-pane props (`SelectedResultNode`, `Detail*`, `DetailExecutionSteps`, 83–98) + their handlers |
| `BuildResultsViewModel.Analysis.cs` | `FailureAlerts`/`FlakyTests`, `OnAnalyzeFailurePattern*` (153/156), `DetectConsecutiveFailures` (568), `LoadFlakyTests` (602) |
| `BuildResultsViewModel.Trends.cs` | scope/range: `CurrentScope`, `SelectedTimeRange`, `SetScope*`/`SetRange*` (700+), `GenerateTrendReport` (531), `LoadUseCaseTrends` (638) |
| `BuildResultsViewModel.Reporting.cs` | `ExportToCsv`/`ExportToHtml` (373/390), `Send*` (437/447/457), `Copy*` (505/516), capability gating: `Can*Report`, `RefreshReportCapabilities` (136), `OnCapabilitiesChanged` (142) |

### Fix — `MainWindow.xaml.cs` → 4 partials (code-behind is already `partial`)
Seams confirmed by handler grouping:

| New file | Members (by current line) |
|----------|---------------------------|
| `MainWindow.xaml.cs` (core) | ctor, `OnWindowLoaded` (316), `OnWindowClosed` (305), VM property-change wiring (371) |
| `MainWindow.Tray.cs` | `SetupTrayIcon` (121), `OnWindowClosing` (207), `MinimizeToTray` (215), `RestoreFromTray` (237) |
| `MainWindow.Layout.cs` | `Apply*PaneLayout` (402/434/451), `LoadDesignTokens` (478), `MainContentGrid_SizeChanged` (499), inline editor/folding (341/522/535) |
| `MainWindow.TreeInteraction.cs` | the big block: selection (325/331), context menus `OnWatchListContextMenuOpening` (554) + `OnTemplateContextMenuOpening` (737), drag/drop (893–1057) |
| `MainWindow.Log.cs` | `SubscribeToLogAutoScroll` (1057), `OnLogBatchFlushed` (1065), `OnScrollToLogEntry` (1075), `OnCopySelectedLog` (1088), `OnFocusLogSearchRequested` (1117) |

### Follow-on candidates (list now, do as capacity allows)
- `ExecutionController.cs` (939) — split by route group into partials (status/sessions, trigger, cancel/retry) or thin endpoint extensions.
- `ExecutionDashboardVM.cs` (941) and `MainViewModel.Execution.cs` (1,027) — split by concern once their seams are mapped.

### Deeper option (NOT this phase)
Much of `MainWindow.TreeInteraction.cs` (drag/drop, context-menu construction) is logic living in code-behind rather than the ViewModel or attached behaviors — a real MVVM smell. Moving it to behaviors/VM is higher-effort and higher-risk; **defer**. Phase 3 only relocates it into a partial so it's findable.

### Caveats
- Pure file moves: keep the **same `namespace` and class name**; add `partial` only where a class isn't already partial (both targets here already are).
- Don't reorder or rename members; move them verbatim so `git` shows clean moves and diffs stay reviewable.
- Watch `#region` boundaries and shared `private` fields — fields stay in the core file; partials reference them directly.
- XAML-referenced event handlers must remain `public`/`internal` with identical signatures (they're resolved by name from `MainWindow.xaml`).

### Acceptance criteria
- No file in the list exceeds ~600 lines after the split (or has a documented reason).
- `dotnet build TestAgentSolution.sln` succeeds; WPF app launches and the relevant surfaces (build results, tray, drag/drop, context menus, log pane) work unchanged.
- `git` shows the changes as moves, not rewrites; no member bodies modified.

---

## Verification
```powershell
dotnet build TestAgentSolution.sln
dotnet test TestControllerGrpc.Tests/TestControllerGrpc.Tests.csproj
```
For C3, also a manual smoke of the WPF controller: open Build Results, minimize-to-tray/restore, drag-drop a tree node, open both context menus, and exercise the log pane.

## Definition of done
- [ ] C1: characterization tests added (incl. the missing `WaitForAgentRecoveryAsync` path) and green before+after; duplicated stream blocks collapsed to `StreamOnceAsync`/`RunResilientStreamAsync`; no behavioral change.
- [ ] C3: `BuildResultsViewModel` and `MainWindow.xaml.cs` split into the partials above; build + tests green; clean move-only diffs.
- [ ] No functional change anywhere in this phase; gRPC contracts, resilience policy, and RBAC untouched.
```
