# Current State — TestAgentSolution

## Solution Layout

| Project | Purpose |
|---------|---------|
| `TestControllerGrpc.Core` | Shared library: proto-generated gRPC types, domain models (`WatchListConfig`), service interfaces (`IActionPipelineExecutor`, `IAppLogger`, etc.), XML parser, session manager, logging infrastructure. |
| `TestControllerGrpc` | WPF controller desktop app — hosts gRPC server, REST/SignalR WebApi, file watchers, and the full action-pipeline executor with MVVM UI. |
| `TestController.Api` | ASP.NET Core class library of shared controllers, SignalR hubs, middleware, and security (multi-identity auth). Referenced by both WPF host and standalone WebApi. |
| `TestController.WebApi` | Standalone ASP.NET Core host — REST + SignalR for the React client, acts as proxy to agents via gRPC. |
| `TestController.WebClient` | React 18 + TypeScript SPA (Vite, Tailwind, Zustand, SignalR) served by WebApi. |
| `TestAgentGrpc` | Windows agent service — hosts gRPC server (`TestAgentService`), runs commands locally, system-tray UI (WinForms). |
| `TestAgentDisplay` | WPF read-only agent display panel — gRPC client connecting to a running agent for monitoring. |
| `TestController.Dashboard` | Standalone WPF dashboard — connects to controller via SignalR (read-only feed consumer). |
| `TestControllerGrpc.Tests` | xUnit tests for the controller WPF app and Core library. |
| `TestController.WebApi.Tests` | xUnit integration tests for the WebApi (uses `WebApplicationFactory`). |

## Public Interfaces in ControlNode.Core

**To be created in Phase 0.** Per `docs/Requirements/RoleBasedSecuritywithLocks/02_Implementation_Roadmap.md`, planned types include `IUserContext`, `Role`, `Permission`, `IAuthorizationService`, `IAuditWriter`. None exist yet.

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

- **State management:** Zustand (stores in `src/stores/`: `agentStore`, `executionStore`, `watchlistStore`, `resultsStore`, `connectionStore`)
- **API client:** Custom `apiFetch<T>()` wrapper in `src/lib/api.ts` with correlation ID tracking, HTML-detection, structured errors. Also `axios` instances in hooks.
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
| Security | `AddMultiIdentitySecurity()` — NTLM/negotiate + API-key + roles |
| API | `AddControllerApi()` — shared controllers + SignalR hub |
| Infra | `AddSignalR()`, `AddCors()`, `AddRateLimiter()`, `AddOpenApi()`, `AddHealthChecks()`, `AddOpenTelemetry()` (Prometheus exporter) |
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

Append a new section to docs/architecture/CURRENT_STATE.md:

## RBAC Feature (Phase 0.5 complete — YYYY-MM-DD)

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

Output: only the diff to append. Do not regenerate the whole file.