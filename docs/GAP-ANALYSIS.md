# TestAgentSolution — Production Gap Analysis

> **Audit date:** 2026-05-22 | **Branch:** `ExeDashboadImpl`
> **Context:** Distributed test execution platform for AVEVA System Platform QA (4-10 agent VMs in vCloud)
> **Stack:** .NET 10 + WPF + ASP.NET Core + gRPC + React + SignalR

---

## Priority Summary

### P0 — Block Release (2 issues)

| # | Issue | Category |
|---|-------|----------|
| 1 | **No authentication** — anyone with network access can execute commands on agents | Security |
| 2 | **`ExecutionLogViewerDialog` deadlock** — `.GetAwaiter().GetResult()` on UI thread | Concurrency |

### P1 — Fix Soon (12 issues)

| # | Issue | Category |
|---|-------|----------|
| 3 | Authorization is spoofable (`X-Source` header) | Security |
| 4 | No HubFilter on `ControllerHub` | Error Handling |
| 5 | WebApi dispatcher has no Polly resilience | Error Handling |
| 6 | Parameter file writes not atomic | Error Handling |
| 7 | `ForceRelease` bypasses `_atomicLock` | Concurrency |
| 8 | CommandExecutor TOCTOU race | Concurrency |
| 9 | `ResetChannelAsync` TOCTOU (partial fix in place) | Concurrency |
| 10 | Correlation ID not propagated to gRPC | Observability |
| 11 | CommandPolicy in AuditOnly mode (not enforced) | Security |
| 12 | `LiveLogger` no virtualization — browser freeze on long sessions | UX |
| 13 | Config typo `jvbak` in Production appsettings | Deployment |
| 14 | No top-level README + ops runbook | Documentation |

### P2 — Nice to Have (18 issues)

Various improvements to timer disposal, pagination, gRPC TLS, metrics, tests, and documentation (detailed below).

---

## 1. Error Handling & Resilience

### What Exists Today

- Polly resilience pipeline (retry 3x + circuit breaker + 24h timeout) on WPF dispatcher
- `RequestLoggingMiddleware` catching all unhandled API exceptions -> structured JSON errors
- `SendSafeAsync` wrapper on all SignalR broadcasts (5s timeout + catch)
- Atomic session persistence via temp-file + rename pattern
- `LockRecoveryService` for orphaned lock cleanup after controller crash
- `WaitForAgentRecoveryAsync` for UNAVAILABLE recovery (60s polling)

### Gaps

| Gap | Impact | Fix Location | Priority |
|-----|--------|-------------|----------|
| **No try/catch or HubFilter in `ControllerHub`** — `RequestFleetSnapshot()` can throw unhandled | SignalR connection drops for all clients in group | `TestController.Api/Hubs/ControllerHub.cs` — add global `IHubFilter` | **P1** |
| **WebApi dispatcher has NO Polly resilience** — no retry/circuit-breaker | Standalone dashboard deployments have zero fault tolerance | `TestController.WebApi/Services/StandaloneAgentDispatcher.cs` — add `ResiliencePipeline` | **P1** |
| **Parameter file writes NOT atomic** — direct `File.WriteAllLines` | Power loss mid-write -> corrupt pipeline parameters -> cascading failures | `TestControllerGrpc.Core/Services/ParameterResolver.cs:103` and `TestController.Api/Controllers/ExecutionController.cs:405` — use temp+rename | **P1** |
| **Session persistence is async** — `ThreadPool.QueueUserWorkItem` with 5s throttle | Hard crash loses up to 5s of action results | `TestControllerGrpc.Core/Services/ExecutionSessionManager.cs` — flush synchronously on session complete | **P2** |

---

## 2. Concurrency & Thread Safety

### What Exists Today

- `ConcurrentDictionary` for agents, health states, active executions
- `SemaphoreSlim` (`_executionLock`) for single-command serialization on agent
- `_atomicLock` in `AgentLockManager` for lock acquisition/release
- Volatile state + event-driven state machine in `CommandExecutor`

