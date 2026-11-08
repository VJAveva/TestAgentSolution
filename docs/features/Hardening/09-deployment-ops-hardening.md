# Deployment & Operations Hardening Plan

**Scope:** Publish scripts, deploy scripts, IIS/WebApi hosting, agent node rollout, smoke tests, production readiness.

---

## Key Responsibilities

- Publish WebApi/WebClient/controller/agent artifacts.
- Deploy to IIS/server shares.
- Restart services safely.
- Validate fleet, telemetry, SignalR, controller proxy, and agent versions.

---

## Loopholes / Risks

| ID | Priority | Risk | Remediation | Status |
|---|---:|---|---|---|
| OPS-001 | P1 | Agent nodes can run old binaries missing `ForceReady`/watchdog. | Add agent capability/version check before execution. | Done |
| OPS-002 | P1 | Publish/deploy scripts may copy stale or partial artifacts. | Add artifact manifest + checksum validation. | Done |
| OPS-003 | P1 | IIS restart/deploy can interrupt active WebClient sessions without warning. | Add maintenance mode and pre-deploy active-session check. | Done |
| OPS-004 | P1 | No automated smoke test for active execution stream safety after deployment. | Add smoke test that opens monitor while command streams. | Done |
| OPS-005 | P2 | Production config not validated before service start. | Add config validator and fail-fast startup checks. | Done |
| OPS-006 | P2 | Logs/audit retention not centrally documented. | Add retention policy and cleanup job. | Done |

---

## Required Changes

- [x] Add `/api/agents/{name}/capabilities` or gRPC capability endpoint.
- [x] Add deployment manifest with build timestamp, git SHA, proto hash, agent capability version.
- [x] Add smoke script: fleet, details, telemetry, SignalR negotiate, force-ready support, active stream.
- [x] Add pre-deployment check: no active sessions unless `-Force`.
- [x] Add production config validator.
- [x] Add centralized log retention and cleanup script.

---

## Agent Rollout Checklist

For each agent node:

- [ ] Stop existing agent service/tray process.
- [ ] Deploy new `TestAgentGrpc` binaries.
- [ ] Validate `appsettings.json` contains `MaxExecutionTimeoutMinutes`.
- [ ] Start agent.
- [ ] Verify registration with controller.
- [ ] Verify `ForceReady` RPC is implemented.
- [ ] Verify `GetAgentSnapshot` works.
- [ ] Run short command and cancel it; confirm Ready state.

---

## WebApi Rollout Checklist

- [ ] Build and publish fresh artifacts.
- [ ] Stop IIS/site/app pool.
- [ ] Copy artifacts with manifest validation.
- [ ] Start IIS/site/app pool.
- [ ] Validate `/api/agents/fleet`.
- [ ] Validate `/api/agents/{name}/telemetry` for healthy and offline agents.
- [ ] Validate SignalR negotiate endpoint.
- [ ] Validate WebClient bundle loads.

---

## Smoke Test Commands

```powershell
# Build
 dotnet build TestAgentSolution.sln -v q

# Focused tests
 dotnet test TestControllerGrpc.Tests\TestControllerGrpc.Tests.csproj --filter ExecutionStreamSafeguardTests
 dotnet test TestController.WebApi.Tests\TestController.WebApi.Tests.csproj

# Runtime validation
 Invoke-RestMethod http://jvgr22:8080/api/agents/fleet
 Invoke-RestMethod http://jvgr22:8080/api/agents/JVHIST/telemetry
```
