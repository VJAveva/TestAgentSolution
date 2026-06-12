# Phase 3c Context Pack — Web Client Pipeline Lock UI

> Attach this file to every Copilot session while working on Phase 3c. Detach when Phase 3c ships and switch to `phase-4-context.md`.

> Companion reading: `docs/rbac/04_UI_Mockup_Catalog.md` Mockups 3, 4, 6, 9; `docs/rbac/05_Default_Mode_Design.md` §5 (Lock Model in Default Mode), §7 (UI Differences); `docs/architecture/CURRENT_STATE.md` "Pipeline Lock Coordination" section; `docs/architecture/CONVENTIONS.md` React section.

---

## Goal

Ship the Web client-side lock UI — Zustand store, SignalR event wiring, badge, conflict modal, force-release dialog, and lock-aware trigger gating — WEB ONLY. After Phase 3c:

- `lockStore` holds the live lock map keyed by pipeline Tag; derived selector `isLockedByOther(tag)` compares against `authStore.user.userId`
- `LockEvents` subscribes to all 5 SignalR lock events on the existing `/hubs/controller` connection
- On connect AND reconnect: `GET /api/locks` → `lockStore.setAll` (full resync, never polling)
- Every WatchItem tree row renders `LockBadge` (blue = your run + elapsed mm:ss; amber = other owner)
- `DisabledTriggerButton` gains a "locked by other" disabled reason with tooltip naming the owner
- 409 response body from trigger path → opens `LockConflictModal` with the `PipelineLockDto` from the response (no refetch)
- `LockConflictModal` (Mockup 6) shows owner card with initials avatar, role, client chip, acquired time, expiry countdown; force-release section visible only when `can('Pipeline_ForceRelease')` — Senior Manager only on web
- `ForceReleaseDialog` (Mockup 9) enforces reason >= 10 chars + affirmation checkbox; on 200 the dialog closes and badge updates via `PipelineLockStolen` broadcast — NO optimistic update
- Default mode: Observer sees "Locked by Default user (WPF)" from the same DTO — zero special-casing
- Guest in Secured mode: sees badges, never sees force-release section

No WPF work in this phase (done in Phase 3b).

---

## Where things land

| Concern | Path | Project |
|---|---|---|
| Lock store | `src/stores/lockStore.ts` | `TestController.WebClient` |
| Lock SignalR events | `src/signalr/LockEvents.ts` | `TestController.WebClient` |
| Lock badge | `src/components/watchlist/LockBadge.tsx` | `TestController.WebClient` |
| WatchListTree diff | `src/components/watchlist/WatchListTree.tsx` (modify) | `TestController.WebClient` |
| Conflict modal | `src/components/dialogs/LockConflictModal.tsx` | `TestController.WebClient` |
| Force-release dialog | `src/components/dialogs/ForceReleaseDialog.tsx` | `TestController.WebClient` |
| DisabledTriggerButton diff | `src/components/common/DisabledTriggerButton.tsx` (modify) | `TestController.WebClient` |
| Trigger path 409 handling | `src/hooks/` or trigger call site (modify) | `TestController.WebClient` |
| Lock store tests | `src/stores/lockStore.test.ts` | `TestController.WebClient` |
| LockBadge tests | `src/components/watchlist/LockBadge.test.tsx` | `TestController.WebClient` |
| ForceReleaseDialog tests | `src/components/dialogs/ForceReleaseDialog.test.tsx` | `TestController.WebClient` |

---

## File targets (8 tasks, 3 blocks)

### Block A — Store + SignalR wiring (no UI dependencies)

| # | Path | What |
|---|---|---|
| 1 | `src/stores/lockStore.ts` | Zustand store: `locks: Record<string, PipelineLockDto>` keyed by `pipelineId` (= WatchItem Tag). Actions: `onAcquired(dto)` → upsert, `onReleased(dto)` → delete, `onExpired(dto)` → delete, `onStolen(dto)` → upsert (new owner), `onRewritten(dto)` → upsert, `setAll(dto[])` → replace entire map. Derived selector: `isLockedByOther(tag)` — returns `true` when `locks[tag]` exists AND `locks[tag].ownerUserId !== useAuthStore.getState().user?.userId`. Also expose `getLock(tag)` for badge/dialog consumers. |
| 2 | `src/signalr/LockEvents.ts` | Subscribe to the 5 lock hub events (`PipelineLockAcquired`, `PipelineLockReleased`, `PipelineLockExpired`, `PipelineLockStolen`, `PipelineLockRewritten`) on the existing `/hubs/controller` HubConnection. Each event calls the corresponding `lockStore` action. On initial connect AND on every reconnect (use the existing reconnect hook path in `useSignalR`): call `GET /api/locks` via `apiFetch<PipelineLockDto[]>` → `lockStore.getState().setAll(data)`. This is the full-resync path; never add a polling timer. |

