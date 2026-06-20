# Pipeline Run — Live State, Lock Enforcement & Attribution
### One combined change: Enforce · Relay · Attribute · Surface

---

## Goal (read first)

After Step 2, a pipeline triggered from the web executes **on the Controller** — but nothing pushes that state back out, so it behaves like request-and-forget. This change makes a run **visible, locked, and attributed across BOTH the WebClient and the WPF UI, live.** It is one cohesive body of work with four parts:

1. **Enforce** — a running pipeline is locked; a second trigger (from anyone, incl. admin) is blocked.
2. **Relay** — the Controller pushes run + lock + owner events to both surfaces (not poll-only).
3. **Attribute** — every run/lock event carries the triggering user; "by \<user>" shows everywhere.
4. **Surface** — the visual states, driven by the UX specs in Part 4 (do not guess these).

## Symptoms being fixed (observed)

- Web and WPF show **no sign a run is executing** (pipeline view empty, treeview shows nothing).
- The **trigger button stays enabled** during a run, on both surfaces.
- **No surface shows which user triggered** (treeview, Execution Dashboard, Agent Fleet).
- A **second user — including an admin — can re-trigger** a running pipeline.
- The **lock never clears** after the run completes.

---

## Step 0 — diagnose and report BEFORE changing anything

The locking/notification subsystem has known gaps; confirm the wiring, then propose the plan, then implement. Report findings first.

1. **Lock authority.** Is `ILockRegistry` / `LockRegistry` a per-PROCESS singleton? Does `TestController.WebApi` have its own instance separate from the Controller's? (If separate, locks don't cross hosts.) Show the DI registration in each host.
2. **Execution location (post Step-2).** Confirm a web trigger now executes on the Controller (not in `StandalonePipelineExecutor` in the WebApi). Quote the path: `ExecutionEndpoints` → forward → Controller executor.
3. **Lock check on trigger.** Does the trigger/execute path call `TryAcquire` and **reject with a conflict** when the pipeline is held by another owner? Or acquire/overwrite regardless? Does an Administrator **bypass** the lock (the same `if role==Admin` that bypasses assignment)? (Admin must NOT bypass the lock.)
4. **Release + expiry.** Confirm `LockRegistry.TryRelease` (LockRegistry.cs:69) has zero production callers and there is no expiry sweeper. Where does `OnExecutionCompleted` run, and does it touch the lock today?
5. **Event delivery — WPF.** When the Controller runs a (web-origin) pipeline, does the WPF UI receive the execution + lock events it binds to (`LockStateService`, the tree / dashboard / fleet view-models), or are those events not raised for web-origin runs? (host-local notifier question)
6. **Event delivery — web.** Where does the WebClient's SignalR connect — the WebApi hub (81) or the Controller hub (5200)? When the Controller emits events, do they reach web clients today, or only same-host clients?

**Report a verdict:** precisely why nothing surfaces during a run, why the button stays enabled, why attribution is missing, and why a second trigger is allowed — which of (1)–(6) is the cause (likely several).

---

## Part 1 — Enforce (single lock authority)  [server logic]

- **One lock source of truth across hosts.** A lock taken on either surface must block the other. If the WebApi ever holds its own registry, route lock acquire/release/query through the Controller (same single-owner pattern as the DB), so there is exactly one authority.
- **Block a second trigger for EVERYONE, incl. Administrator.** If the pipeline is locked by another owner, reject the trigger with a conflict (409) and a clear reason ("running by \<user>"). Do not start a second concurrent run.
- **Admin/Senior-Manager takeover = Cancel or Force-release, then trigger** — NOT a silent re-trigger. (`Pipeline_ForceRelease` = Administrator **and** Senior Manager per the permission matrix.)
- **Release on completion.** In the execution-completed path, call `LockRegistry.TryRelease` and broadcast `PipelineLockReleased`.
- **Expiry sweeper.** A background timer releases past-TTL locks (crashed/abandoned runs) and broadcasts `PipelineLockExpired`.
- **Owner re-trigger of their OWN completed run stays allowed.** The block is only for a DIFFERENT user while a run is ACTIVE.

---

## Part 2 — Relay / push (`ControllerEventRelayService`)  [the deferred plumbing]

Push Controller events to **both** delivery targets, live:

