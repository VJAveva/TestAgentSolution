# 02 — Implementation Roadmap

> **Purpose**: turn the system design into a sequenced, demo-able delivery plan. Eleven phases. Each ends with the system in a working state that a stakeholder can sign off on before the team moves to the next phase.

> **Prerequisite**: read `00_Master_Plan.md` and `01_System_Design.md` first. This document references their decisions and component decomposition.

---

## How to Use This Document

Each phase has the same shape:

1. **Goal** — one sentence on what changes for the user
2. **Scope** — what's in
3. **Out of scope** — what's deliberately not in (to prevent scope creep)
4. **Files to create / modify** — concrete component list
5. **Exit criteria** — checklist that must pass before the phase is "done"
6. **Demo script** — what to show stakeholders at phase end
7. **Risks** — what could go wrong, mitigation
8. **Estimated effort** — engineer-weeks (assuming 2 engineers, adjust linearly for larger teams on parallelizable phases)

A phase is **done** when every exit criterion passes in a staging environment with realistic data. Not when the code compiles. Not when one happy-path test passes.

---

## Dependency Graph

```
   Phase 0  (Identity & AuthZ foundations)
       │
       ▼
   Phase 0.5 (Default mode UX)  ── 5 engineer-days
       │
       ▼
   Phase 1  (User management)
       │
       ▼
   Phase 2  (Pipeline trigger + cancel)
       │
       ├──────────────┬──────────────┐
       ▼              ▼              ▼
   Phase 3       Phase 4         Phase 5
  (Lock coord)  (Retry)         (Bulk ops)
                                    │
                                    ▼
                                Phase 6
                              (Enable/Disable)

   Phase 7  (Read paths + Guest)  ── can start in parallel with Phase 1
       │
       ▼  (joins main line after Phase 6)
       
   Phase 8  (Notifications + flaky)  ── requires data from 2, 4
   Phase 9  (Reports)                ── requires data from 2, 4
   Phase 10 (Audit viewer + hardening)
```

Phases 3, 4, 5, 7, 8, 9 can run in parallel teams **after** their prerequisites are met. Phase 10 is the closer.

---

## Phase 0 — Identity & Authorization Foundations

**Goal**: the system can answer "who is the caller?" and "are they allowed to do X?" for hard-coded users, with audit entries written.

### Scope

- Database schema: `Users`, `Sessions`, `PipelineAssignments`, `AuditEntries` tables
- EF Core context + first migration
- `IUserContext` interface, `Role` and `Permission` enums (full catalog from Design §3.1)
- `IAuthorizationService` + `AuthorizationService` with `CanAsync` (default-deny)
- `ISessionStore` + SQL implementation
- `SessionAuthInterceptor` (gRPC interceptor) wiring `IUserContext` into `ServerCallContext.UserState`
- `IAuditWriter` + a write-only implementation (fire-and-forget channel + background drain)
- A seed migration that inserts **one** Administrator user with a known initial password
- gRPC service `LoginAsync` / `LogoutAsync` (no UI yet — exercised by integration tests)
- **Default mode mechanics (cheap, ~2 days of the phase budget):**
  - `RbacOptions` strongly-typed config bound to `appsettings.json` (`RBAC:Enabled` flag)
  - `IWritableOptions<RbacOptions>` that flips the flag at runtime and persists back to `appsettings.json`
  - `DefaultUser` static class with a stable UUID (`00000000-0000-0000-0000-000000000001`)
  - `SyntheticUserContext` implementation of `IUserContext`
  - `SessionAuthInterceptor` reads `RBAC:Enabled` — when false, skips token validation and injects `DefaultUser.ForClient(clientKind)`
  - `AuthorizationService.CanAsync` short-circuit (per `01_System_Design.md` §3.4): in Default mode return Allow for WPF, Allow for Web read, Deny for Web write
  - `IRbacModeTransitionService` with `SwitchToSecuredAsync` and `SwitchToDefaultAsync`
  - `SystemModeGrpcService` exposing Get / SwitchToSecured / SwitchToDefault RPCs
  - `SystemModeBroadcaster` raising `SystemModeChanged` on the SignalR hub
- Two integration tests:
  - Login with valid creds returns a token
  - RPC with no token returns `Unauthenticated`
  - RPC with token but lacking permission returns `PermissionDenied`
  - Audit entries written for both allow and deny
  - **Default mode tests** (with `RBAC:Enabled = false` fixture): RPC without token succeeds from WPF, RPC from Web with write permission returns `PermissionDenied` with reason `default-mode-web-readonly`

### Out of scope

- Any UI (login forms come in Phase 1)
- Any business RPC handlers (Phase 2)
- Password change flow (Phase 1)

### Files to create

