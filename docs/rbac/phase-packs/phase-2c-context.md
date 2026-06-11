# Phase 2c Context Pack — Web Client Capability Gating & Filtered Pipeline View

> Attach this file to every Copilot session while working on Phase 2c. Detach when Phase 2c ships and switch to `phase-3-context.md`.

> Companion reading: `docs/rbac/01_System_Design.md` §3 (authorization), `docs/rbac/02_Implementation_Roadmap.md` §Phase 2, `docs/rbac/04_UI_Mockup_Catalog.md` Mockup 4 (Engineer "My pipelines" web view) + Mockup 11 (Default mode tooltip), `docs/architecture/CURRENT_STATE.md` "React Structure".

---

## Goal

Ship the Web Client capability layer so Engineers only see assigned pipelines, buttons disable/enable per permission, and 403 responses trigger a toast + capability refresh. After Phase 2c:

- Engineers in Secured mode see a filtered pipeline list ("Showing 3 of 7 pipelines")
- Trigger/Cancel buttons disabled when `can()` returns false, with an explanatory tooltip
- Admin/SrMgr see all pipelines, all buttons enabled
- Guest/Observer see all pipelines, all write buttons disabled (view-only)
- 403 from the server shows a toast + auto-refreshes stale capabilities
- In Default mode, web users are Observers: same as pre-RBAC read-only behavior — zero visible change

No WPF work in this phase (done in 2b).

---

## Where things land

| Concern | Path |
|---|---|
| Pure capability logic | `src/lib/capabilities.ts` |
| React hook binding | `src/hooks/useCapabilities.ts` |
| AuthUser type extension | `src/stores/authStore.ts` (modify) |
| Filtered pipeline list (selector) | `src/stores/watchlistStore.ts` (modify — add selector) |
| DisabledTriggerButton extension | `src/components/common/DisabledTriggerButton.tsx` (modify) |
| 403 handling | `src/lib/api.ts` (modify) |
| Auth-denied toast component | `src/components/common/AuthDeniedToast.tsx` |
| Tests | `src/__tests__/capabilities.test.ts` |

No new packages. All work lands in existing WebClient project.

---

## File targets (8 tasks, 3 blocks)

### Block A — Pure logic + hook (no UI dependencies)

| # | Path | What |
|---|---|---|
| 1 | `src/lib/capabilities.ts` | Pure function: `can(user: AuthUser | null, mode: SystemMode, permission: string, resourceId?: string): boolean`. Logic: (a) if mode `'default'` → only `Pipeline_View` and `Report_View` return true, all writes false (per `default-mode-web-readonly`); (b) if user null → false; (c) if role `'Administrator'` → true; (d) lookup `PERMISSION_CATALOG[role]` → check includes permission; (e) if `resourceId` and role `'Engineer'` → check `user.assignedPipelineIds.includes(resourceId)`. Also export `getDisabledReason(user, mode, permission, resourceId?)` → returns a tooltip string or null. Export `PERMISSION_CATALOG` as a frozen map mirroring `PermissionCatalog.cs`. |
| 2 | `src/hooks/useCapabilities.ts` | `useCan(permission: string, resourceId?: string): boolean` and `useDisabledReason(permission, resourceId?): string | null`. Selects from `useAuthStore` (user) + `useSystemModeStore` (mode) and calls the pure functions. Memoized via Zustand shallow equality — does NOT create new subscriptions per call. |

### Block B — UI integration (depends on Block A)

| # | Path | What |
|---|---|---|
| 3 | `src/stores/authStore.ts` (modify) | Add `assignedPipelineIds: string[]` to `AuthUser` interface. `fetchMe` already deserializes `/api/auth/me` — include the new field. |
| 4 | `src/stores/watchlistStore.ts` (modify) | Add a **derived selector** (not a second copy): `useFilteredWatchItems(): { items: TreeNode[]; label: string }`. Logic: if mode default or role Admin/SrMgr/Guest → all items, label `''`. If Engineer → filter `treeRoots[0].children` where `node.tag` is in `user.assignedPipelineIds`, label `Showing ${n} of ${total} pipelines`. |
| 5 | `src/components/common/DisabledTriggerButton.tsx` (modify) | Accept optional `pipelineTag?: string` prop. Use `useCan('Pipeline_Trigger', pipelineTag)` + `useDisabledReason(...)`. Tooltip text: return value of `getDisabledReason()` — covers all cases (Default mode, not assigned, role lacks permission). Remove the existing hardcoded Default-mode-only logic; the capability system subsumes it. |
| 6 | `src/components/common/AuthDeniedToast.tsx` | Notification component: amber border, shield icon, message from server `reasonCode`, auto-dismiss after 5s. Mounted by a global `useAuthDeniedToast` store (single Zustand atom: `{ visible, message, show(), hide() }`). |