- **WPF UI (in-process):** ensure web-origin runs raise the same events the WPF view-models already bind to (`LockStateService` + the tree / Execution Dashboard / Agent Fleet VMs). If they aren't raised for web-origin runs today, raise them.
- **Web clients (cross-process):** add `ControllerEventRelayService` that subscribes to the Controller's events and re-broadcasts them on the WebApi's SignalR hub to web clients (`lockStore` + a run-state store). This is the bridge that ends "request-and-forget."

**Event contract** (each event carries the owner — see Part 3):

```
RunStarted        { runId, pipeline, owner, startedAt }
RunProgress       { runId, currentStep, status, percent? }
RunCompleted      { runId, result(passed|failed|cancelled), finishedAt }
PipelineLockAcquired { tag, runId, owner, acquiredAt }
PipelineLockReleased { tag, runId }
PipelineLockExpired  { tag, runId }
AgentBusyChanged  { agentId, busy, runId, owner }   // for the Fleet view
```

- Reuse `IRealtimeNotifier` / `SignalRNotifier` / the existing broadcasters — do not build a parallel notification path.
- **Reconnect-resync:** on (re)connect, a client fetches current run + lock state so it is never left stale.

---

## Part 3 — Attribute (owner on everything)

- **Carry the triggering user** on every run and lock event: `owner { userId, displayName, role }`.
- **OS execution identity = the Controller** (it has the local tools/creds); **logical owner = the web/WPF user** who triggered. Keep these distinct — execution runs as the Controller, but the lock owner, the audit record, and the displayed attribution are the user.
- **Show "by \<user>" on every surface, both clients:** the WatchList tree node, the Execution Dashboard header/row, and the Agent Fleet card for the agent running it.

---

## Part 4 — Surface (visual states — YOUR UX GOES HERE)

The plumbing above feeds these visuals. **Do not guess the visuals** — implement them from the specs below. Drop your screenshots / descriptions into each block; until a block is filled, leave that visual to spec rather than inventing it.

**States to represent:** `triggerable` · `running` · `locked (by another)` · `trigger disabled` · `admin-disabled`.

> **[[ YOUR UX — WatchList tree (web + WPF) ]]**
> How a node looks when it is running / locked-by-user (badge text, colour, the "by \<user>" placement), and how the trigger control looks disabled. Paste screenshot/description.

> **[[ YOUR UX — Execution Dashboard (web + WPF) ]]**
> How a live run appears (progress, current step, "started by \<user>"), and the Cancel / Force-release controls. Paste screenshot/description.

> **[[ YOUR UX — Agent Fleet (web + WPF) ]]**
> How a busy agent looks (busy vs available, the running pipeline, "by \<user>"). Paste screenshot/description.

When a block is filled, bind that surface to the events from Part 2 and the owner from Part 3. Keep the four states visually distinct from each other and from the node-type pills (EVT/SEQ/RUN/RMT…). No strikethrough.

---

## Constraints / conventions

- **Server enforces; clients reflect.** The 409/conflict and the lock state are authoritative; UI is convenience.
- Keep admin **Cancel / Force-release** working; do not exempt admin from the lock.
- **Surgical**; reuse `LockRegistry` / `LockService` / `AgentLockManager` / `SignalRNotifier` / the broadcasters and the existing hubs. No parallel locking or notification path.
- Do **not** change the WPF execution path itself (it works); this adds enforcement + event delivery + attribution + visuals.
- CommunityToolkit.Mvvm + DI (WPF); Zustand + `useCan` + `lockStore` (web). Reconnect-resync everywhere.
- Permissions unchanged: Admin & Senior Manager trigger; this changes state visibility + lock behaviour, not authorization.

---

## Acceptance — how to know it works

1. **Live feedback:** trigger a pipeline from the WebClient → within ~1–2s the tree shows it running and the Execution Dashboard shows progress, **on both** the web and the WPF window — no refresh.
2. **Lock + button:** while it runs, the trigger control is disabled and shows "Locked, by \<user>" on both surfaces; a second user (incl. admin) is blocked with "running by \<user>".
3. **Attribution:** the triggering user is shown in the treeview, the Execution Dashboard, and the Agent Fleet (the busy agent), on both surfaces.
4. **Takeover:** admin/Senior-Manager can Cancel or Force-release; after that the pipeline is triggerable again for everyone.
5. **Release:** when the run completes (or the controller is killed mid-run), the lock clears on all clients within the TTL — no stale "Locked" left behind.

---

*Parts 1–3 are well-defined plumbing/logic; Part 4 is yours. Fill the UX blocks, and the events + owner already wired will light them up.*