### Block B — UI components (depends on Block A)

| # | Path | What |
|---|---|---|
| 3 | `src/components/watchlist/LockBadge.tsx` + **WatchListTree.tsx diff** | Two visual variants: **amber** (other-owner): lock icon + `"Locked by {ownerDisplayName} ({ownerClientKind})"` — `bg-amber-900/30 text-amber-400` per existing badge palette. **Blue** (own run): play icon + `"Your run · mm:ss"` with 1-second `setInterval` timer computing elapsed from `acquiredUtc` — `bg-blue-900/30 text-blue-400`. Timer cleanup: `clearInterval` in `useEffect` teardown (return function). Props: `lock: PipelineLockDto \| undefined`, `isOwn: boolean`. Render in `WatchListTree.tsx` per WatchItem row: derive `lock` from `useLockStore(s => s.locks[node.tag])`, derive `isOwn` from comparing `lock?.ownerUserId` to `useAuthStore(s => s.user?.userId)`. |
| 4 | `src/components/dialogs/LockConflictModal.tsx` | Mockup 6. Props: `open: boolean`, `lock: PipelineLockDto`, `onClose: () => void`. Owner card: initials avatar (first letter of `ownerDisplayName`), role badge (if available), client-kind chip (`"WPF"` / `"Web"`), started time (`acquiredUtc` formatted), expiry countdown (computed client-side from `expiresUtc` via 1-second `setInterval`, cleanup on unmount/close). Actions: Close button, "View dashboard" link. Force-release section: visible ONLY when `useCan('Pipeline_ForceRelease')` returns `true` — Senior Manager on web; Guest never sees it. Button text: "Take over and revert…" (opens `ForceReleaseDialog`). |
| 5 | `src/components/dialogs/ForceReleaseDialog.tsx` | Mockup 9. Props: `open: boolean`, `lock: PipelineLockDto`, `onClose: () => void`. State: `reason` (textarea), `isAffirmed` (checkbox). Live character counter: `"{len} / 10 minimum"` — turns green (`text-green-400`) when `>= 10`. Affirmation checkbox label: `"I confirm that {ownerDisplayName}'s run will be cancelled"`. Submit button enabled ONLY when `reason.length >= 10 && isAffirmed`; amber styling when enabled, 45% opacity + `cursor-not-allowed` when disabled. Submit calls `POST /api/locks/{pipelineId}/force-release` with `{ reason }` via `apiFetch`. On 200: close dialog — badge updates via `PipelineLockStolen` broadcast (no optimistic update). On error: show inline error message. |

### Block C — Trigger gating + tests (depends on A + B)

| # | Path | What |
|---|---|---|
| 6 | `src/components/common/DisabledTriggerButton.tsx` (modify) | Add new disabled reason: when `lockStore.isLockedByOther(pipelineTag)` returns `true`, the button is disabled with tooltip `"Pipeline is locked by {ownerDisplayName}"`. This takes priority over permission-denied reasons (locked = cannot trigger regardless of role). |
| 7 | Trigger call site (modify) | In the `apiFetch` trigger path (wherever `POST /api/pipelines/{tag}/trigger` is called): catch 409 responses. Parse `response.lock` as `PipelineLockDto` from the JSON body. Open `LockConflictModal` with the parsed DTO. No refetch — the response body IS the conflict context. |
| 8 | Tests | **`lockStore.test.ts`**: event reducer actions (onAcquired upserts, onReleased/onExpired deletes, onStolen upserts new owner, onRewritten upserts); `setAll` replaces the entire map; `isLockedByOther` selector returns `true` when owner differs, `false` when owner matches current user (mock `authStore`). **`LockBadge.test.tsx`**: renders amber variant for other-owner lock; renders blue variant with timer for own lock; timer cleanup on unmount (spy on `clearInterval`). **`ForceReleaseDialog.test.tsx`**: submit disabled when reason < 10 chars; submit disabled when checkbox unchecked; submit enabled when both conditions met; character counter reflects current length. |

---

## Payload shapes (from Phase 3a — do NOT invent field names)

