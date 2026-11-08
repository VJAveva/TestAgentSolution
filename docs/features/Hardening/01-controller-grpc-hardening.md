# TestControllerGrpc Hardening Plan

**Project:** `TestControllerGrpc`  
**Role:** WPF controller, pipeline orchestration, gRPC dispatcher, embedded web host, agent workspace UI.

---

## Key Responsibilities

- Execute WatchList pipelines locally and remotely.
- Maintain agent gRPC channels and execution resilience.
- Own WPF Agent Workspace Fleet/Monitor/Registry views.
- Host embedded API/SignalR services for dashboard/web access.
- Coordinate locks, sessions, file watchers, and vocabulary reloads.

---

## Current Safeguards Already Added

- [x] `AgentGrpcDispatcher.IsAgentExecuting()` tracks active streamed executions.
- [x] `TestConnectionAsync()` returns synthetic snapshot during active execution instead of making gRPC call.
- [x] `PingAsync()` returns cached success during active execution.
- [x] Monitor polling skips gRPC during execution.
- [x] Monitor health cards use cached metrics while polling is suppressed.
- [x] `RpcException(StatusCode.Cancelled)` handled separately.
- [x] `ForceReady` fallback to `TerminateExecution` for older agents.

---

## Loopholes / Risks

| ID | Priority | Risk | Remediation | Status |
|---|---:|---|---|---|
| CTRL-001 | P1 | Any future direct gRPC polling can reintroduce stream interruption. | Add code review rule + tests for all polling paths. | Done |
| CTRL-002 | P1 | `DiagnoseAgentAsync` can still make gRPC calls during active execution if user clicks diagnostics. | Disable diagnostics during active execution or make it use cached/synthetic data. | Done |
| CTRL-003 | P1 | Credentials can flow through `ActionConfig` and gRPC request. | Redact logs and add credential transport strategy. | In progress — controller log/event redaction implemented |
| CTRL-004 | P1 | Failed/stale gRPC channels can remain until unregister/re-register. | Add channel reset endpoint/action and health-state lifecycle rules. | Done |
| CTRL-005 | P2 | `MonitorVM` mixes UI, telemetry, session, and polling logic. | Extract `MonitorTelemetryCache` / `TelemetryPollingService`. | Done |
| CTRL-006 | P2 | Timeout constants are scattered (`3s`, `5s`, `2min`, `24h`). | Introduce typed `ControllerTimeoutOptions`. | Done |
| CTRL-007 | P2 | Embedded web host DI bridge can fail silently if WPF services are not wired. | Add startup validation and health endpoint for embedded host dependencies. | Done |

---

## Required Changes

### P1

- [x] Add `IsAgentExecuting` guard to `DiagnoseAgentAsync` or block diagnostics while command stream is active.
- [x] Add explicit warning in Monitor UI: "Live metrics paused during execution to protect command stream".
- [x] Add dispatcher-level metric cache service that can serve WPF and WebApi consistently.
- [x] Add explicit channel recycle/reset method for unhealthy agents.
- [x] Redact credentials in controller pipeline logs, UI log entries, dispatcher status, and local output paths.

### P2

- [x] Extract telemetry formatting from `MonitorVM` into pure service with tests.
- [x] Centralize timeout policies.
- [x] Add `ControllerOptions` startup validation.
- [ ] Split `MainViewModel`/agent workspace concerns where practical.

---

## Tests to Add

- [x] `ExecutionStreamSafeguardTests` for dispatcher synthetic path.
- [x] `MonitorTelemetryCacheTests` for cached metrics during active execution.
- [x] `DiagnoseAgent_DoesNotPollDuringActiveExecution`.
- [ ] `CancellationHandlingTests` for `RpcException(Cancelled)` user cancel vs timeout.
- [x] `ChannelResetTests` for recycling failed gRPC channels.
- [x] `ControllerRedactionTests` for shared app logs and session progress events.

---

## Validation

- [ ] Run long install while Monitor is open; confirm no `Cancelled` gRPC exception.
- [ ] Verify Monitor shows cached metrics, not blank cards.
- [ ] Verify after execution completes, real metrics resume.
- [ ] Verify ForceReady and fallback TerminateExecution both work against old/new agents.
