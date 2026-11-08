# Phase 1 — Performance & UX Quick Wins (Implementation Plan)

> **Created:** 2026-06-19
> **Scope:** Highest-ROI, low-risk fixes from the 2026-06-19 code walkthrough.
> **Stack touched:** `TestController.WebClient` (React/Zustand) + `TestControllerGrpc` (WPF).
> **Goal:** Cut render churn on the live dashboard, stop whole-tree re-renders, remove UI-thread freeze risk, and replace blank panes with actionable error states — without touching the resilience, RBAC, or gRPC contracts.

---

## Summary

| # | Item | Area | Impact | Effort | Risk |
|---|------|------|--------|--------|------|
| **A1** | Batch log + heartbeat store writes | WebClient perf | High | S | Low |
| **A2** | Memoized tree + status map (no whole-tree rebuild) | WebClient perf | High | M | Low–Med |
| **C2** | Convert hot-path `Dispatcher.Invoke` → `InvokeAsync` | WPF concurrency | Med | S | Low |
| **B3** | User-visible load-error + retry (no silent blank panes) | WebClient UX | Med | S | Low |

**Recommended order:** A1 → B3 → C2 → A2. (A1/B3/C2 are isolated; A2 touches the most call sites so it lands last.)

**Out of scope for Phase 1** (tracked in later phases): routing/deep-linking (B1), `axios`→`apiFetch` migration (B2), tree virtualization (A3), dispatcher refactor (C1), TRX streaming (D1), load testing (D3), frontend test backfill (E).

---

## A1 — Batch log & heartbeat store writes

### Problem
`addLog` copies the **entire** log array on every call, and the SignalR batch handlers call it **once per item** — so an `AgentOutputBatch` of N lines produces N array copies + N store notifications + N React commits. `AgentHeartbeats` has the same shape against `agentStore` (a full `.map()` per heartbeat).

### Evidence
- `TestController.WebClient/src/stores/executionStore.ts:30` — `addLog` does `[...s.logs, entry]` per call.
- `TestController.WebClient/src/hooks/useSignalR.ts:258` — `AgentOutputBatch` loops `store.addLog(...)`.
- `TestController.WebClient/src/hooks/useSignalR.ts:284` — `AgentHeartbeats` loops `store.updateStatus(...)`.
- `TestController.WebClient/src/stores/agentStore.ts:19` — `updateStatus` maps all agents per call.

### Fix

**1. `executionStore.ts` — add a batch action (single copy, single `set`, one trim, honors pause):**
```ts
addLog: (entry) => set((s) => {
  if (s.isLogPaused) return s;
  const logs = [...s.logs, entry];
  if (logs.length > s.maxLogs) logs.splice(0, logs.length - s.maxLogs);
  return { logs };
}),

// NEW
addLogs: (entries) => set((s) => {
  if (s.isLogPaused || entries.length === 0) return s;
  const logs = s.logs.concat(entries);
  if (logs.length > s.maxLogs) logs.splice(0, logs.length - s.maxLogs);
  return { logs };
}),
```
Add `addLogs: (entries: LogEntry[]) => void;` to the `ExecutionState` interface.

**2. `useSignalR.ts` — `AgentOutputBatch` maps then adds once:**
```ts
conn.on('AgentOutputBatch', (batch: Array<{ agentName?: string; line?: string; kind?: string; sessionId?: string }>) => {
  const now = new Date().toISOString();
  useExecutionStore.getState().addLogs(batch.map(d => ({
    message: d.line ?? '',
    agent: d.agentName,
    sessionId: d.sessionId,
    timestamp: now,
    kind: d.kind as 'stdout' | 'stderr',
    severity: d.kind === 'stderr' ? 'error' : 'info',
  })));
});
```

**3. `agentStore.ts` — add a batch heartbeat action (single `set`):**
```ts
applyHeartbeats: (updates) => set((s) => {
  if (updates.length === 0) return s;
  const map = new Map(updates.map(u => [u.name.toLowerCase(), u.status]));
  const now = new Date().toISOString();
  return {
    agents: s.agents.map(a => {
      const status = map.get(a.name.toLowerCase());
      return status ? { ...a, status, lastCheckedUtc: now } : a;
    }),
  };
}),
```
Add `applyHeartbeats: (updates: { name: string; status: string }[]) => void;` to `AgentState`, and point the `AgentHeartbeats` handler at it.

