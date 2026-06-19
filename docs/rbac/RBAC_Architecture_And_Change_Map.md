# RBAC + Default Mode — Architecture & Change Map

**TestAgentSolution** · .NET 10 distributed gRPC test orchestration platform
Feature: Role-Based Access Control with a switchable Default (open) / Secured (authenticated) mode.

---

> ## Corrections applied (2026-06-17 verification pass)
>
> 1. **§5.1 Core** — Added 22 omitted files: `Audit/AuditEntry.cs`, `Audit/IAuditWriter.cs`, `Authorization/AuthDecision.cs`, `Authorization/IAuthorizationService.cs`, `Authorization/PipelineAuthorizationDeniedException.cs`, `Identity/ClientKind.cs`, `Identity/IUserContext.cs`, `Identity/PipelineAssignment.cs`, `Identity/Role.cs`, `Identity/Session.cs`, `Identity/SyntheticUserContext.cs`, 9 `Locking/` infrastructure files, `Models/NotificationEntities.cs`, `Models/NotificationOptions.cs`.
> 2. **§5.2 Persistence** — Added 8 omitted files: `DesignTimeDbContextFactory.cs`, `Audit/AuditRetentionWorker.cs`, 4 `Configurations/` files, 3 `Identity/` files. Added second migration `AddNotificationTables`.
> 3. **§5.3 Api** — Added 20+ omitted files: 8 Controllers (`AuthController`, `LocksController`, `NotificationsController`, `PipelinesController`, `UserController`, `WatchListController`, `AgentsController`, `HealthController`), 5 Services (`MuteService`, `PipelineAuthorizationGuard`, `SignalRNotifier`, `LockRecoveryService`, `LockRecoveryOptions`), 4 Hubs, 2 Contracts, `Middleware/RequestLoggingMiddleware.cs`, `Security/SecurityServiceExtensions.cs`. Fixed `ResultsController.cs` description — export + send-report live in `TestController.WebApi/Endpoints/ResultsEndpoints.cs`.
> 4. **§5.4 WPF** — Added 6 omitted files: `NotificationMuteClient.cs`, `NotificationSettingsViewModel.cs`, `DeleteUserConfirmDialogViewModel.cs`, `ResetPasswordDialogViewModel.cs`, `Controls/LockBadge.xaml`, `Controls/DefaultModeBanner.xaml`. Added `BuildResultsViewModel.cs` (Phase 9 gating).
> 5. **§5.5 WebClient** — Added 5 omitted files: `api.ts`, `userIdentity.ts`, `LockConflictModal.tsx`, `ForceReleaseDialog.tsx`, `ChangePasswordDialog.tsx`. Added new §5.6 (WebApi standalone endpoints) and §5.7 (Tests).
> 6. **§4 Permissions** — Verified: server and web catalogs in perfect alignment (19 permissions × 4 roles). Added explicit permission matrix table. Corrected SeniorManager description (also lacks `Pipeline_Enable`/`Pipeline_Disable`, not just audit).
> 7. **§10 Gap #1** — Rewritten: `isPrimaryHost` branching is **complete and working**. `WebApi-DB-Decoupling.md` is the outdated artifact. Marked gap as resolved.
> 8. **§8 Phase table** — Added missing key files per phase (e.g. Phase 8: `MuteService.cs`, `NotificationsController.cs`; Phase 9: `ResultsEndpoints.cs`, `ReportAuthzTests.cs`; Phase 10: `AuditRetentionWorker.cs`).

---

> **What this document is.** A single reference that explains (a) how the application is put together, and (b) exactly where the RBAC + Default Mode feature touches the codebase — phase by phase and layer by layer. Use it as a map.
>
> **How to read the references.** *File paths are the durable anchors.* Line numbers are approximate (they drift as code changes) and are given only as a starting point for "look around here."
>
> **Provenance.** Synthesized from the Phase 0–10 implementation work, verified against the live codebase on 2026-06-17.

---

## 1. The feature in one paragraph

The platform can run in two modes. In **Default mode**, there is no authentication: a synthetic "Default" user is injected, the WPF desktop app has full operator control, and the web client is read-only (an "Observer"). In **Secured mode**, RBAC is enforced: users log in, every action is checked against a role's permissions, every pipeline trigger is checked against per-user assignments, and every authorization decision is written to an audit trail. An administrator flips between modes live (no restart), and switching to Secured the first time runs a one-time wizard to create the initial admin. The whole point: the same tool is frictionless for a solo user and controlled for a team, with a clean switch between the two.

---

## 2. The solution at a glance

The feature spans six projects. Two are *hosts* (things that run), four are *libraries* (shared code).

