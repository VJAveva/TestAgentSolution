# TestAgentSolution — System Design & Architecture Diagrams

> **Status:** Current state on `master` (post .NET 10 upgrade)
> **Target framework:** `net10.0` / `net10.0-windows`
> **Companion doc:** see [`ARCHITECTURE.md`](./ARCHITECTURE.md) for the full narrative; this file focuses on **renderable diagrams** (Mermaid) and a concise system-design summary.

---

## 1. Solution at a glance

The system is a **distributed test-orchestration platform** with two operator-facing surfaces backed by a shared API kernel and a fleet of remote agents.

| Project | TFM | Output | Role |
|---|---|---|---|
| `TestControllerGrpc.Core` | `net10.0` | Library | Shared kernel: proto contract, domain models, `PipelineExecutorBase`, `RemoteCommandStreamRunner`, event aggregator, build-results pipeline, interfaces (`IAgentGrpcDispatcher`, `IActionPipelineExecutor`, `IVocabularyMonitor`, `IRealtimeNotifier`, `IFileWatcherManager`, `IAppLogger`). |
| `TestController.Api` | `net10.0` | Library | Shared ASP.NET Core surface: MVC controllers (`Execution`, `WatchList`, `Agents`, `Results`, `Health`), `ControllerHub` (SignalR), `SignalRNotifier`, `LockRecoveryService`, `RequestLoggingMiddleware`, `AddControllerApi()` extension. |
| `TestControllerGrpc` | `net10.0-windows` (WPF) | WinExe | Desktop controller — WPF UI + in-process Kestrel hosting the shared API + gRPC server. Owns `MainViewModel` (12 partials), `ActionPipelineExecutor`, `AgentGrpcDispatcher`, `VocabularyMonitor`, dashboards. |
| `TestController.WebApi` | `net10.0` | Exe | Web controller — Kestrel hosting shared API + SignalR + React SPA from `wwwroot`. Standalone implementations: `StandaloneAgentDispatcher`, `StandalonePipelineExecutor`, `StandaloneVocabularyMonitor`, `AgentRegistry`, `AgentEventRelayService`. |
| `TestController.WebClient` | — | Vite/React SPA | Browser UI; built via MSBuild targets and copied into the WebApi `wwwroot`. |
| `TestAgentGrpc` | `net10.0-windows` | WinExe (tray) | Agent: gRPC server (`TestAgentService`), `CommandExecutor`, `EventBroadcaster`, `AuditLogger`, `SystemMetricsCollector`, `ConnectionHealthMonitor`, WinForms tray UI; gRPC client back to controller (`TestControllerClient`). |
| `TestAgentDisplay` | `net10.0-windows` (WPF) | WinExe | Lightweight viewer subscribing directly to agents via `SubscribeAgentEvents`. |
| `TestControllerGrpc.Tests` / `TestController.WebApi.Tests` | `net10.0` | Test | Unit/integration tests. |

### Key design pillars

1. **Single proto contract** (`TestControllerGrpc.Core/Protos/test_agent.proto`) with two services: `TestControllerService` (hosted by controllers) and `TestAgentService` (hosted by agents).
2. **Two pipeline executors, one base** — `ActionPipelineExecutor` (WPF) and `StandalonePipelineExecutor` (WebApi) both inherit `PipelineExecutorBase` so orchestration logic (sequential/parallel, ref/template expansion, group / tracked group, snapshot-isolated retry, deep-clone) is shared.
3. **Two dispatchers, one interface** — `IAgentGrpcDispatcher` is implemented by both hosts; both delegate streaming through `RemoteCommandStreamRunner`.
4. **Pluggable real-time notification** — `IRealtimeNotifier` is implemented as in-process VM push for WPF and `SignalRNotifier` over `ControllerHub` for the web.

---

## 2. Container view (C4-style)

