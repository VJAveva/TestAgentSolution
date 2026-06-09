# 01 — System Design

> **Purpose**: turn the conceptual architecture in SRS §7 into a concrete engineering design. Reads as the technical reference for everything Phase 0-10 builds.

> **Prerequisite**: read `00_Master_Plan.md` first, especially §3 (Open Decisions Resolved). This document assumes those resolutions: server-side sessions, SQLite with WAL mode, React WebClient, email-only notifications.

---

## 1. Architectural Layers

The system is layered. Each layer has one job. Dependencies flow downward only.

```
 ┌──────────────────────────────────────────────────────────────────┐
 │  Presentation                                                     │
 │  ┌─────────────────────────┐   ┌────────────────────────────┐    │
 │  │  WPF Control Node       │   │  React Web Client          │    │
 │  │  (Admin + Operator)     │   │  (Manager / Engineer /     │    │
 │  │                         │   │   Guest)                   │    │
 │  └────────────┬────────────┘   └──────────────┬─────────────┘    │
 └───────────────┼──────────────────────────────┼────────────────────┘
                 │ gRPC (with session token)    │ gRPC-Web (anonymous OK for Guest)
                 ▼                              ▼
 ┌──────────────────────────────────────────────────────────────────┐
 │  Transport / API Gateway                                          │
 │  ┌──────────────────────────────────────────────────────────────┐ │
 │  │  Kestrel + gRPC + gRPC-Web bridge                             │ │
 │  │  TLS termination here (mTLS for Controller-Agent stays raw)   │ │
 │  └──────────────────────────────────────────────────────────────┘ │
 └────────────────────────────────┬─────────────────────────────────┘
                                  ▼
 ┌──────────────────────────────────────────────────────────────────┐
 │  Authentication                                                   │
 │  ┌──────────────────────────────────────────────────────────────┐ │
 │  │  SessionAuthInterceptor                                       │ │
 │  │   - reads token from metadata                                 │ │
 │  │   - looks up Sessions table                                   │ │
 │  │   - hydrates IUserContext into ServerCallContext.UserState    │ │
 │  │   - Guest path: short-lived anonymous context                 │ │
 │  └──────────────────────────────────────────────────────────────┘ │
 └────────────────────────────────┬─────────────────────────────────┘
                                  ▼
 ┌──────────────────────────────────────────────────────────────────┐
 │  Authorization                                                    │
 │  ┌──────────────────────────────────────────────────────────────┐ │
 │  │  IAuthorizationService.Can(user, permission, resourceId?)     │ │
 │  │   - default-deny                                              │ │
 │  │   - resolves Admin → unconditional allow                      │ │
 │  │   - resolves SrMgr / Engineer / Guest via permission map      │ │
 │  │   - writes audit entry on every call                          │ │
 │  └──────────────────────────────────────────────────────────────┘ │
 └────────────────────────────────┬─────────────────────────────────┘
                                  ▼
 ┌──────────────────────────────────────────────────────────────────┐
 │  Application (RPC Handlers + Use Cases)                           │
 │  ┌────────────┐ ┌────────────┐ ┌────────────┐ ┌────────────┐    │
 │  │ Pipeline   │ │ User       │ │ Report     │ │ Audit      │    │
 │  │ Service    │ │ Service    │ │ Service    │ │ Service    │    │
 │  └─────┬──────┘ └─────┬──────┘ └─────┬──────┘ └─────┬──────┘    │
 └────────┼──────────────┼──────────────┼──────────────┼────────────┘
          │              │              │              │
          ▼              ▼              ▼              ▼
 ┌──────────────────────────────────────────────────────────────────┐
 │  Domain                                                           │
 │  Pipeline, Run, Event, ActionGroup, Action, User, Assignment,     │
 │  Notification, AuditEntry  (POCOs + invariants)                   │
 └────────────────────────────────┬─────────────────────────────────┘
                                  ▼
 ┌──────────────────────────────────────────────────────────────────┐
 │  Infrastructure                                                   │
 │  ┌──────────────┐  ┌─────────────────┐  ┌────────────────────┐  │
 │  │ SQLite (WAL) │  │ Lock Registry   │  │ Pipeline Executor  │  │
 │  │ (EF Core)    │  │ (in-process)    │  │ (existing — gRPC   │  │
 │  │              │  │                 │  │  to agents)        │  │
 │  └──────────────┘  └─────────────────┘  └────────────────────┘  │
 └──────────────────────────────────────────────────────────────────┘
                                  ▲
 ┌──────────────────────────────────────────────────────────────────┐
 │  Background Workers (separate hosts within the same process)      │
 │  ┌─────────────────────┐  ┌──────────────────────────────────┐   │
 │  │ FlakyAnalyzer       │  │ ReportGenerator                  │   │
 │  │ (15-min cadence)    │  │ (weekly / monthly scheduled)     │   │
 │  └─────────────────────┘  └──────────────────────────────────┘   │
 │  ┌─────────────────────┐  ┌──────────────────────────────────┐   │
 │  │ NotificationDispatch│  │ LockExpirySweeper                │   │
 │  │ (consumes queue)    │  │ (5-second cadence)               │   │
 │  └─────────────────────┘  └──────────────────────────────────┘   │
 └──────────────────────────────────────────────────────────────────┘
```