| Project | Type | Responsibility in this feature |
|---|---|---|
| **TestControllerGrpc** | Host (WPF desktop) | The administrator/operator desktop app. Owns the SQLite database. Hosts an embedded ASP.NET Core API (`ControllerWebApiHost`) on port 5200. Contains all WPF UI for login, user management, mode switching, the audit viewer, and the permission-aware pipeline tree. |
| **TestController.WebApi** | Host (standalone IIS) | A separate web API host (port 81) that **proxies** DB-backed operations to the WPF host. Registers `ThrowingDbContextFactory` + Null* stubs so it never opens the database. Also hosts two standalone-only Minimal API endpoints (`/api/results/export` and `/api/results/send-report`). |
| **TestController.Api** | Library (shared) | The shared web layer used by both hosts: controllers, the RBAC feature registration (`AddRbacFeature`), gRPC/HTTP interceptors, authorization services, and the mode-transition service. |
| **TestController.Persistence** | Library | EF Core + SQLite. The `OrchestratorDbContext`, migrations, the audit writer, and the authorization service that reads/writes the DB. |
| **TestControllerGrpc.Core** | Library | The domain core: the permission model, identity types, locking primitives, configuration options, the proto-generated gRPC types, and the analysis/reporting engine. No UI, no DB. |
| **TestController.WebClient** | Host assets (React) | The browser client: React + Vite + Zustand + Tailwind + SignalR. Renders the capability-aware UI (triggerable / view-only / locked), guest login, and live updates. |

**Database ownership rule (locked decision):** the **WPF host is the sole owner of `orchestrator.db`**. Other hosts must proxy DB operations through it, never open the file directly. This is the resolution to the "database is locked" class of bugs. Enforced via `RbacFeatureExtensions.AddRbacFeature(isPrimaryHost: false)` which registers `ThrowingDbContextFactory`.

---

## 3. The two modes

Mode is controlled by a single flag: **`RBAC:Enabled`** (see `TestControllerGrpc.Core/Configuration/RbacOptions.cs`). It is flipped *live* — written back to configuration through `WritableOptions` by the `RbacModeTransitionService`, and observed via `IOptionsMonitor<RbacOptions>` so running components react without a restart.

| | Default mode (`RBAC:Enabled = false`) | Secured mode (`RBAC:Enabled = true`) |
|---|---|---|
| Identity | Synthetic `DefaultUser` (`Core/Identity/DefaultUser.cs`) | Real authenticated `User` with a role |
| WPF desktop | Full operator control | Login required; **Administrator only** (Policy A) |
| Web client | **Read-only Observer** — view everything, change nothing | Per-role: Admin / Senior Manager / Engineer / Guest |
| Auth interceptor | Bypassed (`SessionAuthInterceptor` returns early when not enabled) | Enforced on every call |
| Pipeline triggering | Open | Gated by permission **and** per-user assignment |

**Where the mode lives in code:**
- `Core/Configuration/RbacOptions.cs` — the `Enabled` flag.
- `Core/Configuration/WritableOptions.cs` — lets the transition service flip it live.
- `TestControllerGrpc/Services/CurrentUserHolder.cs` — `IsSecuredMode` derives from the flag; holds the current user.
- `TestController.Api/SystemMode/RbacModeTransitionService.cs` — performs the switch (archive/reactivate users, flip the flag). Switch-to-Default archives users (`IsActive = false`); switch-to-Secured reactivates them.
- `TestController.Api/Interceptors/SessionAuthInterceptor.cs` — short-circuits when RBAC is disabled.
- `TestControllerGrpc/App.xaml.cs` — at startup, Default mode goes straight to `MainWindow`; Secured mode routes through login.

---

## 4. The roles and the permission catalog

Five roles. The permission model is defined once in the Core library and mirrored in the web client for UI gating (the server is always the source of truth).

| Role | Where it lives | Reach |
|---|---|---|
| **Administrator** | WPF + server-enforced | All 19 permissions. Bypasses pipeline-assignment checks (can trigger any pipeline). The only role allowed to log into the WPF desktop (Policy A). |
| **Senior Manager** | Web | Full operator rights (trigger/cancel/retry, bulk ops, force-release, report generation, notification mute). **Not** audit viewing, user management, or pipeline enable/disable. 10 permissions. |
| **Engineer** | Web | Scoped to **assigned** pipelines. Can view all, trigger only assigned. Has `Report_View`, `Pipeline_View`, `Notification_Mute`, and single-pipeline trigger/cancel/retry. 6 permissions. |
| **Guest** | Web | Anonymous session token. **Read-only** (`Pipeline_View`, `Report_View`). Never writes. |
| **Observer** | Web, Default mode | The Default-mode web identity. Read-only, same as Guest in spirit. |

**Permission definitions (server, the source of truth):**
- `TestControllerGrpc.Core/Authorization/Permission.cs` — the enum (19 values): `Pipeline_View`, `Pipeline_Trigger`, `Pipeline_Cancel`, `Pipeline_Retry`, `Pipeline_TriggerAll`, `Pipeline_CancelAll`, `Pipeline_Enable`, `Pipeline_Disable`, `Pipeline_ForceRelease`, `User_Create`, `User_Update`, `User_Delete`, `User_Assign`, `User_Revoke`, `Report_View`, `Report_Generate`, `Audit_View`, `Audit_Export`, `Notification_Mute`.
- `TestControllerGrpc.Core/Authorization/PermissionCatalog.cs` — the role → permission mapping.

**Permission mirror (web, for UI only):**
- `TestController.WebClient/src/lib/capabilities.ts` — `PERMISSION_CATALOG` (frozen), `DEFAULT_MODE_READ_PERMISSIONS` (`Pipeline_View`, `Report_View`), the `can()` function, and `getDisabledReason()`.
- `TestController.WebClient/src/lib/capabilities.test.ts` — the full role × mode × permission test matrix. **This file is the clearest single description of the intended access rules** — read it to understand exactly who can do what.

**Verified alignment (2026-06-17):** server `PermissionCatalog.cs` and web `capabilities.ts` are in **perfect alignment** across all 4 roles and all 19 permissions. No discrepancies.

