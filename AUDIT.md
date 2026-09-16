# TestAgentSolution — Independent Codebase Audit

**Auditor stance:** first-time reader, no prior assumptions, findings derived from the code itself rather than
from the documentation (which is treated as potentially stale until corroborated).

**Status:** Stage 1 of 8 complete. Stages 2–8 pending.

**Read-only:** no code was changed to produce this document.

---

## How to read this document

Every finding leads with a plain-language line — what it means for the product — before any technical detail.
Every claim cites a real file path you can open and check.

Three confidence labels are used throughout, and they mean exactly what they say:

| Label | Meaning |
|---|---|
| **Verified** | I ran a command or read the file and saw this directly. |
| **Traced** | Followed through the code by reading call sites; high confidence, but long chains may have gaps. |
| **Needs verification** | Plausible but not yet confirmed. Listed as an open question, never stated as fact. |

Where an automated exploration pass and my own direct check disagreed, I went with the direct check and said so.
One such correction is noted in §1.3.

---

# Stage 1 — What actually exists (the inventory)

## 1.0 Scale of the thing

**Plain language:** this is a mid-sized system, not a small one. Roughly 110,000 lines of C# across 16 projects,
plus a 14,000-line React app. About a quarter of the C# is tests, which is a healthier ratio than most codebases
this age.

| Measure | Value | Source |
|---|---|---|
| C# projects | 16 | `TestAgentSolution.sln` |
| Production C# | ~64,000 LOC | sum of non-test projects |
| Test C# | ~29,100 LOC | 3 test projects |
| React/TypeScript | 13,712 LOC across 112 files | `TestController.WebClient/src` |
| Deployable applications | 4 | `Publish-All.ps1` lines 83–86 |
| REST controllers | 17 | `TestController.Api/Controllers/` |
| Background/hosted services | 29 | across all projects |
| gRPC contract | 1 shared `.proto`, copied 4× | see §1.4 |

*(Verified — project graph, LOC, file counts and references extracted directly from the `.csproj` files and solution.)*

---

## 1.1 Component inventory

**Plain language:** four things actually get deployed. Everything else is either a shared library those four
depend on, a test project, or a side app that builds but never leaves your machine.

### Shipped applications

| Project | What it is | Talks to | Status |
|---|---|---|---|
| `TestControllerGrpc/` | **The all-in-one desktop controller.** WPF UI + gRPC server for agents + an embedded web API + file watchers. This is the system's centre of gravity. 29,314 LOC / 171 files — the largest project. | Agents (gRPC :5100), its own web API (:5200), SQLite, SMTP, file shares | **Alive — primary** |
| `TestAgentGrpc/` | **The agent service** that runs on each test machine. gRPC server + system tray. Launches the actual test processes. 6,156 LOC. | Controller (registers, heartbeats, streams events) | **Alive** |
| `TestController.WebApi/` | **Standalone web host** serving the React client. Proxies to the controller rather than owning data. 5,045 LOC. | Controller (`ControllerProxyUrl`), agents, React SPA | **Alive** |
| `TestAgentDisplay/` | WPF viewer for agent state. 1,225 LOC, 7 files. Published by `Publish-All.ps1` line 85. | Agents directly | **Alive — but see open question Q1** |

### Shared libraries (not deployed alone; compiled into the above)

| Project | What it is | Referenced by | Status |
|---|---|---|---|
| `TestControllerGrpc.Core/` | **The shared kernel.** 19,448 LOC / 178 files, and it references nothing — everything references it. Contains the domain models, pipeline executor base, session manager, locking, RBAC identity, ADO integration, and the maintenance subsystem. | Everything | **Alive — critical** |
| `TestController.Api/` | REST controllers + SignalR hub + security middleware, shared by **both** hosts. 8,847 LOC. | WPF host, WebApi host | **Alive — critical** |
| `TestController.Impact/` | Code-churn / regression-impact analysis and its search index. 6,466 LOC. | WPF host, WebApi host | **Alive** |
| `TestController.Persistence/` | EF Core + SQLite: RBAC users/roles, audit trail. 1,990 LOC. | Api, WebApi | **Alive** |
| `TestController.Reporting/` | xlsx report generation. Only 374 LOC / 3 files. | WPF host, Api | **Alive — small** |

### Test projects

