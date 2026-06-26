# TestAgentSolution — Architecture & System Design Document

> **Version:** 2.0  
> **Target Framework:** .NET 10 (Windows)  
> **Transport:** gRPC over HTTP/2 (Protobuf) + REST/JSON + SignalR (web tier)  
> **Repository:** `https://github.com/VJAveva/TestAgentSolution`

> **What changed in 2.0:** This document originally described only the three desktop
> components (Controller, Agent, Dashboard). It now also covers the **web tier**
> (`TestController.WebApi` standalone host + `TestController.WebClient` React SPA),
> the **RBAC/security** model, the **EF Core/SQLite persistence** layer
> (`TestController.Persistence`), **observability** (health/metrics/OpenAPI), and the
> **deployment topology** model. See [§13 Enhancements & Changelog](#13-enhancements--changelog).

---

## Table of Contents

1. [System Overview](#1-system-overview)  
2. [High-Level Architecture Diagram](#2-high-level-architecture-diagram)  
3. [Project Structure](#3-project-structure)  
4. [Component Catalog](#4-component-catalog)  
   - 4.1 [TestControllerGrpc](#41-testcontrollergrpc--the-controller)  
   - 4.2 [TestAgentGrpc](#42-testagentgrpc--the-agent)  
   - 4.3 [TestAgentDisplay](#43-testagentdisplay--the-monitoring-dashboard)  
   - 4.4 [TestController.WebApi](#44-testcontrollerwebapi--the-standalone-web-host)  
   - 4.5 [TestController.WebClient](#45-testcontrollerwebclient--the-react-spa)  
   - 4.6 [Shared Libraries](#46-shared-libraries)  
5. [gRPC Service Contracts](#5-grpc-service-contracts)  
6. [Communication Flows](#6-communication-flows)  
   - 6.1 [Agent Registration & Heartbeat](#61-agent-registration--heartbeat)  
   - 6.2 [Command Execution Pipeline](#62-command-execution-pipeline)  
   - 6.3 [File Trigger -> Pipeline Execution](#63-file-trigger--pipeline-execution)  
   - 6.4 [Real-Time Event Streaming](#64-real-time-event-streaming)  
7. [Data Models](#7-data-models)  
   - 7.1 [WatchList Configuration Model](#71-watchlist-configuration-model)  
   - 7.2 [Protobuf Message Catalog](#72-protobuf-message-catalog)  
8. [Deployment Topology](#8-deployment-topology)  
9. [Configuration Reference](#9-configuration-reference)  
10. [Technology Stack](#10-technology-stack)  
11. [Key Design Decisions](#11-key-design-decisions)  
12. [RBAC, Security & Persistence](#12-rbac-security--persistence)  
13. [Enhancements & Changelog](#13-enhancements--changelog)  

---

## 1. System Overview

TestAgentSolution is a **distributed test automation orchestration platform** that coordinates command execution across multiple remote machines (agents) from a central controller. It is designed for CI/CD build verification, automated environment setup, and multi-machine test execution scenarios (AVEVA System Platform QA automation).

**Core Capability:** When a trigger file appears in a watched directory — or a REST/UI request arrives — the controller reads a WatchList XML configuration, resolves parameters, and executes an action pipeline that can run local commands, dispatch remote commands to agents via gRPC, send notification emails, and handle machine reboots — all with real-time stdout/stderr streaming, health monitoring, and template reuse.

The platform has **two host processes** (the WPF Controller and the standalone WebApi), **one React WebClient**, a **Windows Agent** service, and a set of **shared libraries**. Two operating modes — **Default** (open) and **Secured** (RBAC enforced) — are selected at runtime.

### System Roles

| Role | Project | UI | Transport | Port |
|------|---------|-----|-----------|------|
| **Controller (all-in-one host)** | `TestControllerGrpc` | WPF Desktop | gRPC Server + embedded WebApi + SignalR | 5100 (gRPC), 5200 (WebApi) |
| **Standalone Web Host** | `TestController.WebApi` | — (serves SPA) | REST + SignalR + gRPC client | 5200 (or 80/443 via IIS) |
| **React SPA** | `TestController.WebClient` | Browser (React 18) | REST + SignalR | 3000 (dev) / served by WebApi (prod) |
| **Agent** | `TestAgentGrpc` | WinForms System Tray | gRPC Server + Client | 5200 (fallback 5201–5203) |
| **Dashboard** | `TestAgentDisplay` | WPF Desktop | gRPC Client only | N/A |

**Shared libraries:** `TestControllerGrpc.Core` (proto types, domain models, interfaces, `AppLogger`, RBAC types), `TestController.Api` (controllers, SignalR hubs, security middleware, interceptors — referenced by both hosts), `TestController.Persistence` (EF Core + SQLite: DbContext, entities, migrations, authorization service, audit drain).

---

## 2. High-Level Architecture Diagram

```
???????????????????????????????????????????????????????????????????????????????????
?                              CONTROLLER MACHINE                                 ?
?                                                                                 ?
?  ????????????????????????????????????????????????????????????????????????????   ?
?  ?                     TestControllerGrpc (WPF)                             ?   ?
?  ?                                                                          ?   ?
?  ?  ????????????????  ?????????????????????  ??????????????????????????    ?   ?
?  ?  ? MainWindow   ?  ?  MainViewModel    ?  ?  ControllerGrpcServer  ?    ?   ?
?  ?  ?  (WPF View)  ????  (MVVM)           ?  ?  Host (Kestrel:5100)  ?    ?   ?
?  ?  ????????????????  ?????????????????????  ??????????????????????????    ?   ?
?  ?                             ?                         ?                  ?   ?
?  ?           ?????????????????????????????????????????????                  ?   ?
?  ?           ?                 ?                                             ?   ?
?  ?           ?                 ?                                             ?   ?
?  ?  ?????????????????? ????????????????????  ????????????????????????????  ?   ?
?  ?  ? TestController ? ? AgentGrpc        ?  ? ActionPipelineExecutor   ?  ?   ?
?  ?  ? GrpcService    ? ? Dispatcher       ?  ?  (Tree walker)           ?  ?   ?
?  ?  ? (inbound RPCs) ? ? (outbound RPCs)  ?  ?  Sequential / Parallel   ?  ?   ?
?  ?  ?????????????????? ????????????????????  ????????????????????????????  ?   ?
?  ?                            ?                         ?                   ?   ?
?  ?  ???????????????????      ?    ???????????????????  ?                   ?   ?
?  ?  ? VocabularyMonitor?      ?    ? FileWatcher     ?  ?                   ?   ?
?  ?  ? (XML hot-reload) ?      ?    ? Manager         ????                   ?   ?
?  ?  ???????????????????      ?    ???????????????????                       ?   ?
?  ?                            ?                                              ?   ?
?  ?  ???????????????????      ?    ???????????????????                       ?   ?
?  ?  ? ParameterResolver?      ?    ? WatchListXml    ?                       ?   ?
?  ?  ? ([Token] expand) ?      ?    ? Parser          ?                       ?   ?
?  ?  ???????????????????      ?    ???????????????????                       ?   ?
?  ????????????????????????????????????????????????????????????????????????????   ?
?                               ? gRPC calls (HTTP/2)                             ?
???????????????????????????????????????????????????????????????????????????????????
                                ?
          ??????????????????????????????????????????????????
          ?                     ?                          ?
          ?                     ?                          ?
????????????????????  ????????????????????  ????????????????????????????????
?  AGENT MACHINE 1 ?  ?  AGENT MACHINE 2 ?  ?  MONITORING MACHINE          ?
?                  ?  ?                  ?  ?                              ?
? ???????????????? ?  ? ???????????????? ?  ? ?????????????????????????????
? ? TestAgentGrpc? ?  ? ? TestAgentGrpc? ?  ? ?   TestAgentDisplay       ??
? ?              ? ?  ? ?              ? ?  ? ?   (WPF Dashboard)        ??
? ? Kestrel:5200 ? ?  ? ? Kestrel:5200 ? ?  ? ?                          ??
? ? + Tray Icon  ? ?  ? ? + Tray Icon  ? ?  ? ?  AgentConnection         ??
? ?              ? ?  ? ?              ? ?  ? ?  Manager                  ??
? ? ???????????? ? ?  ? ? ???????????? ? ?  ? ?  (gRPC client to N       ??
? ? ?Command   ? ? ?  ? ? ?Command   ? ? ?  ? ?   agents)                ??
? ? ?Executor  ? ? ?  ? ? ?Executor  ? ? ?  ? ?????????????????????????????
? ? ?(Process) ? ? ?  ? ? ?(Process) ? ? ?  ?                              ?
? ? ???????????? ? ?  ? ? ???????????? ? ?  ????????????????????????????????
? ? ???????????? ? ?  ? ? ???????????? ? ?
? ? ?Event     ? ? ?  ? ? ?Event     ? ? ?
? ? ?Broadcast ? ? ?  ? ? ?Broadcast ? ? ?
? ? ???????????? ? ?  ? ? ???????????? ? ?
? ???????????????? ?  ? ???????????????? ?
????????????????????  ????????????????????
```

---

## 3. Project Structure

```
TestAgentSolution/
??? TestControllerGrpc/             # Central orchestration controller (WPF)
?   ??? App.xaml.cs                 # Host builder, DI, hosted services
?   ??? Protos/test_agent.proto     # Shared proto (GrpcServices="Both")
?   ??? Models/
?   ?   ??? WatchListConfig.cs      # Complete XML config domain model
?   ??? Services/
?   ?   ??? ControllerGrpcServerHost.cs   # Kestrel gRPC server on port 5100
?   ?   ??? ControllerHostedService.cs    # Startup: load config, register agents
?   ?   ??? TestControllerGrpcService.cs  # Inbound RPCs from agents
?   ?   ??? AgentGrpcDispatcher.cs        # Outbound RPCs to agents
?   ?   ??? ActionPipelineExecutor.cs     # Action tree execution engine
?   ?   ??? FileWatcherManager.cs         # FileSystemWatcher per WatchItem
?   ?   ??? VocabularyMonitor.cs          # XML hot-reload with debounce
?   ?   ??? WatchListXmlParser.cs         # XML ? WatchListConfig serialization
?   ?   ??? ParameterResolver.cs          # [Token] placeholder resolution
?   ?   ??? ThemeService.cs               # Runtime theme switching
?   ??? ViewModels/
?   ?   ??? MainViewModel.cs              # Central VM: agents, tree, log, pipeline
?   ?   ??? TreeNodeViewModel.cs          # Hierarchical WatchList tree node
?   ?   ??? AgentInfoViewModel.cs         # Per-agent status/metrics display
?   ?   ??? LogEntryViewModel.cs          # Execution log entry
?   ?   ??? ...converters, editors...
?   ??? Views/
?   ?   ??? MainWindow.xaml(.cs)          # Primary UI
?   ?   ??? RawXmlEditorWindow.xaml(.cs)  # WatchItem XML editor (AvalonEdit)
?   ?   ??? TemplateXmlEditorWindow.xaml  # Template XML editor
?   ??? Helpers/
?       ??? SvgIconHelper.cs              # SVG ? rasterized icon cache
?       ??? SvgIconControl.cs             # WPF control for SVG icons
?       ??? SvgIconExtension.cs           # XAML markup extension
?
??? TestAgentGrpc/                  # Remote execution agent (WinForms tray)
?   ??? Program.cs                  # Host builder, Kestrel, WinForms STA loop
?   ??? AgentSettings.cs            # Strongly-typed configuration
?   ??? Protos/test_agent.proto     # Shared proto (GrpcServices="Both")
?   ??? Services/
?   ?   ??? TestAgentGrpcService.cs # Inbound RPCs from controller/dashboard
?   ?   ??? CommandExecutor.cs      # Process execution with streaming I/O
?   ?   ??? EventBroadcaster.cs     # Pub-sub hub (Channel<T>-based)
?   ?   ??? ExecutionTracker.cs     # Execution history ring buffer
?   ?   ??? AgentLifecycleService.cs# Registration, heartbeat, event push
?   ?   ??? SystemMetricsCollector.cs# CPU, memory, disk metrics
?   ??? Clients/
?   ?   ??? TestControllerClient.cs # Outbound gRPC to controller
?   ??? UI/
?       ??? TrayApplicationContext.cs    # System tray with context menu
?       ??? ExecutionMonitorForm.cs      # Real-time execution output window
?
??? TestAgentDisplay/               # Read-only monitoring dashboard (WPF)
?   ??? App.xaml.cs                 # Simple DI container
?   ??? Protos/test_agent.proto     # Shared proto (GrpcServices="Client")
?   ??? Services/
?   ?   ??? AgentConnectionManager.cs# Multi-agent gRPC connection pool
?   ??? ViewModels/
?   ?   ??? MainViewModel.cs        # Dashboard VM
?   ?   ??? AgentNodeViewModel.cs   # Per-agent live status
?   ??? Views/
?       ??? MainWindow.xaml(.cs)    # Dashboard UI
?
??? TestAgentSolution.sln
```

---

## 4. Component Catalog

### 4.1 TestControllerGrpc � The Controller

The controller is a **WPF desktop application** that embeds an ASP.NET Core Kestrel gRPC server. It is the central orchestration point.

#### Hosted Services (IHostedService)

| Service | Purpose |
|---------|---------|
| `ControllerHostedService` | Loads vocabulary XML, registers agents from `appsettings.json`, wires file watchers on startup |
| `ControllerGrpcServerHost` | Runs a Kestrel gRPC server on port 5100 for inbound agent RPCs |

#### Core Services (Singletons)

| Service | Responsibility |
|---------|----------------|
| `AgentGrpcDispatcher` | Manages gRPC channels to agents. Dispatches `RunCommandStreamed`, `GetState`, `GetAgentSnapshot`. Handles reboot wait-for-ready loops. Also executes local commands on the controller machine. Tracks active executions to prevent concurrent gRPC calls on the same channel. Supports configurable timeouts via `ControllerTimeoutOptions`, automatic busy-recovery polling, `ForceReady` escalation, and auto channel reset after consecutive failures. |
| `ActionPipelineExecutor` | Depth-first tree walker for the WatchList action tree. Handles Sequential/Parallel execution, `FailAndContinue`, `Initialize`, `Ref ? Template` expansion, `RunCommand`, `RunRemoteCommand`, `SendMail`. |
| `FileWatcherManager` | Creates `FileSystemWatcher` instances per `WatchItem`. Maps file events (Renamed/Created/Changed) to pipeline executions. Supports hot-reload. |
| `VocabularyMonitor` | Monitors the WatchList XML file for external changes. Debounces (500ms) and fires `ConfigReloaded` for live reload. |
| `WatchListXmlParser` | Serializes/deserializes `WatchListConfig ? XML`. Handles the full action node hierarchy. |
| `ParameterResolver` | Resolves `[Token]` placeholders in action fields. Loads parameters from trigger files and `Initialize` parameter files. |
| `ThemeService` | Runtime theme switching (Dark/Light/High Contrast) via ResourceDictionary replacement. |

#### gRPC Service (Server-Side)

| Service | Inbound From | RPCs |
|---------|-------------|------|
| `TestControllerGrpcService` | Agents | `Register`, `UnRegister`, `UpdateClientState`, `Heartbeat`, `PushExecutionEvents` (client-streaming) |

#### MVVM Layer

| Component | Role |
|-----------|------|
| `MainViewModel` | Manages WatchList tree, agent registry, execution log, pipeline execution, periodic health checks, theme selection. 1800+ lines � the application's nerve center. |
| `TreeNodeViewModel` | Recursive tree node representing WatchList/WatchItem/Event/ActionGroup/Action/Initialize/Ref/Template hierarchy. |
| `AgentInfoViewModel` | Per-agent display model: name, address, state, connection status, CPU/memory/disk metrics. |
| `LogEntryViewModel` | Timestamped log entry with severity (Info/Success/Warning/Error). |

---

### 4.2 TestAgentGrpc � The Agent

The agent is a **WinForms system tray application** with an embedded ASP.NET Core Kestrel gRPC server. It runs on each remote machine.

#### Startup Architecture

```
Program.cs
  ?? WebApplication.CreateBuilder()
  ?    ?? Kestrel: ListenAnyIP(5200, HTTP/2)
  ?    ?? AddGrpc()
  ?    ?? AddHostedService<AgentLifecycleService>()
  ?? app.MapGrpcService<TestAgentGrpcService>()
  ?? app.RunAsync()                              ? background thread
  ?? Application.Run(TrayApplicationContext)     ? STA main thread
```

#### Core Services

| Service | Responsibility |
|---------|----------------|
| `CommandExecutor` | Executes commands via `Process.Start`. Streams stdout/stderr line-by-line through `Channel<T>`. Supports cancellation, timeout, credential-based execution (`runas`), and script interpreter resolution (`.bat`?`cmd.exe`, `.ps1`?`powershell.exe`). Uses `SemaphoreSlim(1,1)` for single-execution guard. Exposes `ForceReady()` for watchdog-initiated state recovery. |
| `StuckExecutionWatchdog` | `BackgroundService` that polls every 60s. If agent is stuck in `Running` beyond `MaxExecutionTimeoutMinutes + WatchdogGraceMinutes`, forcibly resets state via `CommandExecutor.ForceReady()`. Nuclear fallback for when normal CTS timeout fails. |
| `EventBroadcaster` | Multi-subscriber pub-sub hub using `Channel<T>` with `BoundedChannelOptions.DropOldest`. Non-blocking publish. Each subscriber (gRPC stream, tray UI, controller push) gets an independent channel. |
| `ExecutionTracker` | Ring buffer of `ExecutionRecord` with full stdout/stderr capture, timing, exit codes, and outcome tracking. Queryable via `GetHistory`. |
| `SystemMetricsCollector` | Collects CPU usage (delta-based), memory (GC info + working set), disk free space, active process count. |
| `AgentLifecycleService` | `IHostedService` that manages: registration with retry, periodic heartbeat (15s default), automatic re-registration on controller restart, and persistent event push stream. |
| `TestControllerClient` | gRPC client for communicating with the controller. `Register`, `UnRegister`, `UpdateState`, `SendHeartbeat`, `PushEventsAsync` (client-streaming with auto-reconnect). |

#### gRPC Service (Server-Side)

| Service | Inbound From | RPCs |
|---------|-------------|------|
| `TestAgentGrpcService` | Controller, Dashboard | `GetState`, `RunCommand`, `RunCommandStreamed` (server-streaming), `GetLastExitCode`, `GetLastError`, `TerminateExecution`, `SubscribeAgentEvents` (server-streaming), `GetExecutionHistory`, `GetAgentSnapshot` |

#### UI

| Component | Role |
|-----------|------|
| `TrayApplicationContext` | System tray icon with context menu showing state, activity, metrics. Double-click opens monitor. |
| `ExecutionMonitorForm` | Real-time stdout/stderr viewer subscribing to `EventBroadcaster`. Shows execution history and resource metrics. |

---

### 4.3 TestAgentDisplay � The Monitoring Dashboard

A **read-only WPF dashboard** for observing multiple agents in real time. Does not issue commands.

| Component | Responsibility |
|-----------|----------------|
| `AgentConnectionManager` | Manages gRPC channels to N agents. Opens `SubscribeAgentEvents` streaming RPCs. Provides `GetSnapshotAsync` and `GetHistoryAsync`. |
| `MainViewModel` | Lists agents with live status. Add/remove agent connections. |
| `AgentNodeViewModel` | Per-agent: connection state, current activity, stdout/stderr feed, metrics, execution history. |

---

### 4.4 TestController.WebApi — The Standalone Web Host

An **ASP.NET Core host** (`Microsoft.NET.Sdk.Web`, net10.0) that serves the React SPA plus the REST + SignalR surface for browser clients. It does not run a WPF UI. It is the secondary host; in production it is typically fronted by IIS.

#### What it hosts

| Surface | Endpoint(s) | Notes |
|---------|-------------|-------|
| REST API | `/api/...` | Same controllers as the WPF host, registered via `AddControllerApi()` from `TestController.Api`. |
| SignalR hub | `/hubs/controller` (`ControllerHub`) | Real-time execution/agent/lock/mode events to the SPA. |
| SPA fallback | `/` | Serves the built React client (`wwwroot/index.html`). |
| Health | `/healthz/live`, `/healthz/ready` | Liveness + readiness (agent connectivity + cert expiry). |
| Metrics | `/metrics` | Prometheus scrape (OpenTelemetry exporter). |
| API docs | `/openapi/v1.json`, `/scalar` | OpenAPI document + Scalar API reference UI. |
| Client logs | `POST /api/clientlogs` | WebClient log ingestion (anonymous). |

#### Key services

| Service | Responsibility |
|---------|----------------|
| `AgentGrpcClientManager` | gRPC client channels to the agent fleet (the WebApi has no embedded gRPC server). |
| `StandalonePipelineExecutor` | Local execution path used in **Standalone** topology (subclass of `PipelineExecutorBase`); no `SendMail`. |
| `ConfigValidator` | Fail-fast startup validation, including `ValidateDeploymentTopology()` (CoLocated vs Standalone vs Auto). |
| `DeploymentOptions` | Binds `Deployment:Topology`; `ResolveEffective()` reconciles it with `ControllerProxyUrl`. |
| `AgentConnectivityHealthCheck`, `CertificateExpiryHealthCheck` | Readiness probes. |
| `AppMetrics` | Custom OpenTelemetry meters (execution, agents, locks, gRPC latency). |
| Relay/sync hosted services | `ControllerEventRelayService`, `SystemModeSyncService`, `LockExpirySweeper`, `AgentLivenessMonitor`, `LockRecoveryService`. |

#### Deployment topology

The host runs in exactly one of two topologies, chosen explicitly via `Deployment:Topology` and cross-checked against `ControllerProxyUrl` by `ConfigValidator`:

- **CoLocated** — `ControllerProxyUrl` set; the WebApi is a thin gateway and **proxies** execution to the WPF Controller (the single authority + SQLite owner).
- **Standalone** — no proxy; the WebApi executes the full pipeline locally and owns its own lock authority (no WPF anywhere).
- **Auto** — inferred from `ControllerProxyUrl` presence (backward-compatible default).

---

### 4.5 TestController.WebClient — The React SPA

A **React 18 + TypeScript + Vite + Tailwind** single-page app. State is managed with **Zustand** (one store per domain), real-time updates arrive over **SignalR**, and all HTTP goes through a single `apiFetch` wrapper.

| Concern | Implementation |
|---------|----------------|
| HTTP client | `apiFetch<T>()` / `apiGet` / `apiPost` / `apiPut` / `apiDelete` in `src/lib/api.ts`. Adds `X-Request-Id` correlation, `X-Source: WebClient`, and `Authorization: Bearer <token>` (from sessionStorage). No raw `fetch`/axios. |
| State (Zustand) | `useAuthStore`, `useExecutionStore`, `useAgentStore`, `useLockStore`, `useWatchListStore`, `useResultsStore`, `useSystemModeStore`. No Redux, no Context for global state. |
| Real-time | `useSignalR()` hook connects to `/hubs/controller`; handles `SystemModeChanged`, `PipelineLockAcquired/Released`, `ExecutionStarted/Completed/Cancelled`, `AgentStatusChanged`, log/output batches. |
| Dev/prod wiring | Vite dev server on `:3000` proxies `/api` and `/hubs` to `VITE_DEV_PROXY_TARGET` (default `http://127.0.0.1:5200`). `VITE_API_BASE_URL` (empty = same-origin) controls the prod base. |

---

### 4.6 Shared Libraries

| Library | Responsibility |
|---------|----------------|
| `TestControllerGrpc.Core` | Proto-generated gRPC types (`test_agent.proto`, `auth.proto`), domain models (`WatchListConfig`, `ActionConfig`, `ExecutionSession`), service interfaces (`IActionPipelineExecutor`, `IAppLogger`, `IAuthorizationService`, `IAuditWriter`, `ILockRegistry`), `WatchListXmlParserService`, `AppLogger`, and RBAC types (`Permission`, `IUserContext`, `DefaultUser`, `Role`, `ClientKind`). |
| `TestController.Api` | Controllers (`ExecutionController`, `LocksController`, `SystemModeController`, `UserController`, `AuditController`, plus watchlist/agents/results endpoints), `ControllerHub`, middleware, `AddMultiIdentitySecurity()` + `AddControllerApi()` + `AddRbacFeature()` extensions, `SessionAuthInterceptor`, `AuditLoggingInterceptor`. Referenced by **both** hosts. |
| `TestController.Persistence` | EF Core + SQLite: `OrchestratorDbContext` (Scoped), entities (`User`, `Session`, `PipelineAssignment`, `AuditEntry`, `NotificationMute`, `NotificationCooldown`), migrations, `AuthorizationService`, `SessionStore`, `QueuedAuditWriter` + `AuditDrainWorker`, `PasswordHasher`. Referenced by both hosts; only the **primary host** opens the DB. |

---

## 5. gRPC Service Contracts

Defined in `Protos/test_agent.proto` (shared across all three projects):

### TestControllerService (hosted on Controller, port 5100)

```
??????????????????????????????????????????????????????????????????????????????????
? RPC                        ? Request                ? Response                 ?
??????????????????????????????????????????????????????????????????????????????????
? Register                   ? TestAgentRef           ? google.protobuf.Empty    ?
? UnRegister                 ? TestAgentRef           ? google.protobuf.Empty    ?
? UpdateClientState          ? TestAgentRef           ? google.protobuf.Empty    ?
? Heartbeat                  ? HeartbeatRequest       ? google.protobuf.Empty    ?
? PushExecutionEvents        ? stream ExecutionEvent  ? google.protobuf.Empty    ?
??????????????????????????????????????????????????????????????????????????????????
```

### TestAgentService (hosted on each Agent, port 5200)

```
??????????????????????????????????????????????????????????????????????????????????
? RPC                        ? Request                ? Response                 ?
??????????????????????????????????????????????????????????????????????????????????
? GetState                   ? google.protobuf.Empty  ? StateReply               ?
? RunCommand                 ? RunCommandRequest      ? RunCommandReply          ?
? RunCommandStreamed          ? RunCommandRequest      ? stream ExecutionEvent    ?
? GetLastExitCode            ? google.protobuf.Empty  ? ExitCodeReply            ?
? GetLastError               ? google.protobuf.Empty  ? ErrorReply               ?
? TerminateExecution         ? google.protobuf.Empty  ? google.protobuf.Empty    ?
? SubscribeAgentEvents       ? google.protobuf.Empty  ? stream ExecutionEvent    ?
? GetExecutionHistory        ? ExecutionHistoryRequest? ExecutionHistoryReply    ?
? GetAgentSnapshot           ? google.protobuf.Empty  ? AgentSnapshot            ?
??????????????????????????????????????????????????????????????????????????????????
```

---

## 6. Communication Flows

### 6.1 Agent Registration & Heartbeat

```
  Agent                                    Controller
    ?                                          ?
    ????? Register(name, Ready, endpoint) ??????  Agent startup
    ?????? Empty ???????????????????????????????
    ?                                          ?  Controller adds to AgentGrpcDispatcher
    ?                                          ?  + fires AgentRegistered event ? UI
    ?                                          ?
    ?   ???? every 15 seconds ????             ?
    ?   ?                        ?             ?
    ????? Heartbeat(name,state, ???????????????  Controller updates UI metrics
    ?   ?  metrics, timestamp)   ?             ?
    ?   ??????????????????????????             ?
    ?                                          ?
    ?   ???? on 3+ heartbeat failures ???     ?
    ?   ?  Re-Register with controller   ?     ?  Auto-recovery after controller restart
    ?   ??????????????????????????????????     ?
    ?                                          ?
    ????? PushExecutionEvents (stream) ?????????  Long-lived client-streaming RPC
    ?     event, event, event...               ?  (auto-reconnects on failure)
    ?                                          ?
    ????? UnRegister(name, Inactive) ???????????  Agent shutdown
    ?                                          ?
```

### 6.2 Command Execution Pipeline

```
  Controller                                   Agent
    ?                                            ?
    ??? RunCommandStreamed(cmd, args, ?????????  ?
    ?   isReboot, timeout, creds)                ?
    ?                                            ?  CommandExecutor.RunCommandStreamed()
    ?                                            ?    ?? Acquire SemaphoreSlim(1,1)
    ?                                            ?    ?? SetState(Running)
    ?                                            ?    ?? Process.Start(psi)
    ?                                            ?    ?
    ?  ????? stream ExecutionEvent ???????????????  EventQueued
    ?  ????? stream ExecutionEvent ???????????????  EventStarted (PID)
    ?  ????? stream ExecutionEvent ???????????????  EventStdoutLine (per line)
    ?  ????? stream ExecutionEvent ???????????????  EventStderrLine (per line)
    ?  ????? stream ExecutionEvent ???????????????  EventCompleted (exit code)
    ?                                            ?    ?? SetState(Ready)
    ?                                            ?    ?? Release semaphore
    ?                                            ?
    ?  On timeout/cancel:                        ?
    ?  ?? gRPC call disposed ?????????????????????  CancellationToken fires
    ?                                            ?    ?? Process.Kill(entireProcessTree)
    ?                                            ?    ?? SetState(Ready)
    ?                                            ?    ?? Release semaphore
```

### 6.3 File Trigger ? Pipeline Execution

```
  File System          FileWatcherManager       ActionPipelineExecutor     AgentGrpcDispatcher
      ?                       ?                         ?                         ?
      ??? file renamed ????????                         ?                         ?
      ?                       ??? OnTriggered() ?????????                         ?
      ?                       ?   (match Event.Type)    ?                         ?
      ?                       ?                         ??? ExecuteEventAsync() ???
      ?                       ?                         ?   Depth-first walk:     ?
      ?                       ?                         ?                         ?
      ?                       ?                         ? ? Initialize            ?
      ?                       ?                         ? ? ? LoadParameterFile   ?
      ?                       ?                         ? ?                       ?
      ?                       ?                         ? ? ActionGroup (Seq)     ?
      ?                       ?                         ? ? ? Action (RunRemote)?????? ExecuteRemoteCommandAsync()
      ?                       ?                         ? ? ? Action (RunLocal) ?????? ExecuteLocalCommandAsync()
      ?                       ?                         ? ? ? Ref ? Template ?????    (inline expansion)
      ?                       ?                         ? ? ? Action (SendMail) ?????? SMTP send
      ?                       ?                         ? ?                       ?
      ?                       ?                         ? ? ActionGroup (Parallel)?
      ?                       ?                         ? ? ? Task.WhenAll(...)   ?
```

### 6.4 Real-Time Event Streaming

```
                    EventBroadcaster (pub-sub hub)
                              ?
          ?????????????????????????????????????????
          ?                   ?                   ?
          ?                   ?                   ?
   ChannelReader<T>    ChannelReader<T>    ChannelReader<T>
          ?                   ?                   ?
   TrayUI / Monitor    gRPC stream to      gRPC stream to
   Form (WinForms)     Controller           Dashboard
                       (PushExecEvents)     (SubscribeAgentEvents)
```

Each subscriber gets an independent `BoundedChannel<ExecutionEvent>` with `DropOldest` policy so slow consumers never block the execution pipeline.

---

## 7. Data Models

### 7.1 WatchList Configuration Model

```
WatchListConfig
??? FilePath: string
??? WatchItems: List<WatchItemConfig>
?   ??? WatchItemConfig
?       ??? Tag, Path, Filter, IsEnabled
?       ??? Events: List<EventConfig>
?           ??? EventConfig
?               ??? Type: "Renamed" | "Created" | "Changed"
?               ??? ExecutionType: Sequential | Parallel
?               ??? Children: List<IActionNode>
?                   ??? ActionGroupConfig
?                   ?   ??? Tag, ExecutionType, FailAndContinue
?                   ?   ??? Children: List<IActionNode>  (recursive)
?                   ??? ActionConfig
?                   ?   ??? Type: RunCommand | RunRemoteCommand | SendMail
?                   ?   ??? AgentName, Command, Parameters, Timeout
?                   ?   ??? IsReboot, FailAndContinue, Order
?                   ?   ??? UserName, Password
?                   ?   ??? From, To, Title, Body, Attachment, Embed
?                   ??? InitializeConfig
?                   ?   ??? Tag, ParameterFile
?                   ??? RefConfig
?                       ??? TemplateID
??? Templates: List<TemplateConfig>
    ??? TemplateConfig
        ??? ID: string
        ??? Children: List<IActionNode>
```

### 7.2 Protobuf Message Catalog

| Message | Key Fields | Used In |
|---------|-----------|---------|
| `TestAgentRef` | name, state, endpoint | Register, UnRegister, UpdateClientState |
| `RunCommandRequest` | command, arguments, is_reboot, timeout_seconds, user_name, password | RunCommand, RunCommandStreamed |
| `ExecutionEvent` | execution_id, agent_name, event_type, output_line, exit_code, error_message, metrics | All streaming RPCs |
| `HeartbeatRequest` | agent_name, state, metrics, timestamp | Heartbeat |
| `AgentSnapshot` | agent_name, state, current_activity, metrics, executions_completed/failed | GetAgentSnapshot |
| `ResourceMetrics` | cpu_usage_pct, memory_used_mb, memory_total_mb, disk_free_gb, active_process_count | Heartbeat, Snapshot, Events |
| `ExecutionRecord` | execution_id, command, started, finished, exit_code, outcome, stdout_lines, stderr_lines | GetExecutionHistory |

| Enum | Values |
|------|--------|
| `AgentState` | Ready (0), Running (1), Inactive (2) |
| `ExecutionEventType` | Unknown, Queued, Started, StdoutLine, StderrLine, Progress, Completed, Failed, Terminated, StateChanged, Heartbeat |
| `ExecutionOutcome` | Unknown, Success, Failed, Terminated, TimedOut |

---

## 8. Deployment Topology

```
????????????????????????????????????????????????????????????????????????
?                        TYPICAL DEPLOYMENT                            ?
?                                                                      ?
?   Controller Machine (1)          Agent Machines (N)                 ?
?   ???????????????????????        ???????????????????????            ?
?   ? TestControllerGrpc  ?        ? TestAgentGrpc       ?            ?
?   ? - WPF Desktop App   ?????????? - Tray App          ?            ?
?   ? - Kestrel :5100     ?  gRPC  ? - Kestrel :5200     ?  � N       ?
?   ? - WatchList XML     ? HTTP/2 ? - Process execution  ?            ?
?   ? - File watchers     ?        ? - System metrics     ?            ?
?   ???????????????????????        ???????????????????????            ?
?            ?                                                         ?
?            ? gRPC (read-only)                                        ?
?   ???????????????????????                                            ?
?   ? TestAgentDisplay    ?        Optional monitoring                  ?
?   ? - WPF Dashboard     ?        (can connect to agents directly)    ?
?   ???????????????????????                                            ?
????????????????????????????????????????????????????????????????????????

Network Requirements:
  � Controller ? Agent: TCP port 5200 (gRPC/HTTP2) � command dispatch
  � Agent ? Controller: TCP port 5100 (gRPC/HTTP2) � registration, heartbeat
  � Dashboard ? Agent:  TCP port 5200 (gRPC/HTTP2) � monitoring subscription
```

### Web Tier & Topology Modes

The browser experience is served by `TestController.WebApi` (fronted by IIS in
production) plus the `TestController.WebClient` SPA. The WebApi runs in one of two
**topologies**, selected by `Deployment:Topology` and validated at startup by
`ConfigValidator.ValidateDeploymentTopology()`:

| Topology | `ControllerProxyUrl` | Who executes runs | WPF Controller required |
|----------|----------------------|-------------------|-------------------------|
| `CoLocated` | **set** (e.g. `http://localhost:5200`) | WPF Controller (WebApi proxies over gRPC) | **Yes** |
| `Standalone` | **empty/removed** | WebApi (`StandalonePipelineExecutor`) | **No** |
| `Auto` | either | inferred from URL presence | depends |

```
Browser ──HTTP/SignalR──> IIS ──> TestController.WebApi
                                       │
              CoLocated: proxy ────────┤────── Standalone: local executor
                                       │              │
                                  WPF Controller      └── gRPC ──> Agent fleet
                                  (gRPC 5100, owns SQLite)
                                       │
                                   gRPC ──> Agent fleet (5 nodes)
```

The canonical agent roster is **5 nodes**: `JVGR1`, `JVGR2`, `JVKPRI`, `JVKBAK`,
`JVHIST` (static config; no dynamic discovery). See `docs/RUNBOOK.md` →
"Agent Roster (Canonical Source of Truth)".

---

## 9. Configuration Reference

### Controller (`TestControllerGrpc/appsettings.json`)

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `VocabularyFile` | string | `""` | Path to the WatchList XML file loaded on startup |
| `ControllerGrpcPort` | int | `5100` | Kestrel listen port for inbound agent gRPC |
| `Agents[].Name` | string | � | Pre-registered agent friendly name |
| `Agents[].Address` | string | � | Pre-registered agent gRPC address (e.g. `http://host:5200`) |

### Agent (`TestAgentGrpc/appsettings.json`)

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `AgentSettings.AgentName` | string | `Environment.MachineName` | Friendly name for WatchList XML references |
| `AgentSettings.GrpcPort` | int | `5200` | Kestrel listen port for inbound RPCs |
| `AgentSettings.ControllerAddress` | string | `http://localhost:5100` | Controller gRPC address for registration/heartbeat |
| `AgentSettings.AgentEndpoint` | string? | `null` | Override callback address (auto-resolved if null) |
| `AgentSettings.RegistrationRetryCount` | int | `3` | Max registration attempts on startup |
| `AgentSettings.RegistrationRetryIntervalSeconds` | int | `30` | Delay between registration retries |
| `AgentSettings.HeartbeatIntervalSeconds` | int | `15` | Heartbeat push interval |
| `AgentSettings.MaxExecutionHistoryCount` | int | `200` | Ring buffer size for execution history |
| `AgentSettings.MaxOutputLinesPerExecution` | int | `5000` | Max stdout/stderr lines captured per execution |
| `AgentSettings.CollectSystemMetrics` | bool | `true` | Enable CPU/memory/disk collection |
| `AgentSettings.MaxExecutionTimeoutMinutes` | int | `120` | Hard safety-net timeout for executions with no explicit timeout |
| `AgentSettings.WatchdogGraceMinutes` | int | `5` | Grace period beyond MaxExecutionTimeoutMinutes before watchdog force-resets |

---

## 10. Technology Stack

| Layer | Technology | Version |
|-------|-----------|---------|
| **Runtime** | .NET | 10.0 |
| **Transport** | gRPC (Protobuf) | Grpc.AspNetCore 2.62, Google.Protobuf 3.26 |
| **Server** | ASP.NET Core Kestrel | Built-in (HTTP/2) |
| **Controller UI** | WPF | .NET 10 Windows |
| **Agent UI** | WinForms | .NET 10 Windows |
| **Dashboard UI** | WPF | .NET 10 Windows |
| **Web host** | ASP.NET Core (`TestController.WebApi`) | .NET 10 |
| **Web UI** | React + TypeScript + Vite + Tailwind | React 18, TS 5.x, Vite 6, Tailwind 3 |
| **Web state** | Zustand | 5.x |
| **Real-time (web)** | SignalR | @microsoft/signalr 8.x |
| **Persistence** | EF Core + SQLite | EF Core 10 (`TestController.Persistence`) |
| **Password hashing** | BCrypt.Net-Next | via `PasswordHasher` |
| **API docs** | Microsoft.AspNetCore.OpenApi + Scalar.AspNetCore | OpenApi 10.0.9, Scalar 2.16.6 |
| **Metrics** | OpenTelemetry + Prometheus exporter | OTel 1.16, Prometheus exporter (beta) |
| **MVVM** | CommunityToolkit.Mvvm | 8.2.2 |
| **DI/Hosting** | Microsoft.Extensions.Hosting | 10.0.3 |
| **XML Editor** | AvalonEdit | 6.3.0.90 |
| **SVG Rendering** | Svg.Skia + SkiaSharp | 2.0.0.4 |
| **Serialization** | System.Xml.Linq (XElement) | Built-in |

---

## 11. Key Design Decisions

### 1. Dual gRPC Servers � Bidirectional Communication
Both the controller and agent host gRPC servers. The controller calls the agent to dispatch commands; the agent calls the controller to register, heartbeat, and push events. This avoids NAT/firewall issues with server-initiated connections and enables either side to restart independently.

### 2. Channel\<T\>-Based Event Broadcasting
The `EventBroadcaster` uses `System.Threading.Channels` with `BoundedChannelOptions.DropOldest` instead of traditional events. This provides backpressure-aware multi-subscriber fan-out where slow consumers (e.g., a network stream) never block the command execution pipeline.

### 3. Single-Execution Semaphore with Cancellation
`CommandExecutor` uses `SemaphoreSlim(1,1)` to ensure only one command runs at a time per agent. The cancellation token propagates from the gRPC call context through `WaitForExitAsync(ct)`, ensuring the process is killed and the semaphore released when the controller disconnects or times out.

### 4. WatchList XML as the Configuration Contract
The WatchList XML format is the domain language � it defines file triggers, action trees with sequential/parallel groups, template reuse via `<Ref>`, parameter files via `<Initialize>`, and `[Token]` substitution. The controller's entire UI is a visual editor for this XML.

### 5. Automatic Agent Recovery
Agents track consecutive heartbeat failures and automatically re-register when the controller restarts. The heartbeat loop doubles as a connectivity watchdog. Agents also stay functional in standalone mode (accepting commands from any controller that connects) even if initial registration fails.

### 6. WPF + Kestrel Co-hosting
The controller runs a Kestrel gRPC server on a background thread alongside the WPF UI thread. The `ControllerGrpcServerHost` is an `IHostedService` that starts `WebApplication.RunAsync()` inside `Task.Run()`, allowing both event loops to coexist.

### 7. WinForms + Kestrel Co-hosting
The agent uses the same pattern: `app.RunAsync()` on a background thread, `Application.Run(trayContext)` on the STA main thread. A `Mutex` prevents multiple agent instances.

### 8. Hot-Reload Vocabulary
`VocabularyMonitor` watches the XML file with debounced reload (500ms). On change, it re-parses the config, tears down all `FileSystemWatcher` instances, and recreates them � enabling live configuration updates without restarting the controller.

### 9. Active Execution Guard & Stuck-State Recovery
During active `RunCommandStreamed` calls, the dispatcher tracks the agent in `_activeExecutions` and returns synthetic snapshots for health polls � preventing concurrent HTTP/2 calls from triggering GOAWAY/RST_STREAM. On the agent side, `StuckExecutionWatchdog` forcibly resets state if the normal timeout/cancellation flow fails. The controller escalates to `ForceReady` RPC after configurable busy-recovery polling, and auto-resets gRPC channels after consecutive failure thresholds.

---

## 12. RBAC, Security & Persistence

### Two coexisting auth layers

1. **Multi-Identity Security** (`AddMultiIdentitySecurity()`, pre-existing) — HTTP auth on REST routes: NTLM/Negotiate + bearer + API key, exposed as `SecurityPolicies.Admin` / `.User` / `.Anonymous`. Unchanged by RBAC.
2. **RBAC session auth** (`AddRbacFeature()`, added) — runs on the gRPC path via `SessionAuthInterceptor`. The two layers coexist on different request paths.

### Default vs Secured mode

`RbacOptions.Enabled` (bound from `RBAC:Enabled`) selects the mode at runtime:

- **Default mode** (`Enabled = false`) — no token required. WPF clients get full access; web clients get read-only. `SessionAuthInterceptor` injects `DefaultUser.ForClient(clientKind)` (stable UUID for audit correlation). `AuthorizationService.CanAsync` short-circuits to Allow.
- **Secured mode** (`Enabled = true`) — bearer token required (gRPC + REST). Login issues a token stored in `ISessionStore` (SQLite); per-user roles + `PipelineAssignment`s are enforced.

Mode is flipped by `SystemModeController` (gated by the `System_ChangeMode` permission once an admin exists) and broadcast to all clients over SignalR.

### Permission model

`Permission` (`TestControllerGrpc.Core/Authorization/Permission.cs`), named `Resource_Action`:
`Pipeline_View/Trigger/Cancel/Retry/TriggerAll/CancelAll/Enable/Disable/ForceRelease`,
`User_Create/Update/Delete/Assign/Revoke`, `Report_View/Generate`, `Audit_View/Export`,
`Notification_Mute`, `System_ChangeMode`.

Enforcement is **mode-gated and fail-open in Default mode** — controllers call a helper
(e.g. `ExecutionController.IsRbacAuthorizedAsync`) that returns Allow when RBAC is off and
only invokes `CanAsync` in Secured mode, so Default-mode behavior is unchanged.

### Persistence

`OrchestratorDbContext` (EF Core, **Scoped** — the only Scoped service; Singletons reach it
via `IDbContextFactory<OrchestratorDbContext>`). SQLite with WAL, app-generated lowercase
GUID TEXT keys. Entities: `User`, `Session`, `PipelineAssignment`, `AuditEntry`,
`NotificationMute`, `NotificationCooldown`. Migrations: `Initial`, `AddNotificationTables`.
Only the primary host opens the DB; `DatabaseInitializerService` applies migrations on startup.

**Audit is fire-and-forget**: request paths enqueue to `QueuedAuditWriter` (bounded channel,
never awaited); `AuditDrainWorker` batches writes to SQLite; a retention worker purges old entries.

### Observability

`/healthz/live` + `/healthz/ready` (agent connectivity + cert expiry), OpenTelemetry metrics
via `AppMetrics` exported at `/metrics` (Prometheus), OpenAPI at `/openapi/v1.json` with the
Scalar UI at `/scalar`, and the custom category-based `IAppLogger` (`Info`/`Warn`/`Error` —
**not** Serilog) with an optional Seq sink. REST writes are rate-limited (`telemetry` /
`mutation` policies).

---

## 13. Enhancements & Changelog

Production-readiness work delivered in phases (P0–P5). See
`docs/Requirements/TestAgentSolution-Implementation-Plan.md` for the full plan.

| Phase | Area | What landed |
|-------|------|-------------|
| **P0** | Config hygiene | Canonical 5-agent roster (`JVGR1`, `JVGR2`, `JVKPRI`, `JVKBAK`, `JVHIST`) made identical across `TestControllerGrpc`, `TestController.WebApi`, and `TestAgentDisplay` appsettings; fixed `JVKBAK` typo; replaced plaintext `ServicePassword` in `Setup-AgentNode.ps1` with a `[pscredential]` flow. |
| **P2** | Observability | Health checks, OpenTelemetry/Prometheus `/metrics`, structured `IAppLogger` with correlation IDs + Seq sink. |
| **P3** | Decoupling | `Deployment:Topology` (`Auto`/`CoLocated`/`Standalone`) + `ConfigValidator.ValidateDeploymentTopology()`; Vite dev proxy via `VITE_DEV_PROXY_TARGET`; `.env.example` / `.env.development`. |
| **P4** | RBAC enforcement | Added `System_ChangeMode`; mode-gated permission checks in `ExecutionController` (`Pipeline_Trigger`/`CancelAll`/`ForceRelease`), `UserController` (`User_*`), and `SystemModeController` — all fail-open in Default mode. |
| **P5-3** | Deploy/rollback | `deploy/Invoke-Deploy.ps1` (backup → deploy → smoke test → auto-rollback) and `.github/workflows/deploy.yml` (`workflow_dispatch` deploy/rollback). |
| **P5-4** | API discovery + onboarding | Scalar UI at `/scalar` over the existing OpenAPI document; `docs/ONBOARDING.md` first-run guide. |

**Known gap (P5-1, not yet implemented):** there is no run queue. `ExecutionController`
dispatches with fire-and-forget `Task.Run(...)` and returns **409 Conflict** when agents are
busy — multi-team use is currently "collide and 409", not queued.
