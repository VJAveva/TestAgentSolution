 # Test Projects Hardening Plan

**Projects:** `TestControllerGrpc.Tests`, `TestController.WebApi.Tests`  
**Role:** Prevent regressions in execution safety, API contracts, lock/session recovery, config, and deployment assumptions.

---

## Current Useful Coverage

- Agent lock manager tests.
- Session manager tests.
- WebApi endpoint tests.
- Dispatcher execution-stream safeguard tests.
- Parser/results/retry/failure analysis tests.

---

## Test Gaps

| ID | Priority | Gap | Required Tests | Status |
|---|---:|---|---|---|
| TEST-001 | P1 | Polling safeguard only unit-tested at dispatcher level. | Integration simulation with monitor + active stream. | Done |
| TEST-002 | P1 | WebApi telemetry fallback and cache behavior incomplete. | Endpoint tests for offline/cached/active execution. | Done |
| TEST-003 | P1 | Agent stuck-state recovery not fully tested. | ForceReady, TerminateExecution, watchdog, stream cancel tests. | Done |
| TEST-004 | P1 | Proto drift not tested. | Proto hash consistency test. | Done (pre-existing) |
| TEST-005 | P0 | Credential leakage not tested. | Credential redaction tests across logs/audit. | Done (pre-existing) |
| TEST-006 | P2 | React/WebClient polling not tested. | Fake timer tests for hooks. | Done |
| TEST-007 | P2 | Route/error contract not tested. | OpenAPI/route convention/error shape tests. | Done |

---

## Required Changes

- [x] Add test category naming: `Safety`, `Security`, `Contract`, `Integration`, `Regression`.
- [x] Add a lightweight fake gRPC agent test server for stream/poll interference tests.
- [x] Add fixture utilities for synthetic sessions/locks.
- [x] Add proto consistency test.
- [x] Add credential redaction test helper that verifies generated audit logs.
- [x] Add controller redaction tests for app logs and progress events.
- [x] Add CI command list in repository docs.

---

## Must-Have Regression Tests

- [x] Dispatcher returns synthetic snapshot during active execution.
- [x] Dispatcher `PingAsync` skips gRPC during active execution.
- [x] Monitor keeps cached metrics during active execution.
- [x] Diagnostics does not poll active agent.
- [x] WebApi telemetry returns offline response instead of 502.
- [x] Agent returns Ready after cancellation.
- [x] Agent watchdog recovers stuck Running state.
- [x] No plaintext password appears in generated agent audit logs.
- [x] No plaintext password/token appears in controller shared app logs or progress events.
- [x] Proto copies match canonical proto.

---

## Validation Commands

```powershell
dotnet build TestAgentSolution.sln -v q
dotnet test TestControllerGrpc.Tests\TestControllerGrpc.Tests.csproj --no-build
dotnet test TestController.WebApi.Tests\TestController.WebApi.Tests.csproj --no-build
```
