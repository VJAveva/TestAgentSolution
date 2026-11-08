# Build prompt — single-run pipeline lock + observable broadcast

> **Supersedes** `PipelineLock-CrossHost-Authority-Broadcast.md`.
>
> **Rule:** ONE active run per pipeline. While a run is active, **every** new trigger for that pipeline is refused — for everyone, every role, the owner and the Administrator included. Privilege does not let you start a second run; it lets you **cancel**.
> **Re-run = cancel (stops + frees the lock) → trigger again.**
> A lock gates **commands, never the event stream** — watching is always allowed.

---

## Outcome

Any user with access triggers `P` on either client → `P` is locked (`owner`, `ClientKind`). While `P` is active, **no** trigger for `P` succeeds (any user, any role). Every connected client — desktop and browser — sees the `Locked · <owner>` badge and `P`'s live transitions. To re-run, a permitted user cancels `P` (which stops it and frees the lock), then triggers it fresh.

---

## A. Single lock authority (the Controller owns it)

- The Controller's `TestControllerGrpc.Locking.LockRegistry` is **the** authority — it already owns the DB on `5200`.
- WPF trigger path uses it directly; WebApi trigger path consults it over gRPC and registers its lock there too. No per-process registry is treated as authoritative.
- **reconcile:** `LockRegistry` API, `ExecutionEndpoints` (WebApi), the gRPC service the WebApi proxies to.

## B. Lifecycle + the single-run gate

1. **ACQUIRE** — at the trigger command/endpoint, before `ExecuteEventTrackedAsync`:
   `TryAcquire(pipelineId, owner{DisplayName, ClientKind}, token, ttl)`.
2. **THE GATE IS "is there an active lock for this pipeline?"** — **not** "locked by another user." If a lock exists for `P` (any owner, the caller included) → refuse with `409 Conflict`. **No role bypass** — Administrator and Senior Manager are blocked from a second concurrent run exactly like everyone else.
3. **RELEASE — only after the run has fully stopped** (completed, or cancelled *and* torn down). REPLACE the tracked `finally` in `PipelineExecutorBase` (`ExecuteEventTrackedAsync` / `ExecuteGroupTrackedAsync` / `ExecuteSingleActionTrackedAsync`): alongside `_sessionManager.CompleteSession(session.SessionId)`, ADD `LockRegistry.TryRelease(watchItemTag, token)`. `watchItemTag == PipelineId`. Release **succeeds only if it presents the current lock token** (gives `TryRelease` its first caller).
4. **CANCEL** — stops the active run, **awaits teardown, then releases** the lock (stop + release are one operation, in that order). Allowed for: anyone with `Can(user, Trigger/Cancel, pipeline)` (owner + assigned users) **or** Senior Manager **or** Administrator. Reuse the existing `Can(...)`; do not add a permission.
5. **RETRIGGER** = a normal trigger, after cancel has released the lock.
6. **SWEEPER + HEARTBEAT** — a live run **renews** its lock periodically (heartbeat), so the TTL need not match run length; a dead run's lock expires within one interval; a Controller-side background sweeper clears expired locks and emits `PipelineLockExpired`.
7. **FORCE-RELEASE** — janitor-only safety for a **stuck lock with no live run** (Administrator + Senior Manager). It **never** permits a concurrent second run; if a run is live, the path is Cancel, not force-release. (Mostly superseded by the sweeper/heartbeat.)

## C. Cross-host broadcast (the read-transparency)

- Transition events already exist — `NodeProgress`, `NodeFailed`, `LogEntry` (`PipelineLogEntry`), `ActionExecutionResult`, `ExecutionSessionManager` / `session.TrackAgentAction`. **Do not invent new events.**
- Change **delivery scope** so they are not host-local: the Controller emits lock events (`PipelineLockAcquired` / `…Released` / `…Expired`, payload = `PipelineLockDto` via `LockMapper.ToDto`) **and** the transition events to **all** connected clients. The WebApi relays the Controller's `ControllerHub` stream to WebClient SignalR connections.
- **No ownership check anywhere in the broadcast path** — the stream is identical for owner and observers.
- **reconcile:** `IRealtimeNotifier` / `SignalRNotifier`, `ControllerHub`, the WebApi↔Controller SignalR relay. **Topology fork:** if the WebClient connects directly to the Controller's hub, drop the relay and broadcast to all; if it terminates at a WebApi hub, the relay is required.

## D. Client consumption (WPF + WebClient)

- Hold a lock lookup keyed by `PipelineId`: `{ ownerDisplayName, clientKind, acquiredUtc, expiresUtc }`; selector `hasActiveLock(pipelineId)`.
- SignalR handlers: `PipelineLockAcquired`/`…Updated` → upsert; `…Released`/`…Expired` → remove.
- Transition events update tree / fleet / dashboard for **all** pipelines, locked ones included.
- Render: `Locked · {owner} ({kind})` badge on tree node, fleet header, dashboard header; **disable all trigger affordances whenever `hasActiveLock` is true** (no per-role exception). Show **Cancel** to permitted users (owner/assigned + Sr Mgr + Admin). Show **Force-release** only to Administrator / Senior Manager.
- **reconcile:** the WPF node/fleet/dashboard VMs + templates; the React Zustand store + three views + SignalR client; the current-user + role + `Can(...)` accessors.

---

## Impediments — mitigations (built in above)

| Impediment | Mitigation |
|---|---|
| A run crashes / process dies → lock never released → **nobody** can trigger (admins blocked too) | TTL + heartbeat (live run renews, dead run expires) + sweeper; manual admin force-release as last resort |
| Controller restarts mid-run → in-memory lock lost → pipeline looks free while agents busy → phantom double-run | Persist locks in the DB (Controller owns it); reconcile against running sessions on startup; sweep orphans |
| Cancel → immediate retrigger while old run still tearing down → two runs overlap on the same agents | Release the lock only **after** full teardown, not at cancel-request time |
| A late cancel/release lands on a **new** run that reused the pipeline | Lock token per acquisition; release succeeds only with the current token |
| Notification rewiring double-announces or drops events | Single delivery path + de-dup by event id; smoke-test desktop + browser open together |

---

## Acceptance

- Any permitted user triggers `P` → `P` locked (`owner`, `ClientKind`).
- While `P` is active, **every** trigger for `P` — any user, any role, owner included — returns `409`.
- All connected clients show `P`'s badge **and** live transitions; watching is never blocked.
- Cancel (owner/assigned + Sr Mgr + Admin) stops `P`, waits for teardown, releases the lock → `P` retriggerable.
- Crash / stuck lock with no live run → auto-cleared by TTL/sweeper, or admin force-release.
- Execute All → each `Pi` independently single-run and independently observable.

## Files Copilot must reconcile (not seen)

- `LockRegistry` (`TryAcquire`/`TryRelease` signatures, token + TTL), persistence layer
- WPF trigger command; WebApi `ExecutionEndpoints` (acquire + `409`)
- `IRealtimeNotifier` / `SignalRNotifier` + `ControllerHub`; WebApi↔Controller SignalR relay
- WPF node/fleet/dashboard VMs + templates; React Zustand store + three views + SignalR client
- current-user + role + `Can(user, Trigger/Cancel, pipeline)` accessors
