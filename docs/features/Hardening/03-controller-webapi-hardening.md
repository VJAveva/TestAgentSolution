# TestController.WebApi Hardening Plan

**Project:** `TestController.WebApi`  
**Role:** Standalone ASP.NET host, REST/SignalR API, React SPA host, agent monitoring endpoints.

---

## Key Responsibilities

- Serve WebClient SPA.
- Provide REST APIs for agents, execution, watchlist, results, health.
- Manage standalone `AgentRegistry` and gRPC client manager.
- Broadcast agent events via SignalR.
- Gracefully handle unreachable agents.

---

## Current Safeguards Already Added

- [x] `/api/agents/{name}/telemetry` returns offline state instead of HTTP 502 on gRPC failure.
- [x] Force release supports WebClient source.
- [x] WebApi deployment validated on Jvgr22.
- [x] Standalone pipeline/dispatcher output paths redact command secrets before logging or broadcasting.

---

## Loopholes / Risks

| ID | Priority | Risk | Remediation | Status |
|---|---:|---|---|---|
| WEBAPI-001 | P1 | Telemetry fallback returns `200 OK` offline state, which can hide system failure from automation. | Add `isOffline`, `error`, `lastSeenUtc`, and optional `Warning` header. | Done |
| WEBAPI-002 | P1 | Standalone registry can drift from WPF controller registry. | Shared registry provider or proxy-to-controller mode. | Not started |
| WEBAPI-003 | P1 | WebApi telemetry may still call agents during active execution if not aware of WPF active sessions. | Use execution lock/session state to return cached/synthetic telemetry. | Done |
| WEBAPI-004 | P1 | Execution trigger endpoints may not fail fast for offline required agents. | Add preflight agent availability check. | Done |
| WEBAPI-008 | P0 | Standalone WebApi can publish local/remote command args and process output to logs/SignalR. | Redact command lines, output lines, errors, and agent stream events. | Done |
| WEBAPI-005 | P2 | Mixed MVC + Minimal API route conventions. | Adopt route conventions and OpenAPI. | Done |
| WEBAPI-006 | P2 | CORS/CSRF assumptions rely on same-origin deployment. | Add explicit production CORS policy and anti-forgery strategy for mutating endpoints if cross-origin. | Done |
| WEBAPI-007 | P2 | Rate limiting not applied to telemetry/history/audit endpoints. | Add ASP.NET rate limiting policies. | Done |

---

## Required Changes

### P1

- [x] Extend telemetry response contract with `isOffline`, `error`, `lastKnownState`, `lastSeenUtc`.
- [x] Add cache-aware telemetry path: if agent is locked/executing, return cached metrics and active command without gRPC.
- [x] Add trigger preflight: required agents online, not locked by other session, capability compatible.
- [x] Add direct tests for the 502-to-offline fallback.
- [x] Sanitize standalone execution logs and agent stream relay payloads.

### P2

- [x] Add OpenAPI document generation.
- [ ] Add endpoint route convention tests.
- [x] Add rate limits: telemetry/history/audit lower priority; execution mutation endpoints stricter.
- [x] Add CORS config validation.

---

## Tests to Add

- [x] `GetTelemetry_ReturnsOfflineResponse_WhenGrpcUnavailable`.
- [x] `GetTelemetry_ReturnsCached_WhenAgentLockedOrExecuting`.
- [x] `TriggerExecution_ReturnsConflict_WhenRequiredAgentOffline`.
- [ ] `AgentRegistryPersistenceTests`.
- [x] `RateLimitTests` for telemetry endpoints.

---

## Validation

- [ ] Kill an agent service and verify WebClient shows offline without request-failed popups.
- [ ] Run active execution and verify WebApi polling does not disturb WPF stream.
- [ ] Trigger execution with offline required agent; verify fail-fast response.
