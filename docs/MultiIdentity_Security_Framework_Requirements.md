# Multi-Identity Security Framework

**Feature Requirements & System Design Document**

---

| Field | Value |
|---|---|
| **Document version** | 1.0 |
| **Status** | Draft for review |
| **Owner** | Vinod Kumar (QA Engineering, AVEVA) |
| **Target product** | TestAgentSolution |
| **Affected projects** | TestController.Api, TestControllerGrpc, TestControllerGrpc.Core, TestAgentGrpc |
| **Estimated duration** | 8 weeks (phased) |

---

## 1. Executive Summary

TestAgentSolution today authenticates a single user identity — the AVEVA domain administrator — and runs all test automation under that account. While this works for the primary use case, the QA team needs to manually validate four additional user identity scenarios for every release, costing significant engineering hours and creating coverage gaps.

This document defines the requirements, system design, and rollout plan for a unified security framework that enables automation under all five user scenarios while closing five known security gaps (P0 authentication, P0 authorization, P1 transport encryption, P1 command policy enforcement, P2 rate limiting).

The proposed solution uses a **mode-based authentication strategy** where the controller selects one of three authentication modes (Domain, Local, Token) based on the deployment environment. All five user scenarios are supported through this single, configurable framework — no separate codebases or branches.

---

## 2. Problem Statement

### 2.1 Current State

The platform supports exactly one identity configuration:

- Controller and agents run as a domain administrator account (e.g., `XYZ\testagent.svc`)
- API authentication is bypassed entirely — anyone on the corporate network can call any endpoint
- Authorization is based on a client-supplied header (`X-Source: WPF`) that can be trivially forged
- Agent communication is plaintext HTTP/2 — credentials and commands transit unencrypted
- Command policy operates in "audit only" mode — dangerous commands are logged but executed anyway

### 2.2 Business Impact

| Impact area | Today | Cost |
|---|---|---|
| **Test coverage** | Only 1 of 5 customer scenarios automated | ~120 hours/release of manual testing |
| **Security posture** | Multiple P0/P1 gaps in production deployment | Audit findings, customer concerns |
| **Customer adoption** | Customers in workgroup/non-AD environments cannot use the product | Lost revenue opportunity |
| **Compliance** | Plaintext credential transit fails security review | Blocks enterprise deals |

### 2.3 Target State

The platform supports all five identity scenarios automatically, with security controls active and verified:

1. Admin domain user (current) — preserved with no behavior change
2. Admin local user — automated end-to-end
3. Workgroup user — automated with token-based identity
4. Non-admin domain user — automated with restricted capabilities
5. Non-admin local user — automated with restricted capabilities

---

## 3. Stakeholders

| Role | Responsibility | Approval needed |
|---|---|---|
| **QA Engineering Lead** | Functional acceptance | Yes |
| **AVEVA Security Team** | Security review and sign-off | Yes |
| **Customer Success** | Customer deployment validation | Yes |
| **Product Manager** | Scope and priority decisions | Yes |
| **DevOps / IT** | AD group provisioning, certificate management | Notification |
| **Documentation Team** | User-facing deployment guides | Deliverable consumer |

---

## 4. Scope

### 4.1 In Scope

- Authentication framework supporting three modes (Domain, Local, Token)
- Authorization policies for admin-level and user-level actions
- Ownership-based access control on per-session operations
- Transport-layer encryption (mTLS) for controller-to-agent gRPC communication
- Enforcement-mode command policy with allowlist management
- Rate limiting on the public API
- Auditing of all authentication and authorization decisions
- Operator UI for inspecting active mode and user capabilities
- Configuration migration tooling for existing deployments

### 4.2 Out of Scope

- Multi-tenancy (multiple customer organizations on one controller)
- Single sign-on integration (SAML, OAuth)
- Encrypted storage of test artifacts at rest
- Network-level segmentation (firewalls, VLANs)
- Hardware security module (HSM) integration for key storage
- User account provisioning workflows
- Penetration testing engagement

### 4.3 Assumptions

- The controller machine resides in the XYZ Active Directory domain
- AD groups `XYZ\TestAdmins` and `XYZ\TestUsers` exist and are populated
- Certificate authority is available (internal CA preferred, self-signed acceptable for development)
- Agent machines have network connectivity to the controller on a known port
- The current test automation suite is the regression baseline for "no breakage"

