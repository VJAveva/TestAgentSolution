# Phase 2 — UI Ease & Consistency (Implementation Plan)

> **Created:** 2026-06-19
> **Scope:** Navigation, unified data-access, and large-tree rendering — the "UI ease" items from the 2026-06-19 walkthrough.
> **Stack touched:** `TestController.WebClient` (React/Zustand) only.
> **Depends on:** Phase 1 (`docs/Issues/PHASE1-QUICK-WINS.md`) — A3 below builds on A2's status-map + memoized rows.
> **Goal:** Make the app deep-linkable and refresh-safe, give every API call one consistent error/auth path, and keep large WatchLists smooth — without changing backend contracts.

---

## Summary

| # | Item | Area | Impact | Effort | Risk |
|---|------|------|--------|--------|------|
| **B1** | Hash routing — deep-linkable, refresh-safe tabs | UX navigation | High | M | Low |
| **B2** | Finish `axios` → `apiFetch` migration (one error/auth path) | UX consistency / maint. | Med | M | Med |
| **A3** | Virtualize WatchListTree (flatten-then-virtualize) | Perf (large trees) | Med | M–L | Med |

**Recommended order:** B1 → B2 → A3. (B1 is self-contained; B2 is mechanical but broad; A3 is the deepest single-component change and should land last.)

**Decision needed before A3:** how large do real WatchLists get? If < ~50 nodes, A3 is optional (Phase 1 A2 already removes the re-render cost) and can be dropped. A3 matters at 300+ nodes.

---

## B1 — Hash routing (deep-linkable, refresh-safe)

### Problem
The active tab is component state (`useState`), so a page refresh drops the user back to **WatchList**, URLs can't be bookmarked/shared, and browser back/forward do nothing. The same applies to the Monitor sub-tab.

### Evidence
- `TestController.WebClient/src/components/layout/AppShell.tsx:37` — `const [activeTab, setActiveTab] = useState<Tab>('watchlist')`.
- `TestController.WebClient/src/components/layout/AppShell.tsx:140` — `MonitorPage` keeps its own `useState` sub-tab.

### Recommended approach — zero-dependency hash route
A tab shell doesn't need a full router. `@tanstack/react-virtual`, `zustand`, etc. are already in the bundle; adding `react-router-dom` is heavier than this needs. Use the URL hash via `useSyncExternalStore` (React 18, no dep, SSR-safe semantics, survives refresh, gives back/forward for free).

**1. New `hooks/useHashRoute.ts`:**
```ts
import { useSyncExternalStore, useCallback } from 'react';

const subscribe = (cb: () => void) => {
  window.addEventListener('hashchange', cb);
  return () => window.removeEventListener('hashchange', cb);
};
// "#/monitor/timeline" -> "monitor/timeline"
const getSnapshot = () => window.location.hash.replace(/^#\/?/, '');

export function useHashRoute(): [string, (route: string) => void] {
  const route = useSyncExternalStore(subscribe, getSnapshot);
  const navigate = useCallback((r: string) => { window.location.hash = `/${r}`; }, []);
  return [route, navigate];
}
```

**2. `AppShell.tsx` — derive the tab from the route segment:**
```ts
const [route, navigate] = useHashRoute();
const [seg0, seg1] = route.split('/');
const activeTab = (tabs.find(t => t.id === seg0)?.id ?? 'watchlist') as Tab;
// tab button: onClick={() => navigate(t.id)}
```

**3. `MonitorPage` — nest the sub-tab in the route (`monitor/<sub>`):**
```ts
const subTab = (['pipeline','timeline','log'].includes(seg1) ? seg1 : 'pipeline') as 'pipeline'|'timeline'|'log';
// sub-tab button: onClick={() => navigate(`monitor/${t.id}`)}
```
Pass `seg1`/`navigate` down, or read `useHashRoute()` inside `MonitorPage`.

### Optional follow-ons (same mechanism, can defer)
- `results/<buildId>` to deep-link a build; `execution/<sessionId>` to deep-link a session. These need the corresponding store to select-by-id on mount.

