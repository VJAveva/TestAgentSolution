# Phase 3a Context Pack — Pipeline Lock Backend (LockRegistry, Handlers, gRPC Surface)

> Attach this file to every Copilot session while working on Phase 3a. Detach when Phase 3a ships and switch to `phase-3b-context.md`.

> Companion reading: `docs/rbac/03_Integration_With_Lock_Spec.md` §1.1 (two lock layers), §2–4 (reconciliation), §6 (scope); `docs/rbac/01_System_Design.md` §5 (pipeline state machine); `docs/rbac/05_Default_Mode_Design.md` §5 (locks in Default mode); `Pipeline_Lock_Coordination_Spec.md` §4.3–4.4 (model + lifecycle), §10 (failure scenarios); `docs/architecture/CURRENT_STATE.md` "Executor Adapter" section.

---

## Goal

Ship the in-process pipeline lock subsystem — domain types, `LockRegistry`, lock lifecycle handlers, gRPC surface, SignalR broadcast, and lock-aware trigger flow — BACKEND ONLY. After Phase 3a:

- `LockRegistry` (in-memory, singleton) lives ONLY in the WPF controller process
- Trigger flow acquires a pipeline lock (key = WatchItem `Tag`) before calling the executor
- Lock conflicts return gRPC `Aborted` with a serialized `PipelineLockDto` payload
- Same-owner re-acquire succeeds (same `UserId`, any `ClientKind`)
- Heartbeat extends TTL; sweeper expires stale locks and broadcasts release
- Force-release validates `Pipeline_ForceRelease` permission + reason length; audit-only persistence of reason
- Lock events broadcast via SignalR to all connected clients
- Default mode: locks acquired with owner = Default user; no conflicts possible from WPF; Web observers see the lock badge payload
- Standalone WebApi forwards lock operations via `ControllerProxyService` — it NEVER instantiates `LockRegistry` locally
- `RbacModeTransitionService` rewrites lock owners on mode switch

No UI work in this phase (Phase 3b = WPF lock UI, Phase 3c = Web lock UI).

---

## Where things land

| Concern | Path | Project |
|---|---|---|
| Domain types (PipelineLock, OwnerIdentity, LockKind, LockStatus, AcquireResult, LockEvent) | `Locking/` folder | `TestControllerGrpc.Core` |
| `ILockRegistry` interface | `Locking/ILockRegistry.cs` | `TestControllerGrpc.Core` |
| `LockRegistry` implementation | `Locking/LockRegistry.cs` | `TestControllerGrpc.Core` (or new `Infrastructure/` folder in Core — keep flat) |
| `LockExpirySweeper` hosted service | `Locking/LockExpirySweeper.cs` | `TestControllerGrpc.Core` |
| `PipelineLockDto` + mapper | `Contracts/PipelineLockDto.cs`, `Contracts/LockMapper.cs` | `TestController.Api` |
| `LockGrpcService` (List, Get, Release RPCs) | `Services/LockGrpcService.cs` | `TestController.Api` |
| Trigger handler modification (lock step) | `Services/PipelineService.cs` (modify) | `TestController.Api` |
| `ForceReleaseHandler` logic | `Services/PipelineService.cs` (add method) | `TestController.Api` |
| `LockBroadcaster` (SignalR events) | `Hubs/LockBroadcaster.cs` | `TestController.Api` |
| ExecutionHub lock event additions | `Hubs/ExecutionHub.cs` (modify) | `TestController.Api` |
| Heartbeat hub method | `Hubs/ExecutionHub.cs` (modify) | `TestController.Api` |
| Controller-host-only DI extension | `ControllerLockExtensions.cs` (NEW) | `TestControllerGrpc` (WPF host) |
| WebApi lock proxy endpoints | `Endpoints/LockEndpoints.cs` (NEW) or `ExecutionEndpoints.cs` (modify) | `TestController.WebApi` |
| `RbacModeTransitionService` lock rewrite | `SystemMode/RbacModeTransitionService.cs` (modify) | `TestController.Api` |
| Proto definition | `Protos/lock.proto` | `TestControllerGrpc.Core` |
| Unit tests | `Rbac/LockRegistryTests.cs`, `Rbac/ForceReleaseTests.cs` | `TestControllerGrpc.Tests` |
| Integration tests | `Rbac/LockIntegrationTests.cs` | `TestController.WebApi.Tests` |

