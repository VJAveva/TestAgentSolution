# Phase 2a Context Pack — Pipeline Authorization Guard (Backend Only)

> Attach this file to every Copilot session while working on Phase 2a. Detach when Phase 2a ships and switch to `phase-2b-context.md`.

> Companion reading (already in Copilot context via `.github/copilot-instructions.md`): `docs/rbac/01_System_Design.md` §3 (authorization) + §5.1–5.2 (trigger/cancel), `docs/rbac/02_Implementation_Roadmap.md` §Phase 2, `docs/architecture/CURRENT_STATE.md` "Executor Adapter" section, `docs/architecture/CONVENTIONS.md`.

---

## Goal

Ship the backend authorization gate for pipeline trigger and cancel operations. After Phase 2a, an authorized user (WPF or Web) can trigger and cancel a pipeline through the guard; an unauthorized user receives `PermissionDenied` (gRPC) or `403` (REST). Default mode remains unchanged — WPF triggers behave exactly as before when `RBAC:Enabled = false`. No UI filtering changes in this phase.

---

## Where things land in the existing solution

| Concern | Project |
|---|---|
| `PipelineAuthorizationGuard` (wraps `IAuthorizationService.CanAsync`, audit, throws on deny) | **`TestController.Api`** (`Services/PipelineAuthorizationGuard.cs`) |
| `PipelineAuthorizationDeniedException` (domain exception) | **`TestControllerGrpc.Core`** (`Authorization/PipelineAuthorizationDeniedException.cs`) |
| `PipelineService` (gRPC service: trigger / cancel with guard) | **`TestController.Api`** (`Services/PipelineService.cs`) |
| `PipelinesController` (REST endpoints: trigger / cancel with guard) | **`TestController.Api`** (`Controllers/PipelinesController.cs`) |
| `MainViewModel.Execution.cs` (surgical diff: wrap `TriggerEvent` / `CancelExecution` with guard) | **`TestControllerGrpc`** (`ViewModels/MainViewModel.Execution.cs` — modify) |
| DI registration | **`TestController.Api`** (`RbacFeatureExtensions.cs` — modify) |
| WebApi proxy header propagation | **`TestController.WebApi`** (`Services/ControllerProxyService.cs` — modify) |
| Tests | **`TestControllerGrpc.Tests/Rbac/`** + **`TestController.WebApi.Tests/Rbac/`** |

No new projects. All work lands in existing projects.

---

## File targets (10 tasks, 3 blocks)

### Block A — Guard + Exception (no dependencies beyond Phase 1)

| # | Path | What |
|---|---|---|
| 1 | `TestControllerGrpc.Core/Authorization/PipelineAuthorizationDeniedException.cs` | Domain exception: `Permission`, `ResourceId`, `ReasonCode`, `HumanReadable`. Thrown by the guard on deny. Callers convert to `StatusCode.PermissionDenied` (gRPC) or `403` (REST). |
| 2 | `TestController.Api/Services/PipelineAuthorizationGuard.cs` | Singleton. Wraps `IAuthorizationService.CanAsync(user, permission, resourceId)`. On `Allow` → returns silently. On `Deny` → enqueues audit entry (fire-and-forget via `IAuditWriter`) and throws `PipelineAuthorizationDeniedException`. Note: `IAuthorizationService` already enqueues its own audit row per `01_System_Design.md` §3.2 rule 6 — the guard does NOT double-audit. The guard's job is purely: call `CanAsync`, inspect the decision, throw if denied. |

### Block B — gRPC + REST endpoints (depends on Block A)

| # | Path | What |
|---|---|---|
| 3 | `TestController.Api/Services/PipelineService.cs` | Singleton service. Methods: `TriggerAsync(IUserContext user, string pipelineId, CancellationToken ct)` and `CancelAsync(IUserContext user, string runId, CancellationToken ct)`. Calls guard → then delegates to `IActionPipelineExecutor.ExecuteEventTrackedAsync` (trigger) or session CTS cancel (cancel). Does NOT modify or replace the executor. |
| 4 | `TestController.Api/Controllers/PipelinesController.cs` | `POST /api/pipelines/{pipelineId}/trigger` and `POST /api/pipelines/{runId}/cancel`. Resolves `IUserContext` from `SessionAuthInterceptor`. Calls `PipelineService`. Catches `PipelineAuthorizationDeniedException` → returns `403` with `{ error, reasonCode }`. |
| 5 | `TestController.Api/RbacFeatureExtensions.cs` (modify) | Add DI: `services.AddSingleton<PipelineAuthorizationGuard>()` + `services.AddSingleton<PipelineService>()`. |
| 6 | `TestController.WebApi/Services/ControllerProxyService.cs` (modify) | Propagate `Authorization` header from inbound request to WPF controller proxy calls so the controller-side authz sees the original user token, not the proxy's identity. |