**Server-side role → permission table (verified):**

| Permission | Admin | SrMgr | Engineer | Guest |
|---|---|---|---|---|
| Pipeline_View | ✓ | ✓ | ✓ | ✓ |
| Pipeline_Trigger | ✓ | ✓ | ✓* | |
| Pipeline_Cancel | ✓ | ✓ | ✓* | |
| Pipeline_Retry | ✓ | ✓ | ✓* | |
| Pipeline_TriggerAll | ✓ | ✓ | | |
| Pipeline_CancelAll | ✓ | ✓ | | |
| Pipeline_Enable | ✓ | | | |
| Pipeline_Disable | ✓ | | | |
| Pipeline_ForceRelease | ✓ | ✓ | | |
| User_Create | ✓ | | | |
| User_Update | ✓ | | | |
| User_Delete | ✓ | | | |
| User_Assign | ✓ | | | |
| User_Revoke | ✓ | | | |
| Report_View | ✓ | ✓ | ✓ | ✓ |
| Report_Generate | ✓ | ✓ | | |
| Audit_View | ✓ | | | |
| Audit_Export | ✓ | | | |
| Notification_Mute | ✓ | ✓ | ✓ | |

*\* Engineer: requires pipeline assignment for resource-scoped permissions.*

**The read-vs-write rule (locked decision):** *reads are open to everyone* (including Guest and the Default-mode Observer) — dashboards, results, the pipeline tree, agent monitor. Only *writes* are gated. The one exception is **audit data**, which is admin-only even though it's a read (it's sensitive). Pipeline *triggering* additionally requires an *assignment* for non-admins, while *viewing* a pipeline never does.

**`IsReadPermission()` in `AuthorizationService.cs`:** Only `Pipeline_View` and `Report_View` are classified as read permissions. This means Default-mode web clients can only exercise those two.

---

## 5. Change map by layer

This is the heart of the document: where the feature actually lives. Grouped by project, most-foundational first.

### 5.1 TestControllerGrpc.Core (domain core)

| Path | Role in the feature |
|---|---|
| `Authorization/Permission.cs` | The permission enum (19 gated actions). |
| `Authorization/PermissionCatalog.cs` | Role → permission mapping. |
| `Authorization/IAuthorizationService.cs` | The `CanAsync()` contract; returns `AuthDecision`. |
| `Authorization/AuthDecision.cs` | Allow/Deny result with `HumanReadable` reason. |
| `Authorization/PipelineAuthorizationDeniedException.cs` | Typed exception for pipeline auth failures. |
| `Audit/AuditEntry.cs` | One row of the RBAC audit log. |
| `Audit/IAuditWriter.cs` | Fire-and-forget audit interface (`Enqueue`). |
| `Identity/IUserContext.cs` | The user abstraction consumed by authorization. |
| `Identity/User.cs` | The user entity (`IsActive`, role, etc.). |
| `Identity/Role.cs` | The role enum (`Administrator`, `SeniorManager`, `Engineer`, `Guest`). |
| `Identity/ClientKind.cs` | `Wpf` vs `Web` — used in Default-mode gating and audit. |
| `Identity/DefaultUser.cs` | The synthetic user injected in Default mode. |
| `Identity/SyntheticUserContext.cs` | Wraps a synthetic identity for non-authenticated paths. |
| `Identity/PipelineAssignment.cs` | Entity: which user is assigned to which pipeline. |
| `Identity/Session.cs` | Entity: authenticated session (token, expiry). |
| `Locking/PipelineLock.cs` | The pipeline-lock domain object. |
| `Locking/LockKind.cs` | Lock kinds (e.g. `Execution`, `Retry`). |
| `Locking/LockStatus.cs` | Lock status enum. |
| `Locking/ILockRegistry.cs` | Interface for lock storage. |
| `Locking/LockRegistry.cs` | In-memory lock registry implementation. |
| `Locking/LockEvent.cs` | Lock-change events for pub/sub. |
| `Locking/AcquireResult.cs` | Result of a lock acquisition attempt. |
| `Locking/LockOptions.cs` | Lock configuration (expiry timeout, etc.). |
| `Locking/LockExpirySweeper.cs` | Background job that releases expired locks. |
| `Locking/OwnerIdentity.cs` | Lock owner identification. |
| `Services/AgentLockManager.cs` | Core agent-lock coordination (the engine behind "Locked · owner"). |
| `Services/AgentLockEvents.cs` | Lock-change events published via the event aggregator. |
| `Configuration/RbacOptions.cs` | The `Enabled` mode flag. |
| `Configuration/WritableOptions.cs` | Writable wrapper so the mode can be flipped live. |
| `Models/NotificationEntities.cs` | `NotificationMute` + `NotificationCooldown` entities (Phase 8). |
| `Models/NotificationOptions.cs` | Notification configuration (`AutoAlertEnabled`, `CooldownHours`). |

### 5.2 TestController.Persistence (database)