```mermaid
flowchart LR
    subgraph "Operator Surfaces"
        WPF["TestControllerGrpc<br/>WPF Desktop<br/>net10.0-windows"]
        Browser["Browser<br/>React + Vite SPA<br/>(TestController.WebClient)"]
        Display["TestAgentDisplay<br/>WPF viewer"]
    end

    subgraph "Controller Tier"
        WebApi["TestController.WebApi<br/>ASP.NET Core, net10.0"]
        SharedApi[("TestController.Api<br/>shared MVC + SignalR")]
        Core[("TestControllerGrpc.Core<br/>proto, models,<br/>pipeline base, services")]
    end

    subgraph "Agent Tier (1..N machines)"
        Agent1["TestAgentGrpc #1"]
        Agent2["TestAgentGrpc #2"]
        AgentN["TestAgentGrpc #N"]
    end

    subgraph "Filesystem / External"
        Watch[("WatchList XML<br/>+ Templates")]
        Trx[("TRX results<br/>+ build outputs")]
        Logs[("AppLogger<br/>+ AuditLog files")]
    end

    Browser -- "HTTPS REST + SignalR" --> WebApi
    Display -- "gRPC SubscribeAgentEvents" --> Agent1
    Display -. "gRPC" .-> Agent2

    WPF -- "in-proc" --> SharedApi
    WPF -- "uses" --> Core
    WebApi --> SharedApi
    WebApi --> Core
    SharedApi --> Core

    WPF -- "gRPC: RunCommandStreamed,<br/>SubscribeAgentEvents,<br/>GetSnapshot, GetAuditLog" --> Agent1
    WPF -- "gRPC" --> Agent2
    WebApi -- "gRPC" --> Agent1
    WebApi -- "gRPC" --> AgentN

    Agent1 -- "gRPC callback:<br/>Register, Heartbeat,<br/>PushExecutionEvents" --> WPF
    Agent1 -- "gRPC callback" --> WebApi
    Agent2 -- "gRPC callback" --> WPF
    AgentN -- "gRPC callback" --> WebApi

    WPF --- Watch
    WebApi --- Watch
    Agent1 --- Trx
    Agent1 --- Logs
    WPF --- Logs
    WebApi --- Logs
```

---

## 3. gRPC contract surface

```mermaid
flowchart TB
    subgraph TCS["TestControllerService — hosted by Controller"]
        direction TB
        R1["Register(TestAgentRef)"]
        R2["UnRegister(TestAgentRef)"]
        R3["UpdateClientState(TestAgentRef)"]
        R4["PushExecutionEvents(stream ExecutionEvent)"]
        R5["Heartbeat(HeartbeatRequest)"]
    end

    subgraph TAS["TestAgentService — hosted by Agent"]
        direction TB
        A1["GetState"]
        A2["RunCommand"]
        A3["GetLastExitCode"]
        A4["GetLastError"]
        A5["TerminateExecution"]
        A6["RunCommandStreamed ? stream ExecutionEvent"]
        A7["SubscribeAgentEvents ? stream ExecutionEvent"]
        A8["GetExecutionHistory"]
        A9["GetAgentSnapshot"]
        A10["GetAuditLog"]
        A11["GetConnectionHealth"]
    end

    Ctrl[("Controller<br/>(WPF / WebApi)")] --> TAS
    Agent[("TestAgentGrpc")] --> TCS
```

`ExecutionEvent` carries: `execution_id`, `agent_name`, `event_type` (QUEUED/STARTED/STDOUT/STDERR/PROGRESS/COMPLETED/FAILED/TERMINATED/STATE_CHANGED/HEARTBEAT), output payload, exit code, and `ResourceMetrics`.

---

## 4. WPF host — TestControllerGrpc internals

```mermaid
flowchart TB
    subgraph WPFApp["TestControllerGrpc (WinExe, UseWPF)"]
        direction TB

        subgraph Views["Views (XAML)"]
            MW["MainWindow"]
            EDV["ExecutionDashboardView"]
            BRP["BuildResultsPanel"]
            RDW["ResultsDashboardWindow"]
            AMW["AgentMonitorWindow"]
            RXE["RawXmlEditorWindow"]
            TXE["TemplateXmlEditorWindow"]
        end

        subgraph VMs["ViewModels (CommunityToolkit.Mvvm)"]
            MVM["MainViewModel<br/>(12 partials: File, Execution,<br/>WatchListCrud, TemplateCrud,<br/>Agents, Results, Log,<br/>XmlEditor, ImportExport,<br/>InitParameters, BuildBrowse,<br/>Helpers)"]
            TNVM["TreeNodeViewModel"]
            EDVM["ExecutionDashboardVM<br/>+ SessionCardVM<br/>+ AgentRowVM<br/>+ ActionPillVM"]
            AIVM["AgentInfoViewModel"]
            AMVM["AgentMonitorViewModel"]
            BRVM["BuildResultsViewModel"]
            LBS["LogBufferService"]
        end

        subgraph Svcs["Services"]
            APE["ActionPipelineExecutor<br/>: PipelineExecutorBase"]
            AGD["AgentGrpcDispatcher<br/>: IAgentGrpcDispatcher"]
            FWM["FileWatcherManager<br/>: IFileWatcherManager"]
            VOC["VocabularyMonitor<br/>: IVocabularyMonitor"]
            TGS["TestControllerGrpcService<br/>(server impl)"]
            CGSH["ControllerGrpcServerHost"]
            CWAH["ControllerWebApiHost"]
            CHS["ControllerHostedService"]
            TS["ThemeService"]
        end

        MW --> MVM
        EDV --> EDVM
        BRP --> BRVM
        AMW --> AMVM
        RXE --> MVM
        TXE --> MVM
        MVM --> TNVM
        MVM --> APE
        MVM --> AGD
        MVM --> FWM
        MVM --> VOC
        MVM --> LBS
        APE -. PipelineExecutorBase .-> Core
        AGD -. RemoteCommandStreamRunner .-> Core
        TGS -. implements .-> Core
        CGSH --> TGS
        CHS --> CGSH
        CHS --> CWAH
        CWAH -. hosts .-> SharedApi
    end

    Core[("TestControllerGrpc.Core")]
    SharedApi[("TestController.Api")]
    Agents[("TestAgentGrpc fleet")]

    AGD -- "gRPC client" --> Agents
    Agents -- "gRPC callback" --> TGS
```

