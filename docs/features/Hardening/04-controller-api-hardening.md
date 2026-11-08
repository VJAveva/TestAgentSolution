# TestController.Api Hardening Plan

**Project:** `TestController.Api`  
**Role:** Shared MVC controllers, SignalR hub, middleware, controller API extensions for WPF and standalone hosts.

---

## Key Responsibilities

- Shared REST execution/watchlist/agent/results/health API surface.
- Shared SignalR hub and real-time notifier bridge.
- Lock recovery and execution session endpoints.
- Common middleware and endpoint registration.

---

## Loopholes / Risks

| ID | Priority | Risk | Remediation | Status |
|---|---:|---|---|---|
| API-001 | P1 | Slow SignalR clients can affect broadcast flow if sends are awaited without timeout. | Add bounded queue/timeouts/nonblocking send strategy. | Done |
| API-002 | P1 | Execution endpoints may accept invalid/nonexistent WatchList tags inconsistently. | Return consistent `problem+json` errors. | Done |
| API-003 | P1 | Force-release/cancel actions need full audit trail. | Add structured audit entries with actor/source/reason/session. | Done |
| API-007 | P0 | SignalR payloads can broadcast raw command/output/error strings to browsers. | Redact command, status, line, message, and error payload fields. | Done |
| API-004 | P2 | Error response formats vary across controllers. | Standardize RFC 7807. | Done |
| API-005 | P2 | Shared API can diverge from Minimal API endpoints in WebApi. | Endpoint parity tests and OpenAPI diff. | Not started |
| API-006 | P2 | Lock recovery polling interval/backoff may not scale. | Add configurable backoff and state cache. | Done |

---

## Required Changes

- [x] Add `ApiErrorFactory` for consistent problem responses.
- [ ] Add `IExecutionPreflightService` used by all trigger endpoints.
- [x] Add audit events for cancel, force-release, lock recovery, orphan cleanup.
- [x] Add SignalR broadcast timeout/cancellation token.
- [x] Redact SignalR log, action progress, and agent output payloads before broadcast.
- [ ] Add route and response contract documentation.

---

## Tests to Add

- [x] `ExecutionEndpoint_InvalidTag_ReturnsProblemJson`.
- [x] `ForceRelease_AuditsActorAndReason`.
- [ ] `SignalRNotifier_DoesNotBlockOnSlowClient`.
- [x] Controller redaction tests cover shared event payload sanitization source.
- [ ] `SharedApi_MinimalApi_EndpointParityTests`.
- [x] `LockRecovery_BackoffTests`.

---

## Validation

- [ ] Validate all mutating endpoints have actor/source in logs.
- [ ] Validate UI receives SignalR events even with one slow/disconnected client.
- [ ] Validate error response shape is consistent in WebClient.