---

## File targets (12 tasks, 4 blocks)

### Block A — Domain types (no dependencies)

| # | Path | What |
|---|---|---|
| 1 | `TestControllerGrpc.Core/Locking/PipelineLock.cs` | Record: `PipelineId` (string = Tag), `Owner` (OwnerIdentity), `Kind` (LockKind), `Status` (LockStatus), `AcquiredUtc`, `ExpiresUtc`, `LastHeartbeatUtc`. Immutable snapshot. |
| 2 | `TestControllerGrpc.Core/Locking/OwnerIdentity.cs` | Record: `UserId` (string), `DisplayName`, `ClientKind` (enum). Equality on `UserId` only (same-owner semantics). |
| 3 | `TestControllerGrpc.Core/Locking/LockKind.cs` | Enum: `Trigger`, `Retry`. |
| 4 | `TestControllerGrpc.Core/Locking/LockStatus.cs` | Enum: `Active`, `Released`, `Expired`, `ForceReleased`. |
| 5 | `TestControllerGrpc.Core/Locking/AcquireResult.cs` | Sealed record hierarchy: `AcquireResult.Success(PipelineLock)`, `AcquireResult.Conflict(PipelineLock existingLock)`, `AcquireResult.ReAcquired(PipelineLock)`. |
| 6 | `TestControllerGrpc.Core/Locking/ILockRegistry.cs` | Interface: `AcquireResult TryAcquire(string pipelineId, OwnerIdentity owner, LockKind kind)`, `bool TryRelease(string pipelineId, OwnerIdentity owner)`, `PipelineLock? Get(string pipelineId)`, `IReadOnlyList<PipelineLock> GetAll()`, `void ForceRelease(string pipelineId)`, `bool Heartbeat(string pipelineId, OwnerIdentity owner)`, `event Action<LockEvent> OnLockEvent`, `void RewriteOwners(OwnerIdentity newOwner)`. |
| 7 | `TestControllerGrpc.Core/Locking/LockEvent.cs` | Record: `EventKind` (Acquired/Released/Expired/ForceReleased/Rewritten), `PipelineLock Lock`, `OwnerIdentity? PriorOwner` (for force-release + rewrite). |

### Block B — Registry implementation + sweeper (depends on Block A)

| # | Path | What |
|---|---|---|
| 8 | `TestControllerGrpc.Core/Locking/LockRegistry.cs` | Singleton, `ConcurrentDictionary<string, PipelineLock>`. `TryAcquire`: if key absent or expired → create + emit Acquired; if same owner → update ExpiresUtc + emit ReAcquired (success); else → return Conflict. Default TTL 30s from config `Locks:HeartbeatTimeoutSeconds`. `Heartbeat`: extends `ExpiresUtc` by TTL. `ForceRelease`: removes entry, emits ForceReleased with `PriorOwner`. `RewriteOwners`: iterates all entries, replaces `Owner`, emits Rewritten per entry. Thread-safe via `ConcurrentDictionary` + lock-per-key for atomic transitions. |
| 9 | `TestControllerGrpc.Core/Locking/LockExpirySweeper.cs` | `IHostedService`, 5-second timer. Iterates snapshot of registry, finds entries where `ExpiresUtc < UtcNow`, calls `ForceRelease` (emits Expired event via the same path). Logs via `IAppLogger("LockSweeper", ...)`. |

### Block C — API surface + handlers (depends on A + B)

| # | Path | What |
|---|---|---|
| 10 | `TestController.Api/Contracts/PipelineLockDto.cs` + `Contracts/LockMapper.cs` | DTO: `PipelineId`, `OwnerDisplayName`, `OwnerClientKind`, `AcquiredUtc`, `ExpiresUtc`. Mapper: `PipelineLock → PipelineLockDto`. |
| 11 | `TestController.Api/Services/LockGrpcService.cs` | gRPC service: `ListLocks`, `GetLock(pipelineId)`, `ReleaseLock(pipelineId)`. Uses `ILockRegistry`. Release validates caller is owner (same `UserId`) or has `Pipeline_ForceRelease`. |
| 12 | `TestController.Api/Services/PipelineService.cs` (modify) | In `TriggerPipelineAsync`: after authz check, call `_lockRegistry.TryAcquire(...)`. If `Conflict` → throw `RpcException(StatusCode.Aborted, ...)` with `PipelineLockDto` serialized in trailers. Add `ForceReleaseAsync` method: validates `Pipeline_ForceRelease` permission, validates reason (1–500 chars), calls `_lockRegistry.ForceRelease(...)`, writes audit (prior owner + reason in payload, NOT in AppLogger plaintext). |

