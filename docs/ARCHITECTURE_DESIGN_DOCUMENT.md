# TestAgentSolution � Architecture Design Document

**Version:** 2.3 — Production-Readiness (RBAC, Observability, Deploy Automation)  
**Branch:** `ExeDashboadImpl`  
**Date:** June 2026  
**Scope:** All recent architectural changes, recommendations, performance analysis, E2E blocking points, Controller–Agent communication hardening, and the P0–P5 production-readiness work (RBAC/persistence, observability, deployment topology, deploy/rollback automation). See [§3.8–3.11](#38-rbac--persistence-layer) and the change log therein.

---

## Table of Contents

1. [Solution Overview](#1-solution-overview)
2. [Project Dependency Graph](#2-project-dependency-graph)
3. [Recent Architectural Changes](#3-recent-architectural-changes)
4. [Deployment Topology](#4-deployment-topology)
5. [API Surface & Routing Architecture](#5-api-surface--routing-architecture)
6. [Real-Time Communication (SignalR) Architecture](#6-real-time-communication-signalr-architecture)
7. [Execution Pipeline Flow](#7-execution-pipeline-flow)
8. [Performance Analysis](#8-performance-analysis)
9. [End-to-End Blocking Points](#9-end-to-end-blocking-points)
10. [Architectural Recommendations](#10-architectural-recommendations)
11. [Risk Register](#11-risk-register)

---

## 1. Solution Overview

TestAgentSolution is a **distributed test execution orchestrator** consisting of:

- A **WPF Controller** desktop application that monitors filesystem triggers, executes test pipelines across remote agents via gRPC, and hosts an embedded web server for browser-based monitoring.
- A **Standalone WebApi** that provides the same monitoring/management API without requiring the WPF desktop � suitable for headless/server deployments.
- **Remote Agents** (WinForms) that receive gRPC commands and execute test actions on target machines.
- A **React WebClient** (SPA) that connects to either host for real-time pipeline monitoring.

### Project Map

| Project | Type | TFM | Role |
|---|---|---|---|
| `TestControllerGrpc.Core` | Class Library | `net10.0` | Shared domain: models, interfaces, services (parsing, sessions, events), RBAC types (`Permission`, `DefaultUser`, `IUserContext`) |
| `TestController.Api` | Class Library | `net10.0` | Shared ASP.NET layer: MVC controllers, SignalR hub, bridge, security (`AddMultiIdentitySecurity`, `AddRbacFeature`, `SessionAuthInterceptor`) |
| `TestController.Persistence` | Class Library | `net10.0` | EF Core + SQLite: `OrchestratorDbContext`, entities, migrations, `AuthorizationService`, `SessionStore`, `QueuedAuditWriter` + `AuditDrainWorker` |
| `TestControllerGrpc` | WPF Application | `net10.0-windows` | Desktop controller: full pipeline execution, file watching, embedded web server, owns SQLite DB |
| `TestController.WebApi` | ASP.NET Web Application | `net10.0` | Standalone headless API: REST + SignalR + React SPA host; OpenAPI/Scalar; health + metrics |
| `TestAgentGrpc` | WinForms Application | `net10.0-windows` | Remote agent: receives gRPC commands, executes locally |
| `TestAgentDisplay` | WinForms Application | `net10.0-windows` | Agent monitoring/display UI |
| `TestController.Dashboard` | WPF Application | `net10.0-windows` | Standalone read-only SignalR dashboard |
| `TestAgent.Diagnostics` | WPF Application | `net10.0-windows` | Agent diagnostics utility |
| `TestControllerGrpc.Tests` | xUnit Test | `net10.0-windows` | Unit tests for Core + WPF services |
| `TestController.WebApi.Tests` | xUnit Test | `net10.0` | Integration tests for WebApi endpoints |
| `TestController.ApiTests`, `TestController.LoadTests` | xUnit / load | `net10.0` | Shared-API contract tests; performance/load suite |

---

## 2. Project Dependency Graph

```
TestAgentGrpc (Agent)          TestAgentDisplay (Agent UI)
     ?                                ?
     ??? Protos/test_agent.proto      ??? (standalone)

TestControllerGrpc.Core ????????????????????????????????????
     ? (models, interfaces, services)                      ?
     ?                                                     ?
     ?                                                     ?
TestController.Api ????????????????                        ?
     ? (MVC controllers,          ?                        ?
     ?  ControllerHub,            ?                        ?
     ?  SignalRBridge)            ?                        ?
     ?                            ?                        ?
     ?                            ?                        ?
TestControllerGrpc           TestController.WebApi          ?
(WPF Host)                   (Standalone Host)             ?
     ?                            ?                        ?
     ? embeds via                 ? references              ?
     ? ControllerWebApiHost       ? + own LiveHub           ?
     ?                            ? + own Endpoints         ?
     ?                            ? + Adapter pattern       ?
     ???????????????????????????????????????????????????????
```

---

## 3. Recent Architectural Changes

### 3.1 .NET 10 Upgrade

| Aspect | Before | After |
|---|---|---|
| Target Framework | .NET 8 | .NET 10 Preview |
| Package versions | Stable .NET 8 packages | `10.0.0-preview.3.25171.5` for `Microsoft.Extensions.*` |
| TFMs | `net8.0` / `net8.0-windows` | `net10.0` / `net10.0-windows` |

### 3.2 Core Library Extraction (`TestControllerGrpc.Core`)

**What changed:** Domain models, service interfaces, and platform-agnostic service implementations were extracted from the WPF project into `TestControllerGrpc.Core`.

| Component | Original Location | New Location |
|---|---|---|
| `WatchListConfig`, `ActionConfig`, `BuildResultsConfig`, etc. | `TestControllerGrpc/Models/` | `TestControllerGrpc.Core/Models/` |
| `IActionPipelineExecutor`, `IAgentGrpcDispatcher`, `IVocabularyMonitor` | `TestControllerGrpc/Services/` | `TestControllerGrpc.Core/Services/` |
| `ExecutionSessionManager`, `EventAggregator`, `TrxResultsParser` | `TestControllerGrpc/Services/` | `TestControllerGrpc.Core/Services/` |
| `BuildResultsAggregator`, `BuildTrendAnalyzer`, `FlakyTestDetector` | `TestControllerGrpc/Services/` | `TestControllerGrpc.Core/Services/` |
| `Protos/test_agent.proto` (Grpc codegen) | `TestControllerGrpc/` | `TestControllerGrpc.Core/` |

**Impact:** Both `TestControllerGrpc` (WPF) and `TestController.WebApi` now reference `TestControllerGrpc.Core` instead of duplicating models/interfaces.

### 3.3 Shared API Library (`TestController.Api`)

**What changed:** A new shared library was created to hold the common REST API surface that both the WPF-hosted Kestrel server and the standalone WebApi use.

| Component | Purpose |
|---|---|
| `ExecutionController` | Session CRUD, trigger, cancel |
| `WatchListController` | WatchList read, status, parameters |
| `AgentsController` | Agent listing |
| `HealthController` | Liveness + diagnostics |
| `ResultsController` | Build results, trends, flaky tests, alerts |
| `ControllerHub` | SignalR hub for real-time push |
| `SignalRBridge` | Event-to-SignalR relay with heartbeat throttling |
| `ControllerApiExtensions` | DI registration + middleware wiring |

**Registration pattern:**
```csharp
// In any host:
builder.Services.AddControllerApi();   // registers controllers + SignalRBridge
app.UseControllerApi();                // maps controllers + hub + starts bridge
```

### 3.4 Standalone WebApi with Adapter Pattern

**What changed:** `TestController.WebApi` was created as a fully independent host that can run without the WPF desktop application.

**Adapter implementations:**

| Interface | WPF Implementation | Standalone Adapter |
|---|---|---|
| `IVocabularyMonitor` | `VocabularyMonitor` (FileSystemWatcher) | `StandaloneVocabularyMonitor` (on-demand load) |
| `IAgentGrpcDispatcher` | `AgentGrpcDispatcher` (full gRPC dispatch) | `StandaloneAgentDispatcher` (registry-only, no execution) |
| `IActionPipelineExecutor` | `ActionPipelineExecutor` (full engine) | `StandalonePipelineExecutor` (stub, no execution) |
| `IEventAggregator` | `EventAggregator` (shared) | `EventAggregator` (same implementation) |

**Standalone-specific services:**

| Service | Purpose |
|---|---|
| `AgentRegistry` | Config-based agent management (not gRPC-channel-pooled) |
| `AgentGrpcClientManager` | Direct gRPC channel management for agent queries |
| `WatchListFileService` | File-based WatchList CRUD (load/save/import/export XML) |
| `SignalRBroadcastService` | BackgroundService: subscribes to agent gRPC event streams |
| `LiveHub` | Standalone SignalR hub for agent-streamed events |

### 3.5 Dual Routing Architecture (MVC + Minimal API)

**What changed:** The solution uses two routing strategies simultaneously in the standalone WebApi:

| Layer | Style | Routes |
|---|---|---|
| **Shared** (`TestController.Api`) | MVC Controllers | `/api/execution/*`, `/api/watchlist/*`, `/api/agents`, `/api/health/*`, `/api/results/*` |
| **Standalone** (`TestController.WebApi/Endpoints/`) | Minimal API | `/api/watchlist/xml,import,export,refresh,save`, `/api/agents/register,{name},test,diagnose,snapshot,health,history,audit`, `/api/execution/trigger-all,trigger-event,retry`, `/api/results/export,send-report` |

### 3.6 Bug Fixes Applied During Migration

| Fix | Description |
|---|---|
| **Dual-hub collision** | `UseControllerApi("/hub/live")` was mapping `ControllerHub` at the `LiveHub` path. Fixed: `ControllerHub` ? `/hubs/controller`, `LiveHub` ? `/hub/live` |
| **Missing results endpoint mapping** | `MapResultsEndpoints()` was never called in `Program.cs`. Fixed: added `app.MapGroup("/api/results").MapResultsEndpoints()` |
| **ResultsController route mismatch** | `[HttpGet("{buildNumber}")]` mapped to `/api/results/{buildNumber}` but tests/clients expected `/api/results/builds/{buildNumber}`. Fixed: route changed to `[HttpGet("builds/{buildNumber}")]` |
| **ResultsController incomplete response** | `GetBuilds()` returned only `{buildNumber, modified}` but clients expected `{totalTests, passedTests, failedTests, passRate, health}`. Fixed: now parses each build folder with `TrxResultsParser` + `BuildResultsAggregator` |

### 3.7 Controller�Agent Communication Hardening (ExeDashboadImpl branch)

**What changed:** The gRPC communication between Controller and Agent was hardened to prevent and recover from the "Agent Busy" stuck state, where agents become permanently unresponsive due to failed cancellation flows or corrupted HTTP/2 channels.

#### New Components

| Component | Location | Purpose |
|---|---|---|
| `StuckExecutionWatchdog` | `TestAgentGrpc/Services/` | `BackgroundService` polling every 60s. If agent stuck in `Running` beyond `MaxExecutionTimeoutMinutes + WatchdogGraceMinutes`, force-resets via `CommandExecutor.ForceReady()` |
| `ControllerTimeoutOptions` | `TestControllerGrpc/` | Strongly-typed configuration class bound from `appsettings.json ? Controller:Timeouts`. Replaces all hardcoded timeout values in `AgentGrpcDispatcher` |
| `ExecutionStreamSafeguardTests` | `TestControllerGrpc.Tests/` | 324-line test suite covering active execution guard, busy-recovery, ForceReady escalation, and channel reset |

#### `IAgentGrpcDispatcher` Interface Additions

| New Method | Behavior |
|---|---|
| `bool IsAgentExecuting(string agentName)` | Returns `true` if a `RunCommandStreamed` call is actively in-flight for the agent |
| `Task<bool> ResetChannelAsync(string agentName)` | Disposes old gRPC channel, creates fresh connection, pings to verify. Blocked during active execution to prevent stream interference |

#### Active Execution Guard

During `RunCommandStreamed`, the agent is tracked in `_activeExecutions`. Any concurrent `TestConnectionAsync` or health poll returns a **synthetic snapshot** (state=Running, currentCommand from tracking) without issuing a gRPC call. This prevents HTTP/2 GOAWAY/RST_STREAM from killing the in-flight stream.

#### Busy-Recovery Flow (Configurable)

```
Agent reports BUSY on command dispatch
    ? Wait up to BusyRecoveryMaxSeconds (default: 120)
    ? Poll every BusyRecoveryIntervalSeconds (default: 10)
    ? If still busy ? call ForceReady RPC on agent
    ? If ForceReady succeeds ? retry command
    ? If consecutive failures ? AutoResetFailureThreshold (default: 10)
        ? async ResetChannelAsync (dispose + recreate gRPC channel)
```

#### `ControllerTimeoutOptions` Configuration

```json
"Controller": {
  "Timeouts": {
    "TestConnectionTimeoutSeconds": 5,
    "PingTimeoutSeconds": 3,
    "BusyRecoveryMaxSeconds": 120,
    "BusyRecoveryIntervalSeconds": 10,
    "ChannelConnectTimeoutSeconds": 30,
    "KeepAlivePingDelaySeconds": 60,
    "KeepAlivePingTimeoutSeconds": 30,
    "PooledConnectionIdleMinutes": 5,
    "CircuitBreakerBreakSeconds": 15,
    "AutoResetFailureThreshold": 10,
    "OuterSafetyNetTimeoutHours": 24,
    "RetryMaxAttempts": 3,
    "RetryDelaySeconds": 1
  }
}
```

#### Agent-Side Settings

| Setting | Default | Purpose |
|---|---|---|
| `MaxExecutionTimeoutMinutes` | `120` | Hard safety-net timeout for executions with no explicit timeout |
| `WatchdogGraceMinutes` | `5` | Grace period beyond max timeout before watchdog force-resets |

### 3.8 RBAC & Persistence Layer

A new project, `TestController.Persistence` (EF Core + SQLite), backs role-based access
control. Two auth layers now coexist on different request paths:

- **Multi-Identity Security** (`AddMultiIdentitySecurity()`) — unchanged HTTP auth for REST
  (NTLM/Negotiate + bearer + API key; `SecurityPolicies.Admin/User/Anonymous`).
- **RBAC session auth** (`AddRbacFeature()`) — gRPC path via `SessionAuthInterceptor`.

`RbacOptions.Enabled` selects **Default** (open; WPF full, web read-only; `DefaultUser`
injected) vs **Secured** (bearer token required, per-user roles + `PipelineAssignment`s).
Permissions live in the `Permission` enum (`Resource_Action` naming) and are enforced
**fail-open in Default mode** — controllers (`ExecutionController`, `UserController`,
`SystemModeController`) call mode-gated helpers that only invoke `AuthorizationService.CanAsync`
in Secured mode. `OrchestratorDbContext` is the only **Scoped** service (factory pattern for
Singletons); audit is **fire-and-forget** via `QueuedAuditWriter` + `AuditDrainWorker`.

### 3.9 Observability

`TestController.WebApi` exposes `/healthz/live`, `/healthz/ready` (`AgentConnectivityHealthCheck`
+ `CertificateExpiryHealthCheck`), OpenTelemetry metrics at `/metrics` (Prometheus; custom
`AppMetrics`), OpenAPI at `/openapi/v1.json`, and the **Scalar** API reference UI at `/scalar`.
Logging is the custom category-based `IAppLogger` (not Serilog) with ring buffer, rolling files,
optional Seq sink, and `SecurityRedactor`. REST writes are rate-limited (`telemetry`/`mutation`).

### 3.10 Explicit Deployment Topology

`Deployment:Topology` (`Auto` | `CoLocated` | `Standalone`) is now bound via `DeploymentOptions`
and validated at startup by `ConfigValidator.ValidateDeploymentTopology()`, which fails fast on
contradictions with `ControllerProxyUrl` (CoLocated requires the proxy; Standalone forbids it).
This removes the "source of truth depends on a config string" ambiguity in co-located mode.

### 3.11 Deploy / Rollback Automation

`deploy/Invoke-Deploy.ps1` wraps the per-component deploy scripts with a safety net:
pre-deploy session check → timestamped backup → deploy → smoke test → **auto-rollback** on
failure (and an explicit `-Rollback`). The `workflow_dispatch` workflow
`.github/workflows/deploy.yml` lets anyone deploy/roll back from `docs/RUNBOOK.md`. The
canonical agent roster is 5 nodes (`JVGR1`, `JVGR2`, `JVKPRI`, `JVKBAK`, `JVHIST`), and
`docs/ONBOARDING.md` is the first-run guide (via `/scalar`).

> **Phase map:** P0 config hygiene · P2 observability · P3 topology decoupling ·
> P4 RBAC enforcement · P5-3 deploy/rollback · P5-4 Scalar + onboarding.
> **Known gap (P5-1):** no run queue — `ExecutionController` returns 409 when agents are busy.

---

## 4. Deployment Topology

```
???????????????????????????????????????????????????????
?                  DEPLOYMENT MODE A                   ?
?              WPF Desktop + Embedded Web              ?
?                                                      ?
?  ????????????????????????????????????????????       ?
?  ?  TestControllerGrpc (WPF)                ?       ?
?  ?  ??????????????????????????????????      ?       ?
?  ?  ?  ControllerWebApiHost          ?      ?       ?
?  ?  ?  (Kestrel on port 5200)        ?      ?       ?
?  ?  ?  ????????????????????????????  ?      ?       ?
?  ?  ?  ? TestController.Api       ?  ?      ?       ?
?  ?  ?  ? � MVC Controllers        ?  ?      ?       ?
?  ?  ?  ? � ControllerHub          ?  ?      ?       ?
?  ?  ?  ? � SignalRBridge          ?  ?      ?       ?
?  ?  ?  ????????????????????????????  ?      ?       ?
?  ?  ??????????????????????????????????      ?       ?
?  ?  ??????????????????????????????????      ?       ?
?  ?  ? Real implementations:          ?      ?       ?
?  ?  ? � ActionPipelineExecutor       ?      ?       ?
?  ?  ? � AgentGrpcDispatcher          ?      ?       ?
?  ?  ? � VocabularyMonitor            ?      ?       ?
?  ?  ? � FileWatcherManager           ?      ?       ?
?  ?  ??????????????????????????????????      ?       ?
?  ????????????????????????????????????????????       ?
?         ? gRPC                                       ?
?         ?                                            ?
?  ????????????????  ????????????????                 ?
?  ? TestAgentGrpc?  ? TestAgentGrpc?  ... (N agents) ?
?  ? (Agent 1)    ?  ? (Agent 2)    ?                 ?
?  ????????????????  ????????????????                 ?
???????????????????????????????????????????????????????

???????????????????????????????????????????????????????
?                  DEPLOYMENT MODE B                   ?
?               Standalone Headless API                ?
?                                                      ?
?  ????????????????????????????????????????????       ?
?  ?  TestController.WebApi                    ?       ?
?  ?  (Kestrel, default port 5000)             ?       ?
?  ?  ????????????????????????????????         ?       ?
?  ?  ? TestController.Api           ?         ?       ?
?  ?  ? � MVC Controllers            ?         ?       ?
?  ?  ? � ControllerHub (/hubs/ctrl) ?         ?       ?
?  ?  ? � SignalRBridge              ?         ?       ?
?  ?  ????????????????????????????????         ?       ?
?  ?  ????????????????????????????????         ?       ?
?  ?  ? Minimal API Endpoints        ?         ?       ?
?  ?  ? � WatchListEndpoints         ?         ?       ?
?  ?  ? � AgentEndpoints             ?         ?       ?
?  ?  ? � ExecutionEndpoints         ?         ?       ?
?  ?  ? � ResultsEndpoints           ?         ?       ?
?  ?  ????????????????????????????????         ?       ?
?  ?  ????????????????????????????????         ?       ?
?  ?  ? LiveHub (/hub/live)          ?         ?       ?
?  ?  ? SignalRBroadcastService      ?         ?       ?
?  ?  ????????????????????????????????         ?       ?
?  ?  ????????????????????????????????         ?       ?
?  ?  ? Stub/Adapter implementations ?         ?       ?
?  ?  ? � StandalonePipelineExecutor ?         ?       ?
?  ?  ? � StandaloneAgentDispatcher  ?         ?       ?
?  ?  ? � StandaloneVocabularyMonitor?         ?       ?
?  ?  ????????????????????????????????         ?       ?
?  ????????????????????????????????????????????       ?
?         ? gRPC (query-only)                          ?
?         ?                                            ?
?  ????????????????  ????????????????                 ?
?  ? TestAgentGrpc?  ? TestAgentGrpc?  ... (N agents) ?
?  ????????????????  ????????????????                 ?
???????????????????????????????????????????????????????
```

---

## 5. API Surface & Routing Architecture

### Complete Route Map (Standalone WebApi)

| Method | Route | Source | Handler |
|---|---|---|---|
| **Execution** | | | |
| `GET` | `/api/execution/sessions` | MVC | `ExecutionController.GetSessions` |
| `GET` | `/api/execution/status` | MVC | `ExecutionController.GetStatus` |
| `GET` | `/api/execution/{sessionId}` | MVC | `ExecutionController.GetSession` |
| `POST` | `/api/execution/trigger/{tag}` | MVC | `ExecutionController.TriggerWatchItem` |
| `POST` | `/api/execution/cancel/{sessionId}` | MVC | `ExecutionController.CancelExecution` |
| `POST` | `/api/execution/trigger-all` | Minimal | `ExecutionEndpoints.TriggerAll` |
| `POST` | `/api/execution/trigger-event/{tag}/{idx}` | Minimal | `ExecutionEndpoints.TriggerEvent` |
| `POST` | `/api/execution/retry/{sessionId}` | Minimal | `ExecutionEndpoints.RetrySession` |
| **WatchList** | | | |
| `GET` | `/api/watchlist` | MVC | `WatchListController.GetWatchList` |
| `GET` | `/api/watchlist/{tag}/status` | MVC | `WatchListController.GetStatus` |
| `GET` | `/api/watchlist/{tag}/parameters` | MVC | `WatchListController.GetParameters` |
| `GET` | `/api/watchlist/xml` | Minimal | `WatchListEndpoints.GetWatchListXml` |
| `PUT` | `/api/watchlist` | Minimal | `WatchListEndpoints.SaveWatchList` |
| `POST` | `/api/watchlist/import` | Minimal | `WatchListEndpoints.ImportWatchItems` |
| `GET` | `/api/watchlist/export` | Minimal | `WatchListEndpoints.ExportWatchItems` |
| `POST` | `/api/watchlist/refresh` | Minimal | `WatchListEndpoints.RefreshWatchList` |
| **Agents** | | | |
| `GET` | `/api/agents` | MVC | `AgentsController.GetAgents` |
| `POST` | `/api/agents/register` | Minimal | `AgentEndpoints.RegisterAgent` |
| `DELETE` | `/api/agents/{name}` | Minimal | `AgentEndpoints.UnregisterAgent` |
| `POST` | `/api/agents/{name}/test` | Minimal | `AgentEndpoints.TestAgent` |
| `POST` | `/api/agents/{name}/diagnose` | Minimal | `AgentEndpoints.DiagnoseAgent` |
| `GET` | `/api/agents/{name}/snapshot` | Minimal | `AgentEndpoints.GetAgentSnapshot` |
| `GET` | `/api/agents/{name}/health` | Minimal | `AgentEndpoints.GetAgentHealth` |
| `GET` | `/api/agents/{name}/history` | Minimal | `AgentEndpoints.GetAgentHistory` |
| `GET` | `/api/agents/{name}/audit` | Minimal | `AgentEndpoints.GetAgentAudit` |
| **Results** | | | |
| `GET` | `/api/results/builds` | MVC | `ResultsController.GetBuilds` |
| `GET` | `/api/results/builds/{buildNumber}` | MVC | `ResultsController.GetBuild` |
| `GET` | `/api/results/trends` | MVC | `ResultsController.GetTrends` |
| `GET` | `/api/results/flaky` | MVC | `ResultsController.GetFlakyTests` |
| `GET` | `/api/results/alerts` | MVC | `ResultsController.GetAlerts` |
| `GET` | `/api/results/export/{buildNumber}` | Minimal | `ResultsEndpoints.ExportBuildReport` |
| `POST` | `/api/results/send-report` | Minimal | `ResultsEndpoints.SendReport` |
| **Health** | | | |
| `GET` | `/api/health` | MVC | `HealthController.Health` |
| `GET` | `/api/health/diagnostics` | MVC | `HealthController.Diagnostics` |
| **SignalR** | | | |
| WebSocket | `/hubs/controller` | Shared | `ControllerHub` (bridge events) |
| WebSocket | `/hub/live` | Standalone | `LiveHub` (gRPC-streamed events) |

### Routing Conflict Risk

The MVC controllers and Minimal API endpoints share the same route prefixes (`/api/watchlist`, `/api/agents`, `/api/execution`, `/api/results`). ASP.NET resolves this because:
- MVC routes are registered via `MapControllers()` with explicit `[HttpGet("builds")]` attributes
- Minimal API routes are registered via `MapGroup().MapGet("/xml", ...)` with different sub-paths

**No overlapping routes exist currently**, but adding new routes requires checking both systems.

---

## 6. Real-Time Communication (SignalR) Architecture

### Current Dual-Hub Design

```
                    ???????????????????????????????
                    ?     React WebClient (SPA)    ?
                    ?                              ?
                    ?  HubConnection A             ?  HubConnection B
                    ?  ? /hubs/controller          ?  ? /hub/live
                    ????????????????????????????????
                           ?                  ?
                    ???????????????    ???????????????
                    ?ControllerHub?    ?   LiveHub    ?
                    ?  (shared)   ?    ? (standalone) ?
                    ???????????????    ???????????????
                           ?                  ?
                    ???????????????    ???????????????????????
                    ?SignalRBridge?    ?SignalRBroadcastService?
                    ?             ?    ?(BackgroundService)    ?
                    ? Subscribes: ?    ?                       ?
                    ? �LogEntry   ?    ? Subscribes:           ?
                    ? �NodeProgress?   ? � Agent gRPC streams  ?
                    ? �OutputRcvd ?    ?   (SubscribeAgentEvents)?
                    ? �StatusChgd ?    ?                       ?
                    ? �EventAgg   ?    ? Broadcasts:           ?
                    ?  events     ?    ? �ExecutionLog         ?
                    ?             ?    ? �ExecutionEvent       ?
                    ? Broadcasts: ?    ? �AgentStatus          ?
                    ? �LogEntry   ?    ?????????????????????????
                    ? �ActionProgress?
                    ? �GroupProgress ?
                    ? �AgentOutput   ?
                    ? �AgentStatusChanged?
                    ? �AgentHeartbeats?
                    ? �WatchListReloaded?
                    ? �ExecutionStarted?
                    ? �ExecutionCompleted?
                    ???????????????????
```

### Event Name Contract

| ControllerHub (via SignalRBridge) | LiveHub (via BroadcastService) | Semantic Overlap |
|---|---|---|
| `LogEntry` | `ExecutionLog` | ?? Both describe pipeline log entries |
| `ActionProgress` | `NodeProgress` | ?? Both describe action state changes |
| `AgentOutput` | `ExecutionEvent` | ?? Both describe stdout/stderr output |
| `AgentStatusChanged` | `AgentStatus` | ?? Both describe agent state |
| `AgentHeartbeats` | *(no equivalent)* | Throttled batch push |
| `ExecutionStarted` | *(no equivalent)* | Session lifecycle |
| `ExecutionCompleted` | *(no equivalent)* | Session lifecycle |
| `WatchListReloaded` | *(no equivalent)* | Config change |

**Risk:** A React client connecting to the wrong hub receives differently-named events with different payload shapes.

---

## 7. Execution Pipeline Flow

### End-to-End Trigger ? Result Flow

```
 ?  Browser POST /api/execution/trigger/{tag}
          ?
 ?  ExecutionController.TriggerWatchItem()
          ?  ? Per-tag lock (ConcurrentDictionary) prevents TOCTOU
          ?
 ?  IVocabularyMonitor.CurrentConfig ? resolve WatchItem + Event
          ?
 ?  ParameterResolver ? merge Variables.txt + request params
          ?
 ?  ExecutionSessionManager.BeginSession()
          ?  ? Creates ExecutionSession with snapshot of action tree
          ?
 ?  IEventAggregator.Publish(ExecutionStartedEvent)
          ?  ? SignalRBridge ? ControllerHub ? "ExecutionStarted"
          ?
 ?  IActionPipelineExecutor.ExecuteEventTrackedAsync()
          ?
 ?  ?? ActionGroup (Sequential) ??????????????????????????
     ?  ?a  Action (RunCommand)                           ?
     ?       ? Process.Start() on controller machine      ?
     ?       ? LogEntry event ? SignalRBridge ? broadcast  ?
     ?       ? NodeProgress event ? SignalRBridge          ?
     ?                                                     ?
     ?  ?b  Action (RunRemoteCommand)                     ?
     ?       ? IAgentGrpcDispatcher.ExecuteRemoteCommandAsync()?
     ?       ? gRPC call to TestAgentGrpc                  ?
     ?       ? OutputReceived event ? SignalRBridge        ?
     ?       ? StatusChanged event ? SignalRBridge         ?
     ?                                                     ?
     ?  ?c  Action (SendMail)                             ?
     ?       ? SmtpClient or BuildReportHtmlGenerator      ?
     ?                                                     ?
     ?  ?d  ActionGroup (Parallel)                        ?
     ?       ? Task.WhenAll(children)                      ?
     ?       ? Each child follows ?a/?b/?c               ?
     ??????????????????????????????????????????????????????
          ?
 ?  ExecutionSessionManager.RecordResult() per action
          ?  ? Thread-safe: ConcurrentBag in ExecutionSession
          ?
 ?  ExecutionSessionManager.CompleteSession()
          ?  ? Archives to history (max 50), removes from active
          ?
 ?  IEventAggregator.Publish(ExecutionCompletedEvent)
          ?  ? SignalRBridge ? ControllerHub ? "ExecutionCompleted"
          ?
 ?  Browser receives final state via SignalR
```

---

## 8. Performance Analysis

### 8.1 Performance Hits from Recent Changes

#### ? `ResultsController.GetBuilds()` � N�TRX Parsing per Request

**Change:** `GetBuilds()` was updated to parse every build folder with `TrxResultsParser` + `BuildResultsAggregator.EvaluateBuildHealth()` on every call.

**Impact:**
```
BEFORE:  O(1) per build � just read directory metadata
AFTER:   O(N � M) per build � parse N .trx XML files, aggregate M test results
```

| Builds | TRX Files/Build | Approx Latency (before) | Approx Latency (after) |
|---|---|---|---|
| 10 | 5 | ~2ms | ~200ms |
| 50 | 10 | ~5ms | ~2-5s |
| 100 | 20 | ~8ms | ~10-20s |

**Severity:** ?? **HIGH** � This endpoint is called on page load by the React dashboard.

**Mitigation:** Add a caching layer:
```csharp
// Recommended: cache parsed results keyed by build folder last-write-time
ConcurrentDictionary<string, (DateTime Modified, BuildSummary Summary)> _cache;
```

#### ? Dual MVC + Minimal API Pipeline Overhead

**Impact:** Every request passes through both the MVC middleware pipeline (routing, model binding, filters, content negotiation) AND the Minimal API endpoint routing. Even though only one matches, ASP.NET evaluates both route tables.

**Measured overhead:** ~0.1-0.3ms per request (negligible for typical loads).

**Severity:** ?? **LOW** � Only significant at very high request rates (>10K req/s).

#### ? `SignalRBridge` + `SignalRBroadcastService` Running Simultaneously

**Impact:** In the standalone WebApi, both broadcasting systems are active:
- `SignalRBridge` subscribes to `IActionPipelineExecutor` events ? broadcasts to `ControllerHub`
- `SignalRBroadcastService` subscribes to gRPC agent streams ? broadcasts to `LiveHub`

Since `StandalonePipelineExecutor` is a stub (events never fire), `SignalRBridge` subscribes to events that never trigger � **wasted subscriptions but zero runtime cost**.

However, the `SignalRBridge` heartbeat throttle `Timer` runs every 1 second even when idle.

**Severity:** ?? **NEGLIGIBLE** � One timer tick per second with empty dictionary check.

#### ? `EventAggregator.Publish()` � ThreadPool Dispatch per Handler

**Impact:** Every `Publish<T>()` call queues a `ThreadPool.QueueUserWorkItem` per subscriber. Under 100 agents sending heartbeats (100 � N subscribers):

```
Heartbeats/sec: 100 agents � 1 heartbeat/sec = 100 publishes/sec
Subscribers: ~5 (bridge, UI, logging, etc.)
ThreadPool items/sec: 500 work items/sec
```

**Severity:** ?? **MEDIUM** at scale � ThreadPool work item overhead is ~1-2?s each, but 500/sec adds up. The `SignalRBridge` heartbeat throttling (1-second batching) mitigates this for the SignalR broadcast, but **other subscribers still receive 100 individual callbacks/sec**.

#### ? `WebApplication.CreateBuilder()` inside `ControllerWebApiHost.RunServerAsync()`

**Impact:** The WPF host creates a **second full ASP.NET host** with its own DI container, configuration, logging pipeline. This doubles memory for hosting services (~20-50MB additional).

**Severity:** ?? **MEDIUM** � Unavoidable in the current architecture since WPF's `IHost` and the embedded web server's `WebApplication` have separate DI containers. Singletons are manually bridged.

### 8.2 Performance Hot Paths

| Hot Path | Frequency | Current Perf | Risk |
|---|---|---|---|
| `GET /api/results/builds` | On every dashboard load | ?? O(N�M) TRX parse | Cache needed |
| `GET /api/execution/sessions` | Polling every 2-5s | ?? O(1) ConcurrentDictionary read | OK |
| `GET /api/watchlist` | On every page load | ?? Cached in `IVocabularyMonitor` | OK |
| SignalR `AgentHeartbeats` | 100 agents � 1/sec | ?? Throttled to 1 batch/sec | OK |
| SignalR `LogEntry` | ~10-100/sec during execution | ?? Unbatched, per-entry broadcast | May flood at scale |
| `ExecuteEventTrackedAsync` | Per trigger | ?? Async pipeline | OK |
| gRPC `ExecuteCommand` | Per remote action | ?? Streaming with timeout | OK |

---

## 9. End-to-End Blocking Points

### Block 1: ?? Standalone WebApi Cannot Execute Pipelines

**Where:** `StandalonePipelineExecutor` is a stub � all `Execute*` methods return `Task.CompletedTask` or `false`.

**Impact:** `POST /api/execution/trigger/{tag}` creates a session but **no actions actually run**. The session completes immediately with 0 results.

**E2E Flow Blocked:**
```
Browser ? POST trigger ? ExecutionController ? BeginSession ?
? ExecuteEventTrackedAsync ? StandalonePipelineExecutor ? does nothing ?
? CompleteSession ? session shows 0 passed, 0 failed
```

**To unblock:** Either:
1. Forward the trigger to the WPF controller via a gRPC/HTTP relay
2. Implement real local execution in `StandalonePipelineExecutor`

### Block 2: ?? Standalone WebApi Cannot Execute Remote Commands on Agents

**Where:** `StandaloneAgentDispatcher.ExecuteRemoteCommandAsync()` returns failure.

**Impact:** Even if pipeline execution were implemented, `RunRemoteCommand` actions would fail because the dispatcher doesn't manage gRPC execution channels � only query channels (via `AgentGrpcClientManager`).

**E2E Flow Blocked:**
```
Pipeline ? RunRemoteCommand action ? StandaloneAgentDispatcher
? returns ActionResult(false, -1, "not supported") ?
```

### Block 3: ?? No Hot-Reload in Standalone Mode

**Where:** `StandaloneVocabularyMonitor` does not use `FileSystemWatcher`. Config is loaded once and cached.

**Impact:** If the WatchList XML is modified on disk, the standalone WebApi serves stale data until `POST /api/watchlist/refresh` is called or the `Invalidate()` method is invoked.

### Block 4: ?? Dual-Hub Client Confusion

**Where:** React client must decide which hub to connect to (`/hubs/controller` vs `/hub/live`).

**Impact:**
- Connecting only to `/hubs/controller` ? misses gRPC-streamed agent events
- Connecting only to `/hub/live` ? misses pipeline execution events
- Connecting to both ? receives semantically overlapping events with different names/shapes

### Block 5: ?? Session History Limited to 50

**Where:** `ExecutionSessionManager.CompleteSession()` trims history to 50 entries.

**Impact:** In CI/CD environments with frequent triggers, older sessions are silently discarded. No persistence to disk or database.

### Block 6: ?? WPF-Hosted Web Server Shares Singletons via Manual Bridging

**Where:** `ControllerWebApiHost.RunServerAsync()` manually registers each singleton:
```csharp
builder.Services.AddSingleton(_sessionManager);
builder.Services.AddSingleton(_executor);
// ... 9 more manual registrations
```

**Impact:** Adding a new shared service requires updating this bridging code. Forgetting to bridge a service causes a runtime DI resolution failure (not caught at compile time).

---

## 10. Architectural Recommendations

### Recommendation 1: Migrate MVC Controllers ? Minimal API Endpoints

**Priority:** HIGH  
**Effort:** MEDIUM (2-3 days)

Replace `TestController.Api`'s MVC controllers with Minimal API endpoint mappings to:
- Eliminate `AddControllers()` / `MapControllers()` / `AddApplicationPart()` overhead
- Unify the routing style across the entire solution
- Remove the dual-pipeline evaluation cost
- Simplify the `ControllerApiExtensions` registration

**Before:**
```csharp
// ControllerApiExtensions.cs
public static IMvcBuilder AddControllerApi(this IServiceCollection services)
{
    services.AddSingleton<SignalRBridge>();
    return services.AddControllers()
        .AddApplicationPart(typeof(ControllerApiExtensions).Assembly);
}
```

**After:**
```csharp
public static IServiceCollection AddControllerApi(this IServiceCollection services)
{
    services.AddSingleton<SignalRBridge>();
    return services;
}

public static WebApplication UseControllerApi(this WebApplication app, string hubPath = "/hubs/controller")
{
    app.MapGroup("/api/execution").MapSharedExecutionEndpoints();
    app.MapGroup("/api/watchlist").MapSharedWatchListEndpoints();
    app.MapGroup("/api/agents").MapSharedAgentEndpoints();
    app.MapGroup("/api/results").MapSharedResultsEndpoints();
    app.MapGroup("/api").MapSharedHealthEndpoints();
    app.MapHub<ControllerHub>(hubPath);
    // ...
}
```

**Side effects to manage:**
- `IActionResult` ? `IResult` translation for all return types
- Loss of automatic `[ApiController]` model validation (add manual validation or a filter)
- `AddJsonOptions()` ? `ConfigureHttpJsonOptions()` in both hosts
- Update integration tests (model expectations unchanged if routes are preserved)

### Recommendation 2: Unify SignalR to a Single Hub with Consistent Event Names

**Priority:** HIGH  
**Effort:** MEDIUM (1-2 days)

Merge `ControllerHub` and `LiveHub` into a single hub, or introduce an `IRealtimeNotifier` abstraction:

```csharp
// In TestControllerGrpc.Core
public interface IRealtimeNotifier
{
    Task BroadcastLogEntry(PipelineLogEntry entry);
    Task BroadcastActionProgress(IActionNode node, string status);
    Task BroadcastAgentStatus(string agentName, string status);
    Task BroadcastAgentHeartbeats(IReadOnlyList<AgentHeartbeatPayload> batch);
    Task BroadcastExecutionStarted(ExecutionStartedEvent e);
    Task BroadcastExecutionCompleted(ExecutionCompletedEvent e);
}
```

Each host provides its own implementation wrapping `IHubContext<T>`, preserving the heartbeat throttling and severity classification.

### Recommendation 3: Cache Build Results in `ResultsController`

**Priority:** HIGH  
**Effort:** LOW (half day)

```csharp
public sealed class CachedBuildResultsProvider
{
    private readonly ConcurrentDictionary<string, (DateTime Modified, BuildSummaryDto Summary)> _cache = new();

    public BuildSummaryDto GetOrParse(BuildDiscoveryEntry entry, TrxResultsParser parser, BuildResultsAggregator agg)
    {
        if (_cache.TryGetValue(entry.BuildNumber, out var cached) && cached.Modified == entry.Modified)
            return cached.Summary;

        var node = parser.ParseBuildFolder(entry.Path);
        node = agg.EvaluateBuildHealth(node);
        var summary = MapToDto(node);
        _cache[entry.BuildNumber] = (entry.Modified, summary);
        return summary;
    }
}
```

### Recommendation 4: Type-Safe DI Bridging for WPF Host

**Priority:** MEDIUM  
**Effort:** LOW (half day)

Replace manual singleton bridging with a marker interface or source generator to prevent runtime failures:

```csharp
// Instead of 9+ manual AddSingleton calls:
builder.Services.BridgeFrom(wpfServiceProvider, typeof(ExecutionSessionManager),
    typeof(IActionPipelineExecutor), typeof(IAgentGrpcDispatcher), /* ... */);
```

### Recommendation 5: Persistent Session History

**Priority:** MEDIUM  
**Effort:** MEDIUM (1-2 days)

Replace the in-memory `List<ExecutionSession>` with a lightweight persistence layer (SQLite via EF Core or simple JSON file) to survive restarts and support unlimited history queries.

### Recommendation 6: Implement Pipeline Forwarding in Standalone Mode

**Priority:** LOW (only if headless deployment required)  
**Effort:** HIGH (3-5 days)

Replace `StandalonePipelineExecutor` with an implementation that forwards execution requests to a WPF controller instance via HTTP/gRPC reverse proxy, or implement local-only execution support.

---

## 11. Risk Register

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R1 | React client connects to wrong hub, misses events | HIGH | HIGH | Unify to single hub (Rec. 2) |
| R2 | `/api/results/builds` latency grows with build count | HIGH | HIGH | Add caching (Rec. 3) |
| R3 | New service added but not bridged in WPF host ? runtime crash | MEDIUM | HIGH | Type-safe bridging (Rec. 4) |
| R4 | Dual MVC + Minimal API routing causes confusion for new developers | MEDIUM | MEDIUM | Migrate to single style (Rec. 1) |
| R5 | Session history lost on restart, no audit trail | MEDIUM | MEDIUM | Persistent storage (Rec. 5) |
| R6 | `EventAggregator` ThreadPool flooding at 100+ agents | LOW | MEDIUM | Batch heartbeat publishes before `Publish()` |
| R7 | Standalone WebApi trigger appears to succeed but does nothing | HIGH | HIGH | Clear error response or implement forwarding (Rec. 6) |
| R8 | Preview .NET 10 packages cause runtime issues in production | MEDIUM | HIGH | Pin to stable versions when released |
| R9 | `SignalRBridge` heartbeat timer runs even when no clients connected | LOW | LOW | Check `ControllerHub` connection count before flushing |
| R10 | `StandaloneVocabularyMonitor` serves stale config | MEDIUM | LOW | Add `FileSystemWatcher` or auto-refresh timer |

---

## Appendix A: Test Coverage Summary

| Test Project | Tests | Status |
|---|---|---|
| `TestControllerGrpc.Tests` | 219 | ? All passing |
| `TestController.WebApi.Tests` | 77 | ? All passing |
| **Total** | **296** | ? **All passing** |

### Test Categories

| Category | Count | Coverage Area |
|---|---|---|
| `ResultsEndpointsTests` | 18 | Build results API (builds, trends, alerts, export, email) |
| `AgentEndpointsTests` | 12 | Agent management API (register, unregister, test, diagnose) |
| `WatchListEndpointsTests` | ~15 | WatchList CRUD API |
| `ExecutionEndpointsTests` | ~10 | Execution trigger/cancel API |
| `ScalabilityFixTests` | ~10 | Thread safety, concurrent sessions, event aggregator |
| `SmartRetryTests` | ~8 | Retry logic with exit code filters |
| `VocabularyMonitorTests` | ~6 | FileSystemWatcher debouncing, config reload |
| `ConcurrentSessionTests` | ~6 | Multi-session isolation, cancellation |

---

*Document generated from codebase analysis on branch `upgrade-to-NET10`.*