---

## 2. Component Inventory

### 2.1 Server-side projects (target solution layout)

```
TestAgentSolution/
├── src/
│   ├── ControlNode.Core/                — domain entities, value types, enums, interfaces
│   ├── ControlNode.Infrastructure/      — EF Core context, repositories, lock registry
│   ├── ControlNode.Application/         — use case handlers, validators, mappers
│   ├── ControlNode.Api/                 — gRPC service definitions, RPC handlers, interceptors,
│   │                                       background workers (composition root)
│   └── ControlNode.WebClient/           — React SPA (existing, enhanced)
├── tests/
│   ├── ControlNode.Core.Tests/
│   ├── ControlNode.Application.Tests/
│   └── ControlNode.Api.Tests/            — integration tests via TestServer
└── proto/                                — gRPC service definitions (source of truth)
```

The dependency rule: **Core has no references**; Infrastructure and Application depend on Core; Api depends on all three. WebClient consumes the generated TypeScript stubs from `proto/`.

### 2.2 Key types added by this work

| Type | Layer | Purpose |
|---|---|---|
| `IUserContext` | Core | Identity flowing through every request |
| `Role` (enum) | Core | `Administrator`, `SeniorManager`, `Engineer`, `Guest` |
| `Permission` (enum) | Core | The full permission catalog (see §3.1) |
| `IAuthorizationService` | Core (interface) / Infrastructure (impl) | The `Can()` API |
| `Pipeline` (entity) | Core | Pipeline aggregate, owns state machine |
| `Run` (entity) | Core | One execution of a pipeline |
| `User`, `PipelineAssignment` | Core | RBAC entities |
| `AuditEntry` | Core | One row of the audit log |
| `ISessionStore` | Infrastructure | Session lifecycle |
| `SessionAuthInterceptor` | Api | gRPC interceptor wiring `IUserContext` |
| `IPipelineExecutorAdapter` | Application | Wraps the existing executor with the new identity layer |
| `IFlakyDetector` | Application | Pure function over recent runs |
| `INotificationChannel`, `EmailNotificationChannel` | Infrastructure | Pluggable notification delivery |
| `IReportConsolidator` | Application | Weekly/monthly aggregation |

---

## 3. Authorization Design

### 3.1 The Permission Catalog (full enumeration)

Permissions are named `<Resource>_<Action>` for consistency. Every gRPC handler that mutates state names exactly one permission. Read paths name a permission too (default-deny is real).

| Permission | Default Holders | Resource scope |
|---|---|---|
| `Pipeline_View` | All roles | Pipeline id (or wildcard for "any") |
| `Pipeline_Trigger` | Admin, SrMgr (any) / Engineer (assigned) | Pipeline id |
| `Pipeline_Cancel` | Admin, SrMgr (any) / Engineer (assigned) | Pipeline id |
| `Pipeline_Retry` | Same as `Pipeline_Trigger` of parent | Run id |
| `Pipeline_TriggerAll` | Admin, SrMgr | none |
| `Pipeline_CancelAll` | Admin, SrMgr | none |
| `Pipeline_Enable` | Admin | Pipeline id |
| `Pipeline_Disable` | Admin | Pipeline id |
| `Pipeline_ForceRelease` | Admin, SrMgr | Pipeline id |
| `User_Create` | Admin | none |
| `User_Update` | Admin | User id |
| `User_Delete` | Admin | User id |
| `User_Assign` | Admin | User id + Pipeline id |
| `User_Revoke` | Admin | User id + Pipeline id |
| `Report_View` | Admin, SrMgr (any) / Engineer (assigned) / Guest (any) | Pipeline id or wildcard |
| `Report_Generate` | Admin, SrMgr | none |
| `Audit_View` | Admin | none |
| `Audit_Export` | Admin | none |
| `Notification_Mute` | Engineer (own pipelines) | Pipeline id |

### 3.2 The `Can()` API

```csharp
public interface IAuthorizationService
{
    Task<AuthDecision> CanAsync(
        IUserContext user,
        Permission permission,
        string? resourceId = null,
        CancellationToken ct = default);
}

public sealed record AuthDecision(
    bool Allowed,
    string ReasonCode,           // "ok" | "no-role" | "no-assignment" | "guest-readonly" | …
    string? HumanReadable);      // for UI display when denied
```

Implementation rules:

1. **Default-deny.** The switch on `Permission` ends in `default → Deny("not-handled")`.
2. **Admin shortcut.** First check: if `user.Role == Administrator`, return `Allow("admin")` immediately. No further evaluation.
3. **Guest restriction.** If `user.Role == Guest` and permission ∉ {`Pipeline_View`, `Report_View`}, return `Deny("guest-readonly")`.
4. **Engineer + assignment.** For permissions that scope to a pipeline (`Pipeline_Trigger`, `Pipeline_Cancel`, `Pipeline_Retry`), require `AssignmentRepository.Exists(user.Id, resourceId)`.
5. **Senior Manager.** Allowed on any pipeline-scoped permission, but **not** on `User_*`, `Pipeline_Enable/Disable`, or `Audit_*`.
6. **Audit on every call.** Every decision — allow or deny — is queued to the audit writer. Even reads.

### 3.3 Where authorization checks live

Authorization is enforced **only** in the RPC handlers (Application layer). The Core domain knows nothing about users or permissions; the Infrastructure layer knows nothing about them either. This means:

- Adding a new permission requires changes only to `Permission` enum and `AuthorizationService.CanAsync` (NFR-MNT-01).
- Handlers always look the same:

```csharp
public async Task<TriggerPipelineResponse> TriggerPipelineAsync(
    TriggerPipelineRequest request, ServerCallContext context)
{
    var user = context.GetUserContext();
    var decision = await _authz.CanAsync(user, Permission.Pipeline_Trigger, request.PipelineId);
    if (!decision.Allowed)
        throw new RpcException(new Status(StatusCode.PermissionDenied, decision.HumanReadable!));

    // … business logic …
}
```

---

## 4. Authentication Flow

### 4.1 Login (creating a session)

```
Client                       AuthInterceptor       AuthService            DB
  │                                │                    │                  │
  │   gRPC LoginRequest            │                    │                  │
  │ (username, password)           │                    │                  │
  │───────────────────────────────▶│                    │                  │
  │                                │   Authenticate     │                  │
  │                                │───────────────────▶│                  │
  │                                │                    │  SELECT User     │
  │                                │                    │ ─────────────────▶
  │                                │                    │  bcrypt verify   │
  │                                │   Session(token)   │                  │
  │                                │◀───────────────────│                  │
  │                                │                    │  INSERT Session  │
  │                                │                    │ ─────────────────▶
  │  LoginResponse(token, role,    │                    │                  │
  │   mustChangePassword, perms)   │                    │                  │
  │◀───────────────────────────────│                    │                  │
```