### Block D — Broadcast, DI, mode-switch, proxy, tests (depends on A + B + C)

| # | Path | What |
|---|---|---|
| 13 | `TestController.Api/Hubs/LockBroadcaster.cs` | Subscribes to `ILockRegistry.OnLockEvent`. Maps event → SignalR hub method: `PipelineLockAcquired`, `PipelineLockReleased`, `PipelineLockExpired`, `PipelineLockForceReleased`, `PipelineLockRewritten`. Payload = `PipelineLockDto` + `priorOwnerDisplayName` where applicable. |
| 14 | `TestController.Api/Hubs/ExecutionHub.cs` (modify) | Add `HeartbeatLockAsync(string pipelineId)` client→server method. Calls `_lockRegistry.Heartbeat(pipelineId, caller)`. |
| 15 | `TestControllerGrpc/ControllerLockExtensions.cs` (NEW) | Extension: `AddControllerLockServices(this IServiceCollection)`. Registers `LockRegistry` as singleton `ILockRegistry`, `LockExpirySweeper` as hosted service, `LockBroadcaster` as singleton. Called ONLY from `App.xaml.cs` (WPF host). NOT from `AddRbacFeature()`. |
| 16 | `TestController.Api/SystemMode/RbacModeTransitionService.cs` (modify) | In `SwitchToSecuredAsync` + `SwitchToDefaultAsync`: call `_lockRegistry.RewriteOwners(newOwner)` where `newOwner` is the new Admin (Secured) or `DefaultUser.ForClient(Wpf)` (Default). |
| 17 | `TestController.WebApi/Endpoints/LockEndpoints.cs` (NEW) | REST proxy endpoints: `GET /api/locks`, `GET /api/locks/{pipelineId}`, `DELETE /api/locks/{pipelineId}`, `POST /api/locks/{pipelineId}/force-release`. Forwards via `ControllerProxyService`. Returns 409 + JSON `PipelineLockDto` on conflict (mirrors gRPC Aborted). |
| 18 | `TestControllerGrpc.Tests/Rbac/LockRegistryTests.cs` | Unit tests: acquire, re-acquire same owner, conflict different owner, release, heartbeat extends TTL, expiry after timeout, force-release, rewrite owners. |
| 19 | `TestController.WebApi.Tests/Rbac/LockIntegrationTests.cs` | Integration: trigger returns 409 on conflict with full DTO; force-release requires permission; released on completion; sweeper fires after timeout. |

---

## Sequence rules

- **Phases 0 + 0.5 + 1a + 1b + 2a + 2b + 2c must be complete** — authz, sessions, trigger flow, pipeline state.
- **Block A first** — pure domain types, no dependencies.
- **Block B depends on A** — registry uses domain types.
- **Block C depends on A + B** — gRPC surface calls registry.
- **Block D depends on all** — wiring, broadcast, tests cover the full stack.
- **Task 15 (ControllerLockExtensions) and Task 17 (WebApi proxy) can run in parallel** once Block C exists.

---

## Watch out for

1. **`LockRegistry` is in-memory and lives ONLY in the controller process (WPF host).** The standalone WebApi NEVER instantiates it — it forwards lock operations via `ControllerProxyService`. Do NOT register `LockRegistry` in `AddRbacFeature()`; register it in a controller-host-only extension (`ControllerLockExtensions.cs` in the `TestControllerGrpc` project). The WebApi project's DI has no `ILockRegistry` binding — its endpoints are pure HTTP proxies to the controller.

2. **`AgentLockManager` is a DIFFERENT layer (locks agent machines per session).** Do NOT modify it, do NOT merge concepts. Both locks fire on one trigger: pipeline lock first (user-level, blocks before execution starts), then `AgentLockManager` (existing flow, inside the executor). The two have different keys (`WatchItem.Tag` vs agent machine names), different lifecycles, different owners. They coexist; Phase 3a does not touch `AgentLockManager` at all.