### Block C — 403 handling + tests (depends on A + B)

| # | Path | What |
|---|---|---|
| 7 | `src/lib/api.ts` (modify) | In the `!response.ok` branch: if `response.status === 403`, extract `body.reasonCode`, call `useAuthDeniedToast.getState().show(body.error)`, then call `useAuthStore.getState().fetchMe()` (fire-and-forget — refreshes stale capabilities). Still throw the structured error as before so callers can handle it if needed. |
| 8 | `src/__tests__/capabilities.test.ts` | Table-driven Vitest matrix: `[role, mode, permission, resourceId, expected]` tuples covering every row of `PermissionCatalog`. Must mirror `TestControllerGrpc.Tests/Rbac/AuthorizationServiceTests.cs` decision cases so client and server can't drift. |

---

## Sequence rules

- **Phase 2a + 2b must be complete** — server returns `AssignedPipelineIds` in `/api/auth/me`.
- **Block A first** — pure logic before UI bindings.
- **Block B depends on Block A** — hooks call the pure functions.
- **Task 3 (authStore extension) can run in parallel with Task 1** — no code dependency.
- **Block C (403 handling + tests) depends on A + B** — toast is mounted, fetchMe refresh cascades.

---

## Watch out for

1. **`capabilities.ts` is a UX hint, NOT security.** The server-side guard from Phase 2a (`PipelineAuthorizationGuard` → `IAuthorizationService.CanAsync`) is the source of truth. Never skip a server call because the client returned false — the client check only drives button state.

2. **Default mode: web users are Observers.** `can(user, 'default', 'Pipeline_View')` → true. `can(user, 'default', 'Pipeline_Trigger', id)` → false. This matches the server's `default-mode-web-readonly` behavior exactly. The pre-RBAC behavior (trigger button disabled with tooltip) must remain pixel-identical.

3. **Guest in Secured mode ≠ Observer in Default mode.** Guest has a token + `isGuest: true` + capabilities `['Pipeline_View', 'Report_View']`. Observer in Default mode has no token (or a synthetic one). They both see all pipelines, both can't trigger — but they're different identities with different badges. Do NOT merge these concepts in code; the `can()` function handles both correctly by checking the permission catalog.

4. **Zustand selector for filtering: derive, don't duplicate.** The filtered list is a selector over `watchlistStore` + `authStore` + `systemModeStore`, NOT a second copy of the tree data. Pattern: `export function useFilteredWatchItems() { ... }` using `useShallow` from zustand for stable references. Never store filtered results in store state — that creates sync bugs when assignments update.

5. **The existing `useSignalR` reconnect already calls into stores on reconnect.** On `onreconnected`, session groups are rejoined. This is the right place to re-fetch user data if needed. Verify the existing `App.tsx` calls `fetchMe()` on mount with a stored token — that covers reconnect scenarios. Do NOT add a polling timer; the existing reconnect + server-denial refresh is sufficient.

6. **`apiFetch` already has structured error handling with `correlationId`.** Extend it for the 403/reasonCode case — don't wrap it in a new function. The 403 handler lives inside the existing `!response.ok` branch. Pattern: check `response.status === 403`, show toast, fire `fetchMe()`, then throw as usual.

7. **Tooltip copy from Mockup 4 and Mockup 11.** Default mode: "Trigger is unavailable in Default mode. Switch to Secured mode to enable pipeline triggering from the Web Client." Not assigned: "You are not assigned to this pipeline. Contact your administrator." Role lacks permission: "Your role (Guest) does not have trigger permissions." These come from `getDisabledReason()` — one function, all cases.