### Gaps

| Gap | Impact | Fix Location | Priority |
|-----|--------|-------------|----------|
| **`ExecutionLogViewerDialog` uses `.GetAwaiter().GetResult()` on UI thread** — textbook WPF deadlock | UI freeze (hangs indefinitely) | `TestControllerGrpc/Views/Dialogs/ExecutionLogViewerDialog.xaml.cs:37` — make method async, use `await` | **P0** |
| **`ForceRelease` / `ForceReleaseAll` bypass `_atomicLock`** | Logical race: lock state corruption, stale locks possible | `TestControllerGrpc.Core/Services/AgentLockManager.cs:154-176` — acquire `_atomicLock` | **P1** |
| **CommandExecutor TOCTOU** — state check vs semaphore not atomic | Two commands accepted simultaneously; one silently queues | `TestAgentGrpc/Services/CommandExecutor.cs:80-95` — move state check inside semaphore `TryWait` | **P1** |
| **`ResetChannelAsync` TOCTOU** — disposes endpoint after `IsAgentExecuting` check | `ObjectDisposedException` (already hit in production) | `TestControllerGrpc/Services/AgentGrpcDispatcher.cs:1055-1093` — use `TryRemove` + null-check pattern, or add reader lock | **P1** |
| **`Dispatcher.Invoke` (synchronous) from background threads** — 4 call sites | Potential UI freeze if dispatcher is blocked | `MainViewModel.Execution.cs:60,137,331` and `MonitorVM.cs:443,465` — change to `InvokeAsync` | **P2** |
| **WebApi `ReadToEndAsync().GetAwaiter().GetResult()`** on request thread | Thread pool starvation under load | `TestController.WebApi/Program.cs:239` — use `await` | **P2** |

---

## 3. Logging & Observability

### What Exists Today

- Custom `AppLogger` with ring buffer (5000 entries), daily file rotation, component-specific files, errors-only file
- Correlation IDs on HTTP requests (`X-Request-Id` header + middleware)
- Agent-side `AuditLogger` (JSON lines, 50MB rotation, 30-day retention)
- Admin action audit trail (force-release, cancel, terminate — all logged with actor/source)
- Rich health/diagnostics endpoints (`/api/health`, `/api/health/diagnostics`, `/api/health/logs`)

### Gaps

| Gap | Impact | Fix Location | Priority |
|-----|--------|-------------|----------|
| **Correlation ID NOT propagated across gRPC boundary** — agent logs have no controller correlation | Cannot trace an action end-to-end across controller->agent | `RemoteCommandStreamRunner.cs` — add `correlationId` to gRPC metadata headers; `TestAgentGrpcService.cs` — read and log it | **P1** |
| **No file size cap on AppLogger daily files** — only date-based rotation | A busy day could fill disk (single-day file grows unbounded) | `TestControllerGrpc.Core/Services/AppLogger.cs` — add max file size with numbered rollover | **P2** |
| **No standard `IHealthCheck` integration** — custom endpoints only | Cannot use Azure/k8s health probes, no readiness vs liveness distinction | `TestController.Api/Program.cs` — `AddHealthChecks()` + `MapHealthChecks()` | **P2** |
| **No metrics/trace export** (OpenTelemetry, Prometheus) | No dashboards for pipeline throughput, agent utilization, or failure rates | New: add `OpenTelemetry.Extensions.Hosting` to `TestController.Api` | **P2** |

---

## 4. Security

### What Exists Today

- `SecurityRedactor` — redacts passwords, tokens, connection strings in all logs/output (4 regex patterns, well-tested)
- `CommandPolicyEvaluator` — detects shell injection, allowlisted commands/paths
- Path traversal validation on log file endpoints (`..` blocked)
- Soft ownership checks on session cancel (owner-only from WebClient)
- Startup warning when running plaintext gRPC in non-Development

### Gaps

