# Operations Runbook

Operational procedures for monitoring, troubleshooting, and recovering the TestAgentSolution platform.

---

## Table of Contents

1. [Deployment Topology](#deployment-topology)
2. [Deploy & Rollback](#deploy--rollback)
3. [Agent Roster (Canonical Source of Truth)](#agent-roster-canonical-source-of-truth)
4. [Health Checks](#health-checks)
5. [Agent Down / Unreachable](#agent-down--unreachable)
6. [Session Stuck / Orphaned](#session-stuck--orphaned)
7. [Lock Orphaned](#lock-orphaned)
8. [Controller Restart](#controller-restart)
9. [WebApi / IIS Issues](#webapi--iis-issues)
10. [SignalR Disconnections](#signalr-disconnections)
11. [High Memory / TRX Parsing](#high-memory--trx-parsing)
12. [Channel Reset Storm](#channel-reset-storm)
13. [Log Retention & Cleanup](#log-retention--cleanup)
14. [Escalation Contacts](#escalation-contacts)

---

## Deployment Topology

The standalone `TestController.WebApi` host runs in exactly one of two topologies.
The choice is **explicit**: set `Deployment:Topology` in `appsettings.json`. It is
validated at startup by `ConfigValidator`, which cross-checks it against
`ControllerProxyUrl` and logs the effective mode (`[ConfigValidator] Deployment
topology: ...`).

| Topology | `ControllerProxyUrl` | Who executes runs | WPF Controller required |
|------------|----------------------|----------------------------------|-------------------------|
| `CoLocated` | **set** (e.g. `http://localhost:5200`) | WPF Controller (WebApi proxies) | **Yes** |
| `Standalone` | **empty / removed** | WebApi (`StandalonePipelineExecutor`) | **No** |
| `Auto` | either | inferred from URL presence | depends |

### CoLocated mode

WebApi is a thin web gateway in front of the WPF Controller. Execution,
dashboard, and fleet calls are proxied over gRPC to the Controller, which is the
single authority and SQLite DB owner.

- `Deployment:Topology` = `CoLocated`
- `ControllerProxyUrl` = the running Controller's URL (required)
- Startup **fails fast** if `ControllerProxyUrl` is missing.

### Standalone mode (no WPF anywhere)

WebApi is the sole host. It executes the full submit → watch pipeline locally,
owns its own lock authority, and runs with **no WPF Controller running anywhere**.

1. Set `Deployment:Topology` = `Standalone`.
2. **Remove** (or blank) `ControllerProxyUrl` in `appsettings.json` /
   `appsettings.Production.json`.
3. Confirm at startup: `[ConfigValidator] Deployment topology: Standalone`.
4. Verify the path end-to-end: `POST /api/execution/...` (submit) then watch via
   `/hubs/controller` SignalR — both served entirely by the WebApi process.

---

## Deploy & Rollback

Deployments go through `deploy/Invoke-Deploy.ps1`, which wraps the per-component
deploy scripts with a backup → deploy → smoke-test → auto-rollback safety net.
**Anyone with repo + node access can deploy and roll back** — you do not need the
original author. Run it locally or via the **deploy** GitHub Actions workflow
(`.github/workflows/deploy.yml`, `Run workflow` → choose `deploy`/`rollback`).

### Deploy a new build

```powershell
# WebApi (with pre-deploy session check, snapshot, smoke test, auto-rollback on failure)
.\deploy\Invoke-Deploy.ps1 -Component webapi -TargetNode WEBSERVER01 -BaseUrl http://WEBSERVER01

# Controller / Agent
.\deploy\Invoke-Deploy.ps1 -Component controller -TargetNode CONTROLLER01
.\deploy\Invoke-Deploy.ps1 -Component agent -TargetNode JVGR1 -ControllerNode CONTROLLER01
```

What happens, in order:

1. **Pre-deploy check** (`Invoke-PreDeployCheck.ps1`) — blocks if active sessions
   are running. Add `-Force` to override.
2. **Backup** — the current remote deployment is snapshotted to
   `publish\_backups\<component>-<node>\<timestamp>` **before** the destructive
   `robocopy /MIR`. This is what makes rollback possible.
3. **Deploy** — calls the matching `deploy-*.bat`.
4. **Smoke test** (`Invoke-SmokeTest.ps1`, webapi only) — on failure the script
   **automatically restores the pre-deploy snapshot**.

### Roll back

```powershell
# Restore the most recent snapshot
.\deploy\Invoke-Deploy.ps1 -Component webapi -TargetNode WEBSERVER01 -BaseUrl http://WEBSERVER01 -Rollback

# Restore a specific snapshot (folder name under publish\_backups\<component>-<node>)
.\deploy\Invoke-Deploy.ps1 -Component webapi -TargetNode WEBSERVER01 -Rollback -BackupName 20260626-141200
```

Rollback stops the IIS site (webapi), `robocopy /MIR` the backup back over the
remote folder, restarts the site, then re-runs the smoke test to confirm health.

> Backups accumulate under `publish\_backups\`. Prune old snapshots with
> `Invoke-LogCleanup.ps1` conventions or manually; keep at least the last known-good.

Startup **fails fast** if `Standalone` is declared while `ControllerProxyUrl` is
still set (the contradiction would otherwise silently fall back to proxying).

> The React WebClient is topology-agnostic: it uses same-origin relative URLs by
> default (`VITE_API_BASE_URL` empty), so the **same build** serves either host
> with no rebuild. See `TestController.WebClient/.env.example`.

---

## Agent Roster (Canonical Source of Truth)

The agent fleet is defined in **four** config files. They MUST stay in sync —
divergence (a missing agent or a mistyped host) is a silent production defect.
Agent names are matched **case-insensitively** in code, so casing is cosmetic;
the canonical convention is **UPPERCASE**.

| Agent | Host | Address |
|-------|------|---------|
| JVGR1  | JVGR1  | `http://JVGR1:5200`  |
| JVGR2  | JVGR2  | `http://JVGR2:5200`  |
| JVKPRI | JVKPRI | `http://JVKPRI:5200` |
| JVKBAK | JVKBAK | `http://JVKBAK:5200` |
| JVHIST | JVHIST | `http://JVHIST:5200` |

Files that must carry the identical roster:

1. `TestControllerGrpc/appsettings.json` (WPF host) — `Agents[]`
2. `TestController.WebApi/appsettings.json` (WebApi base) — `Agents[]`
3. `TestController.WebApi/appsettings.Production.json` — `Agents[]`
4. `TestAgentDisplay/appsettings.json` — `DefaultAgents[]` (URL-only schema)

When adding/removing an agent, update all four and diff them before deploying.

---

## Health Checks

### API Health Endpoint

```
GET /healthz/ready          → 200 OK = healthy
GET /api/health/diagnostics → extended system info (CPU, memory, agents, sessions)
```

### Agent Fleet Status

```
GET /api/agents → list all registered agents with health state
```

Check for agents with `status: "Unhealthy"` or `status: "CircuitOpen"`.

### Prometheus Metrics

```
GET /metrics → Prometheus scrape endpoint
```

---

## Agent Down / Unreachable

**Symptoms:** Agent shows "Offline" in Fleet panel; `ConsecutiveFailures > 0`.

**Diagnosis:**

1. Check agent service is running on the target machine:
   ```powershell
   Get-Service TestAgentGrpc -ComputerName <AgentNode>
   ```
2. Check network connectivity (port 5200 default):
   ```powershell
   Test-NetConnection -ComputerName <AgentNode> -Port 5200
   ```
3. Check agent crash logs:
   ```
   \\<AgentNode>\C$\TestAgent\logs\agent_crash.log
   ```

**Resolution:**

| Cause | Fix |
|-------|-----|
| Agent process crashed | Restart service: `Restart-Service TestAgentGrpc -ComputerName <Node>` |
| Firewall blocking gRPC port | Open port 5200 (or configured port) in Windows Firewall |
| DNS resolution failure | Verify hostname resolves; consider using IP in agent config |
| TLS certificate expired | Renew certificate; restart agent |

**Forced Channel Reset (from controller):**
```
POST /api/agents/{name}/reset
```

---

## Session Stuck / Orphaned

**Symptoms:** Session shows "Running" but no progress; agent shows idle.

**Diagnosis:**

1. Check active sessions:
   ```
   GET /api/execution/sessions
   ```
2. Check session's assigned agent is still online.
3. Review execution logs for the session ID.

**Resolution:**

1. **Abort the session** (graceful):
   ```
   POST /api/execution/sessions/{sessionId}/abort
   ```
2. **Force-cancel** (if abort doesn't respond):
   ```
   DELETE /api/execution/sessions/{sessionId}
   ```
3. If the issue persists after abort, the agent may need a channel reset.

---

## Lock Orphaned

**Symptoms:** Agent locked but no session is using it; cannot assign new work.

**Diagnosis:**

1. Check lock state:
   ```
   GET /api/agents/{name}/lock
   ```
2. Verify the claimed session ID still exists in active sessions.

**Resolution:**

- **Force-release the lock:**
  ```
  POST /api/agents/{name}/force-release
  ```
  ⚠️ Only use when confirmed no session is running on that agent.

- The `LockRecoveryService` automatically detects and releases orphaned locks on a configurable interval.

---

## Controller Restart

**Pre-restart checklist:**

1. Check for active sessions: `GET /api/execution/sessions`
2. If sessions are running, warn users or wait for completion.
3. Agents will detect controller loss after 3 failed heartbeats and log `[ControllerLost]`.

**Post-restart:**

- Agents will automatically re-register on next heartbeat cycle.
- Browser clients will reconnect via SignalR auto-reconnect (exponential backoff).
- Orphaned locks will be cleaned by `LockRecoveryService` within its scan interval.

---

## WebApi / IIS Issues

### Application Pool Crash

```powershell
# Check app pool state
Get-WebAppPoolState -Name "TestController"

# Restart
Restart-WebAppPool -Name "TestController"
```

### Port Conflict

Check if the configured port is in use:
```powershell
Get-NetTCPConnection -LocalPort 5001 | Select-Object OwningProcess
```

### Failed Startup

Check Windows Event Log:
```powershell
Get-WinEvent -LogName Application -MaxEvents 20 |
    Where-Object { $_.ProviderName -like '*ASP.NET*' -or $_.ProviderName -eq '.NET Runtime' }
```

---

## SignalR Disconnections

**Symptoms:** Dashboard shows "Reconnecting" or "Offline"; events not updating.

**Common causes:**

| Cause | Fix |
|-------|-----|
| WebSocket not supported by proxy | Enable WebSocket in IIS/reverse proxy |
| Connection idle timeout (proxy) | Increase idle timeout or configure keep-alive pings |
| Large broadcast backlog | Check 50+ agent limit; consider group-based broadcast |
| CORS misconfiguration | Verify `AllowedOrigins` in `appsettings.json` |

**Client-side recovery:** The WebClient automatically reconnects with jittered exponential backoff and resumes session groups.

---

## High Memory / TRX Parsing

**Symptoms:** WebApi memory usage spikes periodically.

**Diagnosis:**

1. Check memory via diagnostics endpoint.
2. Large TRX files (100MB+) load entirely into memory during parsing.

**Mitigation:**

- The TRX cache uses LRU eviction (500 entries max) with file-timestamp invalidation.
- Monitor for very large TRX files and consider splitting test assemblies.
- Restart the WebApi process if memory doesn't recover (potential large object heap fragmentation).

---

## Channel Reset Storm

**Symptoms:** Multiple agents resetting simultaneously during network outage; logs filled with reset messages.

**Cause:** Fleet-wide outage triggers auto-reset for all agents at the same failure threshold.

**Mitigation (built-in):** Auto-reset is rate-limited to max 1 reset per 30 seconds per agent. The system will log `Auto-reset skipped — cooldown active` when throttled.

**Manual recovery after outage:**
1. Wait for network to stabilize.
2. Check fleet status: `GET /api/agents`
3. For agents still showing unhealthy, trigger manual reset:
   ```
   POST /api/agents/{name}/reset
   ```

---

## Log Retention & Cleanup

Run the log cleanup script to purge old audit/execution logs:

```powershell
.\deploy\Invoke-LogCleanup.ps1 -RetentionDays 30
```

Default paths cleaned:
- Agent audit logs
- Execution session logs
- Crash dump logs

Schedule via Windows Task Scheduler for automated cleanup.

---

## Escalation Contacts

| Level | Scope | Contact |
|-------|-------|---------|
| L1 | Agent restart, lock release, basic connectivity | On-call QA engineer |
| L2 | Session recovery, controller restart, deployment | Platform team |
| L3 | Data corruption, security incidents, architecture | Engineering lead |

---

## Related Docs

- [Security Troubleshooting](TROUBLESHOOTING.md) — Auth/token/TLS issues
- [Deployment Guide](../deploy/README.md) — Deploy scripts and procedures
- [Architecture](ARCHITECTURE.md) — System design overview
