# Security Framework Troubleshooting Runbook

Quick-reference guide for diagnosing and resolving common security issues.

---

## Diagnostic Endpoints

| Endpoint | Auth | Purpose |
|----------|------|---------|
| `GET /api/security/status` | Anonymous | Active mode, health |
| `GET /api/security/readiness` | Admin | Full diagnostic report |
| `GET /api/security/whoami` | User | Current identity/role |
| `GET /api/security/capabilities` | User | Effective permissions |
| `GET /healthz/ready` | Anonymous | Infrastructure health |

---

## Common Issues

### 1. HTTP 401 — Unauthenticated

**Symptoms:** All requests return 401 Unauthorized.

**Diagnosis:**
```
GET /api/security/status
→ Check "mode" and "isHealthy" fields
```

**Causes & Fixes:**

| Cause | Fix |
|-------|-----|
| Domain mode, client not on domain | Join domain or switch to Token/Local mode |
| Token mode, missing/invalid `Authorization` header | Include `Authorization: Bearer <token>` |
| Token expired or revoked | Rotate token: `POST /api/tokens/{name}/rotate` |
| Negotiate auth failing (Kerberos ticket issues) | Run `klist purge` on client, re-authenticate |

---

### 2. HTTP 403 — Forbidden

**Symptoms:** Authenticated user gets 403 on specific endpoints.

**Diagnosis:**
```
GET /api/security/capabilities
→ Check which capabilities are false for your role
```

**Causes & Fixes:**

| Cause | Fix |
|-------|-----|
| User attempting Admin-only operation | Use an Admin account or request elevation |
| User not in required AD group | Add user to `TestControllerAdmins` / `TestControllerUsers` group |
| Group membership cached | Wait for cache expiry (`GroupCacheMinutes`) or restart service |
| Token has User scope, needs Admin | Create a new Admin-scoped token |

---

### 3. HTTP 429 — Rate Limited

**Symptoms:** Requests throttled with `Retry-After` header.

**Diagnosis:**
- Check `Retry-After` response header for wait time
- Review `Security:RateLimit:RequestsPerMinute` configuration

**Causes & Fixes:**

| Cause | Fix |
|-------|-----|
| Automation exceeding 60 req/min | Increase `RequestsPerMinute` in config |
| Burst of requests from polling | Add backoff/jitter to client polling |
| Single user consuming all capacity | Investigate client; per-user limits protect others |

**Configuration adjustment:**
```json
{
  "Security": {
    "RateLimit": {
      "RequestsPerMinute": 120,
      "AdminRequestsPerMinute": 600
    }
  }
}
```

---

### 4. AD Unreachable (Domain Mode)

**Symptoms:** Status shows `isHealthy: false`, users cannot authenticate.

**Diagnosis:**
```
GET /api/security/status
→ "diagnostic" field shows AD connectivity error

GET /api/security/readiness (if accessible)
→ "authDiagnostic" shows details
```

**Fixes:**
1. Verify network connectivity to domain controllers: `nltest /dsgetdc:YOURDOMAIN`
2. Check DNS resolution: `nslookup YOURDOMAIN`
3. If `FallbackToLocal: true` is configured, service falls back to Local auth automatically
4. Restart the service after resolving network issues

---

### 5. Certificate Expired / Invalid (TLS Mode)

**Symptoms:** gRPC connections fail, readiness shows cert issues.

**Diagnosis:**
```
GET /api/security/readiness
→ "certificateValid": false
→ "issues" array describes the problem
```

**Fixes:**

| Issue | Fix |
|-------|-----|
| Cert not found in store | Install cert: `Import-Certificate -CertStoreLocation Cert:\LocalMachine\My` |
| Cert expired | Renew cert, update thumbprint in config, restart |
| Private key not accessible | Grant service account read access via cert MMC |
| Cert about to expire (warning) | Plan renewal within `CertExpiryWarningDays` |

**Certificate renewal steps:**
1. Obtain new certificate from CA
2. Install in `LocalMachine\My` store
3. Update `Security:Transport:CertThumbprint` in `appsettings.json`
4. Restart the service
5. Verify: `GET /api/security/readiness` → `certificateValid: true`

---

### 6. Token Store Issues (Token Mode)

**Symptoms:** Token operations fail, 500 errors on `/api/tokens`.

**Fixes:**

| Issue | Fix |
|-------|-----|
| Token store file missing | Service creates on first token creation |
| DPAPI encryption fails | Ensure service runs under consistent identity (not SYSTEM switching) |
| File permissions | Grant service account read/write to token store path |
| Corrupted store | Backup, delete store file, re-create tokens |

---

### 7. Startup Failures

**Symptoms:** Service fails to start, logs show `Configuration validation failed`.

**Diagnosis:** Check Windows Event Log or service log file for the exact error.

**Common startup errors:**

| Error | Fix |
|-------|-----|
| "No agents configured" | Add at least one agent entry to `Agents` section |
| "VocabularyFile path not configured" | Set `VocabularyFile` path in config |
| "TLS mode requires a certificate" | Configure `CertThumbprint` or `CertFilePath` |
| "RequireDomain must be specified" | Set `Security:Domain:RequireDomain` |

---

### 8. Audit Log Issues

**Symptoms:** Audit events not being written.

**Fixes:**

| Issue | Fix |
|-------|-----|
| Audit disabled | Set `Security:Audit:Enabled: true` |
| Log path doesn't exist | Create directory or fix `Security:Audit:LogPath` |
| Disk full | Implement log rotation or increase disk space |
| No permissions to write | Grant service account write access to audit path |

---

## Audit Log Analysis

### View recent authentication failures:
```powershell
Get-Content "C:\TestControllerService\Logs\security-audit*.log" |
  Select-String "AuthenticationFailed" |
  Select-Object -Last 20
```

### Count requests per user (last hour):
```powershell
Get-Content "C:\TestControllerService\Logs\security-audit*.log" |
  Where-Object { $_ -match (Get-Date).ToString("yyyy-MM-dd HH") } |
  Select-String -Pattern '"user":"([^"]+)"' |
  ForEach-Object { $_.Matches[0].Groups[1].Value } |
  Group-Object | Sort-Object Count -Descending
```

### Find rate-limited users:
```powershell
Get-Content "C:\TestControllerService\Logs\security-audit*.log" |
  Select-String "RateLimited" |
  Select-Object -Last 10
```

---

## Escalation Path

1. **Self-service:** Check `/api/security/status` and `/api/security/readiness`
2. **Admin intervention:** Review audit logs, adjust config, restart service
3. **Infrastructure:** AD/DNS/certificate issues require domain admin or PKI team
4. **Development:** Unexpected 500 errors with stack traces → file a bug with logs