| Gap | Impact | Fix Location | Priority |
|-----|--------|-------------|----------|
| **NO authentication framework** — no JWT, Windows Auth, or cookies configured | Anyone with network access can execute arbitrary commands on all agent VMs | `TestController.Api/Program.cs` — `AddAuthentication().AddNegotiate()` (Windows/NTLM for internal AVEVA network) | **P0** |
| **Authorization is trivially spoofable** — `X-Source: WPF` header grants admin rights | Malicious internal user can force-release locks, cancel any session | `TestController.Api/Controllers/ExecutionController.cs` — replace header checks with `[Authorize(Policy = "Admin")]` | **P0** |
| **CommandPolicy default is `"AuditOnly"`** — injection detected but commands still execute | Crafted command parameter could achieve RCE on agent VMs | `TestAgentGrpc/appsettings.json` — change `"Mode": "Enforce"` | **P1** |
| **No TLS on gRPC** — all agent communication is plaintext HTTP/2 | Credential/command interception; MITM possible | `TestAgentGrpc/Program.cs:42` — add TLS cert; channel addresses -> `https://` | **P1** (unless network is physically isolated) |
| **No rate limiting on API** | DoS via rapid API calls could overwhelm controller | `TestController.Api/Program.cs` — `AddRateLimiter()` | **P2** |

---

## 5. Resource Management

### What Exists Today

- gRPC channels properly disposed via `AgentEndpoint.Dispose()` with cascading `DisposeHttpClient=true`
- All critical event subscriptions properly paired (subscribe + unsubscribe in Dispose)
- `LogBufferService` bounded at 5000/10000 entries with `DropOldest`
- CancellationTokenSource properly disposed in all services and ViewModels
- Background services respect `CancellationToken` for graceful shutdown

### Gaps

| Gap | Impact | Fix Location | Priority |
|-----|--------|-------------|----------|
| **`FleetVM._healthTimer` and `RegistryVM._healthTimer` never stopped** | Timer fires during shutdown; prevents GC of ViewModel graph | `TestControllerGrpc/ViewModels/AgentWorkspace/FleetVM.cs:62` and `RegistryVM.cs:69` — implement `IDisposable`, stop timer | **P2** |
| **`AgentMonitorViewModel.ActionHistory` grows unbounded** | Long monitoring sessions -> OOM | `TestControllerGrpc/ViewModels/AgentMonitorViewModel.cs` — cap at ~200 entries with trim-from-start | **P2** |
| **`ControllerGrpcServerHost.Dispose()` sync-over-async** | Potential deadlock at shutdown | `TestControllerGrpc/Services/ControllerGrpcServerHost.cs:101` — implement `IAsyncDisposable` | **P2** |

---

## 6. Testability

### What Exists Today

- **~50+ unit tests** (xUnit + Moq) covering: pipeline execution, lock manager, session persistence, channel reset, smart retry, TRX parsing, security redaction, failure analysis
- **~15+ integration tests** (WebApplicationFactory) covering: all API endpoints, lock endpoints, health endpoints, deployment endpoints
- **All core services behind interfaces** (`IAgentGrpcDispatcher`, `IActionPipelineExecutor`, `IFileWatcherManager`, `IAppLogger`, etc.)
- **ViewModel-level testability** via `CreateForFeed()` factory methods that bypass Dispatcher
- **Code coverage** via `coverlet.collector`
- **CI**: GitHub Actions (build + test + TRX artifact upload)

### Gaps

| Gap | Impact | Fix Location | Priority |
|-----|--------|-------------|----------|
| **No test for `WaitForAgentRecoveryAsync`** | Regression risk on the newly added recovery path | `TestControllerGrpc.Tests/Services/` — add recovery wait unit test | **P1** |
| **No UI automation tests** (no Selenium/Playwright/WinAppDriver) | WPF regressions caught only manually | New project: `TestControllerGrpc.UITests` | **P2** |
| **No end-to-end integration test** with real gRPC agent | Cross-process failures only found in production | Add Docker-based agent + controller integration test | **P2** |
| **No load/stress test** for 50+ agents | Scalability unknowns at fleet growth | New: `TestController.LoadTests` with NBomber or k6 | **P2** |