| Path | Role in the feature |
|---|---|
| `OrchestratorDbContext.cs` | The EF Core context. Exposes `Users`, `PipelineAssignments`, `Sessions`, `AuditEntries`, `NotificationMutes`, `NotificationCooldowns`. |
| `DesignTimeDbContextFactory.cs` | Design-time factory for `dotnet ef migrations`. |
| `Configurations/AuditEntryConfiguration.cs` | Maps the `AuditEntries` table (3 query indexes). |
| `Configurations/UserConfiguration.cs` | Maps the `Users` table. |
| `Configurations/SessionConfiguration.cs` | Maps the `Sessions` table. |
| `Configurations/PipelineAssignmentConfiguration.cs` | Maps the `PipelineAssignments` table. |
| `Configurations/NotificationConfigurations.cs` | Maps `NotificationMutes` + `NotificationCooldowns` (Phase 8). |
| `Migrations/20260612041335_Initial.cs` | Creates `Users`, `PipelineAssignments`, `Sessions`, and `AuditEntries` (with the three query indexes `IX_AuditEntries_TimestampUtc`, `_ActionName_TimestampUtc`, `_UserId_TimestampUtc`). |
| `Migrations/20260617021814_AddNotificationTables.cs` | Creates `NotificationMutes` + `NotificationCooldowns` (Phase 8). |
| `Audit/QueuedAuditWriter.cs` | Buffered, channel-based writer that batches audit rows into the DB. Contains `AuditDrainWorker` hosted service. |
| `Audit/AuditRetentionWorker.cs` | Daily retention purge for old audit entries (Phase 10). |
| `Authorization/AuthorizationService.cs` | DB-backed authorization checks; also enqueues audit entries. Contains `IsReadPermission()`. |
| `Identity/PasswordHasher.cs` | Argon2-based password hashing. |
| `Identity/ReadablePasswordGenerator.cs` | Generates human-readable temporary passwords. |
| `Identity/SessionStore.cs` | Session token storage and validation. |

### 5.3 TestController.Api (shared web layer)

| Path | Role in the feature |
|---|---|
| `RbacFeatureExtensions.cs` | **`AddRbacFeature(isPrimaryHost)`** — registers all RBAC services. Primary host gets real DB + services; secondary gets `ThrowingDbContextFactory` + `NullSessionStore` + `NullAuthorizationService` + `NullAuditWriter`. Contains `DatabaseInitializerService` (runs migrations at startup). |
| `Interceptors/SessionAuthInterceptor.cs` | gRPC/HTTP interceptor that authenticates the session and short-circuits in Default mode. |
| `Interceptors/AuditLoggingInterceptor.cs` | Enqueues an audit entry for intercepted calls. |
| `Security/SecurityAuditLogger.cs` | A **separate, file-based** security log (`RetentionDays = 90`). Distinct from the DB audit table — see §6. |
| `Security/SecurityOptions.cs` | Security configuration (rate limit, audit file path, retention, TLS). |
| `Security/SecurityServiceExtensions.cs` | `AddMultiIdentitySecurity()` — NTLM/negotiate + API-key + roles middleware. |
| `Security/SessionOwnershipChecker.cs` | Verifies a session owns the thing it's acting on. |
| `Security/AuthorizationPolicies.cs` | ASP.NET Core authorization policy definitions. |
| `Services/AuthService.cs` | Login / guest-login / session validation; audits auth events. |
| `Services/UserService.cs` | User CRUD + pipeline assignment; audits each change (8+ audit call sites). |
| `Services/PipelineService.cs` | Pipeline operations; authorization + audit. |
| `Services/PipelineAuthorizationGuard.cs` | Centralized pipeline permission + assignment check. |
| `Services/RetryService.cs` | Phase 4 — gated retry endpoint; audits retry. |
| `Services/EnableDisableService.cs` | Phase 6 — gated enable/disable; audits the toggle. |
| `Services/LockService.cs` | Phase 3 — pipeline-lock acquire/release; audits and broadcasts. |
| `Services/LockRecoveryService.cs` | Recovers stale locks on startup. |
| `Services/MuteService.cs` | Phase 8 — notification mute/unmute with authorization + audit. |
| `Services/SignalRNotifier.cs` | Broadcasts state changes (lock, permission, mode) to connected web clients. |
| `Controllers/AuthController.cs` | Login/logout/guest-login/change-password/me endpoints. |
| `Controllers/UserController.cs` | User CRUD + pipeline assignment REST endpoints. |
| `Controllers/PipelinesController.cs` | Pipeline trigger/cancel with authorization. |
| `Controllers/ExecutionController.cs` | Trigger/cancel/force-release; authorization + audit. |
| `Controllers/LocksController.cs` | Lock list/get/release/force-release REST endpoints. |
| `Controllers/NotificationsController.cs` | Phase 8 — notification mute list/mute/unmute REST endpoints. |
| `Controllers/AuditController.cs` | Phase 10 — `GET /api/audit` (filter + paginate) and `GET /api/audit/export` (streamed CSV), admin-gated via `Audit_View`. |
| `Controllers/ResultsController.cs` | Results, trends (`/api/results/trends`), flaky, alerts, build detail. **Export and send-report are NOT here** — they live in `TestController.WebApi/Endpoints/ResultsEndpoints.cs`. |
| `Controllers/SystemModeController.cs` | Reports current mode (`/api/system-mode`). |
| `Controllers/SecurityController.cs` | Security status; sets `CanViewAuditLog = (role == Admin)`. |
| `Controllers/WatchListController.cs` | WatchList tree data for the web client. |
| `Controllers/AgentsController.cs` | Agent list + per-agent audit (`/api/agents/{name}/audit`). |
| `Controllers/HealthController.cs` | Health check endpoint. |
| `Hubs/ControllerHub.cs` | SignalR hub for real-time state broadcasts. |
| `Hubs/LockBroadcaster.cs` | Broadcasts lock acquire/release events to web clients. |
| `Hubs/SystemModeBroadcaster.cs` | Broadcasts mode-change events to web clients. |
| `Hubs/HubExceptionFilter.cs` | Global exception filter for SignalR hub methods. |
| `Contracts/PipelineLockDto.cs` | Lock DTO shared between server and clients. |
| `Contracts/LockMapper.cs` | Maps domain lock objects to DTOs. |
| `SystemMode/RbacModeTransitionService.cs` | The switch-mode engine: archive/reactivate users, flip `RBAC:Enabled`, audit the transition. |
| `Middleware/RequestLoggingMiddleware.cs` | HTTP request/response logging middleware. |