3. **Lock key = WatchItem `Tag`** (the Phase 2 identity decision). The `PipelineLock.PipelineId` field stores the `Tag` value. This must match what `PipelineService.TriggerPipelineAsync` receives as `request.PipelineId`. Consistency with Phase 2's pipeline identity model is critical.

4. **Same-owner re-acquire succeeds (same `UserId`, any client).** `OwnerIdentity` equality is on `UserId` only — `ClientKind` and `DisplayName` are informational. This is required for Default mode where two WPF windows share the Default user (`00000000-0000-0000-0000-000000000001`). If the same user triggers from WPF and then retries from Web (Secured mode), the lock re-acquires without conflict.

5. **Lock release must be wired to ALL terminal paths: completion, cancel, failure, AND execution-session crash.** Completion/cancel/failure: hook into the existing executor flow (see `CURRENT_STATE.md` "Executor Adapter" — events fire on `NodeFailed`, `NodeProgress`; when the pipeline run reaches terminal state, release the lock). Crash: the `LockExpirySweeper` catches this via TTL — if no heartbeat arrives within 30s, the lock expires and is broadcast as released. ALL five paths (success, failure, cancel, manual release, TTL expiry) must end in `LockRegistry.TryRelease` or `ForceRelease` + a `PipelineLockReleased`/`Expired` event.

6. **Conflict response carries a full `PipelineLockDto`** (owner `displayName`, `clientKind`, `acquiredUtc`, `expiresUtc`) so Phase 3b/3c can render Mockup 6 without a second round-trip. gRPC: `StatusCode.Aborted` + DTO serialized as a trailing metadata entry (`lock-conflict-bin` key, protobuf or JSON bytes). REST (WebApi proxy): HTTP 409 + JSON body `{ "error": "pipeline-locked", "lock": { ... } }`.

7. **Force-release: server re-validates reason length (1–500 chars) AND `Pipeline_ForceRelease` permission** even though the UI gates it client-side. Audit payload includes the reason text and prior owner identity — but NEVER log the reason in plaintext via `AppLogger` (it could contain sensitive text). Write to the `AuditEntries` table only (via `IAuditWriter` fire-and-forget). The `_logger.Info("Lock", "Force-release on {pipelineId} by {userId}")` is acceptable; the reason text stays out of AppLogger.

8. **Mode switch rewrites lock owners** (per `05_Default_Mode_Design.md` §8.2 and §9.1). `RbacModeTransitionService` gains a `RewriteLockOwners` step: call `_lockRegistry.RewriteOwners(newOwner)`. This emits `LockEvent(Rewritten, ...)` per entry, which `LockBroadcaster` translates to `PipelineLockRewritten` SignalR events. Clients update their badge text from "Default user" to real username (or vice versa) in real-time.

9. **Default mode: locks still acquired** (owner = Default user via `DefaultUser.ForClient(ClientKind.Wpf)`) so the Web Observer sees "Locked by Default user (WPF)". No conflict dialogs appear in Default mode because WPF is the sole writer. Multi-WPF-window case: second instance's trigger re-acquires (same UserId → success per rule #4). The `LockBroadcaster` still fires events — Web Observer uses them to show/hide lock badges.

10. **Audit fire-and-forget rule still applies to lock events.** All lock audit writes go through `IAuditWriter.Enqueue(...)`, never awaited in the request path. Lock acquisition audit is logged with `ActionName = "Pipeline_LockAcquire"`; release with `"Pipeline_LockRelease"`; force-release with `"Pipeline_ForceRelease"` (carries reason in a `Payload` JSON field on the `AuditEntry`).

11. **`LockRegistry` is NOT persisted to SQLite.** It is purely in-memory. On controller restart, all locks are lost — this is by design (per Lock spec §4.7). In-flight runs will re-trigger the `AgentLockManager` on the agent side; the pipeline lock layer simply starts fresh. The sweeper handles the edge case of a client holding a stale lock reference after restart (heartbeat will fail, client sees lock expired).

12. **Proto definition (`lock.proto`) declares the gRPC surface** for direct gRPC clients (WPF). The WebApi does NOT host this gRPC service — it exposes REST equivalents via `LockEndpoints.cs` that proxy to the controller. Keep the proto in `TestControllerGrpc.Core/Protos/` alongside existing `test_agent.proto`.

---

## How to start a task in this phase

```
[SPEC]
- docs/rbac/03_Integration_With_Lock_Spec.md §1.1, §3, §4
- docs/rbac/01_System_Design.md §5 — Pipeline State Machine
- docs/rbac/05_Default_Mode_Design.md §5 — Lock Model in Default Mode
- Pipeline_Lock_Coordination_Spec.md §4.3–4.4 (model types + lifecycle)
- docs/architecture/CURRENT_STATE.md — Executor Adapter, RBAC Phase status

[CURRENT STATE]
- Phase 0 + 0.5 + 1a + 1b + 2a + 2b + 2c complete
- PipelineService.TriggerPipelineAsync exists (Phase 2a) — needs lock step
- RbacModeTransitionService exists — needs RewriteLockOwners step
- ControllerProxyService forwards trigger calls from WebApi → controller
- LockRegistry does NOT exist yet (this phase creates it)
- AgentLockManager exists — DO NOT TOUCH

[TASK]
Generate <path>

[CONSTRAINTS]
- LockRegistry registered in TestControllerGrpc host ONLY (ControllerLockExtensions)
- NOT in AddRbacFeature() — WebApi has no ILockRegistry binding
- OwnerIdentity equality on UserId only (same-owner re-acquire)
- Lock key = WatchItem Tag string
- gRPC conflict = StatusCode.Aborted + PipelineLockDto in trailers
- REST conflict = 409 + JSON body
- Force-release reason: audit table only, NEVER in AppLogger plaintext
- All terminal paths release the lock (completion, cancel, failure, crash/TTL)
- Sweeper 5-second cadence, 30s default TTL
- Audit via IAuditWriter.Enqueue (fire-and-forget, not awaited)
- IAppLogger for operational logs, not ILogger<T>
- Singleton lifetime for LockRegistry, LockBroadcaster
- LockExpirySweeper is IHostedService

[OUTPUT]
- The file content
- Nothing else
```

---

## Exit checklist (don't move to Phase 3b until all green)

- [ ] `LockRegistry` singleton registered only in WPF host (`ControllerLockExtensions`)
- [ ] WebApi `LockEndpoints` proxy to controller — no local `ILockRegistry` resolution
- [ ] `TryAcquire` returns `Conflict` with full `PipelineLock` when different user holds lock
- [ ] `TryAcquire` returns `ReAcquired` when same `UserId` (any `ClientKind`) re-acquires
- [ ] `Heartbeat` extends `ExpiresUtc` by configured TTL
- [ ] `LockExpirySweeper` expires stale locks within 5-second sweep + broadcasts `Expired`
- [ ] gRPC trigger on locked pipeline → `StatusCode.Aborted` + `PipelineLockDto` in metadata
- [ ] REST trigger (via WebApi proxy) on locked pipeline → HTTP 409 + JSON `PipelineLockDto`
- [ ] `ForceRelease` requires `Pipeline_ForceRelease` permission (verified in test)
- [ ] `ForceRelease` validates reason 1–500 chars server-side
- [ ] Force-release audit entry contains reason + prior owner; AppLogger does NOT log reason text
- [ ] Lock released on run completion (Passed state)
- [ ] Lock released on run cancellation (Cancelled state)
- [ ] Lock released on run failure (Failed state)
- [ ] Lock released by sweeper on heartbeat timeout (crash scenario)
- [ ] `RewriteOwners` called in `SwitchToSecuredAsync` and `SwitchToDefaultAsync`
- [ ] `PipelineLockRewritten` SignalR event broadcast after rewrite
- [ ] Default mode: trigger acquires lock with Default user as owner (no conflict)
- [ ] Default mode: two WPF instances → re-acquire succeeds (same UserId)
- [ ] `AgentLockManager` completely untouched — no imports, no modifications
- [ ] All Phase 0–2c tests still pass (regression)
- [ ] `LockRegistryTests` cover: acquire, conflict, re-acquire, release, heartbeat, expiry, force-release, rewrite
- [ ] `LockIntegrationTests` cover: trigger conflict 409, force-release authz, sweeper expiry