---

## 5. Web host — TestController.WebApi internals

```mermaid
flowchart TB
    subgraph WebApi["TestController.WebApi (net10.0)"]
        direction TB

        Pgm["Program.cs<br/>• Kestrel: 4h keepalive,<br/>  no min data rates<br/>• camelCase JSON +<br/>  string enums<br/>• AddControllerApi()<br/>• AddSignalR()<br/>• React build target"]

        subgraph Endp["Minimal-API Endpoints"]
            EE["ExecutionEndpoints"]
            WLE["WatchListEndpoints"]
            AE["AgentEndpoints"]
            RE["ResultsEndpoints"]
        end

        subgraph WSvc["Services"]
            ACM["AgentGrpcClientManager"]
            AR["AgentRegistry"]
            WLF["WatchListFileService"]
            AERS["AgentEventRelayService<br/>(IHostedService)"]
            SAD["StandaloneAgentDispatcher<br/>: IAgentGrpcDispatcher"]
            SPE["StandalonePipelineExecutor<br/>: PipelineExecutorBase"]
            SVM["StandaloneVocabularyMonitor<br/>: IVocabularyMonitor"]
        end

        SharedApi[("TestController.Api<br/>controllers + ControllerHub<br/>+ SignalRNotifier<br/>+ LockRecoveryService")]
        Core[("TestControllerGrpc.Core")]

        Pgm --> Endp
        Pgm --> WSvc
        Pgm --> SharedApi
        SharedApi --> Core
        WSvc --> Core
        SAD --> ACM
        SAD --> AR
        SPE --> SAD
        AERS --> ACM
    end

    SPA["TestController.WebClient<br/>React + Vite + Tailwind"]
    Agents[("TestAgentGrpc fleet")]

    SPA -- "REST/JSON" --> Endp
    SPA -- "REST/JSON" --> SharedApi
    SPA -- "WebSocket (SignalR)" --> SharedApi
    SAD -- "gRPC client" --> Agents
    Agents -- "gRPC callback" --> SharedApi
```

---

## 6. Agent — TestAgentGrpc internals

```mermaid
flowchart TB
    subgraph Agent["TestAgentGrpc (WinExe, UseWindowsForms)"]
        direction TB

        Pgm["Program.cs (generic host + tray)"]

        subgraph ASvc["Services"]
            TAS["TestAgentGrpcService<br/>(impl of TestAgentService)"]
            CE["CommandExecutor"]
            EB["EventBroadcaster<br/>(fan-out)"]
            ET["ExecutionTracker (history)"]
            ALS["AgentLifecycleService"]
            CHM["ConnectionHealthMonitor"]
            SMC["SystemMetricsCollector"]
            AL["AuditLogger"]
            RG["ReportGenerator"]
        end

        subgraph ACli["Clients"]
            TCC["TestControllerClient<br/>(calls TestControllerService)"]
        end

        subgraph TUI["WinForms Tray UI"]
            TAC["TrayApplicationContext"]
            EMF["ExecutionMonitorForm"]
            CDF["ConnectionDetailForm"]
        end

        Cfg["AgentSettings + AuditSettings<br/>+ NotificationSettings"]

        Pgm --> ALS
        Pgm --> TUI
        ALS --> TAS
        ALS --> TCC
        ALS --> CHM
        ALS --> SMC
        TAS --> CE
        TAS --> EB
        TAS --> ET
        TAS --> AL
        CE --> EB
        CE --> AL
        TCC --> EB
        EMF --> EB
    end

    Ctrl[("Controller (WPF / WebApi)")]

    TCC -- "Register / Heartbeat /<br/>PushExecutionEvents" --> Ctrl
    Ctrl -- "RunCommand[Streamed] /<br/>Subscribe / Snapshot /<br/>AuditLog / Health" --> TAS
```

