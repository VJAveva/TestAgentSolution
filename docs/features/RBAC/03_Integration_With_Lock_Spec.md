# 03 — Integration with the Pipeline Lock Coordination Spec

> **Purpose**: explain exactly how `Pipeline_Lock_Coordination_Spec.md` folds into the RBAC delivery plan as Phase 3, what to keep from that spec, what to adjust, and how the two pieces of work fit together cleanly.

> **Prerequisite**: read `00_Master_Plan.md`, `01_System_Design.md`, `02_Implementation_Roadmap.md`, and the existing `Pipeline_Lock_Coordination_Spec.md`.

---

## 1. The Short Version

The Lock spec was written **before** the RBAC SRS was published. It made reasonable assumptions about identity and authorization that turn out to be partially built and partially defined in the RBAC work. Now that we have both, the right move is:

- **Keep** the Lock spec's core design — `LockRegistry`, lock lifecycle, SignalR events, conflict handling, UX patterns.
- **Replace** the Lock spec's identity stubs with the real identity layer built in Phase 0.
- **Replace** the Lock spec's hand-rolled capability check with a `Pipeline_ForceRelease` permission in the catalog.
- **Defer** the Lock work from "freestanding feature" to "Phase 3 of the RBAC delivery".

No code from the Lock spec is wasted. The interfaces line up almost exactly with what Phase 0 produces; the changes are surgical, not structural.

### 1.1 Important — there are TWO lock layers

The codebase audit (`docs/architecture/CURRENT_STATE.md`) revealed an **existing `AgentLockManager`**. This is a different layer of locking and must not be confused with the pipeline lock work:

| Lock layer | What it locks | When acquired | Released | Scope |
|---|---|---|---|---|
| **`AgentLockManager`** (existing) | Remote *agents* (Agent1, Agent2 machines) | When `MainViewModel.TriggerEvent()` starts execution | When the execution session ends | Per-execution-session — prevents two sessions from using the same agent simultaneously |
| **Pipeline lock** (this spec, Phase 3) | A *pipeline* (WatchItem) | When a user clicks Trigger from any client | When the run completes or the user releases | Per-WatchItem — prevents two users from triggering the same pipeline at once |

Both layers coexist. Phase 3 does NOT touch `AgentLockManager` — it adds a complementary lock at a higher level. Concretely:
- User Alice clicks Trigger on WatchItem "WarmSetup on Four nodes" → **pipeline lock** acquired with Alice as owner
- The execution starts → **agent lock** acquired on the 4 specific agents that WatchItem references
- User Bob tries to trigger the same WatchItem → **pipeline lock** acquisition fails → Bob sees the conflict dialog (Mockup 6) showing Alice as owner
- User Charlie tries to trigger a different WatchItem that needs Agent1 → **pipeline lock** succeeds (different pipeline) but **agent lock** fails → execution waits or errors per existing `AgentLockManager` semantics

The two locks have different keys, different lifecycles, different UIs, and different ownership models. They are complementary safety nets at different layers of the execution stack.

---

## 2. Side-by-Side Comparison

| Concern | Lock spec (original) | RBAC SRS / Design | Resolution |
|---|---|---|---|
| Identity interface | `IUserContext` defined inline in the Lock spec | `IUserContext` defined in `ControlNode.Core/Identity` (Phase 0) | **Use the Phase 0 version.** Delete the Lock spec's standalone definition. |
| Identity sources | "Negotiate for WPF, JWT for Web" | Session tokens (server-side store) for both clients | **Use server-side sessions** for both. Both clients call `LoginAsync` and pass the token. |
| `ClientKind` enum | `Wpf, WebClient, Cli, Service` | `Wpf, Web, Cli` | **Align on the SRS naming.** `WebClient` → `Web`, drop `Service` (executor-callbacks use a service identity that doesn't map to a user role anyway). |
| Authorization capability | `pipeline:force-release` (string key) | `Pipeline_ForceRelease` (enum value in `Permission`) | **Use the enum.** Same semantics; consistent with the rest of the system. |
| "Capability check" | Custom `ClaimsPrincipal.HasCapability()` | `IAuthorizationService.CanAsync(user, Permission.Pipeline_ForceRelease, pipelineId)` | **Use `CanAsync`.** All authorization goes through the same service. |
| Trigger lock acquisition | Happens "before" the trigger flow, separately | Happens **inside** the `TriggerPipelineHandler` after authz check | **Move it into the handler.** No separate lock-acquisition RPC call; transparent to the client on the happy path. |
| Lock failure response | `409 Conflict` with `PipelineLockDto` | gRPC `Aborted` status with `PipelineLockDto` payload (Design §10 error table) | **Use `Aborted`** with the same payload shape. The HTTP 409 mapping happens at gRPC-Web bridge for the browser. |
| Force-release endpoint | `POST /api/pipelines/{id}/force-release` (REST) | gRPC `PipelineService.ForceReleaseAsync` | **Use gRPC.** REST surface is gRPC-Web; same endpoint, different transport. |
| Hub for events | New `ExecutionHub` SignalR hub | Same hub, with additional event types added | **Reuse the existing hub.** Add the 4 lock events as new methods. |
| Storage | In-memory `LockRegistry` singleton | Same — in-memory in the controller process | **Unchanged.** |
| Persistence of audit | Audit suggested but not specified | Every lock event writes an `AuditEntries` row through the same `IAuditWriter` | **Wire it through.** No new audit channel. |
| Heartbeat mechanism | SignalR `HeartbeatLock(pipelineId)` | Same | **Unchanged.** |

---

## 3. What to Keep Verbatim from the Lock Spec

These sections of `Pipeline_Lock_Coordination_Spec.md` are still correct as written and should be implemented as specified:

- **§4.3 Lock model** — `PipelineLock`, `OwnerIdentity`, `LockKind`, `LockStatus`, `AcquireResult` types. Just place them in `ControlNode.Core/Locking` instead of a separate spec namespace.
- **§4.4 Lock lifecycle** state diagram and transitions.
- **§4.6 Lock event broadcast** — the four SignalR events and their payloads.
- **§4.7 Why this approach (and not alternatives)** — the rationale is unchanged.
- **§7 SignalR Contract Additions** — all hub method signatures and direction (client→server, server→client) stay as written. Auth scheme on the hub is the same `SessionAuthInterceptor` used by gRPC.
- **§8 WPF Client Changes** — file list, lock badge UI, trigger button enablement, conflict dialog. All unchanged.
- **§9 WebClient Changes** — TypeScript hooks (`useLocks`, `useLockFor`, `useCanTrigger`, `useLockHeartbeat`), components (`LockBadge`, `ConflictModal`, `ForceReleaseModal`). All unchanged.
- **§10 Lock Lifecycle & Failure Handling** — every scenario (happy path, collision, takeover, heartbeat timeout, edge cases). Unchanged.
- **§14 Risk Register** — unchanged.

---

## 4. What to Adjust in the Lock Spec

### 4.1 Identity-related sections

**Lock spec §4.2 (Identity & authentication)** — rewrite to point at the RBAC Phase 0 identity layer.

- ❌ Remove: "Two authentication schemes co-exist via ASP.NET Core's policy scheme" + the table comparing Bearer and Negotiate.
- ✅ Replace with: "Identity is provided by `IUserContext`, populated by `SessionAuthInterceptor` from a session token in gRPC metadata. The same interceptor authenticates both gRPC and SignalR hub calls. See `01_System_Design.md` §4."

### 4.2 Authorization-related sections

**Lock spec §4.5 (Authorization model)** — rewrite to point at the permission catalog.

- ❌ Remove: "Two role-like capabilities (kept minimal — bigger RBAC is a separate workstream)" and the custom capability table.
- ✅ Replace with: "Authorization uses the central `IAuthorizationService` and `Permission` enum from Phase 0. The capabilities used here are `Pipeline_Trigger` (assigned-Engineer + SrMgr + Admin) and `Pipeline_ForceRelease` (SrMgr + Admin). See `Permission` catalog in `01_System_Design.md` §3.1."

### 4.3 API endpoint sections

**Lock spec §6 (API Contract Additions)** — REST endpoints become gRPC RPCs.

| Original (REST) | Becomes (gRPC) |
|---|---|
| `GET /api/locks` | `LockService.ListAsync` |
| `GET /api/locks/{pipelineId}` | `LockService.GetAsync(pipelineId)` |
| `POST /api/pipelines/{id}/lock` | (folded into) `PipelineService.TriggerAsync` |
| `DELETE /api/pipelines/{id}/lock` | `LockService.ReleaseAsync(pipelineId)` |
| `POST /api/pipelines/{id}/force-release` | `PipelineService.ForceReleaseAsync(pipelineId, reason, revert)` |
| `POST /api/pipelines/{id}/heartbeat` | Hub-only — no separate RPC |

For the Web Client, gRPC-Web exposes the same operations over HTTP/1.1; the browser still calls what looks like a regular HTTP endpoint, but it routes through the generated gRPC-Web client.

### 4.4 Feature flag

**Lock spec's `Locks:Enabled` feature flag** — still useful, still in the configuration. The Roadmap document already says Phase 3 turns on the enforcement; this flag is the on/off switch the team flips at the end of Phase 3.

---

## 5. The Unified Permission Catalog (Updated)

Adding the lock permissions to the catalog from Design §3.1:

| Permission | Default Holders | Resource scope | Comes from |
|---|---|---|---|
| `Pipeline_View` | All | Pipeline | Design |
| `Pipeline_Trigger` | Admin, SrMgr (any) / Engineer (assigned) | Pipeline | Design |
| `Pipeline_Cancel` | Admin, SrMgr (any) / Engineer (assigned) | Pipeline | Design |
| `Pipeline_Retry` | Same as Trigger of parent | Run | Design |
| `Pipeline_TriggerAll` | Admin, SrMgr | none | Design |
| `Pipeline_CancelAll` | Admin, SrMgr | none | Design |
| `Pipeline_Enable` | Admin | Pipeline | Design |
| `Pipeline_Disable` | Admin | Pipeline | Design |
| **`Pipeline_ForceRelease`** | Admin, SrMgr | Pipeline | **Lock spec → folded in** |
| `User_Create` | Admin | none | Design |
| `User_Update` | Admin | User | Design |
| `User_Delete` | Admin | User | Design |
| `User_Assign` | Admin | User + Pipeline | Design |
| `User_Revoke` | Admin | User + Pipeline | Design |
| `Report_View` | All (scoped) | Pipeline | Design |
| `Report_Generate` | Admin, SrMgr | none | Design |
| `Audit_View` | Admin | none | Design |
| `Audit_Export` | Admin | none | Design |
| `Notification_Mute` | Engineer (own) | Pipeline | Design |

`Pipeline_ForceRelease` is the only addition. Its placement aligns with `Pipeline_Cancel` semantically — both are "interrupting an in-flight pipeline" actions — but it's distinct because force-release also takes over from another user.

---

## 6. Updated Phase 3 Definition

Replacing the brief "Phase 3 — Lock Coordination Integration" entry in `02_Implementation_Roadmap.md` with the full picture:

### Phase 3 — Lock Coordination Integration

**Goal**: when a Web user triggers a pipeline, the WPF user sees it locked. Conflicts are visible in real time; force-release with revert is available to authorized users.

### Scope (concrete file list)

```
ControlNode.Core/Locking/PipelineLock.cs
ControlNode.Core/Locking/OwnerIdentity.cs
ControlNode.Core/Locking/LockKind.cs
ControlNode.Core/Locking/LockStatus.cs
ControlNode.Core/Locking/AcquireResult.cs
ControlNode.Core/Locking/LockEvent.cs
ControlNode.Core/Locking/ILockRegistry.cs

ControlNode.Infrastructure/Locking/LockRegistry.cs
ControlNode.Infrastructure/Locking/LockExpirySweeper.cs

ControlNode.Application/Locks/AcquireLockHandler.cs        // internal — called by TriggerPipelineHandler
ControlNode.Application/Locks/ReleaseLockHandler.cs
ControlNode.Application/Locks/ForceReleaseHandler.cs
ControlNode.Application/Locks/HeartbeatHandler.cs

ControlNode.Application/Pipelines/TriggerPipelineHandler.cs   // MODIFIED — now acquires lock as step 1
ControlNode.Application/Pipelines/ForceReleaseHandler.cs

ControlNode.Api/Services/LockGrpcService.cs
ControlNode.Api/Services/PipelineGrpcService.cs               // MODIFIED — ForceReleaseAsync method added
ControlNode.Api/Hubs/ExecutionHub.cs                          // MODIFIED — 4 new events + heartbeat method
ControlNode.Api/Hubs/LockBroadcaster.cs                       // listens to ILockRegistry, broadcasts to hub
ControlNode.Api/Contracts/PipelineLockDto.cs
ControlNode.Api/Contracts/Mapping/LockMapper.cs

ControlNode.WpfClient/Services/ILockSnapshotService.cs
ControlNode.WpfClient/Services/LockSnapshotService.cs
ControlNode.WpfClient/Services/HeartbeatService.cs
ControlNode.WpfClient/Converters/OwnerToBadgeTextConverter.cs
ControlNode.WpfClient/Resources/Styles.xaml                  // EXTENDED — LockBadgeStyle
ControlNode.WpfClient/ViewModels/Operator/PipelineListViewModel.cs   // MODIFIED — lock awareness
ControlNode.WpfClient/Views/Operator/PipelineListView.xaml           // MODIFIED — lock badge
ControlNode.WpfClient/Views/Dialogs/ConflictDialog.xaml(.cs)
ControlNode.WpfClient/Views/Dialogs/ForceReleaseDialog.xaml(.cs)

ControlNode.WebClient/src/types/orchestration.ts             // EXTENDED — PipelineLockDto, OwnerIdentityDto
ControlNode.WebClient/src/api/locks.ts                       // (thin gRPC-Web client)
ControlNode.WebClient/src/signalr/ExecutionHubClient.ts      // MODIFIED — lock methods + events
ControlNode.WebClient/src/hooks/useLocks.ts
ControlNode.WebClient/src/hooks/useLockFor.ts
ControlNode.WebClient/src/hooks/useCanTrigger.ts             // // MODIFIED — combines lock state + authz hook
ControlNode.WebClient/src/hooks/useLockHeartbeat.ts
ControlNode.WebClient/src/components/common/LockBadge.tsx
ControlNode.WebClient/src/components/common/ConflictModal.tsx
ControlNode.WebClient/src/components/common/ForceReleaseModal.tsx
ControlNode.WebClient/src/views/PipelineListView.tsx          // MODIFIED — LockBadge, button disable
ControlNode.WebClient/src/views/MyPipelinesView.tsx           // MODIFIED — same

proto/lock.proto
```

### Out of scope for Phase 3

- Per-action-group locks (we lock per pipeline; nothing finer)
- Cross-controller locking (single controller in v1; see Lock spec §14 risk register)
- Lock history / forensics UI (the audit log captures the events; a dedicated viewer is a follow-up)

### Exit criteria (combined from Lock spec §13 and this integration)

- [ ] All exit criteria from `Pipeline_Lock_Coordination_Spec.md` §13 (testing strategy) pass
- [ ] `Pipeline_ForceRelease` permission is in the catalog and verified by `AuthorizationServiceTests`
- [ ] WPF and Web both display the lock badge when the other client holds a lock
- [ ] Trigger from a non-holder returns gRPC `Aborted` with `PipelineLockDto` (verified by integration test)
- [ ] Force-release with revert successfully takes over, audit entry shows actor + reason + prior owner
- [ ] Locks expire within 30 s of last heartbeat, broadcast received by all connected clients
- [ ] Same user, two devices: holds the same lock seamlessly (no spurious conflicts)
- [ ] Acceptance criterion #8 from SRS §12 ("Engineer's pipeline assignment is revoked … the Engineer's attempt to trigger that pipeline is rejected") still holds, integrated with lock behavior

### Estimated effort

**2 engineer-weeks.** This is half the original Lock-spec estimate because Phase 0 provides identity + audit infrastructure and Phase 2 provides the pipeline state machine — the Lock spec assumed all of those needed building.

---

## 7. Where the Two Documents Now Live

After this integration:

- **`Pipeline_Lock_Coordination_Spec.md`** stays as the **detailed reference** for the Lock feature. Engineers building Phase 3 read it for the UX details, the lifecycle scenarios, the testing strategy, the risk register.
- **`00–02_*.md` (this document set)** is the **orchestration layer** — the bigger plan that includes the Lock feature as one of eleven phases.

Practically: anyone implementing Phase 3 reads both. The Implementation Roadmap (document 02) describes what phase 3 delivers and when; the Lock spec describes how to build it.

The Lock spec does **not** need to be rewritten. Sections 2 (current state), 4.2 (identity), 4.5 (authz), and 6 (REST endpoints) are slightly out of date with this integration, but the deltas are listed in §4 above and are small. Any engineer reading both will follow the integration document's reconciliation.

---

## 8. Decisions That Should Be Reconfirmed Before Starting Phase 3

Three things to verify with the technical reviewer at the Phase 2 → Phase 3 transition:

1. **Lock semantics on retries.** When an Engineer retries a failed Action on a pipeline, should the retry acquire its own lock? My recommendation: **yes** — retry is conceptually a continuation of the pipeline's work, and it shouldn't proceed if someone else has taken over the pipeline. The retry handler acquires the lock the same way Trigger does.

2. **Bulk-trigger lock acquisition.** When Trigger All Idle fires off 50 pipelines, does each one get its own lock? My recommendation: **yes** — same lock model per pipeline, all acquired in the same DB transaction that performs the atomic state transition. If any individual lock acquisition fails (which shouldn't happen since the pipeline must be Idle to be triggered, and Idle implies no lock), the pipeline is added to the skipped list.

3. **Force-release scope.** Can SrMgr force-release a pipeline held by an Admin? My recommendation: **no** — restrict force-release to "release a peer or subordinate's lock". Add a role-comparison step in `ForceReleaseHandler`: SrMgr can only force-release locks held by Engineers and other SrMgrs; Admin can force-release anyone's. This isn't in the original Lock spec but is a natural extension of the role model.

---

## 9. The Other Direction: How RBAC Benefits the Lock Spec

It's also worth saying explicitly what the Lock spec *gains* from being folded into RBAC:

- **Real identity instead of placeholder.** "ravi.kumar (WebClient)" is now backed by a real `User` row with a known role, assigned pipelines, and password lifecycle — not a stub.
- **Granular authorization.** Force-release isn't a blanket capability; it can be conditioned on roles, on which pipeline, on the relationship between the actor and the original owner.
- **Audit completeness.** Every lock event is part of the same audit trail as every other authorization decision. Filtering by user shows their lock acquisitions alongside their trigger and view actions — one consistent history.
- **Session lifecycle alignment.** Lock expiry and session expiry share semantics. When an Admin deletes a user, their sessions are revoked **and** any locks they hold are immediately released.
- **Engineer self-service.** Once the user model exists, an Engineer can see "all the pipelines I currently hold locks on" in their personal view — and release them or transfer them.

The unified design is genuinely better than either piece alone.

---

## 10. Final Sequencing Recommendation

Build in the order laid out in `02_Implementation_Roadmap.md`:

1. Phases 0, 1, 2 in strict sequence.
2. Phase 3 (locks) immediately after Phase 2.
3. Phases 4, 5, 6, 7 can start in parallel teams as soon as their prerequisites are met.
4. Phases 8, 9, 10 are the long tail.

The Lock work is **not** something to "try to fit in alongside" — it belongs squarely in Phase 3, with everything it needs (identity, authz, pipeline state machine, audit) already in place from Phases 0-2.

That's the integration. Document 02's roadmap is now the single source of truth for sequencing; the Lock spec is the detailed design reference for what gets built during Phase 3 specifically.
