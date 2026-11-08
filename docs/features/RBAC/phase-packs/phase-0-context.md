# Phase 0 Context Pack — Identity & Authorization Foundations

> Attach this file to every Copilot session while working on Phase 0. Detach when Phase 0 ships and switch to `phase-0.5-context.md`.

> Companion reading (already in Copilot context via `.github/copilot-instructions.md`): `docs/rbac/01_System_Design.md`, `docs/rbac/05_Default_Mode_Design.md`, `docs/architecture/CURRENT_STATE.md`, `docs/architecture/CONVENTIONS.md`.

---

## Goal

Build the identity, authorization, audit, and Default-mode-switch mechanics that every later phase depends on. Exit when the system can answer "who is the caller?" and "are they allowed?" — including the Default-mode short-circuit where WPF gets unrestricted access without a token.

**Phase delivers no end-user UI.** All testing is via integration tests against the gRPC API.

---

## Where things land in the existing solution

The actual codebase has no `ControlNode.*` projects. The RBAC work grafts onto the existing structure as follows:

| Concern | Project |
|---|---|
| Domain types (`IUserContext`, `Permission`, `Role`, entities, options) | **`TestControllerGrpc.Core`** (existing — add `Identity/`, `Authorization/`, `Audit/`, `Configuration/` folders) |
| EF Core + SQLite (DbContext, configurations, migrations, repositories) | **`TestController.Persistence`** (NEW project — the only structural addition) |
| Authorization service implementation | **`TestController.Persistence`** (same project for cohesion with DbContext) |
| Audit drain worker | **`TestController.Persistence`** |
| gRPC interceptors, RbacModeTransitionService, AuthGrpcService, SystemModeGrpcService | **`TestController.Api`** (existing — add `Interceptors/`, `Services/`, `SystemMode/` folders) |
| auth.proto | **`TestControllerGrpc.Core/Protos/`** (existing folder, next to `test_agent.proto`) |
| Tests | **`TestControllerGrpc.Tests/Rbac/`** + **`TestController.WebApi.Tests/Rbac/`** (both existing) |

The new `TestController.Persistence` project must be referenced from both hosts:
- `TestControllerGrpc/TestControllerGrpc.csproj` (the WPF host)
- `TestController.WebApi/TestController.WebApi.csproj` (the standalone web host)

---

## File targets (24 tasks, 4 blocks)

### Block A — Core types in TestControllerGrpc.Core (no dependencies; do first)

| # | Path | What |
|---|---|---|
| 1 | `TestControllerGrpc.Core/Identity/IUserContext.cs`, `ClientKind.cs` | Identity flowing through every request |
| 2 | `TestControllerGrpc.Core/Identity/Role.cs`, `TestControllerGrpc.Core/Authorization/Permission.cs` | Enums per `01_System_Design.md` §3.1 (full 19-entry catalog) |
| 3 | `TestControllerGrpc.Core/Authorization/AuthDecision.cs`, `IAuthorizationService.cs` | Single `Can()` API per `01_System_Design.md` §3.2 |
| 4 | `TestControllerGrpc.Core/Identity/User.cs`, `PipelineAssignment.cs`, `TestControllerGrpc.Core/Audit/AuditEntry.cs` | POCO entities, no logic |
| 5 | `TestControllerGrpc.Core/Identity/DefaultUser.cs`, `SyntheticUserContext.cs` | Per `05_Default_Mode_Design.md` §3 — stable UUID `00000000-0000-0000-0000-000000000001` |
| 6 | `TestControllerGrpc.Core/Configuration/RbacOptions.cs` | Single `Enabled` boolean, bound to `appsettings.json` `RBAC` section |

### Block B — Persistence + Infrastructure (depends on A)

| # | Path | What |
|---|---|---|
| 7a | Create `TestController.Persistence/TestController.Persistence.csproj`, add to .sln, reference from both hosts | New project — only structural addition |
| 7b | `TestController.Persistence/OrchestratorDbContext.cs` + `Configurations/*.cs` | EF Core with SQLite provider per `01_System_Design.md` §7.1. WAL pragmas in `OnConfiguring`. |
| 8 | `TestController.Persistence/Migrations/0000_Initial.cs` | Run `dotnet ef migrations add Initial -p TestController.Persistence`, then have Copilot clean up |
| 9 | `TestController.Persistence/Identity/SessionStore.cs` | `ISessionStore` impl: insert, lookup by token hash, revoke |
| 10 | `TestController.Persistence/Identity/PasswordHasher.cs` | bcrypt wrapper (cost factor 12) |
| 11 | `TestController.Persistence/Authorization/AuthorizationService.cs` | Per `01_System_Design.md` §3.2 + §3.4 — **Default mode short-circuit must come BEFORE role evaluation** |
| 12 | `TestControllerGrpc.Core/Configuration/WritableOptions.cs` | Generic writable options that serializes changes back to `appsettings.json`. Used by mode-transition service. |
| 13 | `TestController.Persistence/Audit/QueuedAuditWriter.cs`, `AuditDrainWorker.cs` | Fire-and-forget audit pattern per `01_System_Design.md` §11 — bounded queue (default 10K), async drain. Uses existing `IAppLogger` for the drain worker's own diagnostics. |
| 14 | `TestController.Api/SystemMode/RbacModeTransitionService.cs` | Per `05_Default_Mode_Design.md` §8 + §9 — atomic transitions both directions |