---

## 7. Deployment & Ops

### What Exists Today

- Comprehensive deploy scripts (`deploy-all.bat`, `deploy-agent.bat`, `deploy-webapi.bat`)
- Pre-deploy safety check (`Invoke-PreDeployCheck.ps1` — blocks if active sessions)
- Post-deploy smoke test (`Invoke-SmokeTest.ps1` — 7-step validation)
- Deploy manifest with SHA256 checksums + proto hash verification
- Log cleanup script (30-day retention)
- Fleet patching script (`Patch-AgentFleet.ps1` — parallel, with vCloud snapshot)
- Config validation at startup (`ConfigValidator.cs`)
- Graceful shutdown in all hosted services

### Gaps

| Gap | Impact | Fix Location | Priority |
|-----|--------|-------------|----------|
| **Config typo in Production** — `jvbak` instead of `jvkbak` | Agent unreachable in production fleet | `TestController.WebApi/appsettings.Production.json` — fix address | **P1** |
| **No readiness/liveness distinction** — same endpoint for both | Can't distinguish "still starting" from "unhealthy" for probes | `TestController.Api/Program.cs` — map `/healthz/live` and `/healthz/ready` | **P2** |
| **No rollback script** | Failed deploy requires manual intervention | `deploy/` — add `rollback-agent.bat` (restore from backup dir) | **P2** |
| **No proto version negotiation** — binary compat assumed | Agent/controller version mismatch -> silent gRPC failures | `TestAgentGrpcService.cs` — add version field to `GetCapabilities` | **P2** |

---

## 8. User Experience

### What Exists Today

- Excellent SignalR reconnection: exponential backoff, indefinite retry, tab visibility recovery, network-online recovery, session group rejoin
- `SessionReconnector`: detects orphaned sessions on page load, offers reconnect + log backfill
- `ConnectionStatus` component: 3-state indicator (Live/Reconnecting/Offline)
- `ErrorBoundary`: global React catch with reload button
- Loading states in 8+ components
- Specific error messages with context (agent name, status code, correlation ID)
- Client error logging (`appLogger`) with optional server-side ingestion

### Gaps

| Gap | Impact | Fix Location | Priority |
|-----|--------|-------------|----------|
| **`LiveLogger` has no virtualization** — renders all DOM nodes | 10000+ log entries -> browser lag/freeze for QA engineers | `TestController.WebClient/src/components/execution/LiveLogger.tsx` — add `@tanstack/react-virtual` (LogViewer already uses it) | **P1** |
| **No toast/notification for background session completion** | QA engineer misses finished pipeline if on another browser tab | `TestController.WebClient/src/hooks/useSignalR.ts` — add browser `Notification` API push | **P2** |
| **No confirmation dialog on force-release** | Accidental force-release kills another QA's running pipeline | `TestController.WebClient` — add confirm modal on force-release button | **P2** |

---

## 9. Performance & Scalability

### What Exists Today

- TRX parser with `ConcurrentDictionary` cache (500 entries, LRU eviction, file-timestamp invalidation)
- `LogViewer` with `@tanstack/react-virtual` (virtual scrolling)
- `LogBufferService` with `BoundedChannel(5000)` + batch UI updates at 100ms intervals
- WebClient log trim at 50,000 entries

### Gaps

| Gap | Impact | Fix Location | Priority |
|-----|--------|-------------|----------|
| **No pagination on API responses** — `/api/results/builds` returns ALL builds at once | Slow responses + high memory with hundreds of builds | `TestController.Api/Controllers/` — add `?page=1&pageSize=20` query params | **P2** |
| **TRX parsing loads entire file into memory** (`XDocument.Load`) | Large TRX files (100MB+) spike memory usage | `TestControllerGrpc.Core/Services/TrxResultsParser.cs:137` — consider `XmlReader` streaming for giant files | **P2** |
| **No rate limit on auto channel resets** | Channel reset storm during fleet-wide outage (seen in logs) | `AgentGrpcDispatcher.cs` — rate-limit to max 1 reset per 30s per agent | **P2** |
| **50+ agent scaling untested** | Fleet growth could hit SignalR broadcast limits or hub memory | Load test needed; consider group-based SignalR broadcast | **P2** |