### 4.4 Constraints

- Must not break the existing admin domain user scenario
- Must support side-by-side operation during rollout (old and new agents on the same controller)
- Configuration must be expressible in standard `appsettings.json` — no GUI-only setup
- All security decisions must be logged for audit
- No external SaaS dependencies (everything must work in air-gapped labs)

---

## 5. Functional Requirements

### 5.1 Five User Identity Scenarios

The system must support test automation under all five scenarios. Each row describes the expected behavior:

#### Scenario 1: Admin Domain User (Primary)

**Identity:** Domain user who is a member of `XYZ\TestAdmins` AD group

**Expected capabilities:**
- All read operations
- Trigger any pipeline
- Cancel any session (own or others')
- Force-release any agent lock
- Add, edit, remove agents in registry
- Modify WatchList and Template configurations
- View all logs and diagnostics

#### Scenario 2: Admin Local User

**Identity:** Local Windows account in `Administrators` group on the controller machine

**Expected capabilities:** Same as Scenario 1, with identity established via local-account challenge instead of AD lookup

#### Scenario 3: Workgroup User

**Identity:** User on a machine that is not joined to a domain; authenticates via pre-shared token

**Expected capabilities:**
- All read operations
- Trigger pipelines on agents associated with their token
- Cancel own sessions only
- No admin operations

#### Scenario 4: Non-Admin Domain User

**Identity:** Domain user who is a member of `XYZ\TestUsers` but not `XYZ\TestAdmins`

**Expected capabilities:**
- All read operations on dashboards and results
- Trigger pipelines on agents they are authorized for
- Cancel own sessions only
- Cannot force-release locks, unregister agents, or modify WatchList

#### Scenario 5: Non-Admin Local User

**Identity:** Local Windows account not in `Administrators` group

**Expected capabilities:** Same restricted capabilities as Scenario 4, with local-account identity

### 5.2 Authentication Requirements

| ID | Requirement |
|---|---|
| AUTH-01 | Controller must support three authentication modes: Domain, Local, Token |
| AUTH-02 | Active mode is configured in `appsettings.json` and cannot be changed at runtime |
| AUTH-03 | Domain mode uses Windows Integrated Authentication (Negotiate/Kerberos/NTLM) |
| AUTH-04 | Local mode uses Windows account challenge against the local SAM database |
| AUTH-05 | Token mode uses pre-shared bearer tokens stored encrypted in `secrets.json` |
| AUTH-06 | Authentication failures must return HTTP 401 with no information disclosure |
| AUTH-07 | Authenticated user identity must be propagated to all downstream operations |
| AUTH-08 | Authentication must fall back to Local mode if Domain mode is configured but AD is unreachable |
| AUTH-09 | Anonymous access is permitted only on the health endpoint (`/api/health`) |
| AUTH-10 | All authentication decisions must be logged with user identity and outcome |

### 5.3 Authorization Requirements

| ID | Requirement |
|---|---|
| AUTHZ-01 | Authorization policies are evaluated after authentication succeeds |
| AUTHZ-02 | The `Admin` policy requires membership in the configured admin group |
| AUTHZ-03 | The `User` policy requires membership in the user group OR the admin group |
| AUTHZ-04 | The `Anonymous` policy permits unauthenticated access (health endpoints only) |
| AUTHZ-05 | Ownership-based checks apply to per-session operations: only the session owner OR an admin may cancel, modify, or interrogate a session |
| AUTHZ-06 | Authorization failures must return HTTP 403 with a non-revealing message |
| AUTHZ-07 | Authorization decisions must be logged with policy name, user, and outcome |
| AUTHZ-08 | The `X-Source` header MUST NOT be used for authorization (currently abused) |

### 5.4 Transport Security Requirements

| ID | Requirement |
|---|---|
| TLS-01 | All controller-to-agent gRPC communication must support TLS 1.2 or higher |
| TLS-02 | Server certificates must be validated against a configured trust root |
| TLS-03 | Client certificates (mutual TLS) must be supported for agent-to-controller authentication |
| TLS-04 | A dual-listener period is required: agents simultaneously accept plaintext (5200) and TLS (5443) during migration |
| TLS-05 | After migration completion, plaintext listener is disabled by configuration flag |
| TLS-06 | Certificate rotation must not require agent code changes |
| TLS-07 | TLS errors must be logged with sufficient detail for diagnosis (cert thumbprint, issuer) |

### 5.5 Command Policy Requirements

| ID | Requirement |
|---|---|
| CMD-01 | Command policy must operate in one of two modes: AuditOnly (current) or Enforce |
| CMD-02 | In Enforce mode, commands matching any blocklist pattern are rejected before execution |
| CMD-03 | In Enforce mode, commands outside the allowlist must be rejected unless the user is in the admin group |
| CMD-04 | Allowlist must support patterns for common test operations (PowerShell, batch, MSI installer, robocopy) |
| CMD-05 | Rejected commands must return a specific error code to the controller for telemetry |
| CMD-06 | Rejection events must be logged with full command text (after secret redaction) |
| CMD-07 | Allowlist must be editable without rebuilding the agent binary |

### 5.6 Rate Limiting Requirements

| ID | Requirement |
|---|---|
| RATE-01 | API endpoints must enforce a per-user rate limit (default: 60 requests/minute) |
| RATE-02 | The rate limit must be configurable per endpoint group |
| RATE-03 | Rate limit violations must return HTTP 429 with a `Retry-After` header |
| RATE-04 | Health endpoints are exempt from rate limiting |
| RATE-05 | Admin users may have a higher rate limit than standard users |

### 5.7 Auditing Requirements

| ID | Requirement |
|---|---|
| AUDIT-01 | Every authentication attempt must be logged (success and failure) |
| AUDIT-02 | Every authorization decision on sensitive operations must be logged |
| AUDIT-03 | All admin actions (force-release, unregister, modify) must be audited with timestamp, user, and target |
| AUDIT-04 | Audit logs must be written to a separate sink from application logs |
| AUDIT-05 | Audit log retention must be at least 90 days |
| AUDIT-06 | Audit logs must include enough context to reconstruct the action (request ID, session ID) |

### 5.8 Operator Experience Requirements

| ID | Requirement |
|---|---|
| OP-01 | The WPF controller must display the active authentication mode in the UI |
| OP-02 | The WPF controller must display the current user's identity and effective capabilities |
| OP-03 | A readiness diagnostic must indicate the status of AD connectivity, certificate validity, and policy mode |
| OP-04 | Configuration errors at startup must produce clear error messages, not stack traces |
| OP-05 | The WebClient must display the current user's identity in the header |

---

## 6. Non-Functional Requirements

### 6.1 Performance

| ID | Requirement | Target |
|---|---|---|
| NFR-PERF-01 | Authentication overhead per request | < 5 ms |
| NFR-PERF-02 | Authorization policy evaluation | < 1 ms |
| NFR-PERF-03 | mTLS handshake | < 50 ms on cold connection |
| NFR-PERF-04 | Command policy evaluation | < 1 ms per command |

### 6.2 Availability

| ID | Requirement |
|---|---|
| NFR-AVAIL-01 | If AD is unavailable, the system must fall back to Local mode within 10 seconds |
| NFR-AVAIL-02 | Certificate expiry must produce a warning 30 days in advance |
| NFR-AVAIL-03 | Rate limit exhaustion must not affect other users |

### 6.3 Maintainability

| ID | Requirement |
|---|---|
| NFR-MAINT-01 | All authentication code must be in one module to facilitate review |
| NFR-MAINT-02 | All authorization policies must be expressed declaratively, not in controller code |
| NFR-MAINT-03 | Changing the allowlist must not require recompilation |
| NFR-MAINT-04 | Configuration schema must be documented in a JSON schema file |

### 6.4 Backwards Compatibility

| ID | Requirement |
|---|---|
| NFR-COMPAT-01 | Existing automation under Scenario 1 (admin domain) must work without configuration changes |
| NFR-COMPAT-02 | Old agent binaries (without mTLS) must continue to work during the migration window |
| NFR-COMPAT-03 | The `X-Source` header must continue to be accepted for telemetry only (no auth use) |

---

## 7. System Architecture

### 7.1 Component Overview

The security framework spans four components of TestAgentSolution. Each component takes a specific responsibility:

| Component | Responsibility |
|---|---|
| **TestController.Api** | Authenticates HTTP/SignalR clients; enforces authorization policies; rate limiting |
| **TestControllerGrpc** (WPF) | Displays active mode and capabilities; calls API as authenticated user |
| **TestControllerGrpc.Core** | Stamps ownership on sessions; ownership re-verification on cancel |
| **TestAgentGrpc** | Verifies controller via mTLS; evaluates command policy in Enforce mode |

### 7.2 Request Flow

A request to trigger a pipeline traverses these layers:

```
[Client]
   |  Negotiate header (NTLM/Kerberos) OR Bearer token OR Client cert
   v
[Authentication middleware]                      ← layer 1
   |  Validates credential against AD/local/token store
   v
[Rate limiter]                                   ← layer 2
   |  Enforces per-user request budget
   v
[Authorization policy gate]                      ← layer 3
   |  Checks AD group membership against policy
   v
[Controller endpoint]                            ← layer 4
   |  Stamps session.OwnerSid on creation
   v
[Pipeline executor]
   |  Opens gRPC channel to agent
   v
[mTLS handshake]                                 ← layer 5
   |  Mutual certificate validation
   v
[Agent service]
   |  Receives command + caller identity claim
   v
[Command policy evaluator]                       ← layer 6
   |  Checks against allowlist/blocklist in Enforce mode
   v
[Process execution]
   |  Runs as agent service account
   v
[Result returned through reverse chain]
```

### 7.3 The Three Authentication Modes

Each mode is a complete implementation; the controller picks one at startup based on configuration. The mode determines how identity is established but not how authorization is enforced — that is uniform across modes.

#### 7.3.1 Domain Mode (Default)

**When to use:** Controller and clients are joined to the same AD domain (the AVEVA environment)

**Mechanism:**
- HTTP API uses ASP.NET Core's `Negotiate` authentication handler
- SignalR connections inherit HTTP authentication
- User identity is the Windows Security Identifier (SID)
- Group membership is queried via `WindowsIdentity.Groups`

**Configuration:**
```json
{
  "Security": {
    "AuthMode": "Domain",
    "RequireDomain": "XYZ",
    "AdminGroup": "XYZ\\TestAdmins",
    "UserGroup": "XYZ\\TestUsers",
    "FallbackToLocal": true
  }
}
```

#### 7.3.2 Local Mode

**When to use:** Controller is on a workgroup machine or fallback when AD is unreachable

**Mechanism:**
- HTTP API uses Basic Auth over HTTPS (with strong password policy enforced)
- Credentials validated against the local Windows account database (SAM)
- Group membership via local `Administrators` group

**Configuration:**
```json
{
  "Security": {
    "AuthMode": "Local",
    "AdminGroup": ".\\Administrators",
    "RequireHttps": true,
    "MinPasswordLength": 12
  }
}
```

#### 7.3.3 Token Mode

**When to use:** Mixed environments, customer labs without AD, or for service-to-service automation

**Mechanism:**
- HTTP API uses Bearer token authentication
- Tokens are pre-shared, stored encrypted in `secrets.json` (DPAPI-protected)
- Each token is associated with a role (Admin / User) and optionally a set of allowed agents
- Token rotation supported via API

**Configuration:**
```json
{
  "Security": {
    "AuthMode": "Token",
    "TokenStore": "secrets.json",
    "TokenRotationDays": 30,
    "EnforceHttps": true
  }
}
```

### 7.4 Authorization Policy Model

Three policies govern access. Each endpoint declares which policy applies:

| Policy | Used by | Granted to |
|---|---|---|
| **Anonymous** | `/api/health`, `/api/health/logs` | Anyone, even unauthenticated |
| **User** | Read operations, trigger pipeline, cancel own session | Members of admin OR user group; valid tokens |
| **Admin** | Force-release lock, cancel any session, unregister agent, modify WatchList | Members of admin group only |

Ownership-based checks are layered on top of policies:

```
Effective_Permission = Policy_Permission AND (Is_Owner OR Is_Admin)
```

This means a non-admin user can cancel their own session (policy allows, ownership matches) but cannot cancel someone else's (policy allows but ownership fails).

### 7.5 Capability Matrix per User Type

| Capability | Admin Domain | Admin Local | Workgroup | Non-Admin Domain | Non-Admin Local |
|---|---|---|---|---|---|
| Read dashboards | ✓ | ✓ | ✓ | ✓ | ✓ |
| Read results | ✓ | ✓ | ✓ | ✓ | ✓ |
| Trigger pipeline | ✓ | ✓ | ✓ (token's agents) | ✓ (allowed agents) | ✓ (allowed agents) |
| Cancel own session | ✓ | ✓ | ✓ | ✓ | ✓ |
| Cancel any session | ✓ | ✓ | ✗ | ✗ | ✗ |
| Force-release lock | ✓ | ✓ | ✗ | ✗ | ✗ |
| Unregister agent | ✓ | ✓ | ✗ | ✗ | ✗ |
| Modify WatchList | ✓ | ✓ | ✗ | ✗ | ✗ |
| Revert VM snapshot | ✓ | ✓ | Configured | ✗ | ✗ |
| View audit log | ✓ | ✓ | ✗ | ✗ | ✗ |

### 7.6 Transport Encryption Architecture

#### 7.6.1 Certificate Topology

Three certificates are involved:

| Certificate | Subject | Purpose | Issuer |
|---|---|---|---|
| **Root CA** | TestAgentSolution Root CA | Trust anchor for all components | Internal CA or self-generated |
| **Controller cert** | CN=controller.xyz.local | Server cert for gRPC + Web API | Root CA |
| **Agent cert** | CN=jvgr1.xyz.local, etc. | Client cert for mTLS to controller | Root CA |

#### 7.6.2 Migration Approach (Dual-Listener)

To roll out TLS without downtime, agents listen on both ports during the transition:

```
Migration Phase 1:  Agent listens on  5200 (plaintext)  + 5443 (TLS)
                    Controller connects to              5200 (plaintext)
                    
Migration Phase 2:  Agent listens on  5200 (plaintext)  + 5443 (TLS)
                    Controller connects to              5443 (TLS)
                    Old controllers (if any) still use  5200
                    
Migration Phase 3:  Agent listens on                       5443 (TLS only)
                    Controller connects to              5443 (TLS)
                    Plaintext 5200 disabled
```

Phase transitions are gated by a configuration flag and verified before advancing.

### 7.7 Command Policy Architecture

The agent's `CommandPolicyEvaluator` operates in three logical stages:

```
1. Pattern matching:    Regex against known dangerous patterns (rm -rf, format,
                        shell metacharacters, etc.) — always blocks in Enforce mode
                        
2. Allowlist check:     Command's executable + arguments matched against
                        configured allowlist patterns — required in Enforce mode
                        unless caller is admin
                        
3. Path validation:     Working directory and any file paths in arguments
                        are checked against allowed roots — prevents
                        directory traversal
```

The allowlist is loaded from `commandpolicy.json` on the agent at startup. It can be reloaded without restart via a signal RPC.

### 7.8 Auditing Architecture

Audit events are emitted to a dedicated Serilog sink (separate from application logs):

| Event type | Fields |
|---|---|
| **Authentication** | timestamp, mode, identity, outcome, source IP |
| **Authorization** | timestamp, user, policy, resource, decision |
| **Admin action** | timestamp, user, action type, target, parameters |
| **Command rejection** | timestamp, user, agent, command (redacted), reason |
| **TLS handshake** | timestamp, peer, outcome, cipher suite |

Audit log location: `C:\TestControllerService\Logs\audit-{date}.log` with 90-day retention.

---

## 8. Configuration Schema

### 8.1 Controller-Side `appsettings.json`

```json
{
  "Security": {
    "AuthMode": "Domain",
    "Domain": {
      "RequireDomain": "XYZ",
      "AdminGroup": "XYZ\\TestAdmins",
      "UserGroup": "XYZ\\TestUsers",
      "FallbackToLocal": true,
      "GroupCacheMinutes": 10
    },
    "Local": {
      "AdminGroup": ".\\Administrators",
      "RequireHttps": true
    },
    "Token": {
      "TokenStore": "secrets.json",
      "TokenRotationDays": 30
    },
    "Transport": {
      "GrpcMode": "PlaintextAndTls",
      "CertThumbprint": "ABCDEF...",
      "RequireMutualTls": false
    },
    "RateLimit": {
      "Enabled": true,
      "RequestsPerMinute": 60,
      "AdminRequestsPerMinute": 300
    },
    "Audit": {
      "Enabled": true,
      "RetentionDays": 90,
      "LogPath": "C:\\TestControllerService\\Logs"
    }
  }
}
```

### 8.2 Agent-Side `appsettings.json`

```json
{
  "Agent": {
    "ServiceAccount": "XYZ\\testagent.svc",
    "Listeners": {
      "Plaintext": { "Enabled": true, "Port": 5200 },
      "Tls": { "Enabled": true, "Port": 5443, "CertThumbprint": "..." }
    },
    "CommandPolicy": {
      "Mode": "Enforce",
      "AllowlistPath": "commandpolicy.json",
      "BlockOnUnknown": true
    },
    "Audit": {
      "Enabled": true,
      "LogPath": "C:\\TestAgentService\\Logs"
    }
  }
}
```

### 8.3 Command Policy `commandpolicy.json`

```json
{
  "Allowlist": [
    { "Command": "powershell.exe", "ArgPattern": "^-NoProfile -File [\"']?[A-Za-z]:\\\\.+\\.ps1[\"']?( .*)?$" },
    { "Command": "cmd.exe", "ArgPattern": "^/c [\"']?[A-Za-z]:\\\\.+\\.bat[\"']?( .*)?$" },
    { "Command": "msiexec.exe", "ArgPattern": "^/i [\"']?[A-Za-z]:\\\\.+\\.msi[\"']?( .*)?$" },
    { "Command": "robocopy.exe", "ArgPattern": "^[\"']?[A-Za-z]:\\\\[^\"']+[\"']? [\"']?[A-Za-z]:\\\\[^\"']+[\"']?( .*)?$" }
  ],
  "Blocklist": [
    { "Pattern": "(format\\s+[a-z]:)|(\\brm\\b.*-rf)" },
    { "Pattern": "[;&|]\\s*(rm|del|format|shutdown)" }
  ],
  "AllowedPathRoots": ["C:\\TestArtifacts", "C:\\Builds"]
}
```

---

## 9. Implementation Plan

The work is divided into seven phases. Phases 1-3 are mandatory for the first release; phases 4-7 may overlap based on resource availability.

### 9.1 Phase 1: Foundation (Weeks 1-2)

**Goal:** Establish the authentication framework with no behavior change to existing users.

**Deliverables:**
- Configuration schema in `appsettings.json` with full JSON schema documentation
- `IAuthenticationModeProvider` interface and three implementations (Domain/Local/Token)
- Startup logic to select mode based on configuration
- Negotiate authentication wired up in Domain mode
- Identity captured into `HttpContext.User` and `Session.OwnerSid`
- Audit logger sink configured separately from application logs

**Exit criteria:**
- Existing Scenario 1 (admin domain user) automation continues to pass
- Audit log shows authentication events for every request
- Operator UI displays active mode and resolved user identity
- Mode switch does not require code changes

### 9.2 Phase 2: Authorization (Weeks 2-3)

**Goal:** Replace header-based authorization with policy-based gates.

**Deliverables:**
- Three named policies: `Anonymous`, `User`, `Admin`
- `[Authorize(Policy = "...")]` attributes on every controller endpoint
- Ownership stamp on `ExecutionSession` and re-verification on cancel
- Removal of `X-Source` header as an authorization input
- Capability inquiry endpoint: `GET /api/security/capabilities`

**Exit criteria:**
- All existing automation passes under `Admin` policy
- Manual test: a Non-Admin domain user cannot force-release a lock (returns 403)
- Manual test: User A cannot cancel User B's session (returns 403)
- Operator UI shows current user's capability table

### 9.3 Phase 3: Command Policy Enforcement (Weeks 3-4)

**Goal:** Move command policy from AuditOnly to Enforce mode without breaking existing tests.

**Deliverables:**
- Comprehensive allowlist file derived from regression test history (last 90 days)
- Allowlist validation tool — run against past commands, report any that would be blocked
- Configuration flag to switch agent into Enforce mode
- Rejection telemetry endpoint
- Admin override capability for ad-hoc commands

**Exit criteria:**
- 100% of regression suite commands match the allowlist (or have admin override)
- Switching one canary agent to Enforce mode causes no test failures
- Audit log shows rejection events with clear reasons

### 9.4 Phase 4: Transport Encryption (Weeks 4-6)

**Goal:** Roll out mTLS without disrupting existing operations.

**Deliverables:**
- Certificate generation script using internal CA (or self-signed for dev)
- Dual-listener support on agents (plaintext + TLS simultaneously)
- Agent patching script update to install certificates alongside binaries
- Controller configuration to prefer TLS, fall back to plaintext
- Validation tool to verify all agents present valid certificates

**Exit criteria:**
- All 10 agents accepting TLS connections
- Controller successfully connects to all agents via TLS
- Plaintext listener can be disabled per-agent without affecting others
- Certificate rotation drill completed (replace cert without service restart)

### 9.5 Phase 5: Non-Admin & Token Mode Validation (Weeks 5-7)

**Goal:** Validate scenarios 2-5 end-to-end.

**Deliverables:**
- Test environments for each scenario (workgroup VM, non-admin domain user, etc.)
- Automation suite extension to run under each identity
- Token provisioning workflow documented
- Per-agent token-to-allowed-agents mapping

**Exit criteria:**
- Full regression suite passes under Scenario 1 (no behavior change)
- Smoke test passes under Scenarios 2, 3, 4, 5
- Documented runbook for each scenario's deployment

### 9.6 Phase 6: Rate Limiting & Hardening (Weeks 6-7)

**Goal:** Production-ready guardrails.

**Deliverables:**
- ASP.NET Core rate limiter configured per-user
- Health endpoint exempted from rate limiting
- 429 responses include `Retry-After` header
- Load test verifying rate limiter does not affect normal usage

**Exit criteria:**
- Normal automation (60 req/min/user) shows zero throttling
- Synthetic burst (200 req/sec) gets throttled to configured rate
- No effect on other users during throttle of one user

### 9.7 Phase 7: Documentation & Handoff (Weeks 7-8)

**Goal:** Make the feature self-service for ops and customer success.

**Deliverables:**
- Deployment guide for each authentication mode
- Troubleshooting runbook for common scenarios (AD unreachable, cert expired, lockout)
- Security model document for customer-facing security reviews
- Audit log analysis recipes (top users, denied actions, etc.)
- Migration guide for existing deployments

**Exit criteria:**
- One ops engineer can deploy the system in each mode following only the documentation
- Documentation passes internal review by security team
- Sample audit log analysis dashboard delivered

---

## 10. Acceptance Criteria

The feature is accepted when all of the following are demonstrably true:

### 10.1 Functional Acceptance

| ID | Criterion |
|---|---|
| AC-F-01 | Existing test automation suite passes under Scenario 1 with no configuration changes |
| AC-F-02 | Smoke test passes under each of Scenarios 2, 3, 4, 5 |
| AC-F-03 | A Non-Admin user attempting an admin operation receives HTTP 403 |
| AC-F-04 | A user attempting to cancel another user's session receives HTTP 403 |
| AC-F-05 | An unauthenticated user reaching any non-health endpoint receives HTTP 401 |
| AC-F-06 | An anonymous user reaching `/api/health` receives a valid response |

### 10.2 Security Acceptance

| ID | Criterion |
|---|---|
| AC-S-01 | The `X-Source: WPF` header has no effect on authorization decisions |
| AC-S-02 | All controller-to-agent traffic is encrypted (verified with packet capture) |
| AC-S-03 | A command with shell injection patterns is rejected in Enforce mode |
| AC-S-04 | An audit log entry exists for every admin action |
| AC-S-05 | Credentials and tokens do not appear in any non-audit log file (verified with grep) |

### 10.3 Operational Acceptance

| ID | Criterion |
|---|---|
| AC-O-01 | The active authentication mode is visible in the WPF UI |
| AC-O-02 | A startup health check reports the state of AD connectivity, cert validity, and policy mode |
| AC-O-03 | Switching modes requires only a configuration change and restart |
| AC-O-04 | Disk usage by audit logs over a 90-day period is documented |

### 10.4 Performance Acceptance

| ID | Criterion |
|---|---|
| AC-P-01 | Median API request latency increases by no more than 10 ms compared to baseline |
| AC-P-02 | mTLS handshake on cold gRPC connection completes within 100 ms |
| AC-P-03 | Throughput on a 50-agent fleet matches pre-feature baseline within 5% |

---

## 11. Risks and Mitigations

| Risk | Probability | Impact | Mitigation |
|---|---|---|---|
| **AD unreachable during deployment** | Medium | High | Fallback to Local mode; cache group memberships for 10 min |
| **Certificate expiry causes outage** | Medium | High | 30-day expiry warnings; automated rotation drill quarterly |
| **CommandPolicy Enforce mode blocks legitimate commands** | High | Medium | Run allowlist validator against 90-day command history before enabling |
| **Existing automation breaks under new auth** | Medium | High | Comprehensive backwards-compat test suite; staged rollout |
| **Rollout requires fleet downtime** | Low | High | Dual-listener architecture eliminates downtime |
| **Token leakage in Token mode** | Medium | High | Tokens stored DPAPI-encrypted; rotation enforced every 30 days |
| **Performance regression from policy evaluation** | Low | Medium | Cache policy decisions per request; benchmark before/after |
| **Documentation gaps cause customer self-service failures** | High | Medium | Documentation written alongside implementation; reviewed by ops team |

---

## 12. Dependencies

| Dependency | Owner | Required by |
|---|---|---|
| Active Directory groups (`XYZ\TestAdmins`, `XYZ\TestUsers`) | IT / DevOps | Phase 1 |
| Internal Certificate Authority OR self-signed cert generation | Security | Phase 4 |
| Certificate distribution mechanism (Group Policy or manual) | IT / DevOps | Phase 4 |
| Test environments for scenarios 2, 3, 4, 5 | QA Engineering | Phase 5 |
| Security team review of policy model | Security | Before Phase 1 exit |
| Customer Success input on Token mode workflow | CS | Phase 5 |

---

## 13. Glossary

| Term | Definition |
|---|---|
| **AD** | Active Directory — Microsoft's directory service used for identity management in Windows networks |
| **AuditOnly mode** | Command policy mode where dangerous patterns are logged but commands still execute (current default) |
| **Authentication (AuthN)** | The process of verifying who someone is |
| **Authorization (AuthZ)** | The process of verifying what someone is allowed to do |
| **DPAPI** | Data Protection API — Windows-provided mechanism for encrypting data with the user or machine key |
| **Enforce mode** | Command policy mode where dangerous or non-allowlisted commands are rejected (target default) |
| **gRPC** | Open-source RPC framework using HTTP/2 — used for controller-to-agent communication |
| **Kerberos** | Network authentication protocol used in AD environments; the modern alternative to NTLM |
| **mTLS** | Mutual TLS — both client and server authenticate with certificates |
| **Negotiate** | A Windows authentication protocol that picks the best available (Kerberos preferred, NTLM fallback) |
| **NTLM** | An older Windows authentication protocol; less secure than Kerberos but supported in non-AD scenarios |
| **OwnerSid** | The Security Identifier of the user who owns a session — used for ownership-based authorization |
| **Policy** | A named set of authorization requirements (e.g., "Admin" policy requires admin group membership) |
| **SAM** | Security Account Manager — the local Windows account database |
| **SID** | Security Identifier — a unique string that identifies a Windows user or group |
| **TLS** | Transport Layer Security — encryption for network communications |

---

## Appendix A: Decision Log

| Decision | Rationale |
|---|---|
| Three modes instead of one universal | Single codebase but different mechanism per environment; no universal mechanism works for both AD and non-AD |
| AD fallback to Local | Operational resilience; controller stays usable if AD has brief outage |
| Non-admin users get "own sessions only" | Matches real QA workflow where engineers don't kill each other's tests |
| Dual-listener for TLS migration | Eliminates the need for fleet-wide downtime |
| Allowlist before Enforce mode | Avoids breaking existing tests on day one |
| `X-Source` header retained for telemetry only | Backwards compatibility with existing dashboards |
| Audit logs separate sink | Compliance evidence must not be commingled with debug noise |
| 90-day audit retention | Industry standard for compliance audits |

---

## Appendix B: Open Questions for Stakeholder Review

1. **AD group naming.** Are `XYZ\TestAdmins` and `XYZ\TestUsers` the correct names, or should these follow an existing AVEVA convention?
2. **Certificate authority.** Will we use an internal AVEVA CA, or generate per-deployment self-signed certs?
3. **Token mode workflow.** For customer labs, who provisions tokens — customer admin or AVEVA support?
4. **Cert rotation cadence.** Is annual rotation acceptable, or does the security team require shorter?
5. **Customer-facing security documentation.** Required as part of this release or follow-up?
6. **vCloud / HyperV operations.** Should non-admin users be able to revert VM snapshots on their authorized agents?
7. **Rate limit defaults.** Is 60 req/min/user appropriate for the heaviest QA workflows?

---

**End of document.**

