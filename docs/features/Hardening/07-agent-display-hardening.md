# TestAgentDisplay / TestController.Dashboard Hardening Plan

**Projects:** `TestAgentDisplay`, `TestController.Dashboard`  
**Role:** Monitoring/dashboard UI surfaces for agent/controller status.

---

## Key Responsibilities

- Display agent health and execution status.
- Connect to agents/controller without mutating execution state.
- Provide operator visibility during long-running tasks.

---

## Loopholes / Risks

| ID | Priority | Risk | Remediation | Status |
|---|---:|---|---|---|
| DISPLAY-001 | P1 | Read-only dashboards may create independent gRPC polling channels that still load agents. | Add execution-aware backoff and separate low-priority polling policy. | Done |
| DISPLAY-002 | P1 | Dashboard status can show stale current command if snapshot is old. | Include timestamp/age in UI and clear on terminal events. | Done |
| DISPLAY-003 | P2 | Connection retries may be synchronized across agents. | Add jittered reconnect backoff. | Done |
| DISPLAY-004 | P2 | Monitoring UI behavior not covered by tests. | Add VM tests for offline/running/complete transitions. | Done |

---

## Required Changes

- [x] Ensure all dashboard gRPC clients use separate channels from command dispatcher.
- [x] Add polling backoff during active execution.
- [x] Add timestamp/age display for snapshot data.
- [x] Add jittered retry policy.
- [x] Avoid showing stale commands after completion.

---

## Tests to Add

- [x] `Dashboard_DoesNotPollAggressivelyDuringExecution`.
- [x] `AgentDisplay_ClearsCurrentCommand_OnCompletedEvent`.
- [x] `ReconnectBackoffTests`.
- [x] `SnapshotAgeDisplayTests`.

---

## Validation

- [x] Open dashboard during long install and verify no command cancellation.
- [x] Disconnect/reconnect agent and verify UI recovers without flicker.
