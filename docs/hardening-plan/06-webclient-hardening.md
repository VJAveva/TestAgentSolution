# TestController.WebClient Hardening Plan

**Project:** `TestController.WebClient`  
**Role:** React/Vite/Tailwind SPA for fleet, monitor, registry, execution dashboard, force release.

---

## Key Responsibilities

- Display fleet state, locks, active sessions, execution dashboard.
- Poll REST endpoints and consume SignalR events.
- Provide force-release and release-all controls.
- Surface agent monitor telemetry and errors.

---

## Loopholes / Risks

| ID | Priority | Risk | Remediation | Status |
|---|---:|---|---|---|
| CLIENT-001 | P1 | Polling intervals can continue aggressively during active execution. | Back off polling when SignalR indicates active execution. | Done |
| CLIENT-002 | P1 | `200 OK` offline telemetry can be treated as healthy unless UI checks `isOffline`. | Standardize telemetry state mapping. | Done |
| CLIENT-003 | P1 | Force release actions need confirmation and reason capture. | Add confirmation modal + reason text. | Done |
| CLIENT-004 | P2 | Hook timers may leak if not cleaned up. | Add React fake timer tests. | Done |
| CLIENT-005 | P2 | Error toasts can flood during outage. | Add toast dedupe/backoff per endpoint. | Done |
| CLIENT-006 | P2 | API type contracts are implicit. | Generate TypeScript client/types from OpenAPI or shared schema. | Done |

---

## Required Changes

- [x] Add execution-aware polling policy: active = SignalR/live events, REST backoff to 15-30s.
- [x] Ensure all telemetry UI honors `isOffline`, `state`, and `lastSeenUtc`.
- [x] Add dedupe for repeated request failures.
- [x] Add confirmation/reason on force release/release all.
- [x] Add generated API contracts or central TS interfaces with tests.

---

## Tests to Add

- [x] `useExecutionDashboard` fake timer tests for interval cleanup/backoff.
- [x] Monitor page tests for `isOffline` telemetry.
- [x] Force release confirmation tests.
- [x] Error toast dedupe tests.
- [ ] SignalR reconnect/backoff tests.

---

## Validation

- [x] During active install, browser should not hammer telemetry every 2s.
- [x] Offline agent should display Offline, not request-failed loop.
- [x] Force release should be auditable with user-confirmed reason.