### 5.4 TestControllerGrpc (WPF host)

| Path | Role in the feature |
|---|---|
| `Services/CurrentUserHolder.cs` | Holds the current user; `IsSecuredMode`; raises `UserChanged`. The badge and gating react to this. |
| `Services/CapabilityChecker.cs` | The WPF-side "can this user do X on pipeline Y?" check. Subscribes to `CapabilitiesChanged`. |
| `Services/AuthClient.cs` | Calls the auth API (login, guest, change password); `IsGuest`. |
| `Services/UserManagementClient.cs` | Calls the user-management API from the WPF admin UI. |
| `Services/LockStateService.cs` | Holds live lock state in WPF; subscribes to lock broadcasts. |
| `Services/SystemModeClient.cs` | Reads/observes mode; `NotifyLocalModeChange`. |
| `Services/AuditClient.cs` | Phase 10 — calls `/api/audit` + `ExportCsvAsync` for the WPF audit viewer. |
| `Services/NotificationDispatcher.cs` | Phase 8 — hosted dispatcher that runs the detectors on completion and sends alert email via the existing generator (respecting mute/cooldown). |
| `Services/NotificationMuteClient.cs` | Phase 8 — WPF HTTP client for notification mute list/mute/unmute. |
| `Services/ControllerWebApiHost.cs` | The embedded ASP.NET Core API hosted inside WPF (port 5200) — the thing the web client proxies to. |
| `Controls/UserIdentityBadge.xaml(.cs)` | The identity badge: Administrator / SeniorManager / Engineer / Guest / Observer / Default variants (icons + colors). |
| `Controls/LockBadge.xaml(.cs)` | Phase 3 — lock status badge on pipeline tree nodes. |
| `Controls/DefaultModeBanner.xaml(.cs)` | Default-mode indicator banner shown in the WPF shell. |
| `ViewModels/Login/LoginViewModel.cs` | WPF login (no guest path — Policy A). |
| `ViewModels/Login/ChangePasswordViewModel.cs` | Change-password flow. |
| `ViewModels/Settings/SecurityModeViewModel.cs` | The mode-switch UI (switch to Secured/Default, disable-confirm dialog). |
| `ViewModels/Admin/UserManagementViewModel.cs` | The admin user-management screen. |
| `ViewModels/Admin/AddUserDialogViewModel.cs` | Create-user dialog. |
| `ViewModels/Admin/AssignPipelinesDialogViewModel.cs` | Assign-pipelines-to-user dialog. |
| `ViewModels/Admin/DeleteUserConfirmDialogViewModel.cs` | Delete-user confirmation dialog. |
| `ViewModels/Admin/ResetPasswordDialogViewModel.cs` | Admin password reset dialog. |
| `ViewModels/Admin/AuditViewerViewModel.cs` | Phase 10 — the WPF audit viewer (filter, paginate, export CSV). |
| `ViewModels/Admin/NotificationSettingsViewModel.cs` | Phase 8 — notification settings display + mute list management. |
| `ViewModels/MainViewModel.cs` (+ partials `.Execution`, `.Security`, `.Agents`, `.Log`) | The main shell VM. `MainViewModel.Security.cs` drives Default-mode banner visibility; `.Execution.cs` holds trigger/cancel/retry and the bulk-trigger filter (`WatchItems.Where(IsEnabled)`). |
| `ViewModels/TreeNodeViewModel.cs` | The pipeline-tree node VM. Carries the "visible but cannot trigger (not assigned / Guest)" state and the enable/disable cascade. |
| `ViewModels/BuildResultsViewModel.cs` | Phase 9 — report commands gated by `CanSendReport` (Report_Generate) and `CanExportReport` (Report_View) via `CapabilityChecker`. |
| `ViewModels/LockBadgeViewModel.cs`, `LockConflictDialogViewModel.cs` | Phase 3 — lock badge + conflict dialog. |
| `Views/MainWindow.xaml(.cs)` | Ribbon integration (HOME/EXECUTION/TOOLS/ADMIN), the security button, the badge, and the tree context menu (retry, enable/disable). |
| `Views/Login/*` | `LoginPage.xaml(.cs)`, `ChangePasswordPage.xaml(.cs)`. |
| `Views/Admin/*` | `UserManagementPage`, `AddUserDialog`, `AssignPipelinesDialog`, `AuditViewerPage`, `UserListView`, `UserDetailView`, `DeleteUserConfirmDialog`, `ResetPasswordDialog`. |
| `Views/Settings/*` | `SecurityModePanel`, `InitialAdminWizard`, `DisableRbacConfirmDialog`, `SettingsView`. |
| `App.xaml.cs` | Startup routing by mode; DI registration; single-instance mutex. |