### Block C — API surface in TestController.Api (depends on A, B)

| # | Path | What |
|---|---|---|
| 15 | `TestController.Api/Interceptors/SessionAuthInterceptor.cs` | Per `05_Default_Mode_Design.md` §12 — read `RBAC:Enabled` via `IOptionsMonitor`, branch on Default vs Secured. **Integrates with, does NOT replace, the existing `AddMultiIdentitySecurity()`.** |
| 16 | `TestController.Api/Interceptors/AuditLoggingInterceptor.cs` | Wraps every RPC, writes audit row through `IAuditWriter` |
| 17 | `TestControllerGrpc.Core/Protos/auth.proto` | `LoginRequest`, `LoginResponse`, `LogoutRequest`, `SystemMode` messages — next to existing `test_agent.proto` |
| 18 | `TestController.Api/Services/AuthGrpcService.cs` | `LoginAsync`, `LogoutAsync`, `ChangePasswordAsync` — token issuance + session insert |
| 19 | `TestController.Api/Services/SystemModeGrpcService.cs` | `Get`, `SwitchToSecured`, `SwitchToDefault` RPCs |
| 20 | `TestController.Api/RbacFeatureExtensions.cs` + surgical diffs to `TestControllerGrpc/App.xaml.cs` and `TestController.WebApi/Program.cs` | New extension method `AddRbacFeature()` grouping all RBAC DI. Both hosts call it. Follows the existing `AddMultiIdentitySecurity()` and `AddControllerApi()` extension-method pattern. |

### Block D — Tests (depends on A, B, C)

| # | Path | What |
|---|---|---|
| 21 | `TestControllerGrpc.Tests/Rbac/AuthorizationServiceTests.cs` | Parameterized with `[FactInBothModes]` — verify Default-mode allow/deny rules AND normal RBAC rules. Naming: `Method_Should_Expected_When_State` per CONVENTIONS.md |
| 22 | `TestController.WebApi.Tests/Rbac/SessionAuthInterceptorTests.cs` | Default mode: no token → success. Secured mode: no token → Unauthenticated. Uses `IClassFixture<TestWebAppFactory>` per existing pattern. |
| 23 | `TestControllerGrpc.Tests/Rbac/RbacModeTransitionServiceTests.cs` | Atomic transitions both directions, idempotent, no orphaned state on failure |
| 24 | `TestController.WebApi.Tests/Rbac/AuthE2ETests.cs` | Full Login → authenticated RPC → audit row written → Logout cycle. Uses `WebApplicationFactory<Program>` per existing integration test pattern. |

---

## Sequence rules

- **Block A must finish before any of B starts** — Persistence types depend on Core interfaces.
- **Block B must finish before any of C starts** — Interceptors and services need the actual implementations to wire up.
- **Tests in Block D can interleave with C** if you prefer test-first; otherwise do them after the production code.
- **Within a block, tasks can be done in any order** but the numbered sequence is the path of least dependency surprise.

---

## Watch out for

1. **Default-mode short-circuit lives in `AuthorizationService.CanAsync`, not in the interceptor.** The interceptor injects identity; the authorization service decides. Don't put the WPF-allow-all logic in the interceptor — that breaks the testable single-responsibility cut.

2. **`SessionAuthInterceptor` reads `IOptionsMonitor<RbacOptions>.CurrentValue` on every call** — not a captured value at construction. This is what makes mode switching live with no restart. Verify the test for "switch mode mid-session" passes.

3. **Audit writes are fire-and-forget**, NOT awaited in the request path. NFR-PRF-01 (5ms p95 for authz) depends on this. The bounded channel + background drain pattern in `01_System_Design.md` §11 is the canonical implementation. If you find yourself awaiting `_auditWriter.WriteAsync(...)` inside `CanAsync`, stop and refactor.

