# TestAgentSolution � Architecture & System Design

> **Generated:** 2025 � **Target:** .NET 10 � **Branch:** `upgrade-to-NET10`

---

## Table of Contents

1. [System Overview](#1-system-overview)
2. [Deployment Modes](#2-deployment-modes)
3. [Project Dependency Graph](#3-project-dependency-graph)
4. [Project Inventory](#4-project-inventory)
5. [Core Domain Model](#5-core-domain-model)
6. [Communication Protocols](#6-communication-protocols)
7. [Agent Locking & Session Isolation](#7-agent-locking--session-isolation)
8. [Pipeline Execution Engine](#8-pipeline-execution-engine)
9. [Real-Time Event Architecture](#9-real-time-event-architecture)
10. [Build Results & Analytics](#10-build-results--analytics)
11. [React WebClient Architecture](#11-react-webclient-architecture)
12. [Data Persistence](#12-data-persistence)
13. [Key Design Decisions](#13-key-design-decisions)
14. [Test Architecture](#14-test-architecture)
15. [File & Folder Index](#15-file--folder-index)
16. [Security & RBAC](#16-security--rbac)
17. [Observability & Operations](#17-observability--operations)
18. [Enhancements & Changelog](#18-enhancements--changelog)

---

## 1. System Overview

TestAgentSolution is a **distributed test execution orchestration platform** consisting of a central **Controller** that manages **WatchList** pipelines and dispatches commands to remote **Agent** nodes via gRPC. It supports two deployment topologies (WPF desktop and standalone WebApi) with a shared React browser client.

```
???????????????????????????????????????????????????????????????????
?                        Browser Clients                          ?
?         React SPA (Vite + TypeScript + Tailwind CSS)            ?
?    ????????????  ????????????  ????????????  ????????????     ?
?    ?WatchList  ?  ?Execution ?  ? Agents   ?  ? Results  ?     ?
?    ?  Tree     ?  ? Monitor  ?  ? Panel    ?  ?Dashboard ?     ?
?    ????????????  ????????????  ????????????  ????????????     ?
?         ??????????????????????????????             ?           ?
?                ?   SignalR    ?    REST API         ?           ?
??????????????????????????????????????????????????????????????????
                 ?             ?                     ?
??????????????????????????????????????????????????????????????????
?                     Controller Host                             ?
?  ???????????????????????????????????????????????????????????   ?
?  ?  TestController.Api (Shared Library)                     ?   ?
?  ?  ???????????????? ???????????????? ??????????????????  ?   ?
?  ?  ? Execution    ? ? WatchList    ? ? Health/Results  ?  ?   ?
?  ?  ? Controller   ? ? Controller   ? ? Controllers     ?  ?   ?
?  ?  ???????????????? ???????????????? ??????????????????  ?   ?
?  ?         ?                                               ?   ?
?  ?  ???????????????? ???????????????? ??????????????????  ?   ?
?  ?  ?SignalRNotifier? ?ControllerHub ? ?LockRecovery    ?  ?   ?
?  ?  ?(IRealtimeNot.)? ?  (SignalR)   ? ?  Service       ?  ?   ?
?  ?  ???????????????? ???????????????? ??????????????????  ?   ?
?  ???????????????????????????????????????????????????????????   ?
?                            ?                                    ?
?  ???????????????????????????????????????????????????????????   ?
?  ?  TestControllerGrpc.Core (Shared Domain)                 ?   ?
?  ?  ???????????????? ???????????????? ??????????????????  ?   ?
?  ?  ?AgentLock     ? ?Execution     ? ?IActionPipeline ?  ?   ?
?  ?  ?  Manager     ? ?SessionManager? ?  Executor      ?  ?   ?
?  ?  ???????????????? ???????????????? ??????????????????  ?   ?
?  ?  ???????????????? ???????????????? ??????????????????  ?   ?
?  ?  ?EventAggregator? ?AgentResolver ? ?WatchListConfig ?  ?   ?
?  ?  ?  (Pub/Sub)   ? ?              ? ?  (Models)      ?  ?   ?
?  ?  ???????????????? ???????????????? ??????????????????  ?   ?
?  ???????????????????????????????????????????????????????????   ?
?                            ?                                    ?
?                    gRPC (Protobuf)                              ?
??????????????????????????????????????????????????????????????????
                             ?
        ???????????????????????????????????????????
        ?                    ?                    ?
????????????????  ??????????????????  ??????????????????
?  TestAgent   ?  ?  TestAgent     ?  ?  TestAgent     ?
?  (jvgr1)     ?  ?  (jvkpri)     ?  ?  (agent-N)     ?
?  gRPC Server ?  ?  gRPC Server   ?  ?  gRPC Server   ?
?  :5200       ?  ?  :5200         ?  ?  :5200         ?
?  ??????????? ?  ?  ???????????  ?  ?  ???????????  ?
?  ?Command  ? ?  ?  ?Command  ?  ?  ?  ?Command  ?  ?
?  ?Executor ? ?  ?  ?Executor ?  ?  ?  ?Executor ?  ?
?  ??????????? ?  ?  ???????????  ?  ?  ???????????  ?
?  ??????????? ?  ?  ???????????  ?  ?  ???????????  ?
?  ?Execution? ?  ?  ?Execution?  ?  ?  ?Execution?  ?
?  ? Tracker ? ?  ?  ? Tracker ?  ?  ?  ? Tracker ?  ?
?  ??????????? ?  ?  ???????????  ?  ?  ???????????  ?
????????????????  ??????????????????  ??????????????????
```

---

## 2. Deployment Modes

The system supports **two mutually exclusive hosting modes** that share 100% of the API surface through `TestController.Api`:

### Mode A � WPF Desktop Controller (TestControllerGrpc)

```
??????????????????????????????????????????????????
?          WPF Application (App.xaml.cs)          ?
?                                                 ?
?  ???????????????    ?????????????????????????  ?
?  ? MainWindow  ?    ?ControllerWebApiHost   ?  ?
?  ?  (WPF UI)   ?    ? (Embedded Kestrel     ?  ?
?  ?  MVVM +     ?    ?  ASP.NET + SignalR)    ?  ?
?  ?  TreeView   ?    ?  Port 5200            ?  ?
?  ???????????????    ?????????????????????????  ?
?         ?                       ?               ?
?         ?????????????????????????               ?
?                ? Shared Singletons              ?
?  ??????????????????????????????????????        ?
?  ? DI Container (Microsoft.Hosting)   ?        ?
?  ? � ExecutionSessionManager          ?        ?
?  ? � AgentLockManager (persisted)     ?        ?
?  ? � ActionPipelineExecutor           ?        ?
?  ? � AgentGrpcDispatcher              ?        ?
?  ? � VocabularyMonitor                ?        ?
?  ? � EventAggregator                  ?        ?
?  ? � AppLogger                        ?        ?
?  ??????????????????????????????????????        ?
?                                                 ?
?  ???????????????????????????????????????       ?
?  ?ControllerGrpcServerHost             ?       ?
?  ? (gRPC server for agent callbacks)   ?       ?
?  ? Port 15100                          ?       ?
?  ???????????????????????????????????????       ?
??????????????????????????????????????????????????
```

- **Single-process**: WPF window + embedded Kestrel + gRPC server
- **Singleton bridging**: `ControllerWebApiHost` injects WPF-created singletons into the Kestrel DI container
- **Trigger parity**: Browser and WPF trigger the same `IActionPipelineExecutor`
- **Primary use**: Desktop operator on the controller machine

### Mode B � Standalone WebApi (TestController.WebApi)

```
??????????????????????????????????????????????????
?         ASP.NET WebApplication (Program.cs)     ?
?                                                 ?
?  ????????????????????????????????????????????  ?
?  ? Shared Controllers (TestController.Api)  ?  ?
?  ? + Minimal API Endpoints (standalone-only) ?  ?
?  ? + SignalR Hub (/hubs/controller)          ?  ?
?  ? + Static files (React SPA in wwwroot/)   ?  ?
?  ????????????????????????????????????????????  ?
?                                                 ?
?  ????????????????????????????????????????????  ?
?  ? Standalone Service Adapters              ?  ?
?  ? � StandalonePipelineExecutor             ?  ?
?  ?   (implements IActionPipelineExecutor)    ?  ?
?  ? � StandaloneAgentDispatcher              ?  ?
?  ?   (implements IAgentGrpcDispatcher)       ?  ?
?  ? � StandaloneVocabularyMonitor            ?  ?
?  ?   (wraps WatchListFileService)           ?  ?
?  ? � AgentGrpcClientManager                 ?  ?
?  ? � AgentRegistry (config-driven)          ?  ?
?  ? � WatchListFileService                   ?  ?
?  ????????????????????????????????????????????  ?
?                                                 ?
?  ????????????????????????????????????????????  ?
?  ? Standalone-Only Minimal API Endpoints    ?  ?
?  ? � /api/watchlist  (file I/O, XML import) ?  ?
?  ? � /api/agents     (gRPC queries)         ?  ?
?  ? � /api/execution  (trigger-all, retry)   ?  ?
?  ? � /api/results    (TRX export, reports)  ?  ?
?  ????????????????????????????????????????????  ?
??????????????????????????????????????????????????
```

- **Headless**: No WPF dependencies; runs on any OS with .NET 10
- **Self-contained**: React SPA built into `wwwroot/` at publish time
- **Adapter pattern**: Standalone services implement the same Core interfaces

---

## 3. Project Dependency Graph

```
TestControllerGrpc.Core          ? No upstream dependencies (pure domain)
       ?          ?
       ?          ?
TestController.Api               ? Shared REST + SignalR library
       ?          ?
       ?          ?
TestControllerGrpc    TestController.WebApi
(WPF Host)            (Standalone Host)
       ?                   ?
       ?                   ?
TestControllerGrpc.Tests   TestController.WebApi.Tests

TestAgentGrpc                    ? Independent (shares .proto only)
TestAgentDisplay                 ? Independent (gRPC client to agents)
TestController.WebClient         ? React SPA (npm, not .csproj reference)
```

### Dependency Matrix

| Project | References | TFM |
|---------|-----------|-----|
| `TestControllerGrpc.Core` | _(none)_ | `net10.0` |
| `TestController.Api` | Core | `net10.0` |
| `TestControllerGrpc` | Core, Api | `net10.0-windows` |
| `TestController.WebApi` | Core, Api | `net10.0` |
| `TestAgentGrpc` | _(none, shares .proto)_ | `net10.0-windows` |
| `TestAgentDisplay` | _(none, shares .proto)_ | `net10.0-windows` |
| `TestControllerGrpc.Tests` | Core, WPF, Agent | `net10.0-windows` |
| `TestController.WebApi.Tests` | Core, Api, WebApi | `net10.0` |
| `TestController.WebClient` | _(npm)_ | TypeScript/Vite |

---

## 4. Project Inventory

### TestControllerGrpc.Core � Shared Domain Layer

Zero-dependency library containing the canonical domain model, service interfaces, and infrastructure:

| Category | Files | Purpose |
|----------|-------|---------|
| **Models** | `WatchListConfig.cs` | `WatchListConfig`, `WatchItemConfig`, `EventConfig`, `ActionGroupConfig`, `ActionConfig`, `InitializeConfig`, `RefConfig`, `TemplateConfig`, `ExecutionSession`, `ActionExecutionResult`, `PipelineExecutionContext` |
| **Models** | `TrxModels.cs`, `BuildResultsConfig.cs` | TRX XML deserialization, build results thresholds |
| **Interfaces** | `IActionPipelineExecutor.cs` | Pipeline execution engine contract |
| **Interfaces** | `IAgentGrpcDispatcher.cs` | gRPC command dispatch contract (`IsAgentExecuting`, `ResetChannelAsync`, `PingAsync`, `TestConnectionAsync`, `ExecuteRemoteCommandAsync`, etc.) |
| **Interfaces** | `IVocabularyMonitor.cs` | WatchList file monitoring contract |
| **Interfaces** | `IRealtimeNotifier.cs` | SignalR broadcast contract (11 event types) |
| **Interfaces** | `IFileWatcherManager.cs` | File-trigger management contract |
| **Interfaces** | `IWatchListXmlParser.cs`, `IAppLogger.cs` | XML parsing, structured logging |
| **Services** | `ExecutionSessionManager.cs` | Session lifecycle: begin ? record ? complete ? history |
| **Services** | `AgentLockManager.cs` | Atomic agent locking with file persistence and version counter |
| **Services** | `AgentResolver.cs` | Extracts agent names from action trees with `[_Variable]` resolution |
| **Services** | `EventAggregator.cs` | Thread-safe pub/sub bus with `ThreadPool` dispatch |
| **Services** | `AgentLockEvents.cs` | `AgentLocksChangedEvent`, `AgentLockInfo` records |
| **Services** | `EventAggregatorEvents.cs` | `AgentRegisteredEvent`, `ExecutionStartedEvent`, etc. |
| **Services** | `ParameterResolver.cs` | Variable file parsing (`_Key,Value` format) |
| **Services** | `WatchListXmlParser.cs`, `WatchListXmlParserService.cs` | XML ? `WatchListConfig` serialization |
| **Analytics** | `BuildResultsAggregator.cs`, `TrxResultsParser.cs`, `CachedBuildResultsProvider.cs` | TRX aggregation with caching |
| **Analytics** | `BuildTrendAnalyzer.cs`, `ConsecutiveFailureDetector.cs`, `FlakyTestDetector.cs` | Trend, failure streak, flaky test detection |
| **Analytics** | `BuildReportHtmlGenerator.cs` | HTML email report generation |
| **Infra** | `AppLogger.cs` | File + in-memory ring-buffer logger |
| **Proto** | `test_agent.proto` | gRPC contract (2 services, 30+ message types) |

### TestController.Api � Shared API Layer

ASP.NET controllers and SignalR hub used identically by both hosts:

| File | Routes / Purpose |
|------|-----------------|
| `ExecutionController.cs` | `POST trigger/{tag}`, `GET sessions`, `GET locks`, `GET can-trigger/{tag}`, `POST cancel`, `POST force-release/{agent}`, `GET reconnect`, `GET {sessionId}/recent-logs`, `GET my-sessions` |
| `WatchListController.cs` | `GET /api/watchlist`, `GET /api/watchlist/{tag}/parameters` |
| `AgentsController.cs` | `GET /api/agents`, agent status |
| `HealthController.cs` | `GET /api/health`, `GET /api/health/diagnostics` (includes agentLocks section), `GET /api/health/logs`, `GET /api/health/log-files` |
| `ResultsController.cs` | `GET /api/results/builds`, `GET /api/results/builds/{id}`, trend data |
| `ControllerHub.cs` | SignalR hub at `/hubs/controller` � `JoinSession`, `LeaveSession`, `JoinAsUser`, `JoinGlobal` |
| `SignalRNotifier.cs` | Bridges `IEventAggregator` + C# events ? SignalR broadcasts (12 event types including `AgentLocksChanged`) |
| `LockRecoveryService.cs` | `BackgroundService` � startup lock validation against agents + periodic orphan detection |
| `ControllerApiExtensions.cs` | `AddControllerApi()` / `UseControllerApi()` � DI registration and pipeline |
| `RequestLoggingMiddleware.cs` | Correlation ID injection and request/response logging |
| `WatchListHelpers.cs` | Shared parameter file resolution |

### TestControllerGrpc � WPF Desktop Controller

| Category | Key Files | Purpose |
|----------|-----------|---------|
| **Host** | `App.xaml.cs` | `IHost` bootstrap, DI registration, singleton creation |
| **Host** | `ControllerWebApiHost.cs` | Embedded Kestrel ASP.NET server (port 5200) with singleton bridging |
| **Host** | `ControllerGrpcServerHost.cs` | gRPC server for agent registration/heartbeat (port 15100) |
| **Host** | `ControllerHostedService.cs` | Startup orchestration (load config, start watchers) |
| **gRPC** | `TestControllerGrpcService.cs` | `TestControllerService` implementation (Register, Heartbeat, PushExecutionEvents) |
| **Services** | `AgentGrpcDispatcher.cs` | Full `IAgentGrpcDispatcher` with channel pooling, health tracking, Polly retry, active execution tracking, configurable timeouts (`ControllerTimeoutOptions`), busy-recovery polling, ForceReady escalation, and auto channel reset on consecutive failures |
| **Services** | `ActionPipelineExecutor.cs` | Full `IActionPipelineExecutor` with WPF-aware logging |
| **Services** | `VocabularyMonitor.cs` | `FileSystemWatcher`-based vocabulary reload |
| **Services** | `FileWatcherManager.cs` | Per-WatchItem file trigger watchers |
| **ViewModel** | `MainViewModel.cs` + 10 partials | MVVM ViewModel: `.Execution.cs`, `.Agents.cs`, `.Log.cs`, `.WatchListCrud.cs`, `.TemplateCrud.cs`, `.File.cs`, `.Results.cs`, `.Helpers.cs`, `.InitParameters.cs`, `.XmlEditor.cs`, `.ImportExport.cs`, `.BuildBrowse.cs` |
| **ViewModel** | `TreeNodeViewModel.cs` | Recursive tree node with execution status propagation |
| **ViewModel** | `BuildResultsViewModel.cs` | TRX results dashboard VM |
| **ViewModel** | `AgentLockDisplayItem.cs` | Lock display model for WPF admin panel |
| **Models** | `PipelineSession.cs` | WPF-specific session tracking with CTS and progress |

### TestController.WebApi � Standalone Controller

| Category | Key Files | Purpose |
|----------|-----------|---------|
| **Host** | `Program.cs` | `WebApplication.CreateBuilder` bootstrap with adapter registrations |
| **Adapters** | `StandalonePipelineExecutor.cs` | Full `IActionPipelineExecutor` without WPF dependencies |
| **Adapters** | `StandaloneAgentDispatcher.cs` | `IAgentGrpcDispatcher` wrapping `AgentGrpcClientManager` |
| **Adapters** | `StandaloneVocabularyMonitor.cs` | `IVocabularyMonitor` wrapping `WatchListFileService` |
| **Services** | `AgentGrpcClientManager.cs` | gRPC channel management for standalone mode |
| **Services** | `AgentRegistry.cs` | Config-driven agent name?address registry |
| **Services** | `WatchListFileService.cs` | File-based WatchList loading/saving |
| **Services** | `AgentEventRelayService.cs` | Relays agent gRPC events to `IEventAggregator` |
| **Endpoints** | `WatchListEndpoints.cs` | Standalone-only: XML import/export, save, refresh |
| **Endpoints** | `AgentEndpoints.cs` | Standalone-only: snapshot, health, history, audit, register/unregister |
| **Endpoints** | `ExecutionEndpoints.cs` | Standalone-only: trigger-all, trigger-event, retry |
| **Endpoints** | `ResultsEndpoints.cs` | Standalone-only: TRX export, email reports |

### TestAgentGrpc � Remote Agent

| File | Purpose |
|------|---------|
| `Program.cs` | Windows Forms entry point with system tray |
| `TestAgentGrpcService.cs` | `TestAgentService` gRPC implementation (RunCommand, GetSnapshot, etc.) |
| `CommandExecutor.cs` | Process execution with streaming stdout/stderr, timeout, reboot handling |
| `ExecutionTracker.cs` | Per-execution history with session tracking |
| `StuckExecutionWatchdog.cs` | BackgroundService: polls every 60s, force-resets agent if stuck beyond `MaxExecutionTimeoutMinutes + WatchdogGraceMinutes` |
| `AgentLifecycleService.cs` | Auto-registration with controller, heartbeat loop |
| `ConnectionHealthMonitor.cs` | Connection health metrics and reconnect logic |
| `SystemMetricsCollector.cs` | CPU, memory, disk metrics via performance counters |
| `InstallProgressMonitor.cs` | ILog + MSI log + EventViewer monitoring during installs |
| `AuditLogger.cs` | Structured audit logging with file rotation |
| `EventBroadcaster.cs` | `IEventAggregator` implementation for agent-side events |
| `Clients/TestControllerClient.cs` | gRPC client for controller (Register, Heartbeat, PushExecutionEvents) |
| `UI/TrayApplicationContext.cs` | System tray icon with context menu |
| `UI/ExecutionMonitorForm.cs` | Real-time execution output form |
| `UI/ConnectionDetailForm.cs` | Connection diagnostics form |

### TestAgentDisplay � Agent Monitor Dashboard

| File | Purpose |
|------|---------|
| `App.xaml.cs` | WPF app connecting to multiple agents |
| `AgentConnectionManager.cs` | Multi-agent gRPC connection management |
| `AgentNodeViewModel.cs` | Per-agent status ViewModel |
| `AuditTimelineViewModel.cs` | Audit log timeline visualization |

---

## 5. Core Domain Model

### WatchList Configuration Hierarchy

```
WatchListConfig
??? FilePath: string
??? WatchItems: List<WatchItemConfig>
?   ??? Tag: string                    (unique identifier, e.g. "Set1")
?   ??? Path: string                   (trigger directory)
?   ??? Filter: string                 (e.g. "trigger.txt")
?   ??? IsEnabled: bool
?   ??? Events: List<EventConfig>
?       ??? Type: string               ("Renamed" | "Created")
?       ??? ExecutionType: ExecutionMode (Sequential | Parallel)
?       ??? Children: List<IActionNode>
?           ??? InitializeConfig       (loads variable file)
?           ??? ActionGroupConfig      (nested group with exec mode)
?           ?   ??? Tag, ExecutionType, FailAndContinue
?           ?   ??? Children: List<IActionNode>  (recursive)
?           ??? ActionConfig           (leaf command)
?           ?   ??? Type: ActionType   (RunCommand | RunRemoteCommand | SendMail)
?           ?   ??? AgentName: string  (literal or "[_Variable]")
?           ?   ??? Command, Parameters, Timeout
?           ?   ??? CompletionCheckCommand, EnableInstallLog
?           ??? RefConfig              (template reference)
?               ??? TemplateID: string
??? Templates: List<TemplateConfig>
    ??? ID: string
    ??? Children: List<IActionNode>
```

### Execution Session Lifecycle

```
????????????????     ??????????????     ?????????????????
?  BeginSession ???????  Running   ??????? CompleteSession?
?  (active map) ?     ?            ?     ? (move to       ?
????????????????     ? AddResult()?     ?  history)      ?
                      ? AddLogEntry?     ?????????????????
                      ??????????????             ?
                            ?                    ?
                      ??????????????     ?????????????????
                      ? CancelSess.?     ?  Final State:  ?
                      ? (CTS.Cancel?     ?  Completed |   ?
                      ?  + history)?     ?  PartialFailure?
                      ??????????????     ?  Failed        ?
                                         ?????????????????

ExecutionSession fields:
  SessionId, WatchItemTag, EventType, StartedUtc, CompletedUtc
  State: SessionState (Running|Completed|PartialFailure|Failed)
  UserId, Source ("WebClient"|"WPF"), LockedAgents[]
  Cts: CancellationTokenSource
  ActionResults: ConcurrentBag<ActionExecutionResult> (thread-safe)
  ResolvedParameters, SnapshotNodes (frozen for retry)
  LogBuffer: List<PipelineLogEntry> (capped at 500, for reconnection backfill)
```

---

## 6. Communication Protocols

### gRPC (Controller ? Agent)

```
???????????????????                    ???????????????????
?   Controller     ?                    ?     Agent        ?
?                  ?                    ?                  ?
? TestController   ???? Register ???????? AgentLifecycle   ?
?   GrpcService    ???? Heartbeat ???????   Service        ?
?   (port 15100)   ???? PushExecEvents??                  ?
?                  ?                    ?                  ?
? AgentGrpc        ???? RunCommand ?????? TestAgentGrpc    ?
?   Dispatcher     ???? GetSnapshot ????   Service        ?
?                  ???? GetState ????????   (port 5200)    ?
?                  ???? GetHistory ??????                  ?
?                  ???? GetAuditLog ????                  ?
?                  ???? TerminateExec???                  ?
?                  ???? RunCmdStreamed???                  ?
???????????????????                    ???????????????????
```

**Proto file:** `test_agent.proto` (shared across all projects)
- `TestControllerService` � hosted by Controller (4 RPCs: Register, UnRegister, UpdateClientState, Heartbeat, PushExecutionEvents)
- `TestAgentService` � hosted by each Agent (10 RPCs: GetState, RunCommand, RunCommandStreamed, GetAgentSnapshot, GetExecutionHistory, GetAuditLog, GetConnectionHealth, GetLastExitCode, GetLastError, TerminateExecution, SubscribeAgentEvents)

### REST API (Browser ? Controller)

All routes served from `TestController.Api` controllers:

| Category | Routes |
|----------|--------|
| **Execution** | `POST trigger/{tag}`, `GET status`, `GET sessions`, `GET {sessionId}`, `POST cancel`, `POST {sessionId}/cancel`, `POST retry/{sessionId}` |
| **Locking** | `GET locks`, `GET can-trigger/{tag}`, `POST force-release/{agent}`, `POST force-release-all` |
| **Session** | `GET reconnect`, `GET {sessionId}/recent-logs`, `GET my-sessions` |
| **WatchList** | `GET /api/watchlist`, `GET /api/watchlist/{tag}/parameters` |
| **Agents** | `GET /api/agents` |
| **Health** | `GET /api/health`, `GET /api/health/diagnostics`, `GET /api/health/logs`, `GET /api/health/log-files` |
| **Results** | `GET /api/results/builds`, `GET /api/results/builds/{id}` |

### SignalR (Controller ? Browser, real-time)

Single hub: `/hubs/controller`

| Event | Source | Payload |
|-------|--------|---------|
| `LogEntry` | `IActionPipelineExecutor.LogEntry` | timestamp, sessionId, severity, category, message |
| `ActionProgress` | `IActionPipelineExecutor.NodeProgress` | actionTag, agentName, status |
| `AgentOutput` | `IAgentGrpcDispatcher.OutputReceived` | agentName, line, kind (stdout/stderr) |
| `AgentStatusChanged` | `IAgentGrpcDispatcher.StatusChanged` | agentName, status |
| `AgentHeartbeats` | `IEventAggregator<AgentHeartbeatEvent>` | batched per-second (agentName, state, metrics) |
| `ExecutionStarted` | `IEventAggregator<ExecutionStartedEvent>` | sessionId, watchItemTag, eventType |
| `ExecutionCompleted` | `IEventAggregator<ExecutionCompletedEvent>` | sessionId, state, passed/failed/total |
| `AgentRegistered` | `IEventAggregator<AgentRegisteredEvent>` | agentName, address |
| `AgentUnregistered` | `IEventAggregator<AgentUnregisteredEvent>` | agentName |
| `AgentLocksChanged` | `IEventAggregator<AgentLocksChangedEvent>` | locks[], reason |
| `WatchListReloaded` | `IVocabularyMonitor.ConfigReloaded` | _(empty � client refetches)_ |
| `ExecutionCancelled` | `ExecutionController` direct | sessionId |

Client groups: `global` (auto-joined on connect), `session:{id}`, `user:{userId}`

---

## 7. Agent Locking & Session Isolation

### Lock Model

```
AgentLockManager (ConcurrentDictionary<agentName, AgentLock>)
?
??? Atomic acquisition: lock ALL required agents or NONE
?   ??? Global lock (_atomicLock) prevents TOCTOU race
?
??? Session-level granularity: locks held for ENTIRE session
?   ??? NOT released when individual agents finish early
?   ??? Prevents environment corruption in parallel pipelines
?
??? Version counter: monotonically increasing on every mutation
?   ??? Clients pass lockVersion for optimistic concurrency
?
??? File persistence: JSON snapshot ? temp file ? atomic rename
?   ??? Restored on construction (survives process restart)
?   ??? Path: C:\TestControllerService\Logs\agent-locks.json
?
??? Force-release: admin-only (WPF source check)
    ??? Single agent or all-at-once emergency reset
```

### Lock Recovery (LockRecoveryService)

```
Startup (10s delay):
  1. Read persisted locks from AgentLockManager
  2. For each lock, ping agent via TestConnectionAsync
  3. Agent BUSY ? lock validated (keep)
  4. Agent FREE ? stale lock removed (agent finished while controller was down)
  5. Agent UNREACHABLE ? keep lock conservatively
  6. Broadcast corrected state via IEventAggregator

Periodic (every 5 minutes):
  1. FindOrphanedLocks(sessionId => sessionManager.GetSession(sessionId) != null)
  2. For each orphan, confirm agent is free via gRPC
  3. Remove confirmed orphans
  4. Broadcast if changed
```

### Race Condition Prevention

```
User A and User B both see agent1 become free:

1. Pre-flight: GET /can-trigger/{tag} ? { canTrigger: true, lockVersion: 42 }
2. TriggerDialog subscribes to AgentLocksChanged (real-time invalidation)
3. Trigger: POST /trigger/{tag} body: { lockVersion: 42 }
4. Server checks: lockVersion != current? ? 409 isStaleState
5. Server atomic lock: per-tag lock ? TryLockAgents ? first wins
6. Loser gets 409 with isRaceCondition=true + retryAdvice
7. SignalR broadcasts AgentLocksChanged ? loser's dialog auto-updates
```

### Agent Resolution

`AgentResolver.ExtractAgentNames` walks the action tree recursively, resolving `[_Variable]` references from parameter files:

```
ActionConfig.AgentName = "[_AgentMachine]"
Parameters: { "_AgentMachine": "jvgr1" }
? Resolved: "jvgr1"
```

---

## 8. Pipeline Execution Engine

### Action Tree Traversal

```
ExecuteEventAsync / ExecuteEventTrackedAsync
  ??? ExecuteChildrenAsync (Sequential or Parallel)
      ??? InitializeConfig ? load parameter file into ctx.Parameters
      ??? RefConfig ? resolve TemplateID ? execute template children
      ??? ActionGroupConfig ? recurse (nested ExecuteChildrenAsync)
      ??? ActionConfig ? dispatch based on Type:
          ??? RunCommand ? ExecuteLocalCommandAsync (Process.Start on controller)
          ??? RunRemoteCommand ? ExecuteRemoteCommandAsync
              ??? gRPC RunCommandStreamed to agent
                  ??? Stream stdout/stderr lines back
                  ??? Wait for exit code
                  ??? CompletionCheckCommand poll loop (MSI wait)
                  ??? InstallProgressMonitor (optional ILog/EventViewer tracking)
```

### Execution Modes

| Mode | Behavior |
|------|----------|
| `Sequential` | Execute children one-by-one; stop on failure unless `FailAndContinue=true` |
| `Parallel` | `Task.WhenAll` on all children; thread-safe result recording via `ConcurrentBag` |

### Session Tracking (Tracked Execution)

```
ExecuteEventTrackedAsync:
  1. BeginSession (returns existing if pre-created by controller)
  2. Link CTS (caller token + session token)
  3. Clone action tree (snapshot for retry)
  4. Execute with per-action result recording:
     - Success/Failure ? session.AddResult(ActionExecutionResult)
     - Each result preserves OriginalNode for retry
  5. CompleteSession ? determine final state from pass/fail counts
```

---

## 9. Real-Time Event Architecture

### Event Flow (3-tier)

```
Layer 1: Source Events
  ???????????????????????     ????????????????????????
  ? C# events:          ?     ? IEventAggregator:     ?
  ? � Executor.LogEntry ?     ? � AgentRegistered     ?
  ? � Executor.NodeProg.?     ? � AgentUnregistered   ?
  ? � Dispatcher.Output ?     ? � AgentHeartbeat      ?
  ? � Dispatcher.Status ?     ? � ExecutionStarted    ?
  ? � VocabMon.Reloaded ?     ? � ExecutionCompleted  ?
  ???????????????????????     ? � AgentLocksChanged   ?
            ?                  ?????????????????????????
            ?                              ?
Layer 2: SignalRNotifier (Bridge)          ?
  ??????????????????????????????????????????????????????
  ? SignalRNotifier                                      ?
  ? � Subscribes to all C# events + IEventAggregator    ?
  ? � Formats payloads for JSON serialization            ?
  ? � Heartbeat throttling: batch flush per second       ?
  ? � SendSafe: swallows errors (never crash publisher)  ?
  ??????????????????????????????????????????????????????
                            ?
Layer 3: SignalR Hub        ?
  ??????????????????????????????????????????????????????
  ? ControllerHub ? Group("global").SendAsync(...)      ?
  ? Browser receives: AgentLocksChanged, LogEntry, etc. ?
  ??????????????????????????????????????????????????????
                            ?
Layer 4: React Client       ?
  ??????????????????????????????????????????????????????
  ? useSignalR hook:                                    ?
  ? � conn.on("LogEntry", ...) ? executionStore.addLog  ?
  ? � conn.on("AgentLocksChanged", ...)                 ?
  ?   ? window.dispatchEvent("agent-locks-changed")     ?
  ?   ? AgentLockPanel / TriggerDialog auto-update      ?
  ??????????????????????????????????????????????????????
```

### IEventAggregator Design

```csharp
// Thread-safe pub/sub with ThreadPool dispatch
// Prevents gRPC thread starvation with 100+ agents
EventAggregator : IEventAggregator
  Publish<T>(T evt)   ? snapshot handlers ? ThreadPool.QueueUserWorkItem each
  Subscribe<T>(handler) ? returns IDisposable unsubscription token
```

---

## 10. Build Results & Analytics

```
TRX Files (on disk)
    ?
    ?
TrxResultsParser
    ?  Parses <TestRun> XML ? TestRunResult, UnitTestResult
    ?
BuildResultsAggregator
    ?  Aggregates per-build: pass/fail/total, duration, feature breakdown
    ?
CachedBuildResultsProvider
    ?  In-memory cache with invalidation on ExecutionCompleted
    ?
    ??? BuildTrendAnalyzer        ? pass-rate trends, regression detection
    ??? ConsecutiveFailureDetector ? streak detection with alerting
    ??? FlakyTestDetector          ? intermittent failure pattern detection
    ??? BuildReportHtmlGenerator   ? email-ready HTML reports

API: ResultsController ? /api/results/builds, /api/results/builds/{id}
UI:  React BuildList, BuildDetail, TrendCharts components
WPF: BuildResultsViewModel + ResultsDashboardWindow
```

---

## 11. React WebClient Architecture

### Technology Stack

| Layer | Technology |
|-------|-----------|
| Build | Vite 6.x |
| Language | TypeScript (strict) |
| Framework | React 18 (`react-jsx` transform) |
| Styling | Tailwind CSS (dark theme) |
| State | Zustand stores (7 stores) |
| HTTP | Custom `apiFetch` wrapper (`src/lib/api.ts`) — no Axios |
| Real-time | `@microsoft/signalr` |
| Icons | `lucide-react` |

### File Structure

```
src/
??? App.tsx                          # Root: SignalR + SessionReconnector + AppShell
??? main.tsx                         # Vite entry
??? index.css                        # Tailwind imports
?
??? lib/
?   ??? api.ts                       # apiFetch<T> wrapper with base URL
?   ??? userIdentity.ts              # Persistent user ID (localStorage)
?
??? hooks/
?   ??? useSignalR.ts                # Hub connection + all 12 event handlers
?   ??? useExecution.ts              # triggerByTag, cancelAll, cancelSession, retrySession
?   ??? useWatchList.ts              # refresh, importXml, exportXml
?   ??? useAgents.ts                 # agent list, status
?   ??? useResults.ts                # build results fetching
?
??? stores/
?   ??? watchlistStore.ts            # WatchList tree state + node status updates
?   ??? executionStore.ts            # Log entries + session tracking
?   ??? agentStore.ts                # Agent status map
?   ??? connectionStore.ts           # SignalR connection state
?   ??? resultsStore.ts              # Build results cache
?   ??? lockStore.ts                 # Pipeline lock state (owner per pipeline)
?   ??? authStore.ts                 # User identity + token (Secured mode)
?   ??? systemModeStore.ts           # Default vs Secured mode (broadcast-driven)
?
??? components/
?   ??? layout/
?   ?   ??? AppShell.tsx             # 3-panel layout (sidebar + tree + content)
?   ?   ??? Sidebar.tsx              # Navigation tabs
?   ?   ??? ConnectionStatus.tsx     # SignalR connection indicator
?   ?
?   ??? watchlist/
?   ?   ??? WatchListTree.tsx        # Recursive tree with lock badges
?   ?   ??? WatchListToolbar.tsx     # Trigger, Cancel, Import, Export, Refresh
?   ?   ??? NodeProperties.tsx       # Selected node detail panel
?   ?
?   ??? execution/
?   ?   ??? TriggerDialog.tsx        # Pre-flight check + lockVersion + build picker
?   ?   ??? ExecutionMonitor.tsx     # Active session progress
?   ?   ??? LiveLogger.tsx           # Streaming log display
?   ?   ??? LogViewer.tsx            # Filterable log panel
?   ?   ??? SessionList.tsx          # Active + history sessions
?   ?   ??? SessionReconnector.tsx   # Browser-restart reconnection banner
?   ?
?   ??? agents/
?   ?   ??? AgentList.tsx            # Registered agents with health
?   ?   ??? AgentDetail.tsx          # Single agent snapshot + history
?   ?   ??? AgentLockPanel.tsx       # Real-time lock status display
?   ?
?   ??? results/
?       ??? BuildList.tsx            # Build results table
?       ??? BuildDetail.tsx          # Per-build test breakdown
?       ??? TrendCharts.tsx          # Pass-rate trend visualization
?
??? types/
    ??? api.ts                       # Shared TypeScript interfaces
```

### Real-Time Data Flow

```
useSignalR (singleton)
    ?
    ??? conn.on("LogEntry")          ? executionStore.addLog()
    ??? conn.on("ActionProgress")    ? watchlistStore.updateNodeStatus()
    ??? conn.on("ExecutionStarted")  ? watchlistStore.updateNodeStatus("Running")
    ??? conn.on("ExecutionCompleted")? watchlistStore.updateNodeStatus(mapped)
    ??? conn.on("AgentOutput")       ? executionStore.addLog()
    ??? conn.on("AgentRegistered")   ? agentStore.updateStatus("Connected")
    ??? conn.on("AgentUnregistered") ? agentStore.updateStatus("Disconnected")
    ??? conn.on("AgentStatusChanged")? agentStore.updateStatus()
    ??? conn.on("AgentHeartbeats")   ? (available for detail components)
    ??? conn.on("WatchListReloaded") ? axios.get ? watchlistStore.setConfig()
    ??? conn.on("AgentLocksChanged") ? window.dispatchEvent("agent-locks-changed")
                                        ??? AgentLockPanel listens ? setLocks()
                                        ??? TriggerDialog listens ? refreshCanTrigger()
```

---

## 12. Data Persistence

| Data | Storage | Location | Lifetime |
|------|---------|----------|----------|
| WatchList config | XML file | Configured path (e.g. `C:\WatchList\WatchList.xml`) | Permanent |
| Variable files | CSV-style text | Per-WatchItem `Initialize` path | Permanent |
| Agent locks | JSON file | `C:\TestControllerService\Logs\agent-locks.json` | Survives restart |
| Active sessions | In-memory (`ConcurrentDictionary`) | `ExecutionSessionManager._active` | Process lifetime |
| Session history | In-memory (`List`, max 50) | `ExecutionSessionManager._history` | Process lifetime |
| Pipeline logs | In-memory (per-session, max 500 entries) | `ExecutionSession._logBuffer` | Session lifetime |
| App logs | File + ring buffer (1000 entries) | `C:\TestControllerService\Logs\controller-*.log` | File: permanent, buffer: process |
| TRX results | XML files on disk | Configured `ResultsRootPath` | Permanent |
| Results cache | In-memory | `CachedBuildResultsProvider` | Invalidated on `ExecutionCompleted` |
| Agent audit logs | JSON files | Per-agent `C:\TestAgentService\Audit\` | Permanent with rotation |
| User identity | `localStorage` | Browser | Permanent per browser |
| **RBAC store** (users, sessions, pipeline assignments, audit, notification mutes) | **SQLite (EF Core)** via `TestController.Persistence` | `orchestrator.db` (path = `RBAC:DatabasePath`) | Permanent (WAL) |
| Auth session token (Secured mode) | `sessionStorage` | Browser | Per-tab |

> The RBAC database is owned by the **primary host only** (WPF Controller, or the
> WebApi in Standalone topology). `OrchestratorDbContext` is the single **Scoped**
> service; Singletons access it via `IDbContextFactory<OrchestratorDbContext>`.
> See [§16 Security & RBAC](#16-security--rbac).

---

## 13. Key Design Decisions

### 1. Session-Level Locking (not Action-Level)

Locks are held for the **entire session** duration, not released when individual agents complete their part. This prevents a second pipeline from modifying an agent's environment (e.g., installing a different build) while sibling parallel groups still expect a consistent state.

### 2. Shared API Library Pattern

`TestController.Api` is a **class library** (not a web project) containing controllers + hub. Both hosts call `AddControllerApi()` / `UseControllerApi()`. This guarantees identical API behavior regardless of deployment mode and eliminates route/behavior drift.

### 3. EventAggregator with ThreadPool Dispatch

All `IEventAggregator.Publish` calls dispatch handlers via `ThreadPool.QueueUserWorkItem`. This prevents gRPC server threads (which receive heartbeats from 100+ agents) from being blocked by slow subscribers like WPF `Dispatcher.InvokeAsync`.

### 4. BeginSession Idempotency

`ExecutionSessionManager.BeginSession` returns an **existing session** if one with the same `sessionId` is already active. This allows the controller to pre-create a session (setting `UserId`, `Source`, `LockedAgents`) before handing off to the executor, which calls `BeginSession` again without overwriting those properties.

### 5. Optimistic Concurrency via Lock Version

Every lock mutation increments a monotonic `Version` counter. The `can-trigger` API returns the current `lockVersion`, the `TriggerDialog` tracks it, and the `trigger` POST sends it back. If the version changed between check and trigger, the server rejects with 409 � no stale-state triggers possible.

### 6. Fire-and-Forget Persistence

Lock file writes are `ThreadPool.QueueUserWorkItem` with `lock (_persistLock)` and error swallowing. Persistence is best-effort � a crash mid-persist just means the lock file is one mutation behind, and `LockRecoveryService` reconciles on next startup.

### 7. DOM Custom Events for Cross-Component Lock Updates

The SignalR `AgentLocksChanged` event is converted to a DOM `CustomEvent("agent-locks-changed")` in `useSignalR`. Multiple unrelated components (`AgentLockPanel`, `TriggerDialog`, `WatchListTree`) independently listen without prop drilling or shared state coupling.

### 8. Active Execution Guard (No Concurrent gRPC on Same Channel)

During `RunCommandStreamed`, the dispatcher registers the agent in an `_activeExecutions` map. Any `TestConnectionAsync` or health poll that arrives while the agent is executing returns a synthetic snapshot (state=Running) without issuing a gRPC call. This prevents HTTP/2 GOAWAY/RST_STREAM from killing the in-flight streaming call.

### 9. Configurable Timeout Strategy (ControllerTimeoutOptions)

All hardcoded timeouts in `AgentGrpcDispatcher` are extracted to `ControllerTimeoutOptions` (bound from `appsettings.json ? Controller:Timeouts`). This includes connection timeouts, keep-alive pings, busy-recovery intervals, circuit breaker durations, retry policies, and the outer safety-net timeout. Allows per-environment tuning without code changes.

### 10. Agent Stuck-State Recovery (StuckExecutionWatchdog)

If the normal CTS timeout ? kill process ? finally block flow fails, the agent could be permanently stuck in `Running`. The `StuckExecutionWatchdog` BackgroundService polls every 60s and forcibly resets the agent state after `MaxExecutionTimeoutMinutes + WatchdogGraceMinutes`. The controller also calls `ForceReady` RPC after waiting `BusyRecoveryMaxSeconds` and the agent is still busy.

### 11. Auto Channel Reset on Consecutive Failures

When an agent accumulates `AutoResetFailureThreshold` (default: 10) consecutive failures, the dispatcher asynchronously disposes the old gRPC channel and creates a fresh one. This recovers from corrupted HTTP/2 connection state without requiring a full service restart.

---

## 14. Test Architecture

### TestControllerGrpc.Tests (279 tests, xUnit + Moq)

| Test File | Coverage Target | Tests |
|-----------|----------------|-------|
| `AgentLockManagerTests.cs` | Atomic locking, session release, force-release, version counter, orphan detection, persistence, concurrency | 40 |
| `ExecutionStreamSafeguardTests.cs` | Active execution guard, concurrent gRPC prevention, channel reset, busy-recovery, ForceReady escalation | varies |
| `AgentResolverTests.cs` | Variable resolution, tree traversal, deduplication | 14 |
| `ExecutionSessionManagerTests.cs` | BeginSession idempotency, RecordResult, CompleteSession, CancelSession, CancelAll, history | ~25 |
| `ExecutionSessionExtensionsTests.cs` | Log buffer, UserId/Source/LockedAgents fields | 8 |
| `ActionPipelineExecutorTests.cs` | Sequential/Parallel execution, FailAndContinue, Ref resolution | varies |
| `ConcurrentSessionTests.cs` | Thread-safe session operations | varies |
| `ScalabilityFixTests.cs` | High-load scenario validation | varies |
| `WatchListXmlParserTests.cs` | XML round-trip serialization | varies |
| `ParameterResolverTests.cs` | Variable file parsing | varies |
| `VocabularyMonitorTests.cs` | File change detection | varies |
| `ConsecutiveFailureDetectorTests.cs` | Streak detection | varies |
| `BuildResultsAggregatorTests.cs` | TRX aggregation | varies |
| + 7 more test files | Various services and models | � |

### TestController.WebApi.Tests (92 tests, xUnit + WebApplicationFactory)

| Test File | Coverage Target | Tests |
|-----------|----------------|-------|
| `ExecutionEndpointsTests.cs` | Trigger, cancel, retry, sessions, status | ~15 |
| `ExecutionLockEndpointsTests.cs` | Locks, can-trigger, force-release, reconnect, diagnostics, stale lockVersion | 15 |
| `AgentEndpointsTests.cs` | Agent registration, queries | varies |
| `AgentRegistryTests.cs` | Config-driven registry | varies |
| `WatchListEndpointsTests.cs` | XML CRUD | varies |
| `WatchListJsonContractTests.cs` | JSON serialization contracts | varies |
| `ResultsEndpointsTests.cs` | TRX results API | varies |
| `TestWebAppFactory.cs` | Shared test fixture with in-memory services | � |

---

## 15. File & Folder Index

```
TestAgentSolution/
??? TestControllerGrpc.Core/           # Shared domain (net10.0)
?   ??? Models/
?   ?   ??? WatchListConfig.cs         # All domain types + ExecutionSession
?   ?   ??? TrxModels.cs
?   ?   ??? BuildResultsConfig.cs
?   ??? Services/
?   ?   ??? AgentLockManager.cs        # Atomic locking with persistence
?   ?   ??? AgentLockEvents.cs         # AgentLocksChangedEvent, AgentLockInfo
?   ?   ??? AgentResolver.cs           # Agent name extraction + variable resolution
?   ?   ??? ExecutionSessionManager.cs # Session lifecycle (idempotent BeginSession)
?   ?   ??? EventAggregator.cs         # IEventAggregator + EventAggregator impl
?   ?   ??? EventAggregatorEvents.cs   # All event record types
?   ?   ??? IActionPipelineExecutor.cs # Execution engine contract
?   ?   ??? IAgentGrpcDispatcher.cs    # gRPC dispatch contract
?   ?   ??? IVocabularyMonitor.cs      # Config monitor contract
?   ?   ??? IRealtimeNotifier.cs       # SignalR broadcast contract
?   ?   ??? IFileWatcherManager.cs     # File trigger contract
?   ?   ??? IWatchListXmlParser.cs     # XML parser contract
?   ?   ??? IAppLogger.cs, AppLogger.cs
?   ?   ??? ParameterResolver.cs
?   ?   ??? WatchListXmlParser.cs, WatchListXmlParserService.cs
?   ?   ??? CachedBuildResultsProvider.cs
?   ?   ??? BuildResultsAggregator.cs, TrxResultsParser.cs
?   ?   ??? BuildTrendAnalyzer.cs, ConsecutiveFailureDetector.cs
?   ?   ??? FlakyTestDetector.cs, BuildReportHtmlGenerator.cs
?   ?   ??? ServiceTypes.cs
?   ??? Protos/
?       ??? test_agent.proto           # Shared gRPC contract
?
??? TestController.Api/                # Shared API library (net10.0)
?   ??? Controllers/
?   ?   ??? ExecutionController.cs     # Trigger, cancel, locks, sessions
?   ?   ??? WatchListController.cs
?   ?   ??? AgentsController.cs
?   ?   ??? HealthController.cs        # Health + diagnostics (incl. agentLocks)
?   ?   ??? ResultsController.cs
?   ??? Hubs/
?   ?   ??? ControllerHub.cs           # SignalR hub (/hubs/controller)
?   ??? Services/
?   ?   ??? SignalRNotifier.cs         # IRealtimeNotifier ? SignalR bridge
?   ?   ??? LockRecoveryService.cs     # BackgroundService: startup + periodic
?   ??? Middleware/
?   ?   ??? RequestLoggingMiddleware.cs
?   ??? ControllerApiExtensions.cs     # AddControllerApi / UseControllerApi
?   ??? WatchListHelpers.cs
?
??? TestControllerGrpc/                # WPF Desktop Controller (net10.0-windows)
?   ??? App.xaml.cs                    # IHost bootstrap, DI registration
?   ??? Services/
?   ?   ??? ControllerWebApiHost.cs    # Embedded Kestrel + singleton bridging
?   ?   ??? ControllerGrpcServerHost.cs# gRPC server host
?   ?   ??? ControllerHostedService.cs # Startup orchestration
?   ?   ??? TestControllerGrpcService.cs# gRPC service implementation
?   ?   ??? AgentGrpcDispatcher.cs     # IAgentGrpcDispatcher (full impl)
?   ?   ??? ActionPipelineExecutor.cs  # IActionPipelineExecutor (WPF-aware)
?   ?   ??? VocabularyMonitor.cs       # IVocabularyMonitor (FileSystemWatcher)
?   ?   ??? FileWatcherManager.cs      # IFileWatcherManager
?   ?   ??? ThemeService.cs
?   ??? ViewModels/
?   ?   ??? MainViewModel.cs + 12 partials
?   ?   ??? TreeNodeViewModel.cs
?   ?   ??? BuildResultsViewModel.cs
?   ?   ??? AgentLockDisplayItem.cs
?   ?   ??? AgentInfoViewModel.cs
?   ?   ??? PipelineSession (in Models/)
?   ?   ??? LogBufferService.cs
?   ??? Views/
?   ?   ??? MainWindow.xaml.cs
?   ?   ??? BuildResultsPanel.xaml.cs
?   ?   ??? ResultsDashboardWindow.xaml.cs
?   ?   ??? AgentMonitorWindow.xaml.cs
?   ?   ??? RawXmlEditorWindow.xaml.cs
?   ?   ??? TemplateXmlEditorWindow.xaml.cs
?   ??? Models/
?       ??? PipelineSession.cs
?       ??? NodeKinds.cs
?
??? TestController.WebApi/             # Standalone Controller (net10.0)
?   ??? Program.cs                     # WebApplication bootstrap
?   ??? Services/
?   ?   ??? StandalonePipelineExecutor.cs
?   ?   ??? StandaloneAgentDispatcher.cs
?   ?   ??? StandaloneVocabularyMonitor.cs
?   ?   ??? AgentGrpcClientManager.cs
?   ?   ??? AgentRegistry.cs
?   ?   ??? WatchListFileService.cs
?   ?   ??? AgentEventRelayService.cs
?   ??? Endpoints/
?       ??? WatchListEndpoints.cs
?       ??? AgentEndpoints.cs
?       ??? ExecutionEndpoints.cs
?       ??? ResultsEndpoints.cs
?
??? TestAgentGrpc/                     # Remote Agent (net10.0-windows)
?   ??? Program.cs
?   ??? AgentSettings.cs, AuditSettings.cs, NotificationSettings.cs
?   ??? Services/
?   ?   ??? TestAgentGrpcService.cs    # TestAgentService impl
?   ?   ??? CommandExecutor.cs         # Process exec with streaming + ForceReady()
?   ?   ??? ExecutionTracker.cs        # History + session tracking
?   ?   ??? StuckExecutionWatchdog.cs  # Watchdog: force-reset stuck agents
?   ?   ??? AgentLifecycleService.cs   # Registration + heartbeat loop
?   ?   ??? ConnectionHealthMonitor.cs
?   ?   ??? SystemMetricsCollector.cs
?   ?   ??? InstallProgressMonitor.cs
?   ?   ??? AuditLogger.cs
?   ?   ??? EventBroadcaster.cs
?   ?   ??? ReportGenerator.cs
?   ??? Clients/
?   ?   ??? TestControllerClient.cs    # gRPC client for controller
?   ??? UI/
?       ??? TrayApplicationContext.cs
?       ??? ExecutionMonitorForm.cs
?       ??? ConnectionDetailForm.cs
?
??? TestAgentDisplay/                  # Agent Monitor Dashboard (net10.0-windows)
?   ??? App.xaml.cs
?   ??? Services/AgentConnectionManager.cs
?   ??? ViewModels/
?   ?   ??? MainViewModel.cs
?   ?   ??? AgentNodeViewModel.cs
?   ?   ??? AuditTimelineViewModel.cs
?   ??? Views/MainWindow.xaml.cs
?
??? TestController.WebClient/          # React SPA (Vite + TypeScript)
?   ??? index.html, vite.config.ts, tsconfig.json
?   ??? package.json, tailwind.config.js
?   ??? src/                           # (see Section 11 for full tree)
?
??? TestControllerGrpc.Tests/          # Core + WPF unit tests (279 tests)
?   ??? Services/ (13 test files)
?   ??? Models/ (5 test files)
?
??? TestController.WebApi.Tests/       # Integration tests (92 tests)
?   ??? TestWebAppFactory.cs
?   ??? 6 test files
?
??? docs/
    ??? ARCHITECTURE.md                # This document
```

---

## 16. Security & RBAC

Two authentication layers coexist on different request paths:

1. **Multi-Identity Security** — `AddMultiIdentitySecurity()` in `TestController.Api`. HTTP auth for REST routes (NTLM/Negotiate + bearer + API key), surfaced as `SecurityPolicies.Admin` / `.User` / `.Anonymous`. Pre-existing; unchanged by RBAC.
2. **RBAC session auth** — `AddRbacFeature()`. Runs on the gRPC path via `SessionAuthInterceptor.ResolveUserAsync(authHeader, clientKind, ct)`.

### Default vs Secured mode

`RbacOptions.Enabled` (from `RBAC:Enabled`) selects the mode at runtime:

| Mode | Token required | WPF clients | Web clients |
|------|----------------|-------------|-------------|
| **Default** (`Enabled=false`) | No | Full access | Read-only |
| **Secured** (`Enabled=true`) | Yes (Bearer) | Per-role/assignment | Per-role/assignment |

In Default mode, `SessionAuthInterceptor` injects `DefaultUser.ForClient(clientKind)` (stable UUID `00000000-0000-0000-0000-000000000001` for audit correlation) and `AuthorizationService.CanAsync` short-circuits to Allow. In Secured mode, login issues a token stored in `ISessionStore` (SQLite); 60-minute inactivity + guest TTL apply.

Mode is toggled by `SystemModeController` (gated by `System_ChangeMode` once an admin exists) and broadcast over SignalR (`SystemModeChanged`) so every client updates live.

### Permission model

`Permission` enum (`TestControllerGrpc.Core/Authorization/Permission.cs`), `Resource_Action` naming:
`Pipeline_View/Trigger/Cancel/Retry/TriggerAll/CancelAll/Enable/Disable/ForceRelease`,
`User_Create/Update/Delete/Assign/Revoke`, `Report_View/Generate`, `Audit_View/Export`,
`Notification_Mute`, `System_ChangeMode`.

Enforcement is **mode-gated / fail-open in Default mode**: controllers call a helper
(`ExecutionController.IsRbacAuthorizedAsync`, `UserController.AuthorizeAsync`,
`SystemModeController.AuthorizeModeChangeAsync`) that returns Allow when RBAC is off and only
invokes `CanAsync` in Secured mode — so Default-mode behavior is byte-for-byte unchanged.

### Persistence (TestController.Persistence)

`OrchestratorDbContext` (EF Core + SQLite, WAL, app-generated lowercase GUID TEXT keys).
Entities: `User`, `Session`, `PipelineAssignment`, `AuditEntry`, `NotificationMute`,
`NotificationCooldown`. Migrations: `Initial`, `AddNotificationTables`. `DatabaseInitializerService`
applies migrations on startup; passwords hashed via `PasswordHasher` (BCrypt).

**Audit is fire-and-forget**: request paths enqueue to `QueuedAuditWriter` (bounded channel,
never awaited); `AuditDrainWorker` batches to SQLite; a retention worker purges old entries.

---

## 17. Observability & Operations

| Concern | Endpoint / mechanism |
|---------|----------------------|
| Liveness | `GET /healthz/live` (always OK) |
| Readiness | `GET /healthz/ready` (`AgentConnectivityHealthCheck` + `CertificateExpiryHealthCheck`) |
| Metrics | `GET /metrics` (OpenTelemetry → Prometheus; custom `AppMetrics` meters) |
| API spec | `GET /openapi/v1.json` (Microsoft.AspNetCore.OpenApi) |
| API reference UI | `GET /scalar` (Scalar.AspNetCore; Bearer preselected) |
| Logging | `IAppLogger` — category-based `Info`/`Warn`/`Error` (**not** Serilog); ring buffer + rolling files + optional Seq sink; `SecurityRedactor` strips secrets |
| Rate limiting | `telemetry` (reads) + `mutation` (writes) fixed-window policies; 429 + RFC 7807 |
| Onboarding | `docs/ONBOARDING.md` (first-run via `/scalar`) |

### Deploy & rollback

Per-component deploy scripts in `deploy/` are wrapped by `deploy/Invoke-Deploy.ps1`
(pre-deploy check → timestamped backup → deploy → smoke test → **auto-rollback** on failure)
and surfaced via the `workflow_dispatch` workflow `.github/workflows/deploy.yml`. See
`docs/RUNBOOK.md` → "Deploy & Rollback".

---

## 18. Enhancements & Changelog

Production-readiness work delivered in phases (P0–P5); see
`docs/Requirements/TestAgentSolution-Implementation-Plan.md`.

| Phase | Area | What landed |
|-------|------|-------------|
| **P0** | Config hygiene | Canonical 5-agent roster (`JVGR1`, `JVGR2`, `JVKPRI`, `JVKBAK`, `JVHIST`) identical across hosts; fixed `JVKBAK` typo; `Setup-AgentNode.ps1` now takes a `[pscredential]` instead of a plaintext password. |
| **P2** | Observability | Health checks, OpenTelemetry/Prometheus `/metrics`, structured `IAppLogger` with correlation IDs + Seq sink. |
| **P3** | Decoupling | `Deployment:Topology` (`Auto`/`CoLocated`/`Standalone`) + `ConfigValidator.ValidateDeploymentTopology()`; Vite dev proxy via `VITE_DEV_PROXY_TARGET`. |
| **P4** | RBAC enforcement | Added `System_ChangeMode`; mode-gated permission checks in `ExecutionController`, `UserController`, `SystemModeController` (fail-open in Default mode). |
| **P5-3** | Deploy/rollback | `deploy/Invoke-Deploy.ps1` + `.github/workflows/deploy.yml`. |
| **P5-4** | API discovery + onboarding | Scalar UI at `/scalar`; `docs/ONBOARDING.md`. |

**Known gap (P5-1):** no run queue. `ExecutionController` dispatches fire-and-forget and
returns **409 Conflict** when agents are busy — multi-team use is "collide and 409", not queued.

---

_Total: **13 projects** (incl. `TestController.Persistence`, `TestController.Dashboard`, `TestAgent.Diagnostics`, `TestController.ApiTests`, `TestController.LoadTests`) + 1 React SPA · .NET 10 + TypeScript + gRPC + SignalR + EF Core/SQLite_