### Block C — WPF integration + Tests (depends on Block A + B)

| # | Path | What |
|---|---|---|
| 7 | `TestControllerGrpc/ViewModels/MainViewModel.Execution.cs` (modify) | Surgical diff to `TriggerEvent()` and `TriggerWatchItem()`: before the existing lock acquisition + executor call, invoke `_guard.AuthorizeAsync(userContext, Permission.Pipeline_Trigger, tag)`. On `PipelineAuthorizationDeniedException` → `AddLog(denial message, LogSeverity.Warning)` and return early. Similarly for `CancelExecution()`: guard with `Permission.Pipeline_Cancel`. In Default mode the guard calls `CanAsync` which short-circuits to Allow for WPF — zero behavioral change. |
| 8 | `TestControllerGrpc/App.xaml.cs` (modify) | Add DI: `services.AddSingleton<PipelineAuthorizationGuard>()`. Inject into `MainViewModel` constructor. |
| 9 | `TestControllerGrpc.Tests/Rbac/PipelineAuthorizationGuardTests.cs` | Unit tests: allow passes through, deny throws correct exception with reason code, Default mode WPF always allows, audit NOT double-written. |
| 10 | `TestController.WebApi.Tests/Rbac/PipelineTriggerAuthzTests.cs` | Integration tests: Engineer triggers assigned pipeline → 200, Engineer triggers unassigned → 403 with `no-assignment`, Guest triggers → 403 with `guest-readonly`, SrMgr triggers any → 200, Default mode WPF → always 200. |

---

## Sequence rules

- **Phase 1b must be complete** — user management and pipeline assignments must exist so we can test assignment-based authorization.
- **Block A first** — guard + exception before endpoints or WPF integration.
- **Block B depends on Block A** — the REST/gRPC endpoints call the guard.
- **Task 7 depends on Block A** — `MainViewModel.Execution.cs` injects the guard.
- **Block C (tests) depends on A + B + task 7** being functional.
- **Task 6 (proxy header) is independent of Block C** — can be done in parallel with task 7.

---

## Watch out for

1. **The existing `IActionPipelineExecutor.ExecuteEventTrackedAsync` must NOT be modified.** The guard wraps the call site, it doesn't replace the executor. The executor knows nothing about users or permissions — that separation is by design per `01_System_Design.md` §3.3.

2. **In Default mode, `MainViewModel` triggers behave EXACTLY as before.** No visible change to anyone running `RBAC:Enabled = false`. The guard calls `IAuthorizationService.CanAsync` which returns `Allow("default-mode-wpf")` immediately (per `01_System_Design.md` §3.4). Regression-test this: a test with `RbacOptions.Enabled = false` + `ClientKind.Wpf` must pass every trigger without hitting the DB.

3. **`IAuthorizationService.CanAsync` caches `AssignedPipelineIds` on the `IUserContext`.** The set is pre-fetched by `SessionAuthInterceptor` when hydrating the user context (per `01_System_Design.md` §4.2 step 4). Don't add a DB round-trip per trigger inside the guard. If the set is empty but the role is Engineer, `AuthorizationService` falls back to a DB check — but that's inside `AuthorizationService`, not in the guard.

4. **Audit writes are fire-and-forget. NEVER await the audit writer in the trigger path.** `IAuthorizationService.CanAsync` already enqueues an audit entry for every decision (per §3.2 rule 6). The guard does NOT add a second audit row — no double-auditing. NFR-PRF-01 (5ms p95 for authz check) must hold.