```typescript
// PipelineLockDto — returned by GET /api/locks, SignalR events, and 409 bodies
interface PipelineLockDto {
  pipelineId: string;          // = WatchItem Tag
  ownerUserId: string;         // UUID of the lock holder
  ownerDisplayName: string;    // e.g. "ravi.kumar"
  ownerClientKind: string;     // "Wpf" | "Web"
  acquiredUtc: string;         // ISO 8601
  expiresUtc: string;          // ISO 8601
}

// SignalR hub events (server → client) on /hubs/controller:
//   PipelineLockAcquired(dto: PipelineLockDto)
//   PipelineLockReleased(dto: PipelineLockDto)
//   PipelineLockExpired(dto: PipelineLockDto)
//   PipelineLockStolen(dto: PipelineLockDto, priorOwnerDisplayName: string)
//   PipelineLockRewritten(dto: PipelineLockDto, priorOwnerDisplayName: string)

// GET /api/locks → PipelineLockDto[]

// 409 response body from trigger endpoint:
// { "error": "pipeline-locked", "lock": PipelineLockDto }

// POST /api/locks/{pipelineId}/force-release
// Request: { "reason": string }   (>= 10 chars, server-validated)
// Response 200: empty (badge update arrives via PipelineLockStolen broadcast)
```

---

## Sequence rules

- **Phase 3a + 3b must be complete** — server-side LockRegistry, LockBroadcaster, PipelineLockDto, SignalR events, gRPC Aborted payload, REST 409, WPF UI all shipped.
- **Block A first** — store + SignalR wiring, no UI components.
- **Block B depends on A** — components consume the store.
- **Block C depends on A + B** — trigger gating uses the store; tests verify full flow.
- **Task 3 includes the WatchListTree.tsx diff** — that's part of the badge's exit criteria, not a separate later step.

---

## Watch out for

1. **`isLockedByOther` is a DERIVED selector** over `lockStore` + `authStore` — never stored state. Compute it on read: `locks[tag]?.ownerUserId !== useAuthStore.getState().user?.userId`. Drift risk on login/logout if you store the boolean.

2. **Reconnect resync is mandatory.** Events missed while disconnected = stale badges. The existing `useSignalR` reconnect hook fires on reconnection — that's where `GET /api/locks` → `lockStore.setAll` goes. Do NOT add a polling timer.

3. **The web path goes through `ControllerProxyService`** — locks live in the controller process. If badges lag or 409 bodies arrive empty, the proxy hop is the first suspect. Verify it forwards the 409 body verbatim and propagates SignalR from the controller hub, not a local one.

4. **Default mode: Observer sees "Locked by Default user (WPF)"** rendered from the same DTO — zero special-casing in the component. The badge just renders `ownerDisplayName` and `ownerClientKind` from the DTO regardless of mode.

5. **Guest in Secured mode: sees badges, never sees force-release.** The `useCan('Pipeline_ForceRelease')` check gates the force-release section. Guest capabilities never include this permission.

6. **Force-release success updates the badge via the `PipelineLockStolen` broadcast** — no optimistic update. The dialog just closes on 200; the store update (and thus badge change) arrives via the SignalR event.

7. **Timer cleanup: `clearInterval` in `useEffect` teardown.** Leak risk across many rows — each own-run LockBadge starts its own interval. Return the cleanup function from `useEffect`. Also clear when `lock` becomes undefined (released) or `isOwn` flips to false.

8. **Tailwind inline per conventions.** Amber/blue colors match the existing badge palette from `UserIdentityBadge` and the current lock indicator in `WatchListTree`: `bg-amber-900/30 text-amber-400` for other-owner, `bg-blue-900/30 text-blue-400` for own-run.

9. **Reference the lock contract for all field names/casing.** DTO fields are camelCase in JSON: `pipelineId`, `ownerUserId`, `ownerDisplayName`, `ownerClientKind`, `acquiredUtc`, `expiresUtc`. Do not invent.

10. **409 body extraction:** The `apiFetch` wrapper returns structured errors. On status 409, parse `response.lock` as `PipelineLockDto`. Open `LockConflictModal` directly with it — no `GET /api/locks/{tag}` refetch.

11. **No refetch on conflict.** The DTO in the 409 response IS the conflict context. Open the modal directly with it.

12. **`AgentLockManager` is completely untouched.** This phase adds UI for pipeline locks, not agent locks. Do not import, modify, or reference `AgentLockManager`.

