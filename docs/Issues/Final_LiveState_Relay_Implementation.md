# Final implementation — live run state across web & WPF
### The relay delta (reuse, don't rebuild)

---

## The decision (simple version)

**The browser talks only to the WebApi. The WebApi listens to the Controller and forwards the live updates to the browser.**

Why this way: it keeps the Controller's internal port private, and it still works when users go remote or you move to Azure — the browser only ever needs the WebApi.

So: **do not point browsers at the Controller (5200) directly.** Build one small bridge inside the WebApi instead.

---

## Already done — do NOT rebuild

- Server-side lock enforcement + release on completion. ✓
- Owner / attribution on lock events, and the WPF surfacing. ✓

Implement **only the four items below.**

---

## The delta — 4 small items

### 1. The bridge — `ControllerEventRelayService` (WebApi `BackgroundService`)

- Model it on the existing **`AgentEventRelayService.cs`** (same pattern, proven).
- Open a `HubConnection` to the Controller hub at `ControllerProxyService.BaseUrl` → `http://localhost:5200/hubs/controller`.
- Subscribe to: `PipelineLockAcquired` / `PipelineLockReleased` / `PipelineLockExpired`, `ExecutionStarted` / `ExecutionCompleted` / `ExecutionCancelled`, `ActionProgress`, `LogEntry`.
- Re-broadcast each, **unchanged**, to the WebApi's `ControllerHub` via `IHubContext<ControllerHub>`.
- Register it **only when `ControllerProxyService.IsConfigured`** (mirror the forwarding middleware).
- This single bridge is what ends "request-and-forget." No new events, no parallel path.

### 2. Resync snapshot — so reconnect isn't stale

- Make the WebApi's `GET /api/locks` (and run-state) **proxy to the Controller**. It currently returns empty when `_lockService is null` (`LocksController.cs:34–38`).
- On (re)connect, the web client fetches this snapshot, so it never shows stale or empty state.

### 3. Owner on run events — attribution

- Lock events already carry display name + kind. Extend the **run events** and **`AgentBusyChanged`** to carry `owner { userId, displayName, role }`.
- Minimal DTO change (`PipelineLockDto` / run DTOs). The OS execution identity stays the Controller; `owner` is the user who triggered.

### 4. Visuals — reuse what already exists (no new design)

- Wire the web `lockStore` + `LockEvents.ts` to the relayed events, and bind the WatchList tree / Execution Dashboard / Agent Fleet to lock + run + owner.
- **Reuse the existing lock badge and disabled-trigger patterns** already in the codebase. Do not invent new visuals, and do not wait on screenshots — refined visuals can be dropped in later.

---

## The topology (the one thing to get right)

```
browser  -->  WebApi (port 81)   /hubs/controller
                    ^
                    |   ControllerEventRelayService  (HubConnection)
                    |
               Controller (port 5200)   /hubs/controller
```

Browser → WebApi hub (same-origin `/hubs`). WebApi → Controller hub via the relay. **Never browser → 5200.**

---

## Constraints

- **Reuse, don't rebuild** — only the four items above.
- **No new events** — the relay forwards the existing ones unchanged.
- **No parallel path** — reuse `ControllerHub` / `IHubContext` / the existing broadcasters.
- **Same-origin `/hubs`**; reconnect-resync via the snapshot from item 2.

---

## Acceptance — how to know it works

1. Trigger a pipeline from the web → within ~1–2s the tree + Execution Dashboard show it running, on **both** the web and the WPF — no refresh.
2. While running: the trigger button is disabled and shows "locked, by \<user>"; a second user (including an admin) is blocked.
3. The triggering user shows in the tree, the Execution Dashboard, and the Agent Fleet.
4. On completion (or a controller restart), the lock clears on all clients — no stale "locked" left behind.
5. Refresh the browser mid-run → it resyncs to the correct state (the snapshot), not empty.

---

UX guidance for Copilot (placement only — reuse existing styles)

No new designs. The relay delivers new info (a live run + who triggered it). Copilot just shows it in the obvious place on each surface, using patterns already in the app, consistently.

Where the triggering user appears — show <display name> (role optional), the same way on all three surfaces:


WatchList tree (web + WPF): on a running / locked node, reuse the existing lock badge → "Locked · <user>", and disable the trigger control with the existing disabled style. Feed the badge the owner from the relayed event.
Execution Dashboard (web + WPF): on an active run, show "started by <user>" in the existing run header / row style, with progress shown the way WPF runs already show it.
Agent Fleet (web + WPF): on a busy agent, reuse the existing agent card to show it busy + the running pipeline + <user>. Do not invent a new card.


Consistency: use the same attribution wording everywhere ("<user>") and the same existing badge / colour for "locked / running" on every surface, so it reads identically across web and WPF.

Not in this pass: the detailed, refined visuals (your screenshots) are a later polish. This pass only makes the existing visuals show the new live data — it does not need new designs.

*One bridge, three small additions. Browser → WebApi → Controller — keep that line and remote access, Azure, and security all stay easy.*