| Project | Scale | Status |
|---|---|---|
| `TestControllerGrpc.Tests/` | 104 files, 17,181 LOC, 1,053 `[Fact]` + 46 `[Theory]` | **Alive — the main safety net** |
| `TestController.WebApi.Tests/` | 98 files, 11,065 LOC, 616 `[Fact]` + 26 `[Theory]` | **Alive** |
| `TestController.ApiTests/` | 14 files, 875 LOC, 14 `[Fact]` | **Alive — thin** |

### Apparently unused / developer-only

**Plain language:** these four build and sit in the solution, but nothing else in the code references them and
no deploy script ships them. They are not necessarily junk — two look like genuine dev tools — but they are
maintenance surface you may have forgotten you own.

| Project | Scale | Evidence | Assessment |
|---|---|---|---|
| `TestController.Dashboard/` | 261 LOC, 2 files | 10 references, **all in `.md` docs**. Not in `Publish-All.ps1`. References `TestControllerGrpc`, so it is a consumer nothing consumes. | **Likely abandoned** — confirm in Stage 4 |
| `TestAgent.Diagnostics/` | 1,470 LOC, 20 files, **zero project references** | 6 references, **all in `.md` docs** (incl. `docs/Requirements/TestAgent_Diagnostics_BuildSpec.md`). Not shipped. | **Likely abandoned or never finished** |
| `TestController.LoadTests/` | 566 LOC, Exe | 7 references, all docs. Contains `SimulatedAgentService` — a fake agent for scale testing. | **Dev tool — probably intentional** |
| `TestController.ImpactEval/` | 200 LOC, 1 file, Exe | 4 references (docs + `.sln`). | **Dev tool — probably intentional** |