### Files
`stores/executionStore.ts`, `stores/agentStore.ts`, `hooks/useSignalR.ts`.

### Acceptance criteria
- A 200-line `AgentOutputBatch` produces **1** store commit (verify with React DevTools Profiler), not 200.
- Log trim still caps at `maxLogs`; pause still suppresses appends.
- A multi-agent `AgentHeartbeats` batch produces **1** `agents` update.

---

## A2 — Memoized tree + status map

### Problem
`updateNodeStatus` rebuilds the **entire** tree object graph (`{ ...n }` on every node, recursively) on every `ActionProgress` event, and `TreeNodeRow` is **not** memoized. Result: during a parallel run, the whole tree re-renders on every status tick.

### Evidence
- `TestController.WebClient/src/stores/watchlistStore.ts:123` — `updateStatusRecursive` spreads every node.
- `TestController.WebClient/src/stores/watchlistStore.ts:155` — `updateNodeStatus` replaces `treeRoots`.
- `TestController.WebClient/src/components/watchlist/WatchListTree.tsx:183` — `TreeNodeRow` is a plain (non-memo) recursive component.

### Fix — separate transient status from structure
Keep `treeRoots` purely structural; move execution status into a `Record<tag, NodeStatus>` map so a status tick only re-renders the rows whose tag changed.

**1. `watchlistStore.ts`:**
```ts
// state
nodeStatus: {} as Record<string, NodeStatus>,

// setConfig: reset status alongside a fresh tree
setConfig: (config) => set({ config, treeRoots: buildTree(config), nodeStatus: {}, error: null }),

// updateNodeStatus: write the map, not the tree (key by lowercased tag)
updateNodeStatus: (tag, status) => set((s) => ({
  nodeStatus: { ...s.nodeStatus, [tag.toLowerCase()]: status as NodeStatus },
})),
```
`updateStatusRecursive` can be deleted. `buildTree`/`buildActionNodeTree` can stop setting `executionStatus` (or leave it as the initial value — readers will use the map).

**2. `WatchListTree.tsx` — read status from the map and memoize the row:**
```ts
import { memo } from 'react';

const TreeNodeRow = memo(function TreeNodeRow({ node, onTriggerRequest }: { node: TreeNode; onTriggerRequest: (tag: string) => void }) {
  const status = useWatchListStore(s => (node.tag ? s.nodeStatus[node.tag.toLowerCase()] : undefined) ?? 'Idle');
  // ...replace every `node.executionStatus` read with `status`...
});
```
Because each row subscribes only to its own tag's slice, a tick for tag `X` re-renders only rows whose tag is `X`. `toggleExpand`/`selectNode` continue to mutate `treeRoots` and behave as before.

### Caveats / verification
- **Shared tags:** if two nodes share a tag, both update — same behavior as the old `updateStatusRecursive` (which matched all nodes by tag).
- **Empty-tag nodes** (Event/Ref) had no meaningful status before and still default to `Idle`.
- Confirm the status-dot legend and `statusDot` map still render for `Running/Success/Failed`.

### Files
`stores/watchlistStore.ts`, `components/watchlist/WatchListTree.tsx` (+ any other `executionStatus` reader — grep `executionStatus` under `WebClient/src`).

### Acceptance criteria
- Triggering a pipeline with ~50 actions: only the changed rows re-render per tick (React DevTools "highlight updates"), not the whole tree.
- Expand/collapse, selection, lock badges, and disabled/view-only pills behave exactly as before.

---

## C2 — Convert hot-path `Dispatcher.Invoke` → `InvokeAsync`

### Problem
Synchronous `Dispatcher.Invoke(...)` from background async paths blocks the calling thread until the UI thread is free; if the UI thread is momentarily busy this can stall or deadlock. These are fire-and-forget UI mutations, so async marshaling is strictly better.

