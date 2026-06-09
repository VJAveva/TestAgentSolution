# TestAgentGrpc Hardening Plan

**Project:** `TestAgentGrpc`  
**Role:** Remote execution agent, gRPC server, process executor, system tray UI.

---

## Key Responsibilities

- Accept commands from controller.
- Execute local processes with stdout/stderr streaming.
- Maintain single-execution guard.
- Report state, metrics, history, audit logs, and lifecycle heartbeats.
- Recover safely after cancellation, timeout, reboot, or controller disconnect.

---

## Current Safeguards Already Added

- [x] `MaxExecutionTimeoutMinutes` safety-net.
- [x] Exception-safe `finally` resets state to Ready and releases lock.
- [x] `ForceReady()` nuclear reset.
- [x] `TerminateExecution()` delayed watchdog fallback to `ForceReady()`.
- [x] `StreamOutputAsync` now accepts cancellation token.
- [x] `StuckExecutionWatchdog` background service.
- [x] `MaxExecutionTimeoutMinutes` added to `appsettings.json`.

---

## Loopholes / Risks

| ID | Priority | Risk | Remediation | Status |
|---|---:|---|---|---|
| AGENT-001 | P0 | Password field in proto can carry plaintext credentials. | Mask all logs and plan secure auth replacement. | In progress — redaction implemented |
| AGENT-002 | P0 | Arbitrary command execution if controller config is compromised. | Add optional command allowlist / path policy. | In progress — AuditOnly policy implemented |
| AGENT-003 | P1 | Long keep-alive and disabled data-rate limits can create zombie connections. | Tune Kestrel limits for production profile. | Done — AgentKestrelOptions |
| AGENT-004 | P1 | `ForceReady()` releases semaphore by `CurrentCount == 0`, which is best-effort but not owner-aware. | Add explicit execution state object with lock ownership tracking. | Done — ExecutionLifecycleState |
| AGENT-005 | P1 | Audit/history may contain sensitive command args. | Add redaction pipeline before persistence. | Done — ExecutionTracker redaction |
| AGENT-006 | P2 | Metrics collection can be called frequently by many clients. | Add cached metrics snapshot and per-client throttling. | Done — 2s TTL cache |
| AGENT-007 | P2 | Watchdog threshold has fixed 5-minute grace. | Move grace interval to config. | Done — WatchdogGraceMinutes |

---

## Required Changes

### P0

- [x] Redact passwords/secrets from audit records, agent activity, and execution events.
- [x] Add `CommandPolicyOptions` with modes: `Disabled`, `AuditOnly`, `Enforce`.
- [x] Add safe path allowlist for command executables/scripts.

### P1

- [x] Add typed `AgentKestrelOptions` for keep-alive/data-rate tuning.
- [x] Add startup warning if running plaintext HTTP/2 outside development.
- [x] Add explicit execution lifecycle state object: `ExecutionId`, `LockAcquired`, `StartedUtc`, `TerminationRequested`, `ResetReason`.
- [x] Add version/capability endpoint so controller can detect old agents before dispatch.

### P2

- [x] Cache metrics for 1-2 seconds inside agent to reduce expensive system calls.
- [x] Add configurable watchdog grace interval.
- [ ] Add structured audit event categories for cancellation, timeout, force ready, watchdog reset.

---

## Tests to Add

- [x] `TerminateExecution_ForcesReady_WhenFinallyDoesNotCompleteQuickly`.
- [x] `StreamOutputAsync_Cancels_WhenExecutionCancelled`.
- [x] `StuckExecutionWatchdog_ForceReady_AfterThreshold`.
- [x] `CredentialRedactionTests` for audit logs.
- [x] `CommandPolicyTests` for AuditOnly/Enforce behavior.
- [x] `AgentCapabilityTests` for `ForceReady` support detection.

---

## Validation

- [ ] Deploy updated agent to one test node.
- [ ] Start long-running silent process; cancel from controller; verify state returns Ready.
- [ ] Simulate controller disconnect; verify process killed or state recovered as designed.
- [x] Verify generated audit logs contain no plaintext passwords/secrets.
