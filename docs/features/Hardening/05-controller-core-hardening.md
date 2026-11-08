# TestControllerGrpc.Core Hardening Plan

**Project:** `TestControllerGrpc.Core`  
**Role:** Shared models, interfaces, event/session/lock services, proto generation for controller-side consumers.

---

## Key Responsibilities

- Canonical domain model for watchlists, actions, execution sessions, locks.
- Shared interfaces for controller/WebApi adapters.
- Shared event aggregator and session management.
- Shared proto-generated agent service contract.

---

## Loopholes / Risks

| ID | Priority | Risk | Remediation | Status |
|---|---:|---|---|---|
| CORE-001 | P1 | Proto duplicated in several projects. | Make Core proto canonical and enforce hash consistency. | Done |
| CORE-002 | P1 | WatchList XML can deserialize invalid/unsafe action definitions. | Add validation layer after parse. | Done |
| CORE-003 | P1 | `CancellationTokenSource` ownership can leak through session objects. | Encapsulate cancellation with methods instead of public CTS. | Done |
| CORE-004 | P1 | No shared timeout policy contract. | Add `TimeoutPolicies` / typed options interfaces. | Done |
| CORE-005 | P2 | EventAggregator behavior under high load needs documented bounded semantics. | Add stress tests and documented event loss policy. | Done |
| CORE-006 | P2 | Lock persistence needs schema versioning. | Add version field and migration strategy. | Done |
| CORE-007 | P0 | Shared logs/events can persist or broadcast sensitive command/output text. | Centralize redaction before logging, persistence, and event publication. | Done |

---

## Required Changes

- [x] Add `WatchListValidator` with command, agent, timeout, and parameter validation.
- [x] Add `ProtoConsistencyTests` comparing all proto copies to Core canonical file.
- [x] Add session cancellation methods: `RequestCancel()`, `DisposeCancellation()`; hide raw CTS.
- [x] Add shared `ExecutionTimeoutOptions` and `PollingOptions`.
- [x] Add lock/session persistence schema version.
- [x] Add shared `SecurityRedactor` for controller/WebApi log and event sanitization.

---

## Tests to Add

- [x] `WatchListValidatorTests`.
- [x] `ProtoConsistencyTests`.
- [x] `ExecutionSessionCancellationOwnershipTests`.
- [x] `TimeoutPolicyTests`.
- [x] `EventAggregatorStressTests`.
- [x] `LockPersistenceMigrationTests`.
- [x] `ControllerRedactionTests` for `AppLogger` and `ExecutionSessionManager` event redaction.

---

## Validation

- [x] Invalid WatchList fails fast before execution.
- [x] Proto mismatch fails CI.
- [x] Session cancel cannot dispose shared CTS unexpectedly.