### 5.5 TestController.WebClient (React)

| Path | Role in the feature |
|---|---|
| `src/lib/capabilities.ts` | The permission mirror + `can()` + `getDisabledReason()` + `DEFAULT_MODE_READ_PERMISSIONS`. |
| `src/lib/capabilities.test.ts` | The authoritative access-rules test matrix. |
| `src/lib/api.ts` | The `apiFetch<T>()` wrapper with 403 → `AuthDeniedToast` handling. |
| `src/lib/userIdentity.ts` | User identity utility (X-User-Id for requests). |
| `src/stores/authStore.ts` | Auth state; `login`, `loginAsGuest`, `isGuest`, `fetchMe`. |
| `src/stores/systemModeStore.ts` | Current mode (`default` / `secured`). |
| `src/stores/lockStore.ts` | Live pipeline-lock state (`PipelineLockDto`). |
| `src/stores/watchlistStore.ts` | Pipeline tree + the show-all-but-gate filtering (`useFilteredWatchItems`). |
| `src/stores/resultsStore.ts` | Trends + alerts state. |
| `src/hooks/useCapabilities.ts` | `useCan()` / `useDisabledReason()` — the React gating hooks. |
| `src/hooks/useResults.ts` | Phase 9 — `fetchTrends`, `exportReport('html'\|'csv')`, `sendReport` (uses `apiFetch` for 403 handling). |
| `src/hooks/useMutes.ts` | Phase 8 — notification mute list. |
| `src/signalr/PermissionEvents.ts` | **Live permissions** — handles the "permissions changed → refetch capabilities" broadcast. |
| `src/signalr/LockEvents.ts` | Phase 3 — lock acquire/release events → update `lockStore`. |
| `src/signalr/SystemModeEvents.ts` | Mode-change events. |
| `src/components/header/UserIdentityBadge.tsx` | Web badge: Guest / Observer / role variants. |
| `src/components/header/UserMenu.tsx` | Change-password (hidden for guests) / sign-out. |
| `src/components/header/DefaultModeBanner.tsx` | The "read-only Observer" banner in Default mode. |
| `src/components/header/ChangePasswordDialog.tsx` | Password change dialog for authenticated web users. |
| `src/components/common/DisabledTriggerButton.tsx` | The view-only/disabled trigger control (shared visual). |
| `src/components/common/AuthDeniedToast.tsx` | 403 toast (the server-side backstop surfacing). |
| `src/components/watchlist/WatchListTree.tsx`, `LockBadge.tsx` | The capability-aware tree + lock badge. |
| `src/components/results/BuildDetail.tsx` | Phase 9 — report export buttons + send button (gated by `useCan('Report_Generate')` with disabled tooltip). |
| `src/components/results/TrendCharts.tsx` | Trend charts + Phase 8 mute controls. |
| `src/components/results/FailureAnalysisDialog.tsx` | Failure-pattern analysis dialog. |
| `src/components/dialogs/LockConflictModal.tsx` | Phase 3 — pipeline lock conflict dialog. |
| `src/components/dialogs/ForceReleaseDialog.tsx` | Phase 3 — admin force-release dialog. |
| `src/views/LoginView.tsx` | Login + "Continue as Guest". |

### 5.6 TestController.WebApi (standalone-only endpoints)

| Path | Role in the feature |
|---|---|
| `Endpoints/ResultsEndpoints.cs` | Phase 9 — `/api/results/export/{buildNumber}` (ungated, Report_View) and `/api/results/send-report` (gated by `Report_Generate` via `SessionAuthInterceptor` + `IAuthorizationService.CanAsync`, with fire-and-forget audit). |
| `Program.cs` | Calls `AddRbacFeature(isPrimaryHost: false)`. Mounts endpoints with `.RequireAuthorization(SecurityPolicies.User)`. |

### 5.7 Tests

| Path | Role in the feature |
|---|---|
| `TestController.WebApi.Tests/Rbac/ReportAuthzTests.cs` | Phase 9 — Report_View allowed for all roles; Report_Generate denied for Engineer/Guest. 9 tests. |
| `TestController.WebApi.Tests/Rbac/NotificationMuteTests.cs` | Phase 8 — mute/unmute authorization, cooldown, audit. 9 tests. |
| `TestController.WebApi.Tests/Rbac/AuditControllerTests.cs` | Phase 10 — audit query/export authorization. 9 tests. |
| `TestController.WebApi.Tests/Rbac/PipelineTriggerAuthzTests.cs` | Phase 2 — pipeline trigger authorization per role/assignment. |
| `TestController.WebApi.Tests/Rbac/AuthE2ETests.cs` | Phase 1 — end-to-end login/logout/session. |
| `TestController.WebApi.Tests/Rbac/AuthMeEndpointTests.cs` | Phase 1 — `/api/auth/me` endpoint. |
| `TestController.WebApi.Tests/Rbac/ModeSwitchE2ETests.cs` | Phase 1 — Default ↔ Secured mode switch. |
| `TestController.WebApi.Tests/Rbac/SessionAuthInterceptorTests.cs` | Phase 1 — interceptor unit tests. |
| `TestController.WebApi.Tests/Rbac/UserCrudIntegrationTests.cs` | Phase 5 — user CRUD integration. |
| `TestController.WebClient/src/lib/capabilities.test.ts` | Full role × mode × permission matrix (web). |

