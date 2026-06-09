# Operations Runbook

Operational procedures for monitoring, troubleshooting, and recovering the TestAgentSolution platform.

---

## Table of Contents

1. [Health Checks](#health-checks)
2. [Agent Down / Unreachable](#agent-down--unreachable)
3. [Session Stuck / Orphaned](#session-stuck--orphaned)
4. [Lock Orphaned](#lock-orphaned)
5. [Controller Restart](#controller-restart)
6. [WebApi / IIS Issues](#webapi--iis-issues)
7. [SignalR Disconnections](#signalr-disconnections)
8. [High Memory / TRX Parsing](#high-memory--trx-parsing)
9. [Channel Reset Storm](#channel-reset-storm)
10. [Log Retention & Cleanup](#log-retention--cleanup)
11. [Escalation Contacts](#escalation-contacts)

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