---

## 10. Documentation

### What Exists Today

- `deploy/README.md` — comprehensive deployment guide with script reference
- `docs/ARCHITECTURE.md`, `ARCHITECTURE_DESIGN_DOCUMENT.md`, `ARCHITECTURE-DIAGRAMS.md`
- `docs/hardening-plan/` — 10 hardening documents (per-component)
- OpenAPI available in Development mode
- Inline XML docs on all public services

### Gaps

| Gap | Impact | Fix Location | Priority |
|-----|--------|-------------|----------|
| **No top-level README.md** | New team members have no entry point; discoverability is poor | Solution root — add README with architecture overview, prereqs, build steps | **P1** |
| **No unified ops runbook** | Incident response is ad-hoc; tribal knowledge problem | `docs/RUNBOOK.md` — troubleshooting steps, escalation, common failures | **P1** |
| **OpenAPI disabled in Production** | API consumers can't discover endpoints in prod | `TestController.WebApi/Program.cs:221` — enable `MapOpenApi()` unconditionally | **P2** |
| **No formal ADRs** | Architectural decisions undocumented, rationale lost when engineers leave | `docs/adrs/` — document: no-auth decision, plaintext gRPC, custom logger vs Serilog | **P2** |

---

## Honest Assessment

### For daily use by QA leads at AVEVA, is this production-ready?

**Conditionally yes** — with caveats.

### Strengths (ready for production)

- Resilience engineering is strong (Polly, recovery wait, crash recovery, lock reconciliation)
- Test coverage is substantive (~65+ tests across unit + integration)
- Deployment tooling is mature (pre-checks, smoke tests, manifests, fleet patching)
- UX is polished (reconnection, session recovery, structured errors, connection status indicator)
- Resource management is disciplined (proper disposal, bounded collections, graceful shutdown)
- Audit trail is comprehensive (admin actions, command history, security redaction)

### Blockers (must fix before untrusted network exposure)

1. The **P0 security gap** (no auth) is acceptable ONLY if the system runs on an isolated vCloud network with no external access. If exposed to any network segment with untrusted users, authentication is mandatory.
2. The **deadlock in `ExecutionLogViewerDialog`** will freeze the WPF app unpredictably. This must be fixed immediately — it's a ticking time bomb every time a QA engineer opens the log viewer.

### Risk Acceptance

If the vCloud network is physically isolated and only QA engineers have access to the machines, the security issues (P0) can be **conditionally deferred** to P1 with documented risk acceptance. The concurrency issues are real but low-frequency in practice with the current 4-10 agent scale. At 50+ agents, they become much more likely.

---

## Recommended Fix Order

```
Week 1:  P0 #2 (deadlock fix — 30 min)
         P1 #4 (HubFilter — 1 hour)
         P1 #13 (config typo — 5 min)
         P1 #14 (README + runbook — half day)

Week 2:  P1 #7 (ForceRelease atomicity — 1 hour)
         P1 #8 (CommandExecutor TOCTOU — 2 hours)
         P1 #6 (atomic file writes — 1 hour)
         P1 #12 (LiveLogger virtualization — 2 hours)

Week 3:  P0 #1 + P1 #3 (auth framework — 1-2 days)
         P1 #11 (CommandPolicy enforce — 30 min config change)
         P1 #10 (gRPC correlation ID — half day)

Week 4:  P1 #5 (WebApi resilience — half day)
         P1 #9 (ResetChannel lock — 2 hours)
         P2 items as capacity allows
```
