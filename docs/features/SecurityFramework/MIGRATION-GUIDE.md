# Security Framework Migration Guide

Guide for migrating existing TestController deployments to the multi-identity security framework.

---

## Overview

The security framework is **opt-in and backwards-compatible**. Existing deployments without a `Security` section in their configuration will operate in `AuthMode: None` (no authentication), preserving existing behavior.

---

## Migration Paths

### Path A: No Authentication → Domain Mode

**Scenario:** Existing deployment on a domain-joined machine, want to add AD-based security.

**Steps:**

1. **Identify AD groups** that will map to Admin and User roles:
   ```
   Admin: YOURDOMAIN\TestControllerAdmins
   User:  YOURDOMAIN\TestControllerUsers
   ```

2. **Add Security section** to `appsettings.json`:
   ```json
   {
     "Security": {
       "AuthMode": "Domain",
       "Domain": {
         "RequireDomain": "YOURDOMAIN",
         "AdminGroup": "YOURDOMAIN\\TestControllerAdmins",
         "UserGroup": "YOURDOMAIN\\TestControllerUsers",
         "FallbackToLocal": true
       }
     }
   }
   ```

3. **Ensure current operators are in the Admin group** — otherwise they'll lose access.

4. **Test with `FallbackToLocal: true`** — provides safety net if AD issues occur.

5. **Restart service** and verify:
   ```
   GET /api/security/status → mode: "Domain", isHealthy: true
   GET /api/security/whoami → role: "Admin" (for admins)
   ```

6. **Verify automation** — Windows-authenticated clients (WPF dashboard, CI agents) should work transparently via Negotiate auth.

---

### Path B: No Authentication → Token Mode

**Scenario:** Headless deployment, CI/CD integration, or non-Windows clients.

**Steps:**

1. **Add Security section**:
   ```json
   {
     "Security": {
       "AuthMode": "Token",
       "Token": {
         "TokenStore": "C:\\TestControllerService\\secrets.json",
         "TokenRotationDays": 30
       }
     }
   }
   ```

2. **Restart service** — first Admin token is auto-generated if store is empty.

3. **Create tokens** for each client:
   ```
   POST /api/tokens
   Body: { "name": "ci-pipeline", "role": "User" }
   → Response includes the bearer token (shown only once)
   ```

4. **Update clients** to include `Authorization: Bearer <token>` header.

5. **Verify:**
   ```
   GET /api/security/whoami
   Authorization: Bearer <token>
   → identity: "ci-pipeline", role: "User"
   ```

---

### Path C: Adding Transport Encryption (TLS)

**Scenario:** Encrypt gRPC traffic between Controller and Agents.

**Steps:**

1. **Obtain certificates:**
   - Server cert for Controller (installed in `LocalMachine\My`)
   - Optionally, client certs for mTLS

2. **Start with dual mode** (zero-downtime migration):
   ```json
   {
     "Security": {
       "Transport": {
         "GrpcMode": "PlaintextAndTls",
         "CertThumbprint": "A1B2C3...",
         "TlsPort": 5443
       }
     }
   }
   ```

3. **Restart Controller** — now accepts both plaintext (port 5200) and TLS (port 5443).

4. **Migrate agents one by one** to use TLS endpoint:
   ```json
   {
     "Agent": {
       "Listeners": {
         "Tls": { "Enabled": true, "Port": 5443, "CertThumbprint": "..." }
       }
     }
   }
   ```

5. **Verify all agents connected via TLS:**
   ```
   GET /api/security/readiness → transportMode: "PlaintextAndTls", certificateValid: true
   ```

6. **Switch to TLS-only** once all agents are migrated:
   ```json
   {
     "Security": {
       "Transport": {
         "GrpcMode": "TlsOnly",
         "CertThumbprint": "A1B2C3..."
       }
     }
   }
   ```

---

### Path D: Adding Rate Limiting

**Scenario:** Protect the API from abuse or runaway automation.

**Steps:**

1. **Add rate limit config** (already enabled by default):
   ```json
   {
     "Security": {
       "RateLimit": {
         "Enabled": true,
         "RequestsPerMinute": 60,
         "AdminRequestsPerMinute": 300
       }
     }
   }
   ```

2. **Monitor for 429s** after deployment — check audit logs for `RateLimited` events.

3. **Adjust limits** if legitimate automation is being throttled.

**Notes:**
- Rate limiting is per-user (based on authenticated identity)
- Health and status endpoints are never rate-limited
- Admin users automatically get the higher `AdminRequestsPerMinute` limit

---

## Rollback Procedure

If issues occur after enabling security:

1. **Quick rollback:** Set `AuthMode` back to `None`:
   ```json
   { "Security": { "AuthMode": "None" } }
   ```

2. **Restart service** — all requests will be treated as Admin again.

3. **Investigate** using audit logs and the readiness endpoint.

---

## Configuration Compatibility Matrix

| Feature | Minimum Config | Full Config |
|---------|---------------|-------------|
| Auth (None) | `"AuthMode": "None"` | N/A |
| Auth (Domain) | `AuthMode` + `RequireDomain` | + `AdminGroup`, `UserGroup`, `FallbackToLocal` |
| Auth (Token) | `AuthMode` only | + `TokenStore`, `TokenRotationDays` |
| TLS | `GrpcMode` + `CertThumbprint` | + mTLS, CA thumbprint, expiry warning |
| Rate Limit | None (defaults apply) | `Enabled`, per-minute limits |
| Audit | None (enabled by default) | `LogPath`, `RetentionDays` |

---

## Breaking Changes

**None.** The security framework is fully backwards-compatible:

- Omitting the `Security` section = `AuthMode: None` (existing behavior)
- All defaults are chosen to maintain existing functionality
- Rate limiting applies per-user with generous defaults (60 req/min)
- Health endpoints remain accessible without authentication

---

## Verification Checklist

After migration, confirm:

- [ ] `GET /api/security/status` returns expected mode
- [ ] `GET /api/security/readiness` shows all green (Admin access required)
- [ ] Existing automation passes without modification
- [ ] WPF Dashboard connects and shows user identity
- [ ] WebClient loads and displays current user in header
- [ ] Audit logs are being written to configured path
- [ ] Rate limiting doesn't affect normal usage patterns