---

## 6. The three audit systems (do not confuse them)

A frequent source of confusion — there are **three separate audit trails**, each with a different purpose:

1. **`AuditEntries` (DB table) — the RBAC audit.** Who triggered/cancelled/retried, who created/assigned users, who switched mode, and every allow/deny decision. Written through `QueuedAuditWriter`, drained by `AuditDrainWorker`, read through `AuditController` (`/api/audit`), viewed in the WPF `AuditViewerViewModel` (Phase 10). Retention purge by `AuditRetentionWorker` (default ~365 days). **This is "the audit" for the feature.**
2. **`SecurityAuditLogger` — a file-based security log.** Separate system, `RetentionDays = 90` (`SecurityOptions.cs`). Lives on disk, not in the DB. Leave it alone.
3. **Agent audit — `/api/agents/{name}/audit`.** Per-agent command history, shown in the web `MonitorPage.tsx` "Audit Log" tab. Unrelated to RBAC.

Retention nuance: the DB `AuditEntries` table has its own retention (Phase 10, `AuditRetentionWorker`, default ~365 days), *separate* from the security file log's 90 days. They are different jobs on different stores.

---

## 7. How an authorization decision flows

A trigger request in Secured mode, end to end:

1. **Client** (WPF or web) checks its local capability first — `CapabilityChecker.Can()` (WPF) or `useCan()` (web) — to enable/disable the button. This is UX only.
2. The request hits the **API**. `SessionAuthInterceptor` authenticates the session (or short-circuits in Default mode).
3. The relevant **service** (e.g. `PipelineService`, `RetryService`) runs the authoritative check via `PipelineAuthorizationGuard`: the user's role permission **and**, for non-admins, the pipeline assignment. Admins bypass the assignment check (`AuthorizationService.cs` returns `Allow("admin")` immediately for `Role.Administrator`).
4. If the pipeline is locked by someone else, the **lock** path (`LockService` / `AgentLockManager`) returns a conflict (409 + `PipelineLockDto`).
5. The decision — allow or deny — is **audited** via `QueuedAuditWriter` into `AuditEntries` (fire-and-forget via `IAuditWriter.Enqueue`).
6. On state changes (lock acquired/released, permissions changed), a **SignalR broadcast** goes out via `LockBroadcaster` / `SystemModeBroadcaster`; clients react (`LockEvents.ts`, `PermissionEvents.ts`) and refetch/update without a reload.

The golden rule throughout: **the client gates for UX, the server enforces for real.** A disabled button is convenience; the 403/409 from the server is the actual control.

---

## 8. Phase-by-phase summary