4. **All UUIDs are app-generated, not DB-generated.** SQLite has no `NEWID()`. The `Users.UserId`, `Sessions.SessionId`, etc., are all `Guid.NewGuid()` in C# code, stored as lowercase hyphenated TEXT in SQLite. The synthetic Default user is the only fixed UUID (`00000000-0000-0000-0000-000000000001`).

5. **The `Permission` enum has 19 entries.** Don't generate a 4-entry "starter" version and plan to extend later — the test matrix in Phase 0 verifies every permission, and you want that coverage from day one. Copy the full catalog from `01_System_Design.md` §3.1.

6. **`RbacModeTransitionService` is atomic — wrap all DB mutations in a single transaction.** Mid-transition crashes must leave the system in a recoverable state (either fully Default or fully Secured, never partial).

7. **The `RBAC:Enabled` flag persists to `appsettings.json` via `IWritableOptions`.** Don't use a separate config file or environment variable.

8. **Default user is never written to the `Users` table.** It exists only in memory as a synthetic context injection. If you find yourself seeding a "Default user" row in the migration, stop.

9. **`AgentLockManager` already exists in the codebase and is a DIFFERENT concept.** AgentLockManager locks remote agents (Agent1, Agent2 machines) to a session for the duration of execution. The pipeline lock work in Phase 3 is a separate layer that locks WatchItems (which the design docs call "pipelines") to a single triggering user/client. Both coexist; Phase 0 doesn't touch AgentLockManager.

10. **Existing `AddMultiIdentitySecurity()` stays.** Phase 0 ADDS session-based auth alongside the existing NTLM/negotiate + API-key + roles mechanism. The new `SessionAuthInterceptor` is gRPC-layer; the existing security is ASP.NET Core auth middleware. They don't conflict — they handle different request paths. Do NOT replace `AddMultiIdentitySecurity()`.

11. **Default to `Singleton` DI lifetime** per CONVENTIONS.md. The codebase uses Singleton almost universally. `IAuthorizationService`, `ISessionStore`, `IAuditWriter`, `SessionAuthInterceptor` are all Singleton. Only the EF Core `OrchestratorDbContext` is Scoped (per EF Core's own requirement) — and you access it through a `IDbContextFactory<OrchestratorDbContext>` from the Singleton services.

12. **Use `IAppLogger`, not `Serilog`, for diagnostic logging in worker classes.** Use `ILogger<T>` only for the hosted background services where the framework expects it.

---

## How to start a task in this phase

Copy this skeleton when opening a Copilot session for any task in Block A-D:

```
[SPEC]
- docs/rbac/01_System_Design.md §<X> — <one line>
- docs/rbac/05_Default_Mode_Design.md §<Y> — <one line if relevant>

[CURRENT STATE]
- Tasks 1..N already done (paths)
- Pending: this task (path)
- Existing patterns: see docs/architecture/CONVENTIONS.md §<section>

[TASK]
Generate <path>

[CONSTRAINTS]
- <task-specific rules from the file targets table above>
- Follow patterns in docs/architecture/CONVENTIONS.md
- Use IAppLogger for logging, CommunityToolkit.Mvvm if WPF, Zustand if React
- Default Singleton DI lifetime
- Do not modify any other file

[OUTPUT]
- The file content
- One line: DI registration to add in AddRbacFeature() extension (if applicable)
- Nothing else
```

---

## Exit checklist (don't move to Phase 0.5 until all green)

- [ ] All 24 files exist and compile
- [ ] `TestController.Persistence` project added to .sln, referenced by both hosts
- [ ] `dotnet test TestAgentSolution.sln` passes with zero failures in Default-mode fixture
- [ ] `dotnet test` passes with zero failures in Secured-mode fixture
- [ ] `dotnet ef database update -p TestController.Persistence` against a clean directory creates the SQLite file with all tables
- [ ] WAL pragmas verified: connect with `sqlite3 orchestrator.db` and run `PRAGMA journal_mode;` — returns `wal`
- [ ] Manual test: start WPF host with `RBAC:Enabled=false`, hit any read RPC from a gRPC client without a token, get a successful response
- [ ] Manual test: flip `RBAC:Enabled=true` via writable options, retry the same RPC without a token, get `Unauthenticated`
- [ ] Seeded Administrator (created by `0001_SeedAdmin.cs` migration) can log in via `AuthGrpcService.LoginAsync`
- [ ] Audit table has rows for every RPC made during manual testing — both allows and denies
- [ ] Existing `AddMultiIdentitySecurity()` still functions for the WebApi REST endpoints (regression test — old tests should still pass)
- [ ] `CURRENT_STATE.md` updated with the new Phase 0 types and patterns