### Alternative considered
`react-router-dom` — standard and better if nested/lazy routes are coming, but adds a dependency + bundle weight and restructures `AppShell`. Recommend the hash approach for Phase 2; revisit react-router only if route nesting grows.

### Files
New `hooks/useHashRoute.ts`; edit `components/layout/AppShell.tsx`.

### Acceptance criteria
- Reloading on any tab (e.g. `#/monitor/timeline`) returns to that exact tab/sub-tab.
- Browser back/forward move between visited tabs.
- A copied URL opens the same view on another browser/session.
- Default (no hash) still lands on WatchList.

---

## B2 — Finish the `axios` → `apiFetch` migration

### Problem
Two data-access layers coexist. They've converged (both attach correlation id + token), but with **asymmetric behavior and different error shapes**:

| Concern | `apiFetch` (`lib/api.ts`) | global `axios` (`lib/axiosConfig.ts`) |
|---|---|---|
| 403 → AuthDeniedToast + `fetchMe()` | ✅ | ❌ |
| 404-HTML (SPA misroute) detection | ✅ | ❌ |
| 401 → clear auth, return to login | ❌ | ✅ |
| Thrown error shape | `{status, error, detail, reasonCode, correlationId}` | `AxiosError` (`err.response.data`) |

So a raw-axios call that 403s shows **no permission toast**, and components must handle **two error shapes**. Migrating onto `apiFetch` unifies this — but only after `apiFetch` is taught the two things axios does that it doesn't.