5. **Cancel authorization: for Phase 2a, treat cancel-own and cancel-others as the same `Pipeline_Cancel` permission.** The `Pipeline_ForceCancel` split (cancelling someone else's run) lands in Phase 3 with lock coordination. For now, any user with `Pipeline_Cancel` on the resource can cancel any run on that pipeline.

6. **Reason codes in audit deny rows must match `01_System_Design.md` §3.2 exactly:**
   - `"no-assignment"` — Engineer triggers a pipeline they are not assigned to
   - `"no-role"` — Guest tries a write permission, or Engineer tries a User_* permission
   - `"guest-readonly"` — Guest tries any non-read permission
   - `"default-mode-web-readonly"` — Web client tries a write in Default mode
   These are already implemented in `AuthorizationService.EvaluateRbacAsync` — the guard surfaces them via the exception.

7. **`PipelineAuthorizationGuard` is Singleton DI (per `CONVENTIONS.md`).** Constructor injects `IAuthorizationService` only. It does NOT inject `IAuditWriter` — the authorization service handles audit internally.

8. **The existing `AgentLockManager` is NOT touched in Phase 2a.** That's Phase 3. The guard runs BEFORE lock acquisition in `TriggerEvent()` — if denied, we never reach the lock check.

9. **The `ControllerProxyService` forwards REST → WPF controller.** When the standalone WebApi receives a trigger request, it must propagate the `Authorization: Bearer <token>` header so the controller-side guard sees the original user, not a service-to-service identity. Currently `ControllerProxyService` creates raw `HttpRequestMessage`s without auth headers — add the header from the inbound `HttpContext.Request.Headers["Authorization"]`.

10. **`PipelineService.TriggerAsync` resolves the pipeline tag from `pipelineId`.** The `pipelineId` in the REST/gRPC call maps to a WatchItem `Tag` in the existing system. The service must map the ID to the tag before calling the executor. Use the existing `WatchListConfig` (available via `IWatchListProvider` or directly from `MainViewModel._config`) to resolve. For the REST path, the `WatchListFileService` singleton in WebApi already parses the config.

11. **Exception hierarchy:** `PipelineAuthorizationDeniedException` extends `Exception` (not `RpcException`). The gRPC handler catches it and wraps in `RpcException(StatusCode.PermissionDenied, ...)`. The REST controller catches it and returns `Results.Problem(statusCode: 403, ...)`. This keeps the domain exception transport-agnostic.

---

## Excluded from this pack (lands in Phase 2b and 2c)

- WatchList tree filtering by assignment in WPF (Phase 2b)
- Trigger button enable/disable based on assignment in WPF (Phase 2b)
- WatchList tree filtering by assignment in WebClient (Phase 2c)
- Trigger button enable/disable based on assignment in WebClient (Phase 2c)
- `Pipeline_ForceCancel` permission split (Phase 3)
- Lock coordination (Phase 3)
- `TriggerAll` / `CancelAll` with assignment-aware filtering (Phase 2b)

---

## How to start a task in this phase

Copy this skeleton when opening a Copilot session for any task in Block A–C:

```
[SPEC]
- docs/rbac/01_System_Design.md §3 — Authorization Design
- docs/rbac/01_System_Design.md §5.1 — Atomic trigger
- docs/rbac/02_Implementation_Roadmap.md §Phase 2 — Pipeline Trigger + Cancel
- docs/architecture/CURRENT_STATE.md — Executor Adapter

[CURRENT STATE]
- Phase 0 + 0.5 + 1a + 1b complete
- Tasks 1..N of Phase 2a already done (paths)
- Pending: this task (path)
- Existing patterns: MainViewModel.Execution.cs trigger flow

[TASK]
Generate <path>

[CONSTRAINTS]
- Singleton DI lifetime for PipelineAuthorizationGuard and PipelineService
- IAuthorizationService already handles audit — guard does NOT double-audit
- Fire-and-forget audit pattern — never await IAuditWriter
- PipelineAuthorizationDeniedException is transport-agnostic (no RpcException)
- Do NOT modify IActionPipelineExecutor or the executor itself
- Default mode (RBAC:Enabled=false) WPF triggers unchanged — regression test
- Guard runs BEFORE AgentLockManager acquisition
- CommunityToolkit.Mvvm for VM modifications
- IAppLogger for logging, not Serilog

[OUTPUT]
- The file content
- One line: DI registration (if applicable)
- Nothing else
```

---

## Exit checklist (don't move to Phase 2b until all green)

- [ ] Engineer triggers an assigned pipeline (WPF) → execution proceeds as before
- [ ] Engineer triggers an unassigned pipeline (WPF) → denied with log message, no execution
- [ ] Senior Manager triggers any pipeline (WPF) → execution proceeds
- [ ] Guest attempts trigger via REST → 403 with `{ error, reasonCode: "guest-readonly" }`
- [ ] Engineer triggers unassigned via REST → 403 with `{ error, reasonCode: "no-assignment" }`
- [ ] Engineer triggers assigned via REST → 200
- [ ] Senior Manager triggers any via REST → 200
- [ ] Default mode (`RBAC:Enabled = false`) WPF trigger → behaves exactly as before (no auth popup, no denial, no visible change)
- [ ] Default mode Web read → allowed; Web write → denied with `default-mode-web-readonly`
- [ ] Cancel own running pipeline with `Pipeline_Cancel` permission → succeeds
- [ ] Cancel without `Pipeline_Cancel` permission → denied
- [ ] Audit log shows `Pipeline_Trigger` allow/deny rows with correct reason codes
- [ ] `Authorization` header propagated through `ControllerProxyService` to WPF controller
- [ ] NFR-PRF-01: authz check completes in < 5ms p95 (no DB hit for cached assignments)
- [ ] All Phase 0 + 0.5 + 1a + 1b tests still pass (regression)
- [ ] `AgentLockManager` not modified — lock acquisition still runs after guard approval
