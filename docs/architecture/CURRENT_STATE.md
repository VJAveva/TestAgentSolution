# Current State — TestAgentSolution

## Solution Layout

| Project | Purpose |
|---------|---------|
| `TestControllerGrpc.Core` | Shared library: proto-generated gRPC types, domain models (`WatchListConfig`), service interfaces (`IActionPipelineExecutor`, `IAppLogger`, etc.), XML parser, session manager, logging infrastructure. |
| `TestControllerGrpc` | WPF controller desktop app — hosts gRPC server, REST/SignalR WebApi, file watchers, and the full action-pipeline executor with MVVM UI. |
| `TestController.Api` | ASP.NET Core class library of shared controllers, SignalR hubs, middleware, and security (multi-identity auth + `AddRbacFeature()` / `SessionAuthInterceptor`). Referenced by both WPF host and standalone WebApi. |
| `TestController.Persistence` | EF Core + SQLite RBAC store (`OrchestratorDbContext`, entities, migrations, `AuthorizationService`, `SessionStore`, `QueuedAuditWriter` + `AuditDrainWorker`, `PasswordHasher`). Referenced by both hosts. |
| `TestController.WebApi` | Standalone ASP.NET Core host — REST + SignalR for the React client, acts as proxy to agents via gRPC. |
| `TestController.WebClient` | React 18 + TypeScript SPA (Vite, Tailwind, Zustand, SignalR) served by WebApi. |
| `TestAgentGrpc` | Windows agent service — hosts gRPC server (`TestAgentService`), runs commands locally, system-tray UI (WinForms). |
| `TestAgentDisplay` | WPF read-only agent display panel — gRPC client connecting to a running agent for monitoring. |
| `TestController.Dashboard` | Standalone WPF dashboard — connects to controller via SignalR (read-only feed consumer). |
| `TestAgent.Diagnostics` | WPF agent diagnostics utility. |
| `TestControllerGrpc.Tests` | xUnit tests for the controller WPF app and Core library. |
| `TestController.WebApi.Tests` | xUnit integration tests for the WebApi (uses `WebApplicationFactory`). |
| `TestController.ApiTests` | xUnit shared-API contract tests. |
| `TestController.LoadTests` | Performance / load test suite. |

## Public Authorization Interfaces (TestControllerGrpc.Core)

**Implemented (RBAC Phases 0–10).** `IUserContext`, `IAuthorizationService` (`CanAsync(user, permission, resourceId?)` with Default-mode short-circuit), `IAuditWriter` (fire-and-forget), and enums `Role` (Administrator/SeniorManager/Engineer/Guest), `ClientKind` (Wpf/Web/Cli), `Permission` (`TestControllerGrpc.Core/Authorization/Permission.cs`). Persistence + interceptors live in `TestController.Persistence` and `TestController.Api`. See the **RBAC Feature** sections at the end of this document for the full surface.

## gRPC Service Definitions

Single proto file duplicated across projects: `TestControllerGrpc.Core/Protos/test_agent.proto`

### `TestControllerService` (hosted by controller)
| RPC | Notes |
|-----|-------|
| `Register(TestAgentRef)` | Agent self-registers on startup |
| `UnRegister(TestAgentRef)` | Agent deregisters on shutdown |
| `UpdateClientState(TestAgentRef)` | Agent pushes state change (Ready/Running/Inactive) |
| `PushExecutionEvents(stream ExecutionEvent)` | Client-streaming: agent pushes stdout/stderr/progress/completion events |
| `Heartbeat(HeartbeatRequest)` | Periodic health + resource metrics |

### `TestAgentService` (hosted by each agent)
| RPC | Notes |
|-----|-------|
| `GetState` | Returns current `AgentState` enum |
| `RunCommand(RunCommandRequest)` | Fire-and-forget command dispatch (returns accepted + execution_id) |
| `RunCommandStreamed(RunCommandRequest)` | Server-streaming: real-time stdout/stderr/progress events |
| `SubscribeAgentEvents` | Open server stream for all lifecycle events |
| `TerminateExecution` / `ForceReady` | Kill running process / reset state |
| `GetExecutionHistory` | Query past runs |
| `GetAgentSnapshot` | Full agent state + resource metrics + capabilities |
| `GetAuditLog` | Date-filtered audit log |
| `GetConnectionHealth` | Controller connection status |
| `ReloadCommandPolicy` | Hot-reload the `commandpolicy.json` allowlist |

## WPF View Structure