The agent is **both** a gRPC server and a gRPC client — server for command dispatch and queries; client for registration, heartbeat, and event push.

---

## 7. Runtime flows

### 7.1 Registration + heartbeat

```mermaid
sequenceDiagram
    autonumber
    participant Agent as TestAgentGrpc
    participant Ctrl as Controller (WPF / WebApi)
    participant Reg as AgentRegistry / VM list

    Agent->>Ctrl: Register(name, endpoint, READY)
    Ctrl->>Reg: upsert
    Ctrl-->>Agent: Empty

    loop every N seconds
        Agent->>Ctrl: Heartbeat(state, metrics, ts)
        Ctrl->>Reg: refresh last-seen + metrics
        Ctrl-->>Agent: Empty
    end

    Agent->>Ctrl: UpdateClientState(RUNNING / READY)
    Note over Ctrl: SignalRNotifier or VM push<br/>to UI

    Agent->>Ctrl: UnRegister
    Ctrl->>Reg: remove
```

### 7.2 Pipeline execution (streamed)

```mermaid
sequenceDiagram
    autonumber
    participant UI as UI (WPF VM / React SPA)
    participant Exec as IActionPipelineExecutor
    participant Disp as IAgentGrpcDispatcher
    participant Run as RemoteCommandStreamRunner
    participant Agent as TestAgentGrpc (CommandExecutor)
    participant Notif as IRealtimeNotifier<br/>(VM push / SignalRNotifier)

    UI->>Exec: ExecutePipeline(rootNode, parameters)
    Note over Exec: PipelineExecutorBase walks tree:<br/>sequential / parallel / group /<br/>tracked group / ref / template
    Exec->>Disp: DispatchAsync(agent, request)
    Disp->>Run: Run(stream)
    Run->>Agent: RunCommandStreamed(req)

    loop streamed events
        Agent-->>Run: ExecutionEvent (STARTED / STDOUT / PROGRESS / ...)
        Run-->>Exec: forward
        Exec->>Notif: NotifyExecutionEvent
        Notif-->>UI: TreeNode color, dashboard pill,<br/>SignalR ReceiveEvent
    end

    Agent-->>Run: COMPLETED / FAILED
    Run-->>Exec: terminal
    Exec->>Notif: NotifySessionCompleted
    Exec-->>UI: result + artifacts
```

### 7.3 Direct event subscription (TestAgentDisplay)

```mermaid
sequenceDiagram
    autonumber
    participant Display as TestAgentDisplay<br/>(AgentConnectionManager)
    participant Agent as TestAgentGrpc<br/>(EventBroadcaster)

    Display->>Agent: SubscribeAgentEvents()
    activate Agent
    loop while connected
        Agent-->>Display: ExecutionEvent (incl. HEARTBEAT)
    end
    Display->>Agent: cancel
    deactivate Agent
```

### 7.4 Build results aggregation

```mermaid
sequenceDiagram
    autonumber
    participant UI as BuildResultsPanel /<br/>SPA Results page
    participant Ctrl as ResultsController /<br/>ResultsEndpoints
    participant Cache as CachedBuildResultsProvider
    participant Agg as BuildResultsAggregator
    participant Parser as TrxResultsParser
    participant Trend as BuildTrendAnalyzer +<br/>FlakyTestDetector +<br/>ConsecutiveFailureDetector
    participant FS as TRX files on disk

    UI->>Ctrl: GET /api/results?build=...
    Ctrl->>Cache: GetResults(buildId)
    alt cache miss
        Cache->>Agg: Aggregate(buildPath)
        Agg->>FS: enumerate *.trx
        Agg->>Parser: Parse(trx)
        Parser-->>Agg: TrxModels
        Agg->>Trend: Analyze(history)
        Trend-->>Agg: trends + flaky + consecutive
        Agg-->>Cache: aggregated payload
    end
    Cache-->>Ctrl: results
    Ctrl-->>UI: JSON (camelCase)
```

---

## 8. Deployment topology