What each phase added and its anchor files. (Phases 0–3 predate this map's detailed sweeps; 4–10 were verified during the build and were each found **substantially pre-existing** — the work was largely gating, surfacing, and wiring rather than building from scratch.)

| Phase | Adds | Key files |
|---|---|---|
| **0** | Foundation: permission model, identity, DB schema, audit writer | `Permission.cs`, `PermissionCatalog.cs`, `IAuthorizationService.cs`, `IUserContext.cs`, `Role.cs`, `OrchestratorDbContext.cs`, `*_Initial.cs`, `QueuedAuditWriter.cs` |
| **1** | Auth + sessions + guest login + mode flag | `AuthService.cs`, `AuthController.cs`, `SessionAuthInterceptor.cs`, `SessionStore.cs`, `RbacOptions.cs`, `authStore.ts`, `LoginView.tsx` |
| **2** | Capability-aware UI; read-vs-write; Observer/Default-mode read-only | `CapabilityChecker.cs`, `capabilities.ts`, `TreeNodeViewModel.cs`, `watchlistStore.ts`, `DefaultModeBanner.tsx`, `PipelineAuthorizationGuard.cs` |
| **3** | Pipeline locking + "Locked · owner" + force-release | `PipelineLock.cs`, `LockRegistry.cs`, `LockService.cs`, `LockStateService.cs`, `lockStore.ts`, `LockEvents.ts`, `LockBroadcaster.cs`, `LockConflictModal.tsx`, `ForceReleaseDialog.tsx` |
| **4** | Retry — gated + web-surfaced (engine pre-existed) | `RetryService.cs`, `MainViewModel.Execution.cs` (`RetryFailed`) |
| **5** | Bulk operations (Trigger All Idle / Cancel All) with skip-classification | `PipelinesController.cs`, `MainViewModel.Execution.cs`, `UserService.cs` |
| **6** | Enable/Disable pipelines — gated + server-enforced (toggle pre-existed) | `EnableDisableService.cs`, `WatchListConfig.IsEnabled`, `DisabledTriggerButton.tsx` |
| **7** | Read paths + Guest hardening (mostly pre-existed) | `capabilities.ts` (guest perms), `authStore.loginAsGuest`, rate-limit/expiry |
| **8** | Automatic notifications + mute (detection engine pre-existed) | `NotificationDispatcher.cs`, `MuteService.cs`, `NotificationsController.cs`, `NotificationEntities.cs`, `useMutes.ts`, `TrendCharts.tsx` (mute controls) |
| **9** | Reports — RBAC gating (engine + CSV/HTML/trends pre-existed) | `ResultsEndpoints.cs` (send-report gated by Report_Generate + audit), `BuildResultsViewModel.cs` (CanSendReport/CanExportReport), `BuildDetail.tsx` (Email button gated), `ReportAuthzTests.cs` |
| **10** | Audit viewer + retention + hardening (audit *writing* pre-existed) | `AuditController.cs`, `AuditViewerViewModel.cs`, `AuditClient.cs`, `AuditRetentionWorker.cs` |
| **Live permissions** | Grant a permission → web updates without reload | `PermissionEvents.ts`, the assignment-change broadcast, `SignalRNotifier.cs` |

---

## 9. Key architectural decisions (locked)

These are the choices that shape everything; record them so they aren't relitigated.

1. **WPF host is the sole owner of `orchestrator.db`.** Other hosts proxy; they never open the DB. Enforced: `AddRbacFeature(isPrimaryHost: false)` registers `ThrowingDbContextFactory` + `NullSessionStore` + `NullAuthorizationService` + `NullAuditWriter` for the standalone WebApi.
2. **WPF is Administrator-only (Policy A).** Non-admins use the web client. WPF login rejects non-admins.
3. **Reads are open to everyone; only writes are gated.** Both clients show *all* pipelines to *all* users; only *triggering* is gated by assignment. Three visual states: **Triggerable** (assigned, enabled), **View-only** (not assigned, dimmed/disabled), **Locked** (running by another, amber). Admin bypasses assignment (`AuthorizationService.cs`: `if (role == Admin) return Allow("admin")`).
4. **Audit viewing is admin-only** — the single exception to "reads are open" (audit data is sensitive). `AuditController.cs` gates via `ResolveAdmin(Permission.Audit_View)`. Because the web client has no admin login path, the audit viewer is **WPF-only** in practice (no web route to `/api/audit` exists in the React app).
5. **Mode switches live, no restart.** `RBAC:Enabled` flipped via `WritableOptions` (writes to `appsettings.json`), observed via `IOptionsMonitor`. Switch-to-Default archives users; switch-to-Secured reactivates them and requires login.
6. **The client gates for UX; the server enforces.** Every gated action has a server-side check (403/409) as the real control; UI disabling is convenience only. The web `apiFetch` wrapper shows `AuthDeniedToast` on 403.
7. **Live permission updates** use the same pattern as locks: server broadcasts "permissions changed" → clients refetch their own capabilities → indicators update. Broadcast-to-all (each client refetches its own; no data leaks).
8. **Three distinct audit systems** (DB RBAC audit / file security log / agent audit) — kept separate, with separate retention.

---

## 10. Known gaps / pending work (not features)

The feature set is complete. The WebApi-DB decoupling **is done** (`isPrimaryHost` branching is active and working). What remains:

1. **~~WebApi–DB decoupling refactor~~ ✅ COMPLETE.** `RbacFeatureExtensions.AddRbacFeature(isPrimaryHost)` fully branches: primary host (WPF) registers real DB, services, audit; secondary host (WebApi) registers `ThrowingDbContextFactory` + Null* stubs. `TestController.WebApi/Program.cs` calls `AddRbacFeature(isPrimaryHost: false)`. **Note:** `docs/architecture/WebApi-DB-Decoupling.md` is outdated — it still claims `isPrimaryHost` is not branched. That doc should be updated or archived.
2. **Deployment plan for August** — decide host model (on-prem IIS vs Azure VM both keep SQLite viable; Azure App Service + Azure SQL would require DB migration), where agents live, concurrent web-user scale, and the machine RAM upgrade.
3. **Full demo-path verification** — Default → switch to Secured → admin login → create Engineer → Engineer's filtered web view → confirm reads-for-all / writes-gated → confirm Default-mode-web read-only is enforced *server-side* (the `ClientKind` detection check in `AuthorizationService.EvaluateDefaultMode`).

---

## 11. Making this document authoritative against your live code

This map was verified against the live codebase on **2026-06-17**. All file paths confirmed, all permission catalogs matched, all locked decisions validated. To re-verify after future changes, run the verification prompt below against the workspace:

```
@workspace I have a consolidated architecture/change-map document at
docs/RBAC_Architecture_And_Change_Map.md describing the RBAC + Default Mode
feature. VERIFY it against the actual code and produce a corrected version.

For each section:
1. Confirm every file path in the §5 change-map tables actually exists; flag
   any that are missing, renamed, or moved, and add any RBAC-related file the
   tables omit (search Authorization, Identity, Locking, the Rbac* services,
   AuditController, NotificationDispatcher, the capability* files, the lock/
   permission SignalR handlers).
2. Refresh the approximate line-number anchors where they've drifted.
3. Confirm §4's role→permission claims against PermissionCatalog.cs and
   capabilities.ts (list any mismatch between server and web mirror).
4. Confirm §9's locked decisions are reflected in code (esp. WPF-only audit,
   admin-bypasses-assignment, reads-open/writes-gated, the three audit systems).
5. Confirm §10's gap list — especially whether RbacFeatureExtensions.cs now
   branches on isPrimaryHost, and whether the WebApi-DB decoupling is done.

Output the corrected Markdown (same structure), plus a short "Corrections
applied" list at the top noting what changed versus the original. Do NOT
invent files; if something can't be confirmed, mark it "unverified".
```

Running that turns this from "a map built from our build notes" into "a map verified against your actual code."

---

*End of document.*