*(Verified — reference counts produced by scanning every `.cs`, `.ps1`, `.bat`, `.csproj`, `.sln` and `.md` in
the repo, excluding each project's own folder.)*

### Orphaned code outside the build

**Plain language:** there is a 56 KB C# file living in your docs folder that nothing compiles. It looks like
real implementation work that was either prototyped there or moved and never cleaned up.

- `docs/AzureIntegration/Program.cs` — **56,222 bytes, 1,079 lines, not compiled by any project.**
  The only `.csproj` mention of `AzureIntegration` is inside an MSBuild *error-message string* in
  `TestController.WebApi/TestController.WebApi.csproj` line 70 — not a compile item. **Verified.**

- `TestControllerGrpc/TestControllerGrpc.csproj` lines 89–90 exclude `Controllers\**` and `Hubs\**` from
  compilation — **but neither folder exists any more.** These are vestigial leftovers from the refactor that
  moved controllers and hubs into `TestController.Api`. Harmless, but they are a small piece of misleading
  evidence for the next person reading the project file. **Verified.**

---

## 1.2 Feature inventory

**Plain language:** derived from what the code exposes — REST endpoints, UI view models, and client stores —
not from the docs. Where a feature is present in one client but not the other, I have said so, because that
asymmetry is a common source of "I thought we had that".

### Features, by where they live

| Feature | Backend | WPF UI | React UI | Apparent state |
|---|---|---|---|---|
| **Pipeline execution & triggering** | `ExecutionController.cs` (1,107 lines) | `MainViewModel.Execution.cs`, `ViewModels/Execution/` (5 files) | `components/execution/` (14 files), `executionStore.ts` | **Complete — the core product** |
| **WatchList / pipeline authoring** | `WatchListController.cs` (280) | `MainViewModel.WatchListCrud.cs`, `.TemplateCrud.cs`, `.XmlEditor.cs`, `ViewModels/WatchBuilder/` | `components/watchlist/` (5), `watchlistStore.ts` | **Complete; WPF far richer** (raw XML editors exist only in WPF) |
| **Agent fleet monitoring** | `AgentsController.cs` (89) | `ViewModels/AgentWorkspace/` (7 files) | `components/agents/` (9), `agentStore.ts` | **Complete** |
| **Results & reporting** | `ResultsController.cs` (494) | `BuildResultsViewModel` (4 partial files), `ViewModels/Results/` | `components/results/` (5), `resultsStore.ts` | **Complete; WPF richer** (TRX parsing + HTML report are WPF-only — see §1.3 fork) |
| **Regression / code-churn impact** | `ImpactController.cs` (332), `TestController.Impact/` (45 files) | `ViewModels/Regression/` (4), `Views/Regression/` (3) | `components/regression/` (3), `regressionStore.ts` | **Complete, recently active** |
| **Build report card** | `BuildReportCardController.cs` (156) | `MainViewModel.ReportCard.cs` | `components/reportcard/` (**1 file**), `reportCardStore.ts` | **Backend > frontend.** Thin React surface — see open question Q3 |
| **RBAC / auth / users** | `AuthController.cs` (130), `UserController.cs` (167), `SecurityController.cs` (168), `TestController.Persistence/` | `ViewModels/Admin/` (7), `ViewModels/Login/` (3) | `components/header/` (5), `authStore.ts` | **Complete** |
| **Audit trail** | `AuditController.cs` (185), `Persistence/Audit/` | `ViewModels/Admin/` | *(no dedicated store)* | **Backend complete; React surface unclear** — Q3 |
| **Pipeline locking** | `LocksController.cs` (73), `Core/Locking/` (11 files) | `LockBadgeViewModel.cs`, `LockConflictDialogViewModel.cs` | `lockStore.ts` | **Complete** |
| **Fleet maintenance (revert/reboot)** | `MaintenanceController.cs` (246), `Core/Maintenance/` (28 files) | `Views/AgentWorkspace/` | *(via maintenance endpoints)* | **Complete and in production** |
| **Windows Update posture** | `Core/Maintenance/WindowsUpdate*.cs`, agent-side `WindowsUpdateDetector.cs` | `FleetUpdatesVM.cs` | — | **Complete** |
| **Golden-image refresh** | `Core/Maintenance/GoldenImageRefreshOperation.cs` | — | — | **BUILT BUT NOT WIRED** — see below |
| **Consolidated run email** | `Core/Services/ConsolidatedRun*.cs`, `Services/ConsolidatedRunMailer.cs` | — | — | **Built, shipped, OFF by default** — see below |
| **System mode (Default/Secured)** | `SystemModeController.cs` (150), `Api/SystemMode/` | `MainViewModel.Security.cs` | `systemModeStore.ts` | **Complete** |
| **Notifications** | `NotificationsController.cs` (87) | `NotificationDispatcher.cs`, `FleetAlertDispatcher.cs` | *(no dedicated store)* | **Complete** |

### Features that are deliberately inert

These matter because they are code you are carrying that does nothing today. Both are intentional, not bugs —
but if you forgot they existed, this is the reminder.

| Feature | State | Evidence |
|---|---|---|
| **Golden-image refresh** (revert → patch → verify → replace baseline) | Fully implemented with a 12-phase state machine and tests, but has **no DI registration, no façade method, and no UI entry point**. Reachable only from tests. | `TestControllerGrpc.Core/Maintenance/GoldenImageRefreshOperation.cs`; documented as deliberate in `docs/Requirements/FleetUpdateOrchestration-Design.md` §9.3 |
| **Consolidated pipeline email** | Deployed and registered, but gated off: `SendConsolidatedEmail` defaults to `false` and the three companion settings default to empty. | `TestControllerGrpc.Core/Models/BuildResultsConfig.cs`; `TestControllerGrpc/Services/ConsolidatedRunMailer.cs` |
| **VM provider batching** | `ScriptBackedVirtualizationProvider` supports multi-VM batched calls, but the per-agent design decision means it is always called with single-element lists. | `TestControllerGrpc.Core/Maintenance/ScriptBackedVirtualizationProvider.cs`; design doc §9.6 |

---

## 1.3 Entry points & main flows

**Plain language:** there are five journeys worth knowing. The first one is the product; if it breaks, nothing
else matters.

### Process entry points *(Verified)*

| Application | Entry file | Lines |
|---|---|---|
| WPF controller | `TestControllerGrpc/App.xaml.cs` | 330 |
| Agent service | `TestAgentGrpc/Program.cs` | 434 |
| Standalone web host | `TestController.WebApi/Program.cs` | 481 |
| Agent display | `TestAgentDisplay/App.xaml.cs` | 24 |

**Correction worth recording:** an automated pass reported the controller's gRPC port as 5201. Reading
`TestControllerGrpc/appsettings.json` directly shows `ControllerGrpcPort: 5100` and `WebApiPort: 5200`, and the
configured agent addresses are `http://<NODE>:5200`. **The correct ports are 5100 (agents → controller) and
5200 (embedded web API).** This is exactly why every number in this document is traced to a file.

### Flow 1 — Pipeline execution *(the critical path)* — **Traced**

Two front doors converge on one engine:

```
WPF:   MainViewModel.TriggerEvent()          [TestControllerGrpc/ViewModels/MainViewModel.Execution.cs]
REST:  ExecutionController.TriggerWatchItem() [TestController.Api/Controllers/ExecutionController.cs]
             │
             ├─ permission check (Secured mode only)
             ├─ single-run gate      ILockRegistry.TryAcquire()   [Core/Locking/]
             ├─ agent reservation    AgentLockManager.TryLockAgents()
             ├─ session created      ExecutionSessionManager.BeginSession()
             ▼
       IActionPipelineExecutor.ExecuteEventTrackedAsync()
             [TestControllerGrpc.Core/Services/PipelineExecutorBase.cs]
             │  walks the pipeline tree: Initialize / Ref / Group / Action
             │  sequential or parallel (semaphore-limited)
             ▼
       ActionPipelineExecutor.ExecuteActionAsync()   [WPF host]
         or StandalonePipelineExecutor.ExecuteActionAsync()   [web host]
             │  retry loop: MaxRetries, backoff, RetryOnExitCodes
             ▼
       AgentGrpcDispatcher.ExecuteRemoteCommandAsync()  ──gRPC :5100──▶
       TestAgentGrpcService.RunCommandStreamed()      [TestAgentGrpc/Services/]
             ▼
       CommandExecutor  → spawns the real process, streams stdout/stderr back
             ▼
       ExecutionSessionManager.CompleteSession()  → results, TRX parse, email
```

**The fork that matters most:** the WPF executor supports `SendMail`, TRX parsing and HTML report generation.
The standalone web executor **does not** — `StandalonePipelineExecutor` returns success for `SendMail` without
sending anything. *(Traced; flagged for verification in Stage 3 — a silent success on a notification path is
the kind of thing that hides for months.)*

### Flow 2 — Agent registration & liveness — **Traced**

- Agent starts → `AgentLifecycleService.StartAsync()` → gRPC `Register` → controller's
  `AgentGrpcDispatcher.RegisterAgent()`.
- Agent heartbeats on a loop (default 30s); controller independently probes each agent
  (`TestController.Api/Services/AgentLivenessMonitor.cs`, default 10s, max 8 concurrent probes).
- Two independent liveness mechanisms — push and poll — is a deliberate belt-and-braces design, but it means
  two places can disagree about whether an agent is up. Noted for Stage 3.

### Flow 3 — Real-time UI updates — **Traced**

`PipelineExecutorBase` raises node-progress events, which reach the two clients by different routes:

| Client | Route | File |
|---|---|---|
| WPF | In-process event, no network | `TestControllerGrpc/Services/IExecutionFeed.cs` (`InProcessExecutionFeed`) |
| React | SignalR hub with reconnect backoff | `TestController.Api/Hubs/ControllerHub.cs`, `useSignalR()` hook → Zustand stores |

### Flow 4 — Two hosting topologies — **Traced**

Both hosts compose the same `TestController.Api` library but register different implementations:

| Concern | WPF host | Standalone web host |
|---|---|---|
| Executor | `ActionPipelineExecutor` (full) | `StandalonePipelineExecutor` (no mail/TRX) |
| Agent registry | `AgentGrpcDispatcher` (live channels) | `AgentRegistry` + `AgentGrpcClientManager` |
| RBAC data | Direct SQLite `OrchestratorDbContext` | Proxied to the controller |
| Session persistence | On disk | **In memory — lost on restart** |

### Flow 5 — Results & reporting — **Traced**

Session results → TRX parsing → HTML/xlsx report → email. Report generation and TRX parsing live in
`TestControllerGrpc.Core/Services/` and `TestController.Reporting/ChurnXlsxBuilder.cs`.

---

## 1.4 External dependencies

**Plain language:** the system reaches outside itself in seven ways. Each one is a place it can fail for reasons
that have nothing to do with your code.

| Dependency | Purpose | Wired in | Note |
|---|---|---|---|
| **gRPC** (:5100) | Controller ↔ agent commands and event streams | `Protos/test_agent.proto`, `AgentGrpcDispatcher`, `TestAgentGrpcService` | Contract file **duplicated in 4 projects** — see below |
| **SignalR** | Server → browser live updates | `TestController.Api/Hubs/ControllerHub.cs`, `TestController.WebApi/Hubs/ImpactProgressHub.cs` | |
| **SMTP** | Alerts, run reports, consolidated email | `NotificationDispatcher.cs`, `FleetAlertDispatcher.cs`, `RegressionReportMailer.cs`, `ConsolidatedRunMailer.cs`, `ActionPipelineExecutor.cs` | **5+ separate senders** — consolidation candidate for Stage 5 |
| **Process / PowerShell** | Running tests; VM operations | `TestAgentGrpc/Services/CommandExecutor.cs`, `Core/Maintenance/PowerShellScriptRunner.cs` | Highest-risk surface for injection — Stage 7 |
| **SQLite (EF Core)** | RBAC, audit, impact index | `TestController.Persistence/`, `TestController.Impact/Impact/Index/` | Two separate databases |
| **Azure DevOps** | Build/work-item data for impact analysis | `TestControllerGrpc.Core/Ado/` (43 files) | Credential via env var (PAT) |
| **VMware vCloud** | Snapshot revert / VM power | `Core/Maintenance/ScriptBackedVirtualizationProvider.cs` → `Utilites/RevertAgents/Vm-Ops.vcloud.ps1` | Script **not yet run against live vCloud** |
| **Azure OpenAI** | Churn summarisation, embeddings | `Core/Ado/Reporting/Llm/`, `Impact/Index/AzureOpenAiEmbeddingProvider.cs` | Off by default |
| **SMB file shares** | Deployment, results, large attachments | `deploy/` scripts, `LargeFilesShare` config | |

### The proto duplication *(Verified)*

`test_agent.proto` (220 lines) exists in **four** locations:

```
TestAgentDisplay/Protos/       TestAgentGrpc/Protos/
TestControllerGrpc/Protos/     TestControllerGrpc.Core/Protos/
```

**All four are currently byte-identical** (SHA-256 `1052551BC07AEA1B…`). So there is no drift *today*. But this
is the contract between the controller and every agent: if someone edits one copy and not the others, the
symptom is a runtime serialisation failure between two components that both compile perfectly. Flagged for
Stage 4.

---

## 1.5 What surprised me

Honest first-impressions worth carrying into later stages:

1. **`MainViewModel` is split across 18 partial-class files** (`MainViewModel.Execution.cs`,
   `.WatchListCrud.cs`, `.Regression.cs`, `.Security.cs`, …). Splitting a class this way keeps files readable
   but does not reduce the coupling — it is one object with an enormous surface. Prime Stage 5 material.

2. **`ExecutionController.cs` is 1,107 lines** — by far the largest controller, and it sits directly on the
   critical path.

3. **29 background services.** That is a lot of independent things running on timers. Each is a potential
   source of behaviour you cannot see in the UI.

4. **The test suite is substantial** (~1,745 test attributes across three projects). Your safety net is better
   than your sense of it probably suggests — Stage 6 will map where it actually points.

5. **`TestAgent.Diagnostics` has zero project references and 1,470 lines.** Something was started here.

---

## 1.6 Open questions from Stage 1

I will not invent answers to these. Flagged for you to confirm or for later stages to resolve.

| # | Question | Why it matters |
|---|---|---|
| **Q1** | Is `TestAgentDisplay` still used? It *is* published by `Publish-All.ps1`, but it has **zero project references** and duplicates the proto. | If unused, it is shipped dead weight that must be kept proto-compatible forever. |
| **Q2** | Are `TestController.Dashboard` and `TestAgent.Diagnostics` abandoned, or paused work you intend to resume? | Determines whether Stage 4 recommends deletion. |
| **Q3** | The report-card and audit features have substantial backends but very thin React surfaces (report card = 1 component file). Intentional, or unfinished? | Classic "half-built feature" signature; needs your intent to classify. |
| **Q4** | Is `docs/AzureIntegration/Program.cs` (1,079 uncompiled lines) a reference sample, or orphaned implementation? | Determines delete vs relocate. |
| **Q5** | Is the standalone web host actually deployed anywhere, or is the WPF host's embedded API the only one in use? | Changes how much the WPF/standalone behaviour divergence actually matters. |

---

## Stage 1 complete

**What I now know:** the component map, what ships versus what merely builds, the feature surface and where
each feature is complete or asymmetric between clients, the five main runtime flows, and every external system
this thing touches.

**What I do not yet know** (and will not guess at): whether any of it is *correct*, where it breaks, what is
genuinely dead, and where your test coverage actually points.

**Next: Stage 2 — the core business logic.** How the system fundamentally works: the domain model, the critical
path step by step, the state machines, and the handful of areas where the real complexity hides.

*Say "continue" when you want Stage 2.*
