# Overall System Hardening & Sanitization Plan

**Created:** 2026-05-21  
**Scope:** TestAgentSolution distributed controller, WebApi, WebClient, agents, shared API/core, monitoring, tests, deployment scripts.  
**Goal:** Track architecture loopholes, reliability gaps, security risks, and required sanitization work without destabilizing the core execution stream.

---

## Executive Summary

The system is functional and has several important resilience fixes already in place:

- Controller-side gRPC polling safeguard during active execution.
- Agent-side `ForceReady`, `TerminateExecution` recovery, safety-net execution timeout, and stuck-execution watchdog.
- WebApi telemetry fallback for unreachable agents.
- WebClient/WPF force-release improvements.

Remaining risks are mostly in **security**, **cross-host consistency**, **configuration drift**, **proto duplication**, **polling coordination**, **test coverage**, and **deployment validation**.

---

## Priority Definitions

| Priority | Meaning |
|---|---|
| P0 | Must fix before broad production use or sensitive environments. Security/data-loss/core-execution risk. |
| P1 | High-value hardening needed for reliability, scale, and operational confidence. |
| P2 | Maintainability, consistency, and long-term platform quality. |

---

## System-Wide Risk Register

| ID | Priority | Area | Risk / Loophole | Required Sanitization | Status |
|---|---:|---|---|---|---|
| SYS-001 | P0 | Security | Credentials can flow through gRPC request fields and may be logged by mistakes. | Mask all credential fields in logs, phase out password fields, add mTLS or Windows auth strategy. | In progress — agent/controller/WebApi redaction implemented |
| SYS-002 | P0 | Command safety | Agent accepts arbitrary command paths/arguments from controller configuration. | Add allowlist/validation mode, safe argument policy, audit rejected commands. | In progress — AuditOnly policy implemented |
| SYS-003 | P1 | gRPC reliability | Polling/health checks can interfere with long-running command streams if bypassing dispatcher safeguard. | Enforce centralized polling contract; add tests across WPF, WebApi, WebClient. | In progress |
| SYS-004 | P1 | Proto drift | Multiple copies of `test_agent.proto` can diverge. | Canonical proto + CI hash consistency test. | Not started |
| SYS-005 | P1 | Config drift | Ports, timeouts, and deployment paths are scattered across code/config/scripts. | Centralize config, add startup validation and config tests. | Not started |
| SYS-006 | P1 | Agent registry drift | WPF dynamic registry and standalone WebApi registry can diverge. | Shared `IAgentRegistryProvider` or file-backed registry with events. | Not started |
| SYS-007 | P1 | Execution preflight | Pipeline can start before all required agents are healthy. | Add required-agent preflight with fail-fast `409 Conflict`/UI warning. | Not started |
| SYS-008 | P1 | Slow client / SignalR | Slow SignalR clients may delay broadcasts if sends are awaited without timeout. | Add bounded queues/timeouts/nonblocking sends. | Not started |
| SYS-009 | P2 | API consistency | REST routes and error responses are inconsistent. | Adopt `/api/v1`, RFC 7807 `problem+json`, OpenAPI tests. | Not started |
| SYS-010 | P2 | Test strategy | Safeguard tests exist for dispatcher only; missing UI/WebApi/WebClient integration. | Add full polling-interference and WebClient timer cleanup tests. | In progress |

---

## Architecture Guardrails

These guardrails should be treated as permanent design constraints:

1. **Do not make gRPC polling calls to an agent while `RunCommandStreamed` is active on that agent.**
2. **Any health/monitoring path must go through `IAgentGrpcDispatcher` or a cache that understands active execution state.**
3. **Any direct gRPC client created outside dispatcher must use a separate channel and must not poll during active execution.**
4. **Agent execution state must always have a recovery path:** normal completion → cancellation → terminate → `ForceReady` → watchdog.
5. **Credentials must never be logged or stored in clear text.**
6. **Proto changes must be synchronized across all generated clients/servers before deployment.**

---

## Cross-Project Work Plan

### Phase 1 — Stabilize Core Execution Safety

- [x] Controller: suppress telemetry/ping gRPC calls during active execution.
- [x] Controller: cache last metrics in Monitor view to avoid blank health cards.
- [x] Agent: add `ForceReady`, safety timeout, watchdog, safer termination.
- [x] Tests: add dispatcher safeguard tests.
- [ ] WebApi: add explicit active-execution/cache behavior where it proxies WPF or runs standalone.
- [ ] WebClient: back off polling during active sessions via SignalR state.

### Phase 2 — Security Sanitization

- [x] Redact `password`, `token`, `secret`, `credential`, `apikey` in agent audit logs and published execution events.
- [x] Redact controller/WebApi pipeline logs, UI logs, SignalR payloads, local command output, and persisted app logs.
- [x] Add credential logging/redaction tests.
- [x] Add controller-side redaction tests for shared logs and progress events.
- [x] Add command policy with compatibility `AuditOnly` mode.
- [ ] Plan mTLS/TLS or Windows auth for production agent-controller communication.
- [ ] Validate file permissions for audit logs and deployment directories.

### Phase 3 — Contract & Configuration Governance

- [ ] Canonicalize proto source.
- [ ] Add proto hash consistency test.
- [ ] Move timeout/port constants into typed options.
- [ ] Add production config validator.
- [ ] Add deployment smoke-test scripts for controller + agent versions.

### Phase 4 — API / UI Consistency

- [ ] Standardize error response format.
- [ ] Generate OpenAPI documentation for WebApi.
- [ ] Add route convention tests.
- [ ] Add React polling hook tests with fake timers.
- [ ] Add UI state transition tests for current command clearing.

---

## Validation Gates

Before promoting a build:

- [ ] `dotnet build TestAgentSolution.sln -v q` passes.
- [ ] `TestControllerGrpc.Tests` passes, including `ExecutionStreamSafeguardTests`.
- [ ] `TestController.WebApi.Tests` passes.
- [ ] Proto consistency test passes.
- [x] Credential sanitization tests pass for agent audit logs and controller shared logs/events.
- [ ] Deployment smoke test validates: fleet, telemetry, active execution, cancel, force ready, reboot handling.

---

## Project-Level Plans

- [01-controller-grpc-hardening.md](01-controller-grpc-hardening.md)
- [02-agent-grpc-hardening.md](02-agent-grpc-hardening.md)
- [03-controller-webapi-hardening.md](03-controller-webapi-hardening.md)
- [04-controller-api-hardening.md](04-controller-api-hardening.md)
- [05-controller-core-hardening.md](05-controller-core-hardening.md)
- [06-webclient-hardening.md](06-webclient-hardening.md)
- [07-agent-display-hardening.md](07-agent-display-hardening.md)
- [08-tests-hardening.md](08-tests-hardening.md)
- [09-deployment-ops-hardening.md](09-deployment-ops-hardening.md)