13. **Store pattern:** Follow `authStore.ts` — `create<T>((set, get) => ({ ... }))`. One store per domain. Use `getState()` for cross-store reads (e.g., `useAuthStore.getState().user?.userId` inside `isLockedByOther`).

14. **Component pattern:** `export default function ComponentName()` — one component per file, PascalCase filename. Tailwind utility classes inline. No CSS modules.

15. **Testing:** Vitest + @testing-library/react + jsdom. `vi.mock()` for store deps, `vi.useFakeTimers()` for interval tests, `renderHook()` for store selector tests.

---

## How to start a task in this phase

```
[SPEC]
- docs/rbac/04_UI_Mockup_Catalog.md Mockups 3, 4, 6, 9
- docs/rbac/05_Default_Mode_Design.md §5, §7
- docs/architecture/CURRENT_STATE.md — Pipeline Lock Coordination section
- docs/architecture/CONVENTIONS.md — React section

[CURRENT STATE]
- Phase 3a + 3b complete: LockRegistry, LockBroadcaster, PipelineLockDto,
  SignalR events, gRPC Aborted payload, REST 409, WPF lock UI all shipped
- useSignalR hook exists with indefinite reconnect and session rejoin
- lockStore does NOT yet exist
- WatchListTree has a placeholder lock indicator (amber span)
- DisabledTriggerButton uses useCan() for permission gating
- apiFetch handles 403 with toast; 409 not yet handled
- authStore exposes user.userId for identity comparison

[TASK]
Generate <path>

[CONSTRAINTS]
- isLockedByOther is derived (lockStore + authStore), never stored
- Reconnect resync via GET /api/locks in useSignalR reconnect path
- No polling timers — broadcast + full-resync-on-reconnect only
- Timer cleanup: clearInterval in useEffect return
- Tailwind inline; amber-900/30, blue-900/30 palette
- 409 body → modal directly, no refetch
- Force-release: no optimistic update, wait for PipelineLockStolen
- useCan('Pipeline_ForceRelease') gates force-release section
- Field names from PipelineLockDto contract — do not invent
- apiFetch wrapper for all API calls (correlation IDs, error handling)
- Zustand pattern: create<T>((set, get) => ({ ... }))
- Vitest + @testing-library/react for tests

[OUTPUT]
- The file content
- Nothing else
```

---

## Exit checklist (don't move to Phase 4 until all green)

- [ ] `lockStore` holds locks keyed by `pipelineId` with all 5 event actions + `setAll`
- [ ] `isLockedByOther(tag)` derives correctly against `authStore.user.userId`
- [ ] `LockEvents` subscribes to all 5 SignalR lock events on `/hubs/controller`
- [ ] `LockEvents` calls `GET /api/locks` on connect AND reconnect (full resync)
- [ ] No polling timer anywhere — broadcast + resync only
- [ ] `LockBadge` renders amber "Locked by {name} ({client})" for other-owner
- [ ] `LockBadge` renders blue "Your run · mm:ss" for own lock with 1s interval
- [ ] Timer `clearInterval` in `useEffect` cleanup (no leak on unmount/release)
- [ ] `LockBadge` integrated into `WatchListTree` per WatchItem row
- [ ] `DisabledTriggerButton` disabled with "locked by other" tooltip when lock held
- [ ] 409 from trigger path → `LockConflictModal` opens with DTO from response body
- [ ] No refetch — 409 body is the conflict context
- [ ] `LockConflictModal` shows owner card (initials, client chip, acquired, expiry countdown)
- [ ] Expiry countdown computed client-side from `expiresUtc`, cleanup on close
- [ ] Force-release section visible ONLY when `useCan('Pipeline_ForceRelease')`
- [ ] Guest never sees force-release section
- [ ] `ForceReleaseDialog` enforces reason >= 10 chars + affirmation checkbox
- [ ] Submit enabled only when BOTH conditions met
- [ ] Submit calls `POST /api/locks/{pipelineId}/force-release`; dialog closes on 200
- [ ] Badge updates via `PipelineLockStolen` broadcast — no optimistic update
- [ ] Default mode: "Locked by Default user (WPF)" renders from same DTO, no special-casing
- [ ] `lockStore.test.ts` passes (event reducers, setAll, isLockedByOther selector)
- [ ] `LockBadge.test.tsx` passes (both variants, timer cleanup)
- [ ] `ForceReleaseDialog.test.tsx` passes (gating matrix: length × checkbox)
- [ ] All Phase 0–3b tests still pass (regression)