```
TestControllerGrpc.Core/Identity/IUserContext.cs
TestControllerGrpc.Core/Identity/Role.cs
TestControllerGrpc.Core/Identity/ClientKind.cs
TestControllerGrpc.Core/Authorization/Permission.cs
TestControllerGrpc.Core/Authorization/AuthDecision.cs
TestControllerGrpc.Core/Authorization/IAuthorizationService.cs
TestControllerGrpc.Core/Identity/User.cs
TestControllerGrpc.Core/Identity/PipelineAssignment.cs
TestControllerGrpc.Core/Identity/DefaultUser.cs
TestControllerGrpc.Core/Identity/SyntheticUserContext.cs
TestControllerGrpc.Core/Audit/AuditEntry.cs
TestControllerGrpc.Core/Audit/IAuditWriter.cs
TestControllerGrpc.Core/Configuration/RbacOptions.cs
TestControllerGrpc.Core/Configuration/WritableOptions.cs

TestController.Persistence/TestController.Persistence.csproj           (NEW project; only structural addition)
TestController.Persistence/OrchestratorDbContext.cs
TestController.Persistence/Configurations/UserConfiguration.cs
TestController.Persistence/Configurations/SessionConfiguration.cs
TestController.Persistence/Configurations/PipelineAssignmentConfiguration.cs
TestController.Persistence/Configurations/AuditEntryConfiguration.cs
TestController.Persistence/Migrations/0000_Initial.cs
TestController.Persistence/Migrations/0001_SeedAdmin.cs

TestController.Persistence/Identity/SessionStore.cs
TestController.Persistence/Identity/PasswordHasher.cs
TestController.Persistence/Authorization/AuthorizationService.cs
TestController.Persistence/Audit/QueuedAuditWriter.cs
TestController.Persistence/Audit/AuditDrainWorker.cs

TestController.Api/Interceptors/SessionAuthInterceptor.cs
TestController.Api/Interceptors/AuditLoggingInterceptor.cs
TestController.Api/Services/AuthGrpcService.cs
TestController.Api/Services/SystemModeGrpcService.cs
TestController.Api/SystemMode/RbacModeTransitionService.cs
TestController.Api/RbacFeatureExtensions.cs                            (new extension method AddRbacFeature())

Surgical diffs:
  TestControllerGrpc/App.xaml.cs                                       (call AddRbacFeature() in DI setup)
  TestController.WebApi/Program.cs                                     (call AddRbacFeature() in DI setup)
  TestControllerGrpc.csproj, TestController.WebApi.csproj              (reference TestController.Persistence)

TestControllerGrpc.Core/Protos/auth.proto                              (next to existing test_agent.proto)

TestControllerGrpc.Tests/Rbac/LoginTests.cs
TestControllerGrpc.Tests/Rbac/AuthInterceptorTests.cs
TestControllerGrpc.Tests/Rbac/AuthorizationServiceTests.cs
TestController.WebApi.Tests/Rbac/AuthE2ETests.cs
```

### Exit criteria

- [ ] `dotnet ef database update` creates all four tables cleanly
- [ ] Seed Administrator exists; can log in via gRPC
- [ ] Without a token, any RPC returns `Unauthenticated`
- [ ] With a token, `IUserContext` is populated from `ServerCallContext`
- [ ] `AuthorizationService.CanAsync(admin, Pipeline_Trigger)` returns Allow
- [ ] `AuthorizationService.CanAsync(unknown, Pipeline_Trigger)` returns Deny with `not-handled`
- [ ] Every allow and deny produces exactly one `AuditEntries` row
- [ ] Inactive sessions are revoked by the housekeeping worker after the configured timeout
- [ ] Password rate-limiting blocks the 6th attempt within a minute

### Demo script

1. Run `dotnet ef database update`. Show the new tables.
2. Run `Login` via a gRPC client with the seed admin. Token returned.
3. Run a hypothetical `Ping` RPC with the token — returns OK.
4. Run the same with no token — `Unauthenticated`.
5. Query `AuditEntries` — show one row per call.
6. Wait 60+ minutes (or set inactivity to 30 s for the demo), run again — `Unauthenticated`, audit entry shows session expired.

### Risks

| Risk | Mitigation |
|---|---|
| Audit drain backs up under load and grows the queue unboundedly | Queue is bounded (default 10k); when full, audit becomes synchronous and degrades p95 — surface this in metrics. |
| WAL file (`orchestrator.db-wal`) is not included in a partial backup | Document that backups must capture `*.db`, `*.db-wal`, and `*.db-shm` together — or use `sqlite3 .backup` which handles this correctly. |
| Token-hash partial index doesn't get used | Confirm `EXPLAIN QUERY PLAN` shows the partial index `IX_Sessions_TokenHash` is selected for session lookups. |