Token is 32 random bytes, base64url-encoded. Stored in SQL as a SHA-256 hash (so a DB leak doesn't expose live tokens). The plaintext is returned exactly once.

### 4.2 Per-request validation (interceptor)

Every subsequent gRPC call carries the token in metadata. The `SessionAuthInterceptor`:

1. Extracts the `authorization: bearer <token>` header.
2. Computes its SHA-256, looks up the `Sessions` table by hash.
3. Rejects if: not found, revoked, or `last_used_utc` more than `SessionInactivityMinutes` (60 default) ago.
4. Hydrates `IUserContext` with `UserId`, `Username`, `Role`, and pre-fetched `AssignedPipelineIds` (one join, cached on the user context for the duration of the request).
5. Updates `last_used_utc` asynchronously (fire-and-forget).
6. Stashes the user context in `context.UserState`.

Guest path: a "Continue as Guest" RPC returns a token with `role: Guest` and no user record — the session row holds a null `user_id` and a non-null `guest_id` (a UUID generated per session). Audit entries record `guest_id` instead of `user_id`.

### 4.3 Logout / revocation

`LogoutAsync` sets `revoked_utc` on the session row. Subsequent requests with that token are rejected.

`User_Delete` and `User_Update(role)` revoke **all** sessions for that user atomically — the next request from that user fails authentication, forcing re-login. This satisfies the SRS implicit requirement of "instant revocation upon assignment change" via the same mechanism.

### 4.4 Inactivity expiry

A background job (every 5 minutes) marks sessions where `now - last_used_utc > SessionInactivityMinutes` as revoked. The interceptor would catch them anyway; the job is for housekeeping and is what makes the `Sessions` table not grow unbounded.

---

## 5. Pipeline State Machine

This is the foundation of FR-PIPE-01 through FR-PIPE-08.

```
                  +----------+
                  |   Idle   |◀───────────┐
                  +----+-----+            │
                       │                  │ (Run completes:
                       │ trigger          │  Passed / Failed)
                       ▼                  │
                  +----+-----+           +──────────+
                  | Running  |──────────▶|  Run     |
                  +----+-----+           |  outcome |
                       │                 +──────────+
                       │ cancel
                       ▼
                  +----+-----+
                  |Cancelled |  (terminal for the Run; Pipeline returns to Idle)
                  +----------+

                  +----------+
                  | Disabled |  (Admin-only; orthogonal — set independently of run state)
                  +----------+
```

**Notes:**
- `Disabled` is an additional flag, not a state in the same dimension as Idle/Running. A pipeline can be `Running` (a job in flight) while a fresh Admin marks it Disabled — the in-flight run completes normally, but no new triggers are accepted.
- `Failed` is a **Run** state, not a **Pipeline** state. When a Run finishes Failed, the Pipeline returns to `Idle` and a new Run could be triggered (or the same one retried).
- The `Pipeline.State` column in the DB has values `Idle`, `Running`. Plus a separate boolean `IsEnabled`. The SRS conflates these into one model in some places; this design separates them for cleaner concurrency.

### 5.1 Atomic trigger (FR-PIPE-06)

```sql
-- Single round-trip, optimistic, race-safe. SQLite RETURNING since 3.35.
UPDATE Pipelines
   SET State = 'Running',
       CurrentRunId = @newRunId,
       LastTransitionUtc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
 WHERE PipelineId = @pipelineId
   AND State = 'Idle'
   AND IsEnabled = 1
 RETURNING PipelineId;
```

If the `RETURNING` clause yields one row: we won the race, proceed to insert the `Run` row and signal the executor.
If it yields zero rows: someone else triggered (or it was disabled). Read the current state, return `FailedPrecondition` with a payload describing why.

### 5.2 Trigger-All-Idle (FR-PIPE-04)

The naïve loop is correct but slow. The fast version is a single statement plus a returned result:

```sql
-- Application generates new run UUIDs server-side (SQLite has no NEWID()).
-- The UPDATE uses RETURNING to emit one row per pipeline successfully transitioned.
UPDATE Pipelines
   SET State = 'Running',
       CurrentRunId = $generatedRunId,                    -- supplied per-row by the app, or via a CTE
       LastTransitionUtc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
 WHERE State = 'Idle'
   AND IsEnabled = 1
 RETURNING PipelineId, CurrentRunId;
```

In practice the application performs this as: generate `N` new UUIDs (one per Idle pipeline) up front, then issue the `UPDATE` in a single transaction using a `WITH RECURSIVE` CTE or — more simply — by binding the new UUIDs in a one-shot prepared statement per row inside one transaction. EF Core handles either pattern cleanly.

The `RETURNING` clause emits one row per pipeline we successfully transitioned. The application then:

1. Reads the unaffected pipelines: `SELECT PipelineId, State, IsEnabled FROM Pipelines WHERE PipelineId NOT IN (...)`.
2. Returns response with `triggered: [(id, runId)]` and `skipped: [(id, reason)]` where reasons are `already_running` or `disabled`.

The executor is signalled once per triggered pipeline. This is dispatched async; the RPC response returns as soon as the DB transaction commits. Per NFR-PRF-03, dispatch of 50 pipelines must complete within 3 seconds — well within SQLite's capability for this row count.

### 5.3 Cancel-All-Running (FR-PIPE-05)

Symmetric:

```sql
UPDATE Pipelines
   SET State = 'Idle',
       LastTransitionUtc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
 WHERE State = 'Running'
 RETURNING PipelineId, CurrentRunId;
```

Note: SQLite's `RETURNING` clause returns the post-update column values. To capture the `CurrentRunId` of the cancelled runs (which we need to update the `Runs` table), the application reads the affected rows first or uses the `CurrentRunId` returned directly (it is unchanged within this statement). For each returned row, mark the run as `Cancelled` (separate update on the `Runs` table inside the same transaction) and signal the executor's cancel hook.

---

## 6. Retry Hierarchy (FR-PIPE-03, FR-UI-03)

Run → Events → ActionGroups → Actions. Retry at any of the three lower levels.

```
public enum RetryScope { Action, ActionGroup, Event }

public sealed record RetryRequest(
    Guid RunId,
    RetryScope Scope,
    Guid TargetId);          // ActionId, ActionGroupId, or EventId
```

The handler:

1. Authorizes against `Pipeline_Trigger` on the **parent pipeline** of the run.
2. Verifies the run is in `Failed` state and the target node is in `Failed` state.
3. Signals the executor with `Executor.RetryAsync(scope, targetId)`.
4. Updates state: target node becomes `Running`. Run state becomes `Running` (recomputed when the retried subtree settles).

**Permission inheritance.** No separate `Retry` permission. If you can trigger the pipeline, you can recover the pipeline (decision documented in SRS §7.5 and confirmed here).

---

## 7. Concrete Data Schema

The SRS §9 conceptual model becomes the following concrete schema. SQLite dialect; EF Core code-first migrations using `Microsoft.EntityFrameworkCore.Sqlite`. Required pragmas (`journal_mode=WAL`, `synchronous=NORMAL`, `foreign_keys=ON`) are set on connection open by `OrchestratorDbContext.OnConfiguring`.

### 7.1 Tables

```sql
-- ─── Identity & RBAC ────────────────────────────────────────────
CREATE TABLE Users (
    UserId             TEXT     NOT NULL PRIMARY KEY,                  -- UUID as lowercase hex
    Username           TEXT     NOT NULL UNIQUE,                       -- max 64 chars (app-enforced)
    Email              TEXT     NOT NULL,                              -- max 256 chars
    Role               INTEGER  NOT NULL,                              -- 0=Admin, 1=SrMgr, 2=Engineer
    PasswordHash       TEXT     NOT NULL,                              -- bcrypt or PBKDF2 with embedded params
    MustChangePassword INTEGER  NOT NULL DEFAULT 0,                    -- 0/1 boolean
    IsActive           INTEGER  NOT NULL DEFAULT 1,                    -- 0/1 boolean
    CreatedUtc         TEXT     NOT NULL,                              -- ISO-8601 UTC
    CreatedByUserId    TEXT     NULL REFERENCES Users(UserId)
);

CREATE TABLE Sessions (
    SessionId    TEXT    NOT NULL PRIMARY KEY,                          -- UUID
    UserId       TEXT    NULL REFERENCES Users(UserId),
    GuestId      TEXT    NULL,                                          -- mutually exclusive with UserId
    TokenHash    BLOB    NOT NULL UNIQUE,                               -- 32 bytes (SHA-256 of opaque token)
    ClientKind   INTEGER NOT NULL,                                      -- 0=Wpf, 1=Web, 2=Cli
    IpAddress    TEXT    NULL,
    CreatedUtc   TEXT    NOT NULL,                                      -- ISO-8601 UTC
    LastUsedUtc  TEXT    NOT NULL,
    RevokedUtc   TEXT    NULL,
    CONSTRAINT CK_Sessions_UserXorGuest CHECK
        ((UserId IS NOT NULL AND GuestId IS NULL) OR (UserId IS NULL AND GuestId IS NOT NULL))
);
CREATE INDEX IX_Sessions_TokenHash ON Sessions(TokenHash) WHERE RevokedUtc IS NULL;

CREATE TABLE PipelineAssignments (
    UserId            TEXT NOT NULL REFERENCES Users(UserId),
    PipelineId        TEXT NOT NULL REFERENCES Pipelines(PipelineId),
    AssignedUtc       TEXT NOT NULL,
    AssignedByUserId  TEXT NOT NULL REFERENCES Users(UserId),
    CONSTRAINT PK_PipelineAssignments PRIMARY KEY (UserId, PipelineId)
);
CREATE INDEX IX_PipelineAssignments_PipelineId ON PipelineAssignments(PipelineId);

-- ─── Pipelines & Runs ───────────────────────────────────────────
CREATE TABLE Pipelines (
    PipelineId        TEXT     NOT NULL PRIMARY KEY,
    Name              TEXT     NOT NULL,
    State             TEXT     NOT NULL,                                -- 'Idle' | 'Running'
    IsEnabled         INTEGER  NOT NULL DEFAULT 1,                      -- 0/1 boolean
    CurrentRunId      TEXT     NULL,                                    -- non-null iff State='Running'
    CreatedUtc        TEXT     NOT NULL,
    LastTransitionUtc TEXT     NOT NULL
);

CREATE TABLE Runs (
    RunId              TEXT NOT NULL PRIMARY KEY,
    PipelineId         TEXT NOT NULL REFERENCES Pipelines(PipelineId),
    TriggeredByUserId  TEXT NULL REFERENCES Users(UserId),
    TriggeredByGuestId TEXT NULL,                                       -- should never be set; guests can't trigger
    State              TEXT NOT NULL,                                   -- 'Running' | 'Passed' | 'Failed' | 'Cancelled'
    StartedUtc         TEXT NOT NULL,
    EndedUtc           TEXT NULL
);
CREATE INDEX IX_Runs_PipelineId_StartedUtc ON Runs(PipelineId, StartedUtc DESC);

CREATE TABLE Events (
    EventId     TEXT     NOT NULL PRIMARY KEY,
    RunId       TEXT     NOT NULL REFERENCES Runs(RunId),
    Name        TEXT     NOT NULL,
    Sequence    INTEGER  NOT NULL,
    State       TEXT     NOT NULL,
    StartedUtc  TEXT     NULL,
    EndedUtc    TEXT     NULL
);
CREATE INDEX IX_Events_RunId ON Events(RunId);

CREATE TABLE ActionGroups (
    ActionGroupId TEXT     NOT NULL PRIMARY KEY,
    EventId       TEXT     NOT NULL REFERENCES Events(EventId),
    Name          TEXT     NOT NULL,
    Sequence      INTEGER  NOT NULL,
    State         TEXT     NOT NULL,
    StartedUtc    TEXT     NULL,
    EndedUtc      TEXT     NULL
);
CREATE INDEX IX_ActionGroups_EventId ON ActionGroups(EventId);

CREATE TABLE Actions (
    ActionId       TEXT     NOT NULL PRIMARY KEY,
    ActionGroupId  TEXT     NOT NULL REFERENCES ActionGroups(ActionGroupId),
    Name           TEXT     NOT NULL,
    Sequence       INTEGER  NOT NULL,
    State          TEXT     NOT NULL,
    Result         TEXT     NULL,                                       -- arbitrary length
    StartedUtc     TEXT     NULL,
    EndedUtc       TEXT     NULL
);
CREATE INDEX IX_Actions_ActionGroupId ON Actions(ActionGroupId);

-- ─── Audit ──────────────────────────────────────────────────────
CREATE TABLE AuditEntries (
    AuditId        INTEGER PRIMARY KEY AUTOINCREMENT,                   -- never reused even after delete
    UserId         TEXT     NULL,                                       -- null for guest
    GuestId        TEXT     NULL,
    RoleAtTime     INTEGER  NOT NULL,
    ActionName     TEXT     NOT NULL,                                   -- the Permission name
    ResourceId     TEXT     NULL,
    Decision       INTEGER  NOT NULL,                                   -- 0=Allow, 1=Deny
    ReasonCode     TEXT     NOT NULL,
    TimestampUtc   TEXT     NOT NULL,
    ClientKind     INTEGER  NOT NULL,
    CorrelationId  TEXT     NULL
);
CREATE INDEX IX_AuditEntries_UserId_Timestamp     ON AuditEntries(UserId, TimestampUtc DESC);
CREATE INDEX IX_AuditEntries_ActionName_Timestamp ON AuditEntries(ActionName, TimestampUtc DESC);
CREATE INDEX IX_AuditEntries_TimestampUtc         ON AuditEntries(TimestampUtc DESC);

-- ─── Notifications ──────────────────────────────────────────────
CREATE TABLE Notifications (
    NotificationId TEXT NOT NULL PRIMARY KEY,
    Type           TEXT NOT NULL,                                       -- 'ConsecutiveFailure' | 'Flaky'
    PipelineId     TEXT NOT NULL REFERENCES Pipelines(PipelineId),
    CreatedUtc     TEXT NOT NULL,
    DispatchedUtc  TEXT NULL,
    Channel        TEXT NOT NULL,                                       -- 'Email' (v1)
    Payload        TEXT NULL                                            -- JSON
);
CREATE INDEX IX_Notifications_Undispatched
    ON Notifications(CreatedUtc) WHERE DispatchedUtc IS NULL;

CREATE TABLE NotificationCooldowns (
    PipelineId       TEXT NOT NULL,
    Type             TEXT NOT NULL,
    CooldownUntilUtc TEXT NOT NULL,
    CONSTRAINT PK_NotificationCooldowns PRIMARY KEY (PipelineId, Type)
);

CREATE TABLE NotificationMutes (
    UserId        TEXT NOT NULL REFERENCES Users(UserId),
    PipelineId    TEXT NOT NULL REFERENCES Pipelines(PipelineId),
    MutedUntilUtc TEXT NOT NULL,
    CONSTRAINT PK_NotificationMutes PRIMARY KEY (UserId, PipelineId)
);
```

**SQLite-specific notes:**
- All UUID columns are stored as lowercase hyphenated text (`"f47ac10b-58cc-4372-a567-0e02b2c3d479"`) — readable in `sqlite3` CLI for debugging. EF Core converts `Guid` ↔ `TEXT` automatically.
- All timestamp columns are ISO-8601 UTC text (`"2026-06-08T14:23:11.123Z"`). EF Core converts `DateTimeOffset` and `DateTime(Kind=Utc)` ↔ `TEXT` automatically. SQL `datetime()` and `julianday()` functions work on this format.
- Boolean columns (`MustChangePassword`, `IsActive`, `IsEnabled`) are `INTEGER` with values 0/1. EF Core converts `bool` ↔ `INTEGER`.
- `INTEGER PRIMARY KEY AUTOINCREMENT` on `AuditEntries.AuditId` guarantees ids never repeat even after deletion (important for audit semantics).
- Partial indexes (the `WHERE` clauses on `IX_Sessions_TokenHash` and `IX_Notifications_Undispatched`) work identically to SQL Server — supported since SQLite 3.8.0.
- `REFERENCES` clauses are enforced only when `PRAGMA foreign_keys = ON` is set per connection (handled by `OrchestratorDbContext.OnConfiguring`).

### 7.2 Migration approach

EF Core code-first migrations. First migration creates the new tables; existing pipeline-execution tables (if any) stay untouched. The `Pipelines` table is **new** in this design — the SRS implies pipelines are currently file-defined (WatchList.xml). Phase 1 includes a one-time import job that reads `WatchList.xml` and seeds the `Pipelines` table with one row per WatchListItem. Subsequent edits to `WatchList.xml` re-run the import (idempotent: match by stable PipelineId derived from the WatchList item id).

---

## 8. Background Workers

All four workers are `IHostedService` implementations in the same process as the gRPC service. Lifetimes:

| Worker | Cadence | What it does |
|---|---|---|
| `FlakyAnalyzerWorker` | Every 15 min (configurable) | Scans recent Runs per pipeline. Emits `Notification` rows when thresholds hit. |
| `NotificationDispatcherWorker` | Every 30 s | Polls `Notifications WHERE DispatchedUtc IS NULL`. Dispatches via `INotificationChannel`. Updates `DispatchedUtc`. |
| `ReportGeneratorWorker` | Cron — weekly (Monday 06:00 UTC), monthly (1st 06:00 UTC) | Runs the consolidation, stores PDF/CSV in a `Reports` folder, links via `Report` rows. Also on-demand via RPC. |
| `SessionHousekeepingWorker` | Every 5 min | Revokes expired sessions; deletes revoked rows older than 30 days. |
| `LockExpirySweeper` (from Lock spec) | Every 5 s | See `Pipeline_Lock_Coordination_Spec.md` §4.4. |

Each worker writes a structured log line on each cycle including duration and items processed. None of them block the request path.

---

## 9. Flaky Detection Algorithm

```
Inputs:
  pipelineId
  N = consecutive-failure threshold (default 3)
  W = flaky window (default 10 runs)
  T = transition threshold (default 3 alternations)

Procedure:
  recent = SELECT TOP W State FROM Runs WHERE PipelineId = pipelineId
                                       ORDER BY StartedUtc DESC

  -- Rule 1: consecutive failures
  if recent.Take(N).All(s == 'Failed'):
      emit Notification(type=ConsecutiveFailure)
      return

  -- Rule 2: flaky pattern
  transitions = count i where recent[i].State != recent[i+1].State
                              AND recent[i].State IN ('Passed','Failed')
                              AND recent[i+1].State IN ('Passed','Failed')
  if transitions >= T:
      emit Notification(type=Flaky)
```

The "emit" step is idempotent against cooldown: check `NotificationCooldowns` for the same `(pipelineId, type)`; if cooldown is in the future, skip.

---

## 10. Error Model (gRPC Status Codes)

| Condition | Status | Detail payload |
|---|---|---|
| Not authenticated | `Unauthenticated` | reason (no-token, expired, revoked) |
| Authorized but denied | `PermissionDenied` | permission name, resource, reason code |
| Pipeline in wrong state | `FailedPrecondition` | current state, expected state |
| Pipeline locked (with Lock feature on) | `Aborted` | `PipelineLockDto` payload (see Lock spec) |
| Duplicate username | `AlreadyExists` | username |
| Pipeline / user / run not found | `NotFound` | resource id |
| Validation failure | `InvalidArgument` | field name, validation message |
| Rate-limited login | `ResourceExhausted` | retry-after seconds |
| Internal | `Internal` | correlation id (do not expose stack) |

All errors carry a `correlation_id` in trailers for client-side display and server-side log correlation.

---

## 11. Performance Targets and How We Hit Them

| NFR | Approach |
|---|---|
| NFR-PRF-01 (authz < 5 ms p95) | All authz checks: one indexed lookup (assignment) at worst, in-memory dictionary for permission map. Allow/deny audit is **fire-and-forget** — written by a separate channel, not in the request path. |
| NFR-PRF-02 (pipeline list < 500 ms p95) | Indexed query, pagination, no N+1. Filter by role at the SQL level. |
| NFR-PRF-03 (TriggerAll < 3 s for 50 pipelines) | Single transaction with `UPDATE … RETURNING` statement plus async executor signals. |
| NFR-SEC-02 (password hashing) | bcrypt at cost factor 12 on PBKDF2 fallback if bcrypt unavailable. |
| NFR-SEC-03 (login rate-limit) | Token bucket per username and per source IP; both must pass. Sliding 1-minute window, 5 attempts. |

---

## 12. What's Next

Read `02_Implementation_Roadmap.md` for the phased plan that turns this design into shipped software.

Read `03_Integration_With_Lock_Spec.md` to understand how the previously-defined Lock coordination work folds into this larger plan as Phase 3.
