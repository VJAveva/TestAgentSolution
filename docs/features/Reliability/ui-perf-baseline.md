# ui-perf-baseline.md

Gate 1 artifact for `docs/reliability/UIPerformance-CopilotPrompts.md`.

## Status: WEB MEASURED · WPF NOT MEASURED

Web numbers below are **real**, captured 2026-09-08 against the live WPF-embedded
controller (PID 27236) with 9 healthy agents registered and **no execution running**.

WPF numbers are still empty. `UiPerfDiagnostics.Install` reads its flag once at
startup, so the already-running controller cannot be instrumented in place — it needs
a relaunch with `UIPERF=1`. Do not fill those cells with estimates.

## Measured — WebClient, 2026-09-08, idle controller, 9 agents, 0 runs

Capture rig: `npm run dev` with the proxy overridden to `:5200`, `localStorage.uiPerf='1'`.

| Route | Window | SignalR msgs | Renders |
|---|---|---|---|
| `#/agents` | 15 s | 175 | `FleetPage` 6 |
| `#/monitor/pipeline` | 15 s | 220 | `ExecutionDashboard` 2 |
| `#/logs` | 15 s | 175 | `LogViewer` 2 |
| `#/monitor/pipeline`, 1 demo session | 20 s | 52 | `ExecutionDashboard` 6, `SessionCard` 6, `AgentRow` 6 |

- Only event flowing at idle is `AgentStatusChanged` — 9 agents = 9 messages per burst,
  **1.8–3.6 msg/s**, 0.7–1.3 KB per 5 s.
- Roughly **10:1 damping** from SignalR messages to component renders after the Phase 3 fixes.
- `SessionCard` / `AgentRow` track the provider **1:1** — they are unmemoized. This was a
  speculative finding in Phase 1; it is now measured.
- Route load: `watchlist` 37.4 ms cold, `monitor` 0.3 ms warm.

REST over 30 s idle: `dashboard-sessions` ×4 (the 8 s poll), `system/mode` ×2,
`execution/reconnect` ×2, `watchlist` ×2, `agents` ×2, `execution/sessions` ×2, `locks` ×1.

**Instrumentation caveat:** `countRest` reads `content-length`, which the Vite dev proxy
does not set, so the KB column reads 0 in dev. Byte totals are only meaningful against a
non-proxied host.

## Defect found by running it

`/api/execution/proxy/dashboard-sessions` returned **404 every 8 s, indefinitely** — that
route is mapped only in `TestController.WebApi/Endpoints/ExecutionEndpoints.cs`, while the
WPF-embedded host serves the plain `/api/execution/dashboard-sessions`
(`TestController.Api/Controllers/ExecutionController.cs` L112). Already recorded as
"Critical" in `docs/Issues/WEBCLIENT_BLANK_PAGE_SECURED_MODE.md` L34 and still unfixed.

Measured: **1 × 404 per 8 s (>225 per 30 min) → 3 on first load, then 0** after latching
onto the working path.

---

## How to capture (WPF — P02)

Instrumentation lives in `TestControllerGrpc/Diagnostics/UiPerfDiagnostics.cs`,
installed from `App.xaml.cs` right after the host is built. Default OFF.

Enable either way:

```powershell
# no rebuild
$env:UIPERF = '1'; .\TestControllerGrpc.exe
```

```jsonc
// TestControllerGrpc/appsettings.json
"UiPerf": { "Enabled": true }
```

Output (rolls daily, in `AppLogger.DefaultLogDirectory`, i.e. `C:\TestControllerService\Logs`):

| File | Contents |
|---|---|
| `ui-perf-YYYYMMDD.log` | every 5 s: frame count / avg / worst, UI-thread wait avg / worst, per-scope timings, binding-error count |
| `ui-binding-errors-YYYYMMDD.log` | the raw binding-error text, one per line |

Line format:

```
14:22:31.104 frames=298 avg=16.71ms worst=61.20ms | uiWait avg=3.4ms worst=812.0ms n=5 | FleetVM.Refresh n=20 avg=7.1ms worst=44.0ms | bindingErrors=140 distinct=2
```

`uiWait worst` is the number that matters: it is measured by posting from a
**background** thread at `DispatcherPriority.Background` and timing how long the post
waits. That wait **is** the lag the user feels.

Scopes wired (P02 item 4, the five heaviest surfaces from P01):

| Scope | File |
|---|---|
| `ExecutionDashboardVM.RefreshTick` | `TestControllerGrpc/ViewModels/Execution/ExecutionDashboardVM.cs` |
| `FleetVM.Refresh` | `TestControllerGrpc/ViewModels/AgentWorkspace/FleetVM.cs` |
| `LogBufferService.Flush` | `TestControllerGrpc/ViewModels/LogBufferService.cs` |
| `ExecutionHistoryPanelVM.RefreshSessions` | `TestControllerGrpc/ViewModels/ExecutionHistoryPanelVM.cs` |
| `RegressionViewModel.ApplyFilter` | `TestControllerGrpc/ViewModels/Regression/RegressionViewModel.cs` |

To count binding errors after a run:

```powershell
Get-Content C:\TestControllerService\Logs\ui-binding-errors-*.log |
  Group-Object | Sort-Object Count -Descending | Select-Object Count, Name -First 20
```

## How to capture (WebClient — P03)

Instrumentation lives in `TestController.WebClient/src/lib/uiPerf.ts` +
`src/hooks/useRenderCount.ts`. Default OFF.

```js
// browser console — no rebuild
localStorage.setItem('uiPerf', '1'); location.reload();
// off
localStorage.removeItem('uiPerf'); location.reload();
```

Build-time alternative: `VITE_UI_PERF=1`.

What it prints:

| Every | Table |
|---|---|
| 5 s | SignalR messages/s + count + KB, **per event name** |
| 5 s | render counts for the top 10 instrumented components |
| 30 s | REST calls: URL (ids collapsed to `{id}` / `{n}`), count, KB |
| on route change | `route-load:<tab>` duration via `performance.measure` |

SignalR counting works by wrapping `HubConnection.on` once in
`instrumentHubConnection()` (called from `useSignalR.ts` before any `.on()`), so
**every** subscription in the app is counted — including `useExecutionDashboard`,
`LogViewer`, `useFleetState` and the lock/permission/system-mode modules.

Components carrying `useRenderCount`: `WatchListTree`, `FleetPage`,
`ExecutionDashboard`, `SessionCard`, `AgentRow`, `TimelineView`, `LogViewer`,
`LiveLogger`, `UnifiedLogView`, `RegressionView`.

### Profiler traces

React DevTools Profiler: install the extension → **Profiler** tab → gear →
tick *Record why each component rendered* → ⏺ → drive the screen for ~15 s → ⏹ →
read the flamegraph and the *Ranked* view. Commits wider than ~16 ms are dropped frames.

Chrome Performance trace: DevTools → **Performance** → ⏺ → same 15 s → ⏹.
Look at *Main* for long tasks (>50 ms) and at *Bottom-Up* filtered to the app bundle.

---

## Baseline table — WPF rows still to fill

Needs a relaunch with `UIPERF=1` **and** a real regression run.

| Screen | Worst frame (ms) | UI-thread wait worst (ms) | Renders / 5 s | Route load (ms) |
|---|---|---|---|---|
| WPF MainWindow (tree + log pane) | | | n/a | n/a |
| WPF Execution Dashboard | | | n/a | n/a |
| WPF Fleet panel | | | n/a | n/a |
| WPF Execution History panel | | | n/a | n/a |
| WPF Code Churn grid | | | n/a | n/a |
| Web Monitor → Pipeline | n/a | n/a | 0.7 (idle) / 1.5 (1 demo session) | 0.3 warm |
| Web Logs (`LogViewer`) | n/a | n/a | 0.7 (idle) | — |
| Web Agents → Fleet | n/a | n/a | 2.0 (idle) | — |
| Web WatchList | n/a | n/a | — | 37.4 cold |
| Web CodeChurn | n/a | n/a | not reachable — Default mode | — |

Binding errors, distinct / total: **not measured** (needs `UIPERF=1`).
SignalR msg/s **under a real regression run**: not measured. Idle figure is 1.8–3.6/s.

---

## Counted (static) — real, read from code

These are counts and constants, not measurements. They are what the Phase 1 ranking
was built on in the absence of runtime numbers.

| Fact | Value | Source |
|---|---|---|
| `AgentOutputEvent` publish rate | one per stdout/stderr **line** | `TestControllerGrpc/ViewModels/MainViewModel.Helpers.cs` L476 |
| `AgentOutputEvent` subscribers, WPF | 2 (`ExecutionDashboardVM`, `MonitorVM`) | grep `Subscribe<AgentOutputEvent>` |
| `AgentOutput`/`AgentOutputBatch`/`LogEntry` subscribers, Web | 3 independent buffers | `useSignalR.ts`, `useExecutionDashboard.tsx`, `LogViewer.tsx` |
| Web log caps | 2 000 / 10 000 / 50 000 | `executionStore.ts` L26, `useExecutionDashboard.tsx` L28, `LogViewer.tsx` L15 |
| WPF log caps | 10 000 main, 5 000 dashboard, 500 live feed, 200 monitor/agent-monitor | `LogBufferService.cs` L40, `ExecutionDashboardVM.cs` L45, `ExecutionHistoryPanelVM.cs` L24, `MonitorVM.cs` L453, `AgentMonitorViewModel.cs` L39 |
| WPF UI-thread timers always running | 100 ms, 1 s ×2, 3 s, 5 s ×3, 30 s, 60 s | see P01 inventory |
| Web polling timers | 500 ms, 1 s ×2, 2 s ×3, 3/8 s, 4 s, 30 s | see P01 inventory |
| Deployed agent count | 9 | `deploy/fleet-inventory.json` |
| Fleet scale target in code | 200 agents ≈ 40 events/s | `FleetVM.cs` L102 comment |
| Code Churn grid rows | 35 | `docs/impact/component-usecase-map.v1.json` (`vobs`) |
| Production `WatchList.xml` node count | **unknown** — file is a `preserveFiles` entry on the deploy target, not in the repo | `deploy/fleet-inventory.json` |