```mermaid
flowchart LR
    subgraph "Operator Workstation A"
        WPF["TestControllerGrpc.exe<br/>+ in-proc Kestrel<br/>(REST + SignalR + gRPC server)"]
    end

    subgraph "Web Server / Container"
        WebApi["TestController.WebApi.exe<br/>(Kestrel + wwwroot SPA)"]
    end

    subgraph "Operator Browsers"
        Chrome["Browser ? SPA"]
    end

    subgraph "Agent Pool"
        A1["AGENT01<br/>TestAgentGrpc.exe :5200"]
        A2["AGENT02<br/>TestAgentGrpc.exe :5200"]
        AN["AGENT-N<br/>TestAgentGrpc.exe :5200"]
    end

    subgraph "Display Workstations"
        D1["TestAgentDisplay.exe"]
    end

    subgraph "Shared Storage"
        S1[("WatchList XML + Templates")]
        S2[("Build / TRX results")]
    end

    Chrome -- "HTTPS + WSS" --> WebApi
    WPF -- "gRPC :5200" --> A1
    WPF -- "gRPC" --> A2
    WebApi -- "gRPC" --> A1
    WebApi -- "gRPC" --> AN
    A1 -- "gRPC callback" --> WPF
    A1 -- "gRPC callback" --> WebApi
    A2 -- "gRPC callback" --> WPF
    AN -- "gRPC callback" --> WebApi
    D1 -- "gRPC SubscribeAgentEvents" --> A1

    WPF --- S1
    WebApi --- S1
    A1 --- S2
    A2 --- S2
    AN --- S2
```

Either controller can run alone; both use the same `TestController.Api` library, differing only in DI-injected implementations of `IAgentGrpcDispatcher`, `IActionPipelineExecutor`, `IVocabularyMonitor`, and `IRealtimeNotifier`.

---

## 9. Cross-cutting concerns

| Concern | Implementation |
|---|---|
| **Logging** | `IAppLogger` / `AppLogger` (file-based per host) + `RequestLoggingMiddleware`; agent-side `AuditLogger`. |
| **Real-time push** | `IRealtimeNotifier` — WPF in-process VM push vs `SignalRNotifier` over `ControllerHub`. |
| **Locking** | `AgentLockManager` + `AgentLockEvents` (Core) + `LockRecoveryService` (Api) prevent two pipelines acquiring the same agent. |
| **Event bus** | `EventAggregator` + `EventAggregatorEvents` (Core). |
| **Resilience** | `Polly.Core` on the WPF dispatcher path; widened Kestrel limits for long runs. |
| **Config** | `appsettings.json` (controllers), `AgentSettings` / `AuditSettings` / `NotificationSettings` (agent). |
| **Theming / icons** | `ThemeService`, `SvgIconControl`, `Svg.Skia`. |
| **XML editing** | `AvalonEdit` + `XmlSyntaxHighlighting.xshd`. |
| **CI** | `.github/workflows/build.yml` builds on `master`. |
| **Testing** | `TestControllerGrpc.Tests` + `TestController.WebApi.Tests`. |

---

## 10. Where to look in code

| Concept | Entry point |
|---|---|
| gRPC contract | `TestControllerGrpc.Core/Protos/test_agent.proto` |
| Pipeline base | `TestControllerGrpc.Core/Services/PipelineExecutorBase.cs` |
| WPF pipeline | `TestControllerGrpc/Services/ActionPipelineExecutor.cs` |
| WebApi pipeline | `TestController.WebApi/Services/StandalonePipelineExecutor.cs` |
| WPF dispatcher | `TestControllerGrpc/Services/AgentGrpcDispatcher.cs` |
| WebApi dispatcher | `TestController.WebApi/Services/StandaloneAgentDispatcher.cs` |
| Stream runner | `TestControllerGrpc.Core/Services/RemoteCommandStreamRunner.cs` |
| Shared controllers | `TestController.Api/Controllers/*.cs` |
| SignalR hub | `TestController.Api/Hubs/ControllerHub.cs` |
| WPF host bootstrap | `TestControllerGrpc/Services/ControllerHostedService.cs`, `ControllerWebApiHost.cs`, `ControllerGrpcServerHost.cs` |
| WebApi bootstrap | `TestController.WebApi/Program.cs` |
| Agent service impl | `TestAgentGrpc/Services/TestAgentGrpcService.cs` |
| Agent command exec | `TestAgentGrpc/Services/CommandExecutor.cs` |
| Agent ? controller client | `TestAgentGrpc/Clients/TestControllerClient.cs` |
| Display viewer | `TestAgentDisplay/Services/AgentConnectionManager.cs` |
| Build results | `TestControllerGrpc.Core/Services/BuildResultsAggregator.cs`, `CachedBuildResultsProvider.cs` |