### Evidence
- `lib/api.ts:74` (403 handling), `:82` (narrowed throw — no response body).
- `lib/axiosConfig.ts:57` (401 handling — exists only here).
- `hooks/useExecution.ts:2` imports axios; `:12` `extractLockFrom409` reads `err.response.data.lock` (the **409 lock DTO** — would be lost under today's `apiFetch` throw).
- Raw-axios users (grep): `hooks/useAgents.ts`, `useAgentTelemetry.ts`, `useExecution.ts`, `useResults.ts`, `useWatchList.ts`, `useFleetState.ts`, `useSignalR.ts`, `components/execution/TriggerDialog.tsx`.

### Fix — step 1: make `apiFetch` a superset (no regressions)

**a. Carry the parsed body on errors** (so 409 lock DTO / `reasonCode` survive) — `lib/api.ts`:
```ts
throw {
  status: response.status,
  error: body.error || response.statusText,
  detail: body.detail,
  reasonCode: body.reasonCode,
  correlationId: body.correlationId || correlationId,
  body,                       // NEW: full parsed body for callers (e.g. 409 { lock })
};
```

**b. Add 401 handling** (port from the axios interceptor) before the 403 block in `lib/api.ts`:
```ts
if (response.status === 401 && !path.includes('/api/auth/')) {
  sessionStorage.removeItem('auth_token');
  const { useAuthStore } = await import('../stores/authStore');
  const st = useAuthStore.getState();
  if (st.isAuthenticated) {
    useAuthStore.setState({ user: null, token: null, isAuthenticated: false, mustChangePassword: false, error: null });
  }
}
```

**c. Add typed verb helpers** to cut boilerplate vs `const { data } = await axios.x`:
```ts
export const apiGet  = <T>(path: string) => apiFetch<T>(path);
export const apiPost = <T>(path: string, body?: unknown) =>
  apiFetch<T>(path, { method: 'POST', body: body !== undefined ? JSON.stringify(body) : undefined });
export const apiDelete = <T>(path: string) => apiFetch<T>(path, { method: 'DELETE' });
```

### Fix — step 2: migrate callers (one hook per PR)
Pattern (from `useExecution.ts`):
```ts
// before
const { data } = await axios.post(`/api/execution/trigger/${encodeURIComponent(tag)}`, params);
// after
const data = await apiPost(`/api/execution/trigger/${encodeURIComponent(tag)}`, params);

// 409 extraction — before
if (error?.response?.status === 409) { const b = error.response.data; if (b?.lock) return b.lock; }
// after
if (error?.status === 409 && error.body?.lock) return error.body.lock as PipelineLockDto;
```
Also migrate the two in-handler calls in `useSignalR.ts` (`ResultsUpdated` → `/api/results/builds`, `WatchListReloaded` → `/api/watchlist`).

### Fix — step 3: retire axios
When no `import axios` remains under `src/` (verify by grep), delete `lib/axiosConfig.ts` and its call site (`main.tsx` `configureAxios()`), and drop `axios` from `package.json`.

### Caveats
- Don't migrate and delete in one PR — keep both layers working until the last caller is moved, then remove axios.
- `apiFetch` sets `Content-Type: application/json`; for GETs with no body that's harmless, but confirm no endpoint rejects it.
- Preserve every existing 409/`reasonCode` branch when rewriting error handling.

### Files
`lib/api.ts` (extend), all hooks listed above, `components/execution/TriggerDialog.tsx`, `hooks/useSignalR.ts`, finally `lib/axiosConfig.ts` + `main.tsx` + `package.json`.

### Acceptance criteria
- Every `/api` call goes through `apiFetch`; `grep "import axios"` under `src/` returns nothing.
- A 403 from **any** call shows the AuthDeniedToast and refreshes capabilities.
- 401 still clears auth and returns to login.
- Lock-conflict (409) modal still appears on trigger/retry.
- One error shape across the app.

---

## A3 — Virtualize WatchListTree (flatten-then-virtualize)

### Problem
The tree renders **every** node recursively (no virtualization). After Phase 1 A2 the re-render cost is gone, but a very large WatchList still mounts a large DOM. Only do this if real trees are big (see decision note above).

### Evidence
- `components/watchlist/WatchListTree.tsx:183` — recursive `TreeNodeRow`; `:279` renders all expanded children into the DOM.
- `@tanstack/react-virtual` is already a dependency (used by `LiveLogger`) — no new package.

### Fix — flatten visible nodes, then virtualize the flat list
A hierarchical tree can't be virtualized directly, so flatten the **currently-expanded** nodes into a flat, depth-tagged array and virtualize that (rows already carry `depth` for indentation, so no recursion is needed to render).

```ts
function flattenVisible(nodes: TreeNode[], out: TreeNode[] = []): TreeNode[] {
  for (const n of nodes) {
    out.push(n);
    if (n.isExpanded && n.children.length) flattenVisible(n.children, out);
  }
  return out;
}
```
Then mirror `LiveLogger`'s `useVirtualizer` over `flattenVisible(roots)`, rendering each row from `node.depth` (the existing `paddingLeft: depth*20+8` already does indentation). Expand/collapse just toggles `isExpanded`, which re-derives the flattened list.

### Caveats
- Do **after** A2 — each row should subscribe to its own `nodeStatus[tag]` slice, so virtualization + per-row status stay cheap.
- The current `TreeNodeRow` renders children inline; flattening changes the render model — re-test expand/collapse, selection highlight, lock badges, trigger buttons, and the assignment-filtered root.
- Variable row heights (wrapping labels) need `measureElement` or a fixed row height; the tree rows are single-line, so a fixed estimate (~28px) is fine.

### Files
`components/watchlist/WatchListTree.tsx`.

### Acceptance criteria
- A 500-node expanded tree keeps only the visible window in the DOM (verify element count) and scrolls smoothly.
- Expand/collapse, selection, badges, lock state, and Engineer assignment filtering behave exactly as before.

---

## Verification (run where Node 20+ is available)
```powershell
cd TestController.WebClient
npm ci
npm run build   # tsc -b && vite build
npm run test    # vitest run
```
Manual: reload on a deep tab (B1), force a 403 and a 409 (B2), scroll a large tree (A3).

## Definition of done
- [ ] B1: tabs + monitor sub-tab are in the URL hash; refresh and back/forward work.
- [ ] B2: `apiFetch` is the single layer (carries body + handles 401/403/404); no `import axios` under `src/`; 403 toast, 401 logout, and 409 lock modal all still fire.
- [ ] A3: large trees virtualized with no behavioral regressions (or explicitly deferred if trees are small).
- [ ] `npm run build` + `npm run test` pass; no backend/contract changes.
```
