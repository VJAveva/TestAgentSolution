# Security Framework Deployment Guide

This guide covers deploying the TestController multi-identity security framework in each supported authentication mode.

---

## Prerequisites

- Windows Server 2019+ or Windows 10/11
- .NET 10.0 Runtime installed
- Network connectivity between Controller and Agent machines
- Administrative access to the deployment machine

---

## 1. Authentication Modes

### 1.1 None Mode (Development / Trusted Network)

**Use when:** Development, testing, or fully trusted single-machine deployments.

**Configuration (`appsettings.json`):**
```json
{
  "Security": {
    "AuthMode": "None"
  }
}
```

**Behavior:** All requests are treated as authenticated Admin. No credentials required.

**⚠️ Warning:** Never use in production environments exposed to untrusted networks.

---

### 1.2 Domain Mode (Active Directory)

**Use when:** Enterprise environments with AD infrastructure.

**Prerequisites:**
- Controller machine joined to the domain
- AD groups created for Admin and User roles
- Service account with domain read permissions (for group lookups)

**Configuration:**
```json
{
  "Security": {
    "AuthMode": "Domain",
    "Domain": {
      "RequireDomain": "YOURDOMAIN",
      "AdminGroup": "YOURDOMAIN\\TestControllerAdmins",
      "UserGroup": "YOURDOMAIN\\TestControllerUsers",
      "FallbackToLocal": true,
      "GroupCacheMinutes": 10
    }
  }
}
```

**Steps:**
1. Create AD groups: `TestControllerAdmins` and `TestControllerUsers`
2. Add appropriate users to each group
3. Set `RequireDomain` to your AD domain short name
4. Deploy configuration and restart the service
5. Verify with `GET /api/security/status` — should show `mode: "Domain"`, `isHealthy: true`

**Browser authentication:** Uses Windows Negotiate (Kerberos/NTLM). No login prompt for domain-joined clients.

---

### 1.3 Local Mode (Workgroup / SAM)

**Use when:** Workgroup environments without AD, or single-machine deployments needing access control.

**Configuration:**
```json
{
  "Security": {
    "AuthMode": "Local",
    "Local": {
      "AdminGroup": ".\\Administrators",
      "RequireHttps": true,
      "MinPasswordLength": 12
    }
  }
}
```

**Steps:**
1. Create local groups or use built-in groups (e.g., `.\\Administrators`)
2. Add users to appropriate local groups
3. Deploy configuration and restart
4. Verify: `GET /api/security/status`

---

### 1.4 Token Mode (Bearer Token)

**Use when:** Service-to-service communication, CI/CD pipelines, or environments without Windows auth.

**Configuration:**
```json
{
  "Security": {
    "AuthMode": "Token",
    "Token": {
      "TokenStore": "C:\\TestControllerService\\secrets.json",
      "TokenRotationDays": 30,
      "EnforceHttps": true
    }
  }
}
```

**Steps:**
1. Start the service with Token mode configured
2. Use the Admin bootstrap token (first run) to create user tokens via `POST /api/tokens`
3. Distribute tokens to clients securely
4. Clients include `Authorization: Bearer <token>` header on requests
5. Rotate tokens before expiry using `POST /api/tokens/{name}/rotate`

**Token Management Endpoints (Admin only):**
- `GET /api/tokens` — List all tokens
- `POST /api/tokens` — Create a new token
- `DELETE /api/tokens/{name}` — Revoke a token
- `POST /api/tokens/{name}/rotate` — Rotate a token

---

## 2. Transport Security (TLS/mTLS)

### 2.1 No Encryption (Default)

```json
{
  "Security": {
    "Transport": {
      "GrpcMode": "Plaintext"
    }
  }
}
```

### 2.2 Dual Mode (Migration)

Run both plaintext and TLS simultaneously while migrating agents:

```json
{
  "Security": {
    "Transport": {
      "GrpcMode": "PlaintextAndTls",
      "CertThumbprint": "A1B2C3...",
      "TlsPort": 5443,
      "CertExpiryWarningDays": 30
    }
  }
}
```

### 2.3 TLS Only (Production)

```json
{
  "Security": {
    "Transport": {
      "GrpcMode": "TlsOnly",
      "CertThumbprint": "A1B2C3...",
      "RequireMutualTls": true,
      "TrustedCaThumbprint": "D4E5F6...",
      "TlsPort": 5443
    }
  }
}
```

**Certificate Installation:**
1. Install server cert in `LocalMachine\My` certificate store
2. Note the thumbprint and configure `CertThumbprint`
3. For mTLS: install CA cert and configure `TrustedCaThumbprint`
4. Grant the service account read access to the private key

---

## 3. Rate Limiting

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

- **Per-user:** Each authenticated user gets their own rate window
- **Admin exemption:** Admin users get `AdminRequestsPerMinute` (default 300)
- **Health endpoints:** Exempt from rate limiting (`/healthz/*`, `/api/security/status`)
- **429 responses:** Include `Retry-After` header with seconds until window resets

---

## 4. Audit Logging

```json
{
  "Security": {
    "Audit": {
      "Enabled": true,
      "RetentionDays": 90,
      "LogPath": "C:\\TestControllerService\\Logs"
    }
  }
}
```

Audit logs capture:
- Authentication events (success/failure)
- Authorization denials (403 responses)
- Admin actions (token management, deployment, cancellation)
- Rate limit violations

---

## 5. Verification

After deployment, verify the security configuration:

1. **Status check:** `GET /api/security/status` (anonymous access)
   - Confirms active mode and health

2. **Readiness check:** `GET /api/security/readiness` (Admin required)
   - Comprehensive diagnostic: auth health, cert validity, rate limit config, audit status

3. **Identity check:** `GET /api/security/whoami` (authenticated)
   - Confirms resolved identity and role

4. **Capabilities check:** `GET /api/security/capabilities` (authenticated)
   - Shows effective permissions for the current user

---

## 6. Firewall & Network

| Port | Protocol | Purpose |
|------|----------|---------|
| 5000 | HTTP | WebAPI (development) |
| 5001 | HTTPS | WebAPI (production) |
| 5200 | HTTP/2 | gRPC plaintext |
| 5443 | HTTP/2+TLS | gRPC encrypted |

Ensure these ports are open between Controller and Agent machines.

---

## 7. Service Account Requirements

| Mode | Requirement |
|------|-------------|
| None | No special permissions |
| Domain | Domain read access (group membership queries) |
| Local | Local SAM read access |
| Token | File system access to token store path |
| TLS | Certificate private key read access |