### Evidence (confirmed fire-and-forget UI collection mutations)
- `MainViewModel.Execution.cs:596` and `:702` — `Application.Current?.Dispatcher.Invoke(() => ActiveSessions.Remove(session))`
- `MainViewModel.cs:695` / `:706` — `ActiveSessions.Add/Remove(session)`
- `MainViewModel.Execution.cs:955` / `:1006`, `MainViewModel.Security.cs:155` — UI-state updates
- (Full set: the ~16 `Dispatcher.Invoke(` hits from the walkthrough grep.)

### Fix — null-safe async pattern
`InvokeAsync` returns a `DispatcherOperation`, so don't await a possibly-null `Application.Current`:
```csharp
// before
Application.Current?.Dispatcher.Invoke(() => ActiveSessions.Remove(session));

// after (inside an async method)
if (Application.Current is { } app)
    await app.Dispatcher.InvokeAsync(() => ActiveSessions.Remove(session));
```
For non-async callers, drop the `await` (fire-and-forget `InvokeAsync` is fine for UI mutations).

### Explicitly NOT changed
- `Dispose()` / shutdown `.GetAwaiter().GetResult()` in `App.xaml.cs:272`, `ControllerWebApiHost.cs:230`, `SystemModeClient.cs:156` — these are intentional synchronous teardown; leave them.

### Files
`ViewModels/MainViewModel.Execution.cs`, `ViewModels/MainViewModel.cs`, `ViewModels/MainViewModel.Security.cs` (and other confirmed hot-path sites).

### Acceptance criteria
- Solution builds; existing WPF tests pass.
- Triggering/cancelling sessions while the UI is under load no longer momentarily freezes the window.
- No behavioral change to `ActiveSessions` contents or ordering.

---

## B3 — User-visible load-error + retry

### Problem
On initial load, `fetchConfig` / `fetchAgents` / `fetchSessions` failures are only `console.error`'d — the user sees an empty pane with no explanation or recovery path. (Only the system-mode boot error has a visible state today.)

### Evidence
- `TestController.WebClient/src/components/layout/AppShell.tsx:42` — `useEffect` fires the three fetches and swallows failures into `console.error`.

### Fix
Track a per-load error + retry in `AppShell` (or surface the error each domain store already stores). Minimal version:
```ts
const [loadError, setLoadError] = useState<string | null>(null);

const loadAll = useCallback(() => {
  setLoadError(null);
  Promise.allSettled([fetchConfig(), fetchAgents(), fetchSessions()]).then(results => {
    const failed = results.filter(r => r.status === 'rejected');
    if (failed.length) setLoadError(`Failed to load ${failed.length} resource(s). Check controller connectivity.`);
  });
}, [fetchConfig, fetchAgents, fetchSessions]);

useEffect(() => { loadAll(); }, [loadAll]);
```
Render a dismissible banner under the header when `loadError` is set, with a **Retry** button calling `loadAll()`. (`WatchListTree` already renders its own store `error`; this banner covers the agents/sessions panes that have no inline error today.)

### Files
`components/layout/AppShell.tsx` (optionally a tiny `components/common/LoadErrorBanner.tsx`).

### Acceptance criteria
- With the controller down on first load, the user sees a banner + working Retry (no blank panes, no console-only failure).
- On success, no banner; behavior unchanged.

---

## Verification (run after each item)

**WebClient (A1, A2, B3):**
```bash
cd TestController.WebClient
npm run build      # tsc -b && vite build  (type-check + bundle)
npm run test       # vitest run
```
Plus a manual Profiler pass: trigger a multi-action pipeline and confirm (a) batched log commits and (b) only-changed-row tree re-renders.

**WPF (C2):**
```bash
dotnet build TestAgentSolution.sln
dotnet test TestControllerGrpc.Tests/TestControllerGrpc.Tests.csproj
```

## Definition of done
- [ ] A1: batch actions added and wired; profiler shows 1 commit per batch.
- [ ] A2: status map + memoized rows; only changed rows re-render; no UI regressions.
- [ ] C2: hot-path invokes converted; build + WPF tests green; no freeze under load.
- [ ] B3: load-error banner + retry; no silent blank panes.
- [ ] `npm run build`, `npm run test`, `dotnet build`, and WPF tests all pass.
- [ ] No changes to gRPC contracts, RBAC, or resilience policies.
```