### Estimated effort

**2 engineer-weeks.** Both engineers full-time. Roughly 60% backend, 40% test infrastructure.

---

## Phase 0.5 — Default Mode UX (one-week mini-phase)

**Goal**: ship the Default mode user experience so the system is usable end-to-end without any RBAC setup. Switching to Secured mode is a one-click flow from Settings.

**Why this is a separate mini-phase**: Phase 0 builds the mechanics (config flag, synthetic user, authz short-circuit). Phase 0.5 builds the visible UI on top so an out-of-box install is productive. Phase 1 (User management) only makes sense once a customer has chosen to switch to Secured mode — so 0.5 logically sits between them.

### Scope

- WPF Settings page with the Security mode panel (Mockup 10)
- WPF persistent Default-mode banner at top of main window (Mockup 11 top half)
- WPF top chrome Default-user badge (dashed border + user icon)
- Web Client Observer badge in top chrome (dashed border + eye icon)
- Web Client trigger button disabled treatment with tooltip ("Trigger is unavailable in Default mode…")
- Web Client lock badge rendering for "Locked by Default user (WPF)" — same component used for real user lock badges, no special-casing
- Initial-Admin-creation wizard (Default → Secured switch)
- DISABLE RBAC confirmation modal (Secured → Default switch)
- `SystemModeChanged` SignalR event handler — both clients reload to the appropriate state on mode flip

### Out of scope

- Real RBAC features (Phase 1 onward)
- Network-restriction warning ("you're in Default mode and exposed on a public IP") — deferred

### Files to create

```
ControlNode.WpfClient/Views/Settings/SettingsView.xaml(.cs)
ControlNode.WpfClient/Views/Settings/SecurityModePanel.xaml(.cs)
ControlNode.WpfClient/ViewModels/Settings/SecurityModeViewModel.cs
ControlNode.WpfClient/Views/Settings/InitialAdminWizard.xaml(.cs)
ControlNode.WpfClient/Views/Settings/DisableRbacConfirmDialog.xaml(.cs)
ControlNode.WpfClient/Controls/DefaultModeBanner.xaml(.cs)
ControlNode.WpfClient/Controls/UserIdentityBadge.xaml(.cs)   (handles Admin / Engineer / SrMgr / Guest / Default user)
ControlNode.WpfClient/Services/SystemModeClient.cs           (calls SystemModeGrpcService)

ControlNode.WebClient/src/components/header/DefaultModeBanner.tsx   (only renders banner if needed in future)
ControlNode.WebClient/src/components/header/UserIdentityBadge.tsx   (handles Admin/Engineer/SrMgr/Guest/Observer/Default user)
ControlNode.WebClient/src/hooks/useSystemMode.ts
ControlNode.WebClient/src/components/common/DisabledTriggerButton.tsx
ControlNode.WebClient/src/signalr/SystemModeEvents.ts

ControlNode.Api.Tests/Phase0_5/ModeSwitchE2ETests.cs
```

### Exit criteria

- [ ] Fresh install opens WPF directly to the main view with the Default-mode banner visible
- [ ] WPF can trigger, cancel, retry without restriction; audit entries show Default user as actor
- [ ] Web Client visits `controller.aveva.local` and lands on the All pipelines view with Observer badge
- [ ] Web Client Trigger button visibly disabled with tooltip; clicking it has no effect
- [ ] When WPF triggers a pipeline, Web Client receives `PipelineLockAcquired` and renders "Locked by Default user (WPF)" badge within 2 seconds
- [ ] Settings > Security mode shows both modes side by side with Default mode marked ACTIVE
- [ ] Clicking "Switch to Secured mode…" opens the initial-Admin wizard
- [ ] Completing the wizard: flag flips, login screen appears in WPF, Web Client reloads to login screen, in-flight pipelines continue running (now owned by the new Admin)
- [ ] From Secured mode, Admin can navigate to Settings > Security mode and switch back via DISABLE RBAC confirmation
- [ ] All Phase 0 tests still pass with `RBAC:Enabled = false` fixture

### Risks

| Risk | Mitigation |
|---|---|
| `SystemModeChanged` event arrives at WebClient but the SignalR reconnect happens during the auth middleware reload, missing the event | Web Client also polls `/api/system/mode` on reconnect; whichever wins triggers the reload. |
| Operator clicks "Switch to Secured mode" without realizing all current Web Client users will lose access | Wizard's confirmation panel lists this as a side-effect. |
| Default mode banner becomes annoying noise for operators who plan to stay in Default forever | Banner is fixed (not dismissible) by design — it is the discoverability anchor for the upgrade path. Operator handbook documents this. |

### Estimated effort

**5 engineer-days** (one engineer-week with 1 engineer, or 2-3 days with 2 engineers working in parallel on WPF + Web).

---

## Phase 1 — User Management

**Goal**: an Administrator can create, modify, and delete Engineer and Senior Manager users via the WPF Admin module, and any of them can log in via the appropriate client.

### Scope

- gRPC `UserService` with `CreateAsync`, `UpdateAsync`, `DeleteAsync`, `ListAsync`, `AssignPipelineAsync`, `RevokePipelineAsync`
- Authz: all gated by `User_*` permissions (Admin-only by §3.1)
- "Last Administrator cannot be deleted" rule (FR-USR-05)
- "First-login password change" enforcement (FR-AUTH-06)
- WPF Admin module: User Management screen
- WPF Login screen (UI for the existing gRPC LoginAsync)
- Web Client Login screen (parallel work — same API)
- Web Client "Continue as Guest" path returning a Guest token (read paths come in Phase 7; for now, Guest token is issued but they can only call read-only RPCs that don't exist yet)
- Audit entries for every user CRUD operation

### Out of scope

- Pipeline operations (Phase 2)
- Engineer self-service profile page
- Bulk user import
- Self-service password reset (admin-mediated only in v1; see SRS §13)

### Files to create

```
ControlNode.Application/Users/CreateUserHandler.cs
ControlNode.Application/Users/UpdateUserHandler.cs
ControlNode.Application/Users/DeleteUserHandler.cs
ControlNode.Application/Users/ListUsersHandler.cs
ControlNode.Application/Users/AssignPipelineHandler.cs
ControlNode.Application/Users/RevokePipelineHandler.cs
ControlNode.Application/Users/Validators/CreateUserValidator.cs
ControlNode.Application/Users/Validators/UpdateUserValidator.cs

ControlNode.Api/Services/UserGrpcService.cs
ControlNode.Api/Services/AuthGrpcService.cs     (extend with ChangePasswordAsync)

ControlNode.WpfClient/Views/Admin/UserManagementView.xaml(.cs)
ControlNode.WpfClient/ViewModels/Admin/UserManagementViewModel.cs
ControlNode.WpfClient/Views/Admin/AddUserDialog.xaml(.cs)
ControlNode.WpfClient/Views/Login/LoginView.xaml(.cs)
ControlNode.WpfClient/Views/Login/FirstLoginPasswordChangeDialog.xaml(.cs)
ControlNode.WpfClient/Services/AuthService.cs
ControlNode.WpfClient/Services/UserManagementClient.cs

ControlNode.WebClient/src/views/LoginView.tsx
ControlNode.WebClient/src/views/FirstLoginPasswordChangeView.tsx
ControlNode.WebClient/src/api/auth.ts
ControlNode.WebClient/src/hooks/useCurrentUser.ts

proto/user.proto

ControlNode.Application.Tests/Phase1/CreateUserHandlerTests.cs
ControlNode.Application.Tests/Phase1/LastAdminRuleTests.cs
ControlNode.Api.Tests/Phase1/UserCrudIntegrationTests.cs
```

### Exit criteria

- [ ] Admin creates an Engineer via the WPF UI; Engineer can log in via Web
- [ ] First login forces password change; flag clears after change
- [ ] Engineer's role is enforced — they cannot call `UserService.CreateAsync` (gets `PermissionDenied`)
- [ ] Deleting the only Administrator fails with `FailedPrecondition`
- [ ] Deleting a user cascades to delete their assignments (FR-USR-04)
- [ ] Admin can assign 2 pipelines to one Engineer; `ListAssignments` returns both
- [ ] Audit log shows the User_Create / User_Assign actions with the Admin's user id
- [ ] Login rate-limit blocks the 6th attempt within a minute (NFR-SEC-03)

### Demo script

1. Log in as the seed admin in WPF. Navigate to User Management.
2. Create Engineer "ravi.kumar" with initial password.
3. Open Web Client in a browser, log in as ravi.kumar. Forced to change password.
4. Back in WPF, assign two pipelines to ravi. Show count badge.
5. In Web Client, log in as ravi — sees the assignments (though no UI to operate on them yet).
6. Try to delete the admin — denied.
7. Show the audit log query for the day's actions.

### Risks

| Risk | Mitigation |
|---|---|
| Engineers create weak initial passwords | Validator: 12+ chars, mixed case + digit. Spec the rules clearly. |
| Concurrent role change while user is mid-session | Role change immediately revokes all sessions for that user (Design §4.3). |
| WPF and Web UI diverge | Both consume the same gRPC service; treat the API as the contract. |

### Estimated effort

**3 engineer-weeks.** One engineer on server + WPF, one on Web Client. Server is the gate — Web can lag by a couple of days but should not start before the gRPC contract is signed off.

---

## Phase 2 — Pipeline Trigger + Cancel with Full AuthZ

**Goal**: an authorized user can trigger and cancel a pipeline. An unauthorized user gets a clear denial. The system race-safely handles concurrent triggers.

### Scope

- `Pipelines` and `Runs` tables (plus child Events / ActionGroups / Actions skeleton — populated in Phase 4 retry work, but rows are created here)
- Import job: read `WatchList.xml` → insert / upsert `Pipelines` rows
- gRPC `PipelineService.TriggerAsync(pipelineId)` — atomic transition (Design §5.1)
- gRPC `PipelineService.CancelAsync(runId)` — best-effort cancel (FR-PIPE-08)
- Adapter `IPipelineExecutorAdapter` — wraps the existing executor with the new identity layer
- Senior Manager and Engineer paths both work (Engineer requires assignment)
- WPF: pipeline list view with Trigger / Cancel buttons (Operator portion of the unified screen, FR-UI-01)
- Web Client: same for Senior Manager; Engineer sees their assigned subset
- Run state transitions: `Running → Passed/Failed/Cancelled` reported by the executor via a callback

### Out of scope

- Retry hierarchy (Phase 4)
- Bulk operations (Phase 5)
- Enable / disable (Phase 6)
- Read views for completed runs (Phase 7 polishes this)
- Locks across clients (Phase 3)

### Files to create

```
ControlNode.Core/Pipelines/Pipeline.cs
ControlNode.Core/Pipelines/Run.cs
ControlNode.Core/Pipelines/Event.cs
ControlNode.Core/Pipelines/ActionGroup.cs
ControlNode.Core/Pipelines/Action.cs
ControlNode.Core/Pipelines/PipelineState.cs
ControlNode.Core/Pipelines/RunState.cs
ControlNode.Core/Pipelines/IPipelineRepository.cs
ControlNode.Core/Pipelines/IRunRepository.cs
ControlNode.Core/Pipelines/IPipelineExecutorAdapter.cs

ControlNode.Infrastructure/Persistence/Configurations/PipelineConfiguration.cs
ControlNode.Infrastructure/Persistence/Configurations/RunConfiguration.cs
ControlNode.Infrastructure/Persistence/Repositories/PipelineRepository.cs
ControlNode.Infrastructure/Persistence/Repositories/RunRepository.cs
ControlNode.Infrastructure/Pipelines/PipelineExecutorAdapter.cs
ControlNode.Infrastructure/Pipelines/WatchListImporter.cs
ControlNode.Infrastructure/Pipelines/WatchListImportWorker.cs

ControlNode.Application/Pipelines/TriggerPipelineHandler.cs
ControlNode.Application/Pipelines/CancelPipelineHandler.cs
ControlNode.Application/Pipelines/ListPipelinesHandler.cs
ControlNode.Application/Pipelines/GetPipelineDetailHandler.cs

ControlNode.Api/Services/PipelineGrpcService.cs
ControlNode.Api/Callbacks/ExecutorCallbackEndpoint.cs   (executor → API when run finishes)

ControlNode.WpfClient/Views/Operator/PipelineListView.xaml(.cs)
ControlNode.WpfClient/ViewModels/Operator/PipelineListViewModel.cs

ControlNode.WebClient/src/views/PipelineListView.tsx
ControlNode.WebClient/src/views/MyPipelinesView.tsx
ControlNode.WebClient/src/api/pipelines.ts
ControlNode.WebClient/src/hooks/usePipelines.ts
ControlNode.WebClient/src/hooks/useTriggerPipeline.ts

proto/pipeline.proto

ControlNode.Application.Tests/Phase2/TriggerAuthzTests.cs
ControlNode.Application.Tests/Phase2/AtomicTransitionTests.cs
ControlNode.Api.Tests/Phase2/PipelineEndToEndTests.cs
ControlNode.Api.Tests/Phase2/ConcurrentTriggerStressTest.cs
```

### Exit criteria

- [ ] WatchList.xml imports cleanly; idempotent on re-run
- [ ] Engineer triggers an assigned pipeline → Running state; Run row created
- [ ] Engineer attempting an unassigned pipeline → `PermissionDenied` with reason `no-assignment`
- [ ] Senior Manager triggers any pipeline successfully
- [ ] Two concurrent triggers on the same Idle pipeline: exactly one wins (stress test runs 50 iterations of 10 concurrent triggers; zero double-triggers observed)
- [ ] Cancel transitions Run → Cancelled and signals the executor
- [ ] Executor callback updates Run state to Passed/Failed; UI reflects it within 2 s
- [ ] Audit entries show TriggerByPipeline and CancelByPipeline actions

### Demo script

1. Trigger import; show 7 pipelines from WatchList.xml.
2. As ravi.kumar (assigned pipelines: A and B), trigger A → Running.
3. Try to trigger C (not assigned) → denied with clear message.
4. As SrMgr "priya.s", trigger C → succeeds.
5. Cancel C → transitions to Cancelled.
6. Show the audit log for the demo session.

### Risks

| Risk | Mitigation |
|---|---|
| Executor adapter is the integration point; bugs are subtle | Spend half a day on a thin proof-of-concept before committing the design. Decide synchronous vs async callbacks early. |
| WatchList.xml schema drifts | Pin the parser to a versioned schema; import worker logs schema-version mismatches. |
| Engineer sees pipelines they shouldn't | Server filters by assignment in `ListPipelinesHandler`. Verified by a test that creates 3 pipelines and assigns 1, then asserts the list returns 1. |

### Estimated effort

**3 engineer-weeks.**

---

## Phase 3 — Lock Coordination Integration

**Goal**: when a Web user triggers a pipeline, the WPF user sees it locked. Conflicts are visible in real time; force-release is available to authorized users.

### Scope

This is the body of `Pipeline_Lock_Coordination_Spec.md`. See that document for full detail. Folded into this phase rather than freestanding because identity (Phase 0) and triggers (Phase 2) are now in place.

Key adjustments to the original Lock spec (detailed in `03_Integration_With_Lock_Spec.md`):

- `IUserContext` is the one from Phase 0, not a freshly-defined one
- `pipeline:force-release` becomes the `Pipeline_ForceRelease` permission, in the catalog
- "Operators group" capability check becomes a permission check via `IAuthorizationService`
- The Lock spec's SignalR hub coexists with the gRPC services — same auth interceptor

### Files to create

See `Pipeline_Lock_Coordination_Spec.md` §12 file generation order, minus duplicates that Phase 0/1/2 already provided.

### Exit criteria

The exit criteria from `Pipeline_Lock_Coordination_Spec.md` Section 13 (testing strategy) apply unchanged.

### Estimated effort

**2 engineer-weeks** (down from the Lock spec's original estimate because identity is already in place).

---

## Phase 4 — Retry Hierarchy

**Goal**: an Engineer can retry a failed Action, Action Group, or Event in a failed run.

### Scope

- `Events`, `ActionGroups`, `Actions` tables fully populated by the executor callbacks (skeleton from Phase 2 fleshed out)
- gRPC `PipelineService.RetryAsync(runId, scope, targetId)` with `RetryScope` enum
- Permission inheritance: `Pipeline_Retry` ≡ `Pipeline_Trigger` of parent
- Executor adapter: `RetryAsync(scope, targetId)`
- WPF and Web UI: hierarchical tree of Run → Events → ActionGroups → Actions with state pills and per-level Retry buttons (FR-UI-03)

### Out of scope

- "Retry on schedule" / auto-retry — out of v1
- Partial-retry of Actions across multiple ActionGroups — single-target only

### Exit criteria

- [ ] A failed Run shows the full tree, expandable to the Action level
- [ ] Engineer retries an Action → that Action only re-runs
- [ ] Retry of a Group re-runs all Actions in that Group
- [ ] Retry of an Event re-runs all Groups in that Event
- [ ] Unauthorized retry → denied
- [ ] After a retry passes, the parent Run state is recomputed (if no other failures remain, transitions to Passed)

### Estimated effort

**2 engineer-weeks.**

---

## Phase 5 — Bulk Operations (Trigger All / Cancel All)

**Goal**: Admin or Senior Manager can trigger all Idle or cancel all Running pipelines in one click, with race-safe per-pipeline outcomes reported back.

### Scope

- gRPC `PipelineService.TriggerAllIdleAsync`, `CancelAllRunningAsync`
- Atomic batch transitions (Design §5.2-5.3)
- Result envelope listing triggered + skipped (with reasons)
- WPF and Web UI: bulk action buttons with confirmation modals (FR-UI-05)
- Authz: `Pipeline_TriggerAll`, `Pipeline_CancelAll` (Admin + SrMgr only)

### Exit criteria

- [ ] "Trigger All Idle" with 5 idle + 1 running + 1 disabled returns triggered: 5, skipped: [running, disabled] with correct reasons
- [ ] Per NFR-PRF-03, 50 pipelines triggered in <3 s (measured)
- [ ] Concurrent invocation of TriggerAll from two SrMgrs: no double-trigger anywhere

### Estimated effort

**1 engineer-week.**

---

## Phase 6 — Enable / Disable Pipelines

**Goal**: Admin can disable a pipeline; it cannot be triggered until re-enabled.

### Scope

- `Pipelines.IsEnabled` column already in schema; toggle endpoint
- gRPC `PipelineService.EnableAsync`, `DisableAsync`
- Authz: `Pipeline_Enable`, `Pipeline_Disable` (Admin only)
- Trigger and TriggerAll respect `IsEnabled`
- WPF Admin UI affordance to toggle
- UI status indicator updates across both clients in real time

### Exit criteria

- [ ] Disabled pipeline can't be triggered individually (returns `FailedPrecondition`)
- [ ] Disabled pipeline skipped by TriggerAll (in `skipped` list with reason `disabled`)
- [ ] Disabling a Running pipeline does NOT cancel it; the in-flight Run completes normally
- [ ] Only Admin sees the Enable / Disable button

### Estimated effort

**1 engineer-week.**

---

## Phase 7 — Read Paths & Guest Access

**Goal**: any role (including Guest) can browse pipelines and run history; mutations are restricted. Engineer sees their assigned subset.

### Scope

- gRPC `PipelineService.ListAsync` returns role-filtered subset (Engineer → assigned only)
- gRPC `RunService.ListAsync(pipelineId, paging)`
- gRPC `RunService.GetAsync(runId)` with full hierarchy
- Guest path: token issued by `LoginAsService.ContinueAsGuestAsync`; read RPCs return; write RPCs `PermissionDenied`
- WPF: full pipeline list, full run history
- Web: pipeline list (filtered), run detail, results
- Performance: NFR-PRF-02 (pipeline list <500 ms p95 for 200 pipelines, 50k runs)

### Exit criteria

- [ ] Guest can browse 100 pipelines and 1000 runs in <2 s total page load
- [ ] Guest attempting a trigger via raw gRPC call → denied with `guest-readonly`
- [ ] Engineer's pipeline list contains only assigned pipelines (verified by integration test)
- [ ] Pagination works at 100/page

### Estimated effort

**2 engineer-weeks.**

---

## Phase 8 — Notifications & Flaky Detection

**Goal**: when a pipeline fails 3 times in a row, the assigned Engineers and the Senior Manager get an email. Flaky pipelines surface in the Manager view.

### Scope

- `FlakyAnalyzerWorker` (Design §8) running every 15 minutes
- `NotificationDispatcherWorker` consuming the queue every 30 s
- `INotificationChannel` abstraction + `EmailNotificationChannel`
- Configurable threshold (N consecutive, window W, transitions T)
- Cooldown table to prevent spam
- Engineer-level mute (FR-NTF-04) — Web Client UI
- Manager/Admin dashboard view of "flaky pipelines" (Web Client section)

### Exit criteria

- [ ] Three consecutive failures of a pipeline → exactly one email arrives at each assigned Engineer's inbox and the SrMgr's inbox within 30 s of the third failure
- [ ] A 4th failure within the cooldown window does NOT generate a duplicate email
- [ ] After cooldown expires, the next failure does generate a fresh email
- [ ] Engineer mutes a pipeline; no emails for that pipeline reach them while the mute is active
- [ ] Flaky pattern (3+ alternations in 10 runs) detected and surfaced in the UI

### Risks

| Risk | Mitigation |
|---|---|
| SMTP outages cause notifications to be lost | `NotificationDispatcher` retries with exponential backoff; failed dispatches stay in the queue. |
| Detection thresholds need tuning per pipeline | Make them configurable per-pipeline (default global) in a follow-up; v1 uses global config only. |

### Estimated effort

**3 engineer-weeks.**

---

## Phase 9 — Reports

**Goal**: Senior Manager generates a weekly or monthly consolidated PDF/CSV report covering all pipelines.

### Scope

- `ReportGeneratorWorker` — weekly Monday 06:00 UTC, monthly 1st 06:00 UTC
- On-demand RPC `ReportService.GenerateAsync(period, format)`
- Engineer-scoped variant: `ReportService.GenerateMyAsync` (assigned pipelines only)
- PDF rendering via QuestPDF (or equivalent); CSV via standard library
- Storage: `wwwroot/reports/{yyyy-MM-dd}/{reportId}.pdf`; `Reports` table tracks metadata
- Web UI: report download view with filter by date range
- Authz: `Report_Generate` (Admin, SrMgr); `Report_View` (everyone, scoped per role)

### Exit criteria

- [ ] SrMgr generates a weekly report covering 7 days of runs; PDF downloads correctly
- [ ] CSV variant opens cleanly in Excel
- [ ] Engineer's "My Reports" includes only their assigned pipelines' data
- [ ] Scheduled run on Monday 06:00 produces last-week's report automatically
- [ ] Report generation does not block live operator actions (verified by load test)

### Estimated effort

**3 engineer-weeks.**

---

## Phase 10 — Audit Log Viewer & Hardening

**Goal**: Admin can browse, filter, and export the audit log. The system has been hardened against the NFRs not previously covered (rate limits, accessibility, structured logs, error responses).

### Scope

- WPF Audit Log section (UC-07)
- Filter UI: user, role, action, resource, date range, decision (allow/deny)
- Pagination at 200/page (audit log can be large)
- CSV export of filtered set
- 12-month retention background job (deletes audit entries older than 12 months)
- Performance optimization sweep based on production-load measurements
- Accessibility audit: keyboard navigation, color-blind-safe status indicators (NFR-USE-02)
- Structured logging review: every RPC handler emits the canonical log shape (NFR-LOG-01)
- Error response polish: every gRPC status carries a useful detail payload (NFR-USE-01)

### Exit criteria

- [ ] All eight SRS acceptance criteria (§12) pass on staging
- [ ] All NFRs in SRS §6 measured and met
- [ ] Audit log query for "all denies in the last 30 days" returns within 2 s
- [ ] Color-blind simulation tools (Coblis, etc.) confirm state-indicator readability
- [ ] Final security review sign-off

### Estimated effort

**2 engineer-weeks.**

---

## Aggregate Timeline

### Sequential team (2 engineers, no parallelization)

```
Phase 0    ───┐ 2w
Phase 0.5  ────┐ 1w   (Default mode UX)
Phase 1    ─────┐ 3w
Phase 2    ──────┐ 3w
Phase 3    ───────┐ 2w   (locks)
Phase 4    ────────┐ 2w   (retry)
Phase 5    ─────────┐ 1w   (bulk)
Phase 6    ──────────┐ 1w   (enable/disable)
Phase 7    ───────────┐ 2w   (reads + guest)
Phase 8    ────────────┐ 3w   (notifications)
Phase 9    ─────────────┐ 3w   (reports)
Phase 10   ──────────────┐ 2w   (audit + hardening)
                          ▼
                      Total: 25 weeks
```

### Parallelized team (5-6 engineers, recommended)

```
Phase 0       ──── 2w
Phase 0.5     ──── 1w     (Default mode UX, can overlap with start of Phase 1)
Phase 1, 7    ──── 3w     (User mgmt + read paths run in parallel)
Phase 2       ──── 3w
Phase 3, 4, 5, 6   ──── 2w (all four parallel after Phase 2)
Phase 8, 9    ──── 3w     (notifications + reports parallel)
Phase 10      ──── 2w     (the closer)
                 ▼
            Total: 16 weeks
```

The savings come from running 3, 4, 5, 6 in parallel (which all depend only on Phase 2) and running 8, 9 in parallel after Phase 7. Phase 0.5 can overlap with the first few days of Phase 1 since Phase 1 starts on server-side User management work before any UI is needed.

---

## Phase 0 — Day-One Checklist (for the team starting tomorrow)

1. Confirm OD-01 through OD-06 with the technical reviewer; lock them in writing.
2. Decide where the SQLite database file lives (typically `C:\ProgramData\TestAgent\orchestrator.db` on the controller node) and set the connection string. No "provisioning" needed — the file is created on first migration.
3. Create the `ControlNode.*` solution skeleton with five projects.
4. Wire up CI: build, test, EF migrations validation on every PR.
5. Set up the gRPC + Kestrel host with TLS dev cert. Confirm a `Ping` round-trip works from a Postman gRPC client.
6. Begin Phase 0 work in priority order: schema first, then `IAuthorizationService`, then interceptor, then `AuthGrpcService`, then tests.
7. Stand up a staging environment by end of Phase 0 so subsequent phases have a real demo target.

---

## When to Cut Scope

If timeline pressure is real, here is the priority order for trimming. Do not trim above the line:

1. **Phase 8 (notifications)** can ship without Flaky detection — keep only consecutive-failure. Saves ~1 week.
2. **Phase 9 (reports)** can ship CSV-only at v1; PDF is the bigger time sink. Saves ~1 week.
3. **Phase 4 (retry)** can ship with only Event-level retry initially; ActionGroup and Action retry follow. Saves ~3-5 days.
4. **Phase 10 (audit viewer)** can defer the UI; the audit log itself is written from Phase 0. Admins can query SQL directly until the viewer ships. Saves ~1 week.

— do not trim above the line —

— do not trim below the line either, these are essentials —

5. Phase 0, 1, 2, 3, 7 — these are the core. Without them there is no usable product.
6. Phase 5, 6 — small enough that trimming them yields little benefit.

---

## What's Next

Read `03_Integration_With_Lock_Spec.md` next if your team includes anyone working on the existing Lock spec — that document describes exactly how that work folds in as Phase 3.

Otherwise, start scheduling. Phase 0 doesn't wait on any decision other than the open decisions resolved in document 00.