8. **Vitest: `capabilities.test.ts` is the critical test.** Use `describe.each` or `it.each` with a matrix: `[role, mode, permission, resourceId, expected]`. Must cover: Admin all-yes, Engineer assigned-yes, Engineer unassigned-no, SrMgr all-pipeline-yes, Guest view-yes trigger-no, Default-mode all-write-no. Mirror the server's `AuthorizationServiceTests` deny-reason cases.

9. **`PERMISSION_CATALOG` must be a frozen object matching `PermissionCatalog.cs`.** Roles: Administrator (all 19 permissions), SeniorManager (10), Engineer (6), Guest (2). If the server catalog changes, this file must change too — add a comment referencing the C# source path.

10. **`AuthDeniedToast` uses a Zustand atom, not React context.** Pattern: `export const useAuthDeniedToast = create<{ visible: boolean; message: string; show: (msg: string) => void; hide: () => void }>((set) => ...)`. The toast component reads from this store and auto-hides after 5s via `setTimeout` in `show()`.

---

## How to start a task in this phase

```
[SPEC]
- docs/rbac/01_System_Design.md §3 — Authorization Design
- docs/rbac/04_UI_Mockup_Catalog.md Mockup 4 + Mockup 11 — Engineer filtered view, tooltip copy
- docs/rbac/02_Implementation_Roadmap.md §Phase 2 — Pipeline Trigger + Cancel
- docs/architecture/CURRENT_STATE.md — React Structure, apiFetch, Zustand stores

[CURRENT STATE]
- Phase 0 + 0.5 + 1a + 1b + 2a + 2b complete
- /api/auth/me now returns assignedPipelineIds (server change in 2b)
- Tasks 1..N of Phase 2c already done (paths)
- Pending: this task (path)
- Existing patterns: apiFetch in src/lib/api.ts, Zustand stores, useSystemModeStore

[TASK]
Generate <path>

[CONSTRAINTS]
- capabilities.ts is pure (no React, no store imports) — testable without DOM
- useCapabilities.ts binds to stores via Zustand selectors
- Filtered list is a derived selector, NOT a store copy
- DisabledTriggerButton uses useCapabilities, not raw store reads
- 403 handler extends apiFetch inline, doesn't wrap it
- Zustand for state (useAuthDeniedToast atom), no React Context
- Vitest for tests; table-driven matrix mirroring server tests
- Do NOT add a polling timer — rely on reconnect + denial refresh
- Tooltip copy per Mockup 4 and Mockup 11 — reference, don't invent

[OUTPUT]
- The file content
- Nothing else
```

---

## Exit checklist (don't move to Phase 3 until all green)

- [ ] Default mode: all pipelines visible, Trigger buttons disabled, tooltip reads "Trigger is unavailable in Default mode…"
- [ ] Secured mode + Admin: all pipelines visible, all Trigger buttons enabled
- [ ] Secured mode + SeniorManager: all pipelines visible, Trigger/Cancel enabled
- [ ] Secured mode + Engineer (assigned to 3 of 7): only 3 pipeline nodes visible, label "Showing 3 of 7 pipelines"
- [ ] Secured mode + Engineer: Trigger enabled for assigned, disabled for unassigned (if navigated by URL)
- [ ] Secured mode + Guest: all pipelines visible, Trigger disabled, tooltip "Your role (Guest) does not have trigger permissions."
- [ ] Server returns 403 on trigger attempt (stale assignment): AuthDeniedToast appears, capabilities auto-refresh, button disables after refresh
- [ ] After 403 refresh: Engineer sees updated filter (assignment revoked → pipeline disappears from list)
- [ ] `capabilities.test.ts` passes with table-driven matrix covering all role × permission × mode combos
- [ ] `PERMISSION_CATALOG` in `capabilities.ts` matches `PermissionCatalog.cs` exactly (19 Admin, 10 SrMgr, 6 Engineer, 2 Guest)
- [ ] `apiFetch` 403 handling does NOT break existing error handling (correlation ID still flows, non-403 errors unaffected)
- [ ] `useSignalR` reconnect behavior unchanged — no new polling timers added
- [ ] `npm run test` passes; `npm run build` passes with no type errors
- [ ] All Phase 0 + 0.5 + 1a + 1b + 2a + 2b tests still pass (regression)