- **MVVM framework:** CommunityToolkit.Mvvm (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`)
- **Main window:** `TestControllerGrpc/Views/MainWindow.xaml` — DynamicResource theming (`{DynamicResource WindowBg}`), three themes in `Themes/` (Dark, Light, HighContrast)
- **View model partials:** `MainViewModel` split into 12+ partial files (`.File.cs`, `.Execution.cs`, `.Agents.cs`, etc.)
- **Navigation:** Tab-based workspace inside MainWindow; separate popup windows for dashboards/dialogs (`ExecutionDashboardWindow`, `AgentMonitorWindow`, `FailureAnalysisDialog`, etc.)
- **Resource dictionaries:** `Views/Styles/` contains design tokens, typography, control styles
- **Agent workspace views:** `Views/AgentWorkspace/` (FleetView, MonitorView, RegistryView)
- **Command pattern:** `[RelayCommand]` source-gen attributes with `CanExecute` named properties
- **Property change:** `[ObservableProperty]` attribute (source-generated `INotifyPropertyChanged`)

## React Structure

- **State management:** Zustand (stores in `src/stores/`: `agentStore`, `executionStore`, `watchlistStore`, `resultsStore`, `connectionStore`, `lockStore`, `authStore`, `systemModeStore`)
- **API client:** Custom `apiFetch<T>()` wrapper in `src/lib/api.ts` with correlation ID tracking, HTML-detection, structured errors (401/403 → AuthDeniedToast).
- **Routing:** No router — single-page tab-based layout (`AppShell.tsx` with `activeTab` state)
- **Hook conventions:** One hook per domain (`useAgents`, `useExecution`, `useWatchList`, `useResults`, `useSignalR`, `useFleetState`). Hooks wrap axios calls + update Zustand stores.
- **Real-time:** SignalR hub at `/hubs/controller` with indefinite exponential-backoff reconnect. Events: `ActionProgress`, `ExecutionStarted/Completed/Cancelled`, `AgentRegistered/StatusChanged`, `LogEntry`, `AgentOutputBatch`.
- **Styling:** Tailwind CSS 3 (`tailwind.config.js`, `postcss.config.js`)
- **Bundler:** Vite 6, TypeScript 5.7

## DI Registrations (Program.cs)

### TestController.WebApi/Program.cs
| Concern | Registrations |
|---------|---------------|
| Shared/Core | `IWatchListXmlParser`, `TrxResultsParser`, `BuildResultsConfig`, `BuildResultsAggregator`, `BuildTrendAnalyzer`, `ConsecutiveFailureDetector`, `BuildReportHtmlGenerator`, `ExecutionSessionManager`, `IAppLogger` |
| Web-specific | `AgentGrpcClientManager`, `AgentRegistry`, `AgentTelemetryCache`, `WatchListFileService`, `ControllerProxyService`, `ConfigValidator` |
| Adapters | `IVocabularyMonitor` → `StandaloneVocabularyMonitor`, `IAgentGrpcDispatcher` → `StandaloneAgentDispatcher`, `IActionPipelineExecutor` → `StandalonePipelineExecutor`, `IEventAggregator` → `EventAggregator` |
| Security | `AddMultiIdentitySecurity()` — NTLM/negotiate + API-key + roles; `AddRbacFeature()` — RBAC session auth + `SessionAuthInterceptor` + audit drain |
| API | `AddControllerApi()` — shared controllers + SignalR hub |
| Infra | `AddSignalR()`, `AddCors()`, `AddRateLimiter()`, `AddOpenApi()` + `MapScalarApiReference()` (`/scalar`), `AddHealthChecks()` (live/ready), `AddOpenTelemetry()` (Prometheus `/metrics`) |
| Background | `AgentEventRelayService` (hosted) |

### TestAgentGrpc/Program.cs
| Concern | Registrations |
|---------|---------------|
| Config | `AgentSettings`, `AgentKestrelOptions`, `CommandPolicySettings`, `NotificationSettings`, `AuditSettings` |
| Core services | `EventBroadcaster`, `ExecutionTracker`, `AuditLogger`, `CommandPolicyEvaluator`, `EnhancedCommandPolicyEvaluator`, `CommandExecutor`, `SystemMetricsCollector`, `TestControllerClient`, `ConnectionHealthMonitor` |
| gRPC | `AddGrpc()` (16 MB message limits) |
| Background | `AuditLogger`, `AgentLifecycleService`, `StuckExecutionWatchdog`, `GrpcListenerWatchdog` |

## Logging Setup

- **Library:** Custom `AppLogger` (not Serilog) in `TestControllerGrpc.Core/Services/AppLogger.cs`
- **Sinks:** In-memory ring buffer (5000 entries, for UI), component-specific daily rolling file (`controller_2026-04-25.log`), shared daily file (`app_*.log`), errors-only daily file (`errors_*.log`)
- **Directory:** `C:\TestControllerService\Logs` (configurable via `Logging:LogDirectory`)
- **Max file size:** 50 MB per file with rollover counter
- **Security:** All messages pass through `SecurityRedactor.Redact()` before writing
- **Log levels:** Uses `Microsoft.Extensions.Logging.LogLevel` enum. Typical pattern: `Info` for operation start/end, `Warning` for recoverable issues, `Error` for handler failures + exception detail.
- **Structured format:** Category-based (`Log(level, category, message)`) with correlation ID and elapsed-ms fields. Not template-based like Serilog — string interpolation.
- **Microsoft.Extensions.Logging:** Also used via `ILogger<T>` for framework-level logging in hosted services.

## Test Projects and Frameworks

| Aspect | Detail |
|--------|--------|
| Framework | xUnit 2.9.3 |
| Mocking | Moq 4.20.72 (TestControllerGrpc.Tests); no mock lib in WebApi.Tests (uses `WebApplicationFactory` with test doubles) |
| Coverage | coverlet.collector 6.0.4 + `coverlet.runsettings` |
| Integration | `Microsoft.AspNetCore.Mvc.Testing` for WebApi endpoint tests |
| Frontend | Vitest 4.1.7 + @testing-library/react + jsdom |
| Fixtures | `IClassFixture<TestWebAppFactory>` for integration; plain classes for unit; `TestFixtures` static helper for test data creation |
| Naming | `Method_Should_Expected_When_State` (dominant) |

## Executor Adapter — How WPF Triggers Pipelines

1. **Entry point:** `MainViewModel.TriggerEvent()` ([RelayCommand] in `MainViewModel.Execution.cs`)
2. **Lock acquisition:** `AgentLockManager.TryLockAgents()` for required remote agents
3. **Session creation:** `ExecutionSessionManager` creates an `ExecutionSession` with snapshot isolation
4. **Pipeline call:** `IActionPipelineExecutor.ExecuteEventTrackedAsync(tag, event, ctx, ct)` — the abstract `PipelineExecutorBase` walks the action tree depth-first
5. **Dispatch:** `ActionPipelineExecutor.ExecuteActionAsync()` dispatches to `IAgentGrpcDispatcher.ExecuteRemoteCommandAsync()` (gRPC) or `.ExecuteLocalCommandAsync()` (local process)
6. **Async pattern:** Full `async Task` with `CancellationToken` throughout. No `.Result`/`.Wait()`.
7. **Error propagation:** Events (`NodeFailed`, `NodeProgress`) fire on failure; `PipelineLogEntry` logged to `IAppLogger`. `FailAndContinue` flag controls whether failure halts the group.
8. **Smart retry:** Per-action `MaxRetries` + exponential/fixed backoff + exit-code filtering

## WatchList.xml Schema

```xml
<?xml version="1.0" encoding="utf-8"?>
<WatchList GlobalVariablesFile="C:\Config\GlobalVars.xml">
  <Templates>
    <Template ID="SharedDeploy">
      <Action Type="RunRemoteCommand" AgentName="Agent1"
              Command="deploy.cmd" Parameters="/silent" />
    </Template>
  </Templates>

  <WatchItem Tag="ConsolidatedBuild" Path="C:\ManualTrigger\"
             Filter="Consolidated.txt" IsEnabled="true"
             BuildNumberField="BuildNumber" DropLocationField="DropLocation"
             BuildBasePath="\\server\repl\Ado\SP\">
    <Event Type="Renamed" ExecutionType="Sequential">
      <Initialize Tag="LoadParams" ParameterFile="params.xml" />
      <ActionGroup Tag="DeployPhase" ExecutionType="Parallel" FailAndContinue="true">
        <Action Type="RunRemoteCommand" AgentName="Agent1"
                Command="install.cmd" Parameters="/q"
                Timeout="300000" MaxRetries="3"
                RetryDelaySeconds="10" RetryBackoff="Exponential"
                RetryOnExitCodes="1;-1" FailAndContinue="false" />
        <Action Type="RunCommand" Command="echo" Parameters="done" />
      </ActionGroup>
      <Ref TemplateID="SharedDeploy" />
      <Action Type="SendMail" From="ci@corp.com" To="team@corp.com"
              Title="Build Complete" Body="See attached" Attachment="report.html" />
    </Event>
  </WatchItem>
</WatchList>
```

## Build Commands

```powershell
# Full pipeline (builds .NET + React, runs tests, publishes to C:\Deployment)
.\Publish-All.ps1
.\Publish-All.ps1 -SkipTests -WebApiUrl "http://controller01:8080"

# Tests with coverage
dotnet test TestAgentSolution.sln --collect:"XPlat Code Coverage" --settings coverlet.runsettings

# React client
cd TestController.WebClient
npm run build          # tsc -b && vite build
npm run test           # vitest run
npm run dev            # vite dev server

# Clean (nuclear)
CleanBuild.bat         # kills devenv, deletes all bin/obj/.vs, clears NuGet cache
```

No Cake/MSBuild custom targets. Build uses `dotnet publish` with `win-x64`, self-contained, single-file defaults per `Publish-All.ps1`.

## Load-Bearing Weirdness

1. **Proto duplication (CS0436 suppressed):** `TestAgentGrpc.csproj` generates its own proto types AND references `TestControllerGrpc.Core` which has the same proto. The `<NoWarn>CS0436</NoWarn>` suppresses the conflict. Removing either copy breaks gRPC server hosting.

2. **STA thread for WinForms tray:** Agent's `Program.cs` spawns a dedicated STA thread for `TrayApplicationContext` because async Main forces MTA. OLE clipboard/dialogs break without this.

3. **Host stays alive after tray exit:** If agent is executing, closing the tray icon does NOT stop the gRPC host — it waits until execution completes or explicit shutdown. See the `Task.WhenAny()` pattern at line ~215 of `TestAgentGrpc/Program.cs`.

4. **Single-instance mutex:** `Global\TestAgentGrpc-SingleInstance` prevents double-launch. Second instance shows MessageBox + exits with code 2.

5. **Port fallback:** Agent tries primary `GrpcPort` (5200), then `FallbackPorts[]` if `AllowPortFallback=true`. Environment variable override propagates the actual port.

6. **ThreadPool pre-warm:** WebApi sets `ThreadPool.SetMinThreads(200, 200)` at startup to avoid 500ms/thread ramp-up under 200-agent load. Removing this causes cascading timeouts.

7. **`App.Services` static accessor:** WPF controller uses a static `App.Services` for service location in places DI can't reach. Breaking this breaks window construction.

8. **`ExecutionSessionManager.cs~RF*.TMP` files:** Leftover temp files from VS refactoring exist in the repo. They are harmless but should not be deleted (may contain in-progress work).

## RBAC Feature (Phase 0 complete — 2026-06-10)

### New Public Interfaces in TestControllerGrpc.Core
- `IUserContext` — identity flowing through every gRPC request (UserId, Username, Email, Role, ClientKind, AssignedPipelineIds)
- `IAuthorizationService` — `CanAsync(user, permission, resourceId?)` with Default-mode short-circuit
- `IAuditWriter` — fire-and-forget audit writer

### New Enums
- `Role` (Administrator/SeniorManager/Engineer/Guest)
- `ClientKind` (Wpf/Web/Cli)
- `Permission` (19 entries — see 01_System_Design.md §3.1)

### New Project
- `TestController.Persistence` — EF Core + SQLite, references TestControllerGrpc.Core
  - DbContext: `OrchestratorDbContext`
  - Tables: Users, Sessions, PipelineAssignments, AuditEntries
  - Mode: SQLite WAL via PRAGMA in OnConfiguring
  - Migrations location: TestController.Persistence/Migrations/
  - Initial admin seeded in 0001_SeedAdmin

### New DI Registration
- `TestController.Api/RbacFeatureExtensions.cs` exposes `AddRbacFeature(this IServiceCollection)`
- Called from both `TestControllerGrpc/App.xaml.cs` and `TestController.WebApi/Program.cs`
- Adds: `IAuthorizationService`, `ISessionStore`, `IAuditWriter`, `SessionAuthInterceptor`,
  `AuditLoggingInterceptor`, `AuditDrainWorker` (hosted), `AuthService`, `SystemModeGrpcService`

### Configuration
- `RBAC:Enabled` boolean in appsettings.json — defaults to false (Default mode)
- Modified at runtime via `IWritableOptions<RbacOptions>` (writes back to appsettings.json)

### Coexists With
- `AddMultiIdentitySecurity()` — still active for REST endpoints with NTLM/API-key/roles
- `AgentLockManager` — still active for per-execution-session agent locking
- `LockRecoveryService` (`TestController.Api/Services/`) — agent-layer recovery only; handles stale agent locks from `AgentLockManager`. Does NOT reference `LockRegistry` or `PipelineLock` types (verified Phase 3c). No interference with the pipeline-lock subsystem.

## RBAC Feature (Phase 0.5 complete) — System Mode UI

### New WPF Views
- Views/Settings/SettingsView.xaml — Settings tab in MainWindow
- Views/Settings/SecurityModePanel.xaml — Mockup 10 implementation
- Views/Settings/InitialAdminWizard.xaml — Default→Secured flow
- Views/Settings/DisableRbacConfirmDialog.xaml — Secured→Default flow
- Controls/DefaultModeBanner.xaml — fixed, non-dismissible
- Controls/UserIdentityBadge.xaml — handles all 6 variants

### New WPF ViewModels
- ViewModels/Settings/SecurityModeViewModel.cs (CommunityToolkit.Mvvm)

### New WPF Services
- Services/SystemModeClient.cs — REST + SignalR for mode operations

### New Web Stores
- src/stores/systemModeStore.ts — Zustand, exposes fetchMode() and onModeChanged()

### New Web Components
- src/components/header/DefaultModeBanner.tsx
- src/components/header/UserIdentityBadge.tsx (6 variants)
- src/components/common/DisabledTriggerButton.tsx
- src/signalr/SystemModeEvents.ts (with reconnect poll fallback)

### New REST Endpoints
- GET /api/system/mode
- POST /api/system/mode/secured
- POST /api/system/mode/default

### New SignalR Events
- SystemModeChanged — broadcast to all clients on mode flip

### Integration Points
- AppShell.tsx now wires systemModeStore + SignalR subscription on mount
- useSignalR.ts now subscribes to SystemModeChanged events
- MainWindow.xaml has new Settings tab

## RBAC Feature (Phase 1a complete) — Auth & Login

### New REST Endpoints (TestController.Api)
- GET /api/auth/me — returns current user + capabilities
- POST /api/auth/login — username + password → session token
- POST /api/auth/guest — anonymous short-lived session
- POST /api/auth/logout — revokes server session
- POST /api/auth/change-password — current + new password

### New Static Catalog
- TestController.Api/PermissionCatalog.cs — role → permissions[] mapping
  used by /api/auth/me to populate the capabilities list

### New WPF
- Views/LoginPage.xaml + LoginViewModel
- Views/ChangePasswordPage.xaml + ChangePasswordViewModel
- Services/AuthClient.cs — HTTP client, in-memory token only
- App.xaml.cs routing: Default → MainWindow; Secured → LoginPage first

### New Web
- src/stores/authStore.ts — Zustand, token in sessionStorage
- src/views/LoginView.tsx — login form + Continue as Guest
- src/views/ChangePasswordView.tsx — first-login forced change
- src/App.tsx route guard: Default → AppShell, Secured+unauth → LoginView,
  mustChangePassword → ChangePasswordView
- UserIdentityBadge now reads from useAuthStore in Secured mode

## RBAC Feature (Phase 3 complete — 2026-06-12): Pipeline Lock Coordination

### Lock architecture
- In-memory LockRegistry lives ONLY in the controller process (WPF host).
  Standalone WebApi forwards lock operations via ControllerProxyService.
- TWO lock layers coexist: LockRegistry locks WatchItems (Tag) per
  user/client; the pre-existing AgentLockManager locks agent machines per
  execution session. Unchanged, unmerged.
- Lock key = WatchItem Tag. Same-owner re-acquire succeeds.
- LockExpirySweeper (hosted) expires stale locks by TTL.

### Core types (TestControllerGrpc.Core/Locks/)
- PipelineLock record, ILockRegistry

### Controller-side (TestController.Api)
- LockRegistry, LockExpirySweeper, LockEventBroadcaster, LockService
  (force-release: reason >=10 server-validated, Pipeline_ForceRelease authz,
  audit payload {reason, priorOwner}, prior-owner notification)
- PipelineAuthorizationGuard extended: TryAcquire after authz Allow;
  conflict → gRPC Aborted / REST 409 with PipelineLockDto body
- RbacModeTransitionService extended: RewriteLockOwners on mode switch

### SignalR contract (/hubs/controller)
- PipelineLockAcquired/Released/Expired/Stolen/Rewritten; DTO shape per
  the lock contract section above
- GET /api/locks for initial/reconnect sync

### WPF (Phase 3b)
- LockStateService (Singleton, Dispatcher-marshaled), LockBadgeViewModel,
  LockBadge control integrated in MainWindow tree ItemTemplate,
  LockConflictDialog, ForceReleaseReasonDialog (both DataContext via
  App.Services, FallbackValue=Collapsed convention)
- CanTrigger* predicates extended with lock state; Aborted/409 → conflict dialog

### Web (Phase 3c)
- lockStore (Zustand) + LockEvents with reconnect resync via GET /api/locks
- LockBadge in WatchListTree, LockConflictModal, ForceReleaseDialog
- DisabledTriggerButton gains "locked by other" reason; 409 body → modal

### Patterns established (reuse in later phases)
- Cross-client live state: broadcast + full-resync-on-reconnect, never
  polling
- Server response carries the full DTO needed by the error UI (no refetch)
- New UI controls ship WITH their visual-tree integration diff in the
  same task

---

## Phase 4 — Retry (RBAC-gated)

### Backend
- `RetryService` (`TestController.Api/Services/RetryService.cs`): mirrors `PipelineService` pattern — `AuthorizeRetryAsync(user, pipelineId, ct)` calls `PipelineAuthorizationGuard` with `Permission.Pipeline_Retry`, acquires pipeline lock, enqueues audit entry.
- `POST /api/pipelines/{pipelineId}/retry` endpoint in `PipelinesController` — 401/403/409 error handling identical to trigger/cancel.
- Registered in `AddRbacFeature()` as Singleton in both primary and secondary host blocks.

### WPF (gate existing RetryFailed command)
- `CanRetryFailed` property: checks `Permission.Pipeline_Retry` + lock state via `_capabilityChecker` + `_lockStateService`.
- `[RelayCommand(CanExecute = nameof(CanRetryFailed))]` on `RetryFailed()`.
- Auth guard inside method body: calls `_pipelineGuard.AuthorizeAsync(…, Permission.Pipeline_Retry, tag)` before execution.
- `RetryFailedCommand.NotifyCanExecuteChanged()` added to `NotifyExecutionCanExecuteChanged()`.
- Context menu in `MainWindow.xaml.cs`: disables retry item with tooltip when `CanExecute` returns false.

### Web (toolbar retry button)
- `retryByTag(tag)` added to `useExecution` hook — calls RBAC endpoint first (`POST /api/pipelines/{tag}/retry`), then delegates to `POST /api/execution/retry-tag/{tag}`.  Handles 409 via `dispatchLockConflict`.
- "Retry Failed" `ToolBtn` appears in `WatchListToolbar` when selected WatchItem has `executionStatus === 'Failed'`.
- Disabled when: `!useCan('Pipeline_Retry', tag)` or `isLockedByOther(tag)` or `busy`.
- Tooltip shows `useDisabledReason` text on disabled state.

### Permission mapping
| Role | Pipeline_Retry |
|------|---------------|
| Administrator | ✓ |
| SeniorManager | ✓ |
| Engineer | ✓ (scoped to assigned pipelines) |
| Guest | ✗ |

## Phase 6 — Enable/Disable Pipelines (RBAC-gated)

### Backend
- `EnableDisableService` (`TestController.Api/Services/EnableDisableService.cs`): `SetEnabledAsync(user, pipelineId, enabled, ct)` — checks `Permission.Pipeline_Enable` or `Pipeline_Disable` via `PipelineAuthorizationGuard`, sets `WatchItemConfig.IsEnabled`, enqueues audit entry.
- `POST /api/pipelines/{pipelineId}/enabled` endpoint in `PipelinesController` — body: `{ enabled: bool }`. Returns 401/403/404 as appropriate.
- Disabled enforcement in trigger/retry paths: `PipelineService.AuthorizeTriggerAsync` and `RetryService.AuthorizeRetryAsync` check `WatchItemConfig.IsEnabled` before authorization; throw `PipelineAuthorizationDeniedException` with `reasonCode = "pipeline-disabled"` when false.
- Both services now inject `IVocabularyMonitor` to access live WatchList state.
- Registered in `AddRbacFeature()` as Singleton in both primary and secondary host blocks.

### WPF (gate existing toggle)
- `MainWindow.xaml.cs` resolves `CapabilityChecker` from DI.
- WatchItem "Include in Trigger All" checkbox: `IsEnabled` set to `_capabilityChecker.Can(Pipeline_Enable/Pipeline_Disable)`. Tooltip "Administrator only" shown when denied.
- WatchList root "Enable All WatchItems" / "Disable All WatchItems" menu items: similarly gated with tooltip.

### Web (disabled indicator + blocked trigger/retry)
- `WatchListTree.tsx`: detects `model.isEnabled === false` on WatchItem nodes; shows red "Disabled" pill with `line-through` style and `opacity-50` dimming. Distinct from "View only" and "Locked" states.
- `WatchListToolbar.tsx`: `isPipelineDisabled` flag blocks Retry button with tooltip "Pipeline is disabled". Trigger button implicitly blocked by server rejection (403 with `pipeline-disabled` reason).
- No enable/disable toggle in web UI (only Administrator has these permissions; web has no Administrator role per SRS).

### Permission mapping
| Role | Pipeline_Enable | Pipeline_Disable |
|------|----------------|-----------------|
| Administrator | ✓ | ✓ |
| SeniorManager | ✗ | ✗ |
| Engineer | ✗ | ✗ |
| Guest | ✗ | ✗ |

## Phase 7 — Read Paths + Guest Access

### What was already implemented (pre-existing)
- **Guest session creation**: `POST /api/auth/guest` → `AuthService.LoginAsGuestAsync` creates session with `GuestId`, returns token.
- **Guest read access**: In Default mode, `NoneAuthenticationHandler` gives all requests an Admin ClaimsPrincipal so `[Authorize(SecurityPolicies.User)]` passes. In Secured mode, `SessionAuthInterceptor` resolves guest token → `SyntheticUserContext(roles=[Guest])`.
- **Guest write denial**: `AuthorizationService.EvaluateRbacAsync` → guest + non-read permission → `Deny("guest-readonly")`. Server-side enforcement in all guarded controllers.
- **Default-mode web read-only**: `AuthorizationService.EvaluateDefaultMode` → `ClientKind.Web` + write permission → `Deny("default-mode-web-readonly")`.
- **Inactivity timeout**: 60-minute inactivity window enforced in `SessionAuthInterceptor` for ALL sessions.
- **Audit on authorization decisions**: `AuthorizationService.EnqueueAudit` fires on every `CanAsync` call (allow AND deny), including guest denials.

### Gaps closed in Phase 7 verification pass
1. **Rate limiting on guest endpoint** (`AuthController.cs`): Added `[EnableRateLimiting("mutation")]` to `POST /api/auth/guest` — limits anonymous guest session creation to 10/min per IP (uses existing "mutation" policy).
2. **Guest absolute TTL** (`SessionAuthInterceptor.cs`): Added `session.GuestId != null && (UtcNow - CreatedUtc) > 60 min → null` check. Guests cannot extend their session indefinitely by staying active; hard 60-minute cap from creation.
3. **Guest session creation audit** (`AuthService.cs`): Injected `IAuditWriter`, fires `Session_GuestCreated` audit entry (fire-and-forget) on every guest login with GuestId and ClientKind.

### Files modified
| File | Change |
|------|--------|
| `TestController.Api/Controllers/AuthController.cs` | `using Microsoft.AspNetCore.RateLimiting` + `[EnableRateLimiting("mutation")]` on `LoginAsGuest` |
| `TestController.Api/Interceptors/SessionAuthInterceptor.cs` | Absolute TTL check for guest sessions after inactivity check |
| `TestController.Api/Services/AuthService.cs` | `IAuditWriter` dependency + audit entry in `LoginAsGuestAsync` |

## Phase 10 — Audit Viewer + Hardening

### New backend — Audit Read API
- `TestController.Api/Controllers/AuditController.cs` — `[ApiController] [Route("api/audit")]`
  - `GET /api/audit` — paginated query with filters (from, to, userId, actionName with wildcard, decision, resourceId, reasonCode). Returns `{ items, total, page, pageSize }`. Admin-only (Audit_View permission).
  - `GET /api/audit/export` — CSV streaming export (max 100K rows). Admin-only (Audit_Export permission).
  - Both use `SessionAuthInterceptor.ResolveUserAsync` → requires `Role.Administrator`.
  - DTOs: `AuditQueryParams`, `AuditEntryDto`, `AuditPageResponse` (defined in same file).

### New backend — Retention
- `TestController.Persistence/Audit/AuditRetentionWorker.cs` — `BackgroundService`, runs daily, deletes AuditEntries older than configurable retention (default 365 days via `Audit:RetentionDays` config).
- `AuditRetentionOptions` — bound from `"Audit"` config section.
- Registered in `RbacFeatureExtensions.AddRbacFeature()` (primary host only).

### New WPF — Audit Viewer
- `TestControllerGrpc/Services/AuditClient.cs` — HTTP client for `/api/audit` and `/api/audit/export`. Uses `IHttpClientFactory("SystemMode")` + bearer token from `AuthClient`.
- `TestControllerGrpc/ViewModels/Admin/AuditViewerViewModel.cs` — CommunityToolkit.Mvvm ObservableObject. Filters, pagination, Search/Export/Clear commands.
- `TestControllerGrpc/Views/Admin/AuditViewerPage.xaml` — DataGrid with all audit columns, filter bar, pagination controls.
- `TestControllerGrpc/Views/AuditViewerWindow.xaml` — Window shell hosting `AuditViewerPage`.
- `MainViewModel.Security.cs` — `[RelayCommand] OpenAuditViewer()` gated by `CapabilityChecker.Can(Audit_View)`.
- DI: `AuditClient` + `AuditViewerViewModel` registered as Singleton in `App.xaml.cs`.

### Tests
- `TestController.WebApi.Tests/Rbac/AuditControllerTests.cs` — 9 tests covering:
  - Auth: 403 for non-admin, 403 for unauthenticated
  - Query: pagination, filter by action (exact + wildcard), filter by decision, filter by userId, ordering by TimestampUtc DESC, page 2

### Hardening verification (all pass)
1. **Audit coverage**: All authorization decisions go through `AuthorizationService.CanAsync` which always enqueues audit. No bypass found.
2. **No secrets**: Audit entries contain only usernames, permission names, resource IDs, reason codes. No tokens, passwords, or hashes.
3. **Append-only**: Only mutation is `AuditRetentionWorker.ExecuteDeleteAsync` (time-cutoff retention). No update/remove anywhere else.
4. **No mutation endpoints**: `AuditController` exposes only `[HttpGet]` — no POST/PUT/DELETE.

### Files created/modified
| File | Change |
|------|--------|
| `TestController.Api/Controllers/AuditController.cs` | New — audit read API |
| `TestController.Persistence/Audit/AuditRetentionWorker.cs` | New — retention BackgroundService |
| `TestController.Api/RbacFeatureExtensions.cs` | Added AuditRetentionOptions + AuditRetentionWorker registration |
| `TestControllerGrpc/Services/AuditClient.cs` | New — WPF HTTP client for audit |
| `TestControllerGrpc/ViewModels/Admin/AuditViewerViewModel.cs` | New — MVVM ViewModel |
| `TestControllerGrpc/Views/Admin/AuditViewerPage.xaml(.cs)` | New — DataGrid view |
| `TestControllerGrpc/Views/AuditViewerWindow.xaml(.cs)` | New — Window shell |
| `TestControllerGrpc/ViewModels/MainViewModel.Security.cs` | Added OpenAuditViewer command |
| `TestControllerGrpc/App.xaml.cs` | DI registrations for AuditClient + AuditViewerViewModel |
| `TestController.WebApi.Tests/Rbac/AuditControllerTests.cs` | New — 9 integration tests |

## Phase 8 — Automatic Notifications + Mute

### Bugfix
- `TestControllerGrpc/Services/ActionPipelineExecutor.cs` — replaced hardcoded `new SmtpClient("smtp")` with `new SmtpClient(_config.SmtpServer, _config.SmtpPort)`. Added `BuildResultsConfig` to constructor injection.

### New backend — Notification dispatch
- `TestControllerGrpc/Services/NotificationDispatcher.cs` — BackgroundService, subscribes to `ExecutionCompletedEvent` via `IEventAggregator`. On completion with failures: runs existing `ConsecutiveFailureDetector.Detect()`, filters muted/cooled-down targets, generates alert email via existing `BuildReportHtmlGenerator.GenerateAlertEmailHtml()`, sends to `QaAlertRecipients` via configured SMTP. Fire-and-forget, does not block execution pipeline.
- **Decision**: fires ONLY on threshold crossings (configurable via `AlertOnThresholdOnly`), NOT every run — avoids duplicating existing CI batch email.

### New backend — Mute service + storage
- `TestControllerGrpc.Core/Models/NotificationEntities.cs` — `NotificationMute` + `NotificationCooldown` POCO entities.
- `TestControllerGrpc.Core/Models/NotificationOptions.cs` — config class (`AutoAlertEnabled`, `AlertOnThresholdOnly`, `CooldownHours`). Bound from `"Notifications"` section.
- `TestController.Persistence/Configurations/NotificationConfigurations.cs` — EF Core table configs (`NotificationMutes`, `NotificationCooldowns`).
- `TestController.Persistence/OrchestratorDbContext.cs` — added `DbSet<NotificationMute>` + `DbSet<NotificationCooldown>`.
- `TestController.Api/Services/MuteService.cs` — Singleton. Mute/Unmute (gated by `Notification_Mute` permission + audited), IsMuted, IsInCooldown, RecordSent.

### New REST — Notification mute endpoints
- `TestController.Api/Controllers/NotificationsController.cs`:
  - `GET /api/notifications/mutes` — list all active mutes (authenticated).
  - `POST /api/notifications/mutes` — mute target (gated by `Notification_Mute`).
  - `DELETE /api/notifications/mutes/{id}` — unmute (gated by `Notification_Mute`).

### New WPF
- `TestControllerGrpc/Services/NotificationMuteClient.cs` — HTTP client for mute API.
- `TestControllerGrpc/ViewModels/Admin/NotificationSettingsViewModel.cs` — CommunityToolkit.Mvvm. Read-only display of settings + mute list management.
- DI: `NotificationMuteClient` + `NotificationSettingsViewModel` + `NotificationDispatcher` (hosted service) in `App.xaml.cs`.

### New Web
- `TestController.WebClient/src/hooks/useMutes.ts` — React hook: list/mute/unmute/isMuted/canMute.
- `TestController.WebClient/src/components/results/TrendCharts.tsx` — Added mute/unmute buttons on each alert card (gated by `Notification_Mute` capability). Shows "MUTED" badge on muted tests.

### Tests
- `TestController.WebApi.Tests/Rbac/NotificationMuteTests.cs` — 9 tests: mute succeeds for admin, mute succeeds for engineer (own pipeline), 403 for unowned, unmute, cooldown suppression, cooldown expiry, 404 on unknown mute, audit.

### DI registration
- `TestController.Api/RbacFeatureExtensions.cs` — `MuteService` + `NotificationOptions` config (primary host only).
- `TestControllerGrpc/App.xaml.cs` — `NotificationDispatcher` hosted service, `NotificationMuteClient`, `NotificationSettingsViewModel`.

### Files created/modified
| File | Change |
|------|--------|
| `TestControllerGrpc/Services/ActionPipelineExecutor.cs` | Bugfix: injected BuildResultsConfig, use configured SMTP |
| `TestControllerGrpc.Core/Models/NotificationEntities.cs` | New — NotificationMute + NotificationCooldown |
| `TestControllerGrpc.Core/Models/NotificationOptions.cs` | New — config class |
| `TestController.Persistence/OrchestratorDbContext.cs` | Added 2 DbSets |
| `TestController.Persistence/Configurations/NotificationConfigurations.cs` | New — EF config |
| `TestController.Api/Services/MuteService.cs` | New — mute/cooldown logic |
| `TestController.Api/Controllers/NotificationsController.cs` | New — REST endpoints |
| `TestController.Api/RbacFeatureExtensions.cs` | MuteService + NotificationOptions DI |
| `TestControllerGrpc/Services/NotificationDispatcher.cs` | New — automatic alert dispatch |
| `TestControllerGrpc/Services/NotificationMuteClient.cs` | New — WPF HTTP client |
| `TestControllerGrpc/ViewModels/Admin/NotificationSettingsViewModel.cs` | New — settings VM |
| `TestControllerGrpc/App.xaml.cs` | DI for dispatcher + mute client + VM |
| `TestController.WebClient/src/hooks/useMutes.ts` | New — React mute hook |
| `TestController.WebClient/src/components/results/TrendCharts.tsx` | Added mute controls |
| `TestController.WebApi.Tests/Rbac/NotificationMuteTests.cs` | New — 9 tests |

---

## Phase 9 — Reports (RBAC-gated)

### What changed
RBAC gating added to the **existing** report engine. No new report functionality.

- **Report_View** (all roles incl. Guest): view reports, view trends, export CSV/HTML.
- **Report_Generate** (Admin + SrMgr only): send report email.
- Trend generation is a READ → gated by Report_View, not Report_Generate.
- PDF export: NOT included.

### Backend
- `TestController.WebApi/Endpoints/ResultsEndpoints.cs` — `SendReport` gated by `Report_Generate` via `SessionAuthInterceptor.ResolveUserAsync` + `IAuthorizationService.CanAsync`. Returns 401/403 for unauthenticated/unpermitted. Fire-and-forget audit entry on successful send (ActionName=`Report_Generate`, resource=buildNumber, reason includes recipients count).
- `/api/results/export` and `/api/results/trends` left open (Report_View — all roles).

### WPF
- `TestControllerGrpc/ViewModels/BuildResultsViewModel.cs` — Constructor takes optional `CapabilityChecker`. Adds `CanExportReport` (Report_View) and `CanSendReport` (Report_Generate) observable properties. Export (CSV/HTML/Trend) commands guard on `CanExportReport`; Send (SendReport, SendQaAlert, SendEmailSummary) commands guard on `CanSendReport`. Subscribes to `CapabilitiesChanged` for re-evaluation (Dispatcher-marshaled).

### Web
- `TestController.WebClient/src/components/results/BuildDetail.tsx` — Email button gated by `useCan('Report_Generate')`. Disabled with tooltip when denied. Export buttons remain visible (Report_View — all roles).
- `TestController.WebClient/src/hooks/useResults.ts` — `sendReport` switched from raw `axios.post` to `apiFetch` so 403 triggers AuthDeniedToast.

### Tests
- `TestController.WebApi.Tests/Rbac/ReportAuthzTests.cs` — 9 tests: Report_View allowed for all 4 roles; Report_Generate allowed for Admin + SrMgr, denied for Engineer + Guest; audit fires on decision.

### DI registrations
- No new DI changes needed. `CapabilityChecker` already registered; `BuildResultsViewModel` already Singleton with auto-resolved constructor params.

### Files modified
| File | Change |
|------|--------|
| `TestController.WebApi/Endpoints/ResultsEndpoints.cs` | SendReport gated by Report_Generate + audit |
| `TestControllerGrpc/ViewModels/BuildResultsViewModel.cs` | CapabilityChecker injection, Can* properties, command guards |
| `TestController.WebClient/src/components/results/BuildDetail.tsx` | Email button gated by useCan('Report_Generate') |
| `TestController.WebClient/src/hooks/useResults.ts` | sendReport → apiFetch for 403 handling |
| `TestController.WebApi.Tests/Rbac/ReportAuthzTests.cs` | New — 9 tests |

---

## Observability & Operations (current)

| Surface | Endpoint / Mechanism | Notes |
|---------|----------------------|-------|
| Liveness | `GET /healthz/live` | Always-200 process liveness |
| Readiness | `GET /healthz/ready` | `AgentConnectivityHealthCheck` + `CertificateExpiryHealthCheck` |
| Metrics | `GET /metrics` | OpenTelemetry → Prometheus exporter; custom `AppMetrics` |
| API spec | `GET /openapi/v1.json` | `AddOpenApi()` document transformer (title/version/description) |
| API reference UI | `GET /scalar` | `MapScalarApiReference()` (BluePlanet theme, Bearer scheme) |
| Onboarding | `docs/ONBOARDING.md` | Zero-to-first-call guide (endpoints, auth, 401/403/409) |
| Logging | `IAppLogger` (not Serilog) | Category-based; ring buffer + rolling files + optional Seq sink + `SecurityRedactor` |
| Rate limiting | `telemetry` / `mutation` policies | `AddRateLimiter()` |

## Deployment Topology (current)

- `Deployment:Topology` config (`Auto` | `CoLocated` | `Standalone`) bound via `DeploymentOptions`, validated at startup by `ConfigValidator.ValidateDeploymentTopology()` (CoLocated requires `ControllerProxyUrl`; Standalone forbids it).
- Canonical 5-agent roster: `JVGR1`, `JVGR2`, `JVKPRI`, `JVKBAK`, `JVHIST` (gRPC :5200, fallback :5201–5203).
- Deploy/rollback automation: `deploy/Invoke-Deploy.ps1` (pre-deploy check → timestamped backup → deploy → smoke test → auto-rollback on failure; explicit `-Rollback`) and `.github/workflows/deploy.yml` (`workflow_dispatch`, self-hosted Windows deploy runner). See `docs/RUNBOOK.md` → "Deploy & Rollback".

## Enhancements & Changelog (production-readiness)

| Phase | What landed |
|-------|-------------|
| **P0** | Config hygiene; canonical 5-agent roster; `[pscredential]` in setup scripts. |
| **P2** | Health checks (`/healthz/*`), OpenTelemetry/Prometheus (`/metrics`), structured `IAppLogger`. |
| **P3** | Explicit `Deployment:Topology` + `ConfigValidator`; Vite dev proxy `VITE_DEV_PROXY_TARGET`. |
| **P4-1** | `System_ChangeMode`; mode-gated permission checks (fail-open in Default mode). |
| **P5-3** | `deploy/Invoke-Deploy.ps1` + `.github/workflows/deploy.yml` (deploy/rollback). |
| **P5-4** | Scalar API reference UI (`/scalar`) + `docs/ONBOARDING.md`. |

**Known gap (P5-1):** no run queue — `ExecutionController` dispatches fire-and-forget (`Task.Run`) and returns **409 Conflict** when target agents are busy; there is no server-side queue/backpressure.