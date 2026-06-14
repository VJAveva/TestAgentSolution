# WebApi Database Decoupling — Architecture Report

> **Date:** 2026-06-14  
> **Status:** Investigation complete — no code changes made

---

## Part 1: Plain-Language Summary

### What's Wrong

The system has two programs that run at the same time:

1. **The WPF Controller** (TestControllerGrpc) — the "brain" desktop application that manages test agents, runs pipelines, and owns all user/security data.
2. **The WebApi** (TestController.WebApi) — a web server that the React dashboard talks to. It's supposed to be a "messenger" that forwards requests to the Controller.

Both programs currently try to open the same database file (`orchestrator.db`) at startup. The database is SQLite — a simple file-based database that only works well with one writer at a time.

### Why IIS Crashes

When you deploy the WebApi to IIS:

- The IIS worker process runs under a restricted account that can't create or write files in the deployment folder.
- Even if you fix file permissions, SQLite still fights with itself because the WebApi's background workers (audit drain, database migration) compete with the Controller for write access to the same file.
- The result: "database is locked", "unable to open database file", or "attempt to write a readonly database" errors at startup — the app crashes before serving its first request.

### What the Fix Is

The WebApi should **never touch the database**. Instead:

- When the React dashboard needs user data, login, or security info, the WebApi should forward ("proxy") those requests to the Controller's embedded API (which already runs on port 5200 and already owns the database).
- The WebApi already does this correctly for pipeline locks — it just needs to do the same for auth, users, and system-mode operations.

---

## Part 2: Current-State Findings

### Investigation 1 — Startup Wiring

| Host | File | Call | `isPrimaryHost` |
|------|------|------|-----------------|
| WPF Controller | `TestControllerGrpc/App.xaml.cs:95` | `services.AddRbacFeature(ctx.Configuration)` | `true` (default) |
| WPF Embedded API | `TestControllerGrpc/Services/ControllerWebApiHost.cs:153` | `builder.Services.AddRbacFeature(builder.Configuration)` | `true` (default) |
| Standalone WebApi | `TestController.WebApi/Program.cs:99` | `builder.Services.AddRbacFeature(builder.Configuration, isPrimaryHost: false)` | `false` |

**Critical finding:** The `isPrimaryHost` parameter is accepted by `AddRbacFeature()` (at `RbacFeatureExtensions.cs:31`) but **never read or used inside the method body**. Every registration executes unconditionally:

| What `AddRbacFeature()` registers | DB access? | File / Line |
|---|---|---|
| `OrchestratorDbContext` (via `IDbContextFactory`) | ✅ Opens SQLite file | `RbacFeatureExtensions.cs:53-61` |
| `DatabaseInitializerService` (hosted service) | ✅ Runs migrations at startup | `RbacFeatureExtensions.cs:64` |
| `SessionStore` (implements `ISessionStore`) | ✅ Reads/writes Sessions table | `RbacFeatureExtensions.cs:68` |
| `AuthorizationService` | ✅ Reads Users table for role checks | `RbacFeatureExtensions.cs:71` |
| `QueuedAuditWriter` + `AuditDrainWorker` (hosted service) | ✅ Background writes to AuditEntries | `RbacFeatureExtensions.cs:74-76` |
| `SessionAuthInterceptor` | ✅ Looks up sessions + users from DB on every request | `RbacFeatureExtensions.cs:79` |
| `AuthService` | ✅ Login/logout/password — reads/writes Users + Sessions | `RbacFeatureExtensions.cs:83` |
| `UserService` | ✅ CRUD on Users table | `RbacFeatureExtensions.cs:84` |
| `RbacModeTransitionService` | ✅ Multi-table transaction (sessions, users, flag) | `RbacFeatureExtensions.cs:85` |
| `PipelineAuthorizationGuard` | ✅ Checks pipeline assignments | `RbacFeatureExtensions.cs:89` |
| `PipelineService` | ✅ Reads assignments for authorization | `RbacFeatureExtensions.cs:90` |
| `RbacOptions` (config) | ❌ No DB | `RbacFeatureExtensions.cs:34` |
| `WritableOptions<RbacOptions>` | ❌ Writes appsettings.json only | `RbacFeatureExtensions.cs:37-41` |
| `PasswordHasher` | ❌ In-memory hash only | `RbacFeatureExtensions.cs:67` |
| `SystemModeBroadcaster` | ❌ SignalR only | `RbacFeatureExtensions.cs:86` |

**Note:** `ThrowingDbContextFactory` exists at `RbacFeatureExtensions.cs:176-183` as a safety guard — but it is never registered anywhere.

### Investigation 2 — DB-Touching Code Active in the WebApi

The standalone WebApi does not directly reference the DB in its own `.cs` files. However, it hosts **shared controllers** from `TestController.Api/Controllers/` via `UseControllerApi()` (line `Program.cs:325`). These controllers inject DB-backed singletons:

| Shared Controller | DB-backed Service Used | Operation |
|---|---|---|
| `AuthController.cs` | `AuthService` | Login (create session), logout (revoke session), change-password, `/me` (session lookup + user load) |
| `UserController.cs` | `UserService` | List/create/update/delete users, assign pipelines, reset password |
| `SystemModeController.cs` | `RbacModeTransitionService` | Switch to Secured / Default (multi-table transactions) |
| `PipelinesController.cs` | `PipelineService` | Authorize trigger/cancel (reads pipeline assignments) |
| `SecurityController.cs` | `SessionAuthInterceptor` | `/whoami`, `/capabilities`, `/status` (session + user lookup) |
| `LocksController.cs` | `SessionAuthInterceptor` + `LockService` | ResolveUser reads DB; LockService is null in WebApi (no lock registry) |

Additionally, two **hosted services** run background DB writes inside the WebApi process:

| Hosted Service | Operation | Risk |
|---|---|---|
| `DatabaseInitializerService` | Runs EF Core migrations at startup | **Crashes immediately** under IIS if it can't write |
| `AuditDrainWorker` | Flushes audit queue to DB every batch | Causes "database is locked" if Controller is also writing |

### Investigation 3 — ControllerProxyService Capability

**Location:** `TestController.WebApi/Services/ControllerProxyService.cs`

| Method | Auth header forwarded? | Body forwarded? | Used by |
|---|---|---|---|
| `GetDashboardSessionsAsync()` | ❌ | N/A | Execution endpoints |
| `GetExecutionStatusAsync()` | ❌ | N/A | Execution endpoints |
| `GetSessionsAsync()` | ❌ | N/A | Execution endpoints |
| `GetRecentLogsAsync(sessionId, count)` | ❌ | N/A | Execution endpoints |
| `ForwardGetAsync(path, authHeader)` | ✅ | N/A | Lock endpoints |
| `ForwardDeleteAsync(path, authHeader)` | ✅ | N/A | Lock endpoints |
| `ForwardPostAsync(path, authHeader)` | ✅ | ❌ **No body** | Lock endpoints |

**What's missing for full proxy coverage:**

1. `ForwardPostWithBodyAsync(path, body, authHeader)` — needed for login, create-user, assign-pipelines, change-password, system-mode switch (all require JSON body).
2. `ForwardPutAsync(path, body, authHeader)` — needed for update-user.
3. The existing session-specific GET methods (dashboard, status, logs) don't forward auth headers — fine for execution data but insufficient for auth-protected endpoints.

### Investigation 4 — Controller Embedded API Endpoints

The WPF Controller's embedded API (`ControllerWebApiHost.cs:192`) calls `app.UseControllerApi()` which maps `app.MapControllers()`. Since the Controllers live in the shared `TestController.Api` assembly, **the Controller exposes the exact same endpoints as the WebApi**:

| Route group | Available on Controller (port 5200)? | Would WebApi need to proxy? |
|---|---|---|
| `api/auth/*` (login, logout, me, change-password, guest) | ✅ Yes | ✅ Yes |
| `api/users/*` (CRUD, assignments, reset-password) | ✅ Yes | ✅ Yes |
| `api/system/mode/*` (get mode, switch secured/default) | ✅ Yes | ✅ Yes |
| `api/security/*` (status, capabilities, whoami) | ✅ Yes | ✅ Yes |
| `api/pipelines/*` (authorize trigger/cancel) | ✅ Yes | ✅ Yes |
| `api/locks/*` | ✅ Yes | ✅ Already proxied |
| `api/execution/*` | ✅ Yes | ✅ Already proxied (via minimal API endpoints) |
| `api/health/*` | ✅ Yes | May remain local (WebApi has its own health) |
| `api/results/*` | ✅ Yes | Reads TRX files, no DB — can stay local |
| `api/agents/*` | ✅ Yes | Reads agent state, no DB — can stay local |
| `api/watchlist/*` | ✅ Yes | Reads XML, no DB — can stay local |

**All DB-backed endpoints already exist on the Controller's embedded API.** No new endpoints need to be created.

### Investigation 5 — Risk Map

If we remove DB access from the WebApi and make it proxy instead:

| WebApi Route | Current implementation | Proxy needed? | Break risk |
|---|---|---|---|
| `POST /api/auth/login` | `AuthController` → `AuthService` → DB | ✅ Must proxy | **High** — login is the first thing users do |
| `POST /api/auth/logout` | `AuthController` → `AuthService` → DB | ✅ Must proxy | Medium |
| `GET /api/auth/me` | `AuthController` → `SessionAuthInterceptor` → DB | ✅ Must proxy | **High** — called on every page load |
| `POST /api/auth/change-password` | `AuthController` → `AuthService` → DB | ✅ Must proxy | Low frequency |
| `POST /api/auth/guest` | `AuthController` → `AuthService` → DB | ✅ Must proxy | Medium |
| `GET/POST/PUT/DELETE /api/users/*` | `UserController` → `UserService` → DB | ✅ Must proxy | Medium — admin-only |
| `POST /api/system/mode/*` | `SystemModeController` → `RbacModeTransitionService` → DB | ✅ Must proxy | Low frequency |
| `GET /api/security/*` | `SecurityController` → interceptor → DB | ✅ Must proxy | Medium |
| `POST /api/pipelines/*/trigger` | `PipelinesController` → `PipelineService` → DB | ✅ Must proxy | **High** — every execution trigger |
| Every request (middleware) | `SessionAuthInterceptor` validates token → DB | ✅ Must proxy or replace | **Critical** — guards all endpoints |
| `/api/locks/*` | Already proxied via `LockEndpoints.cs` | ❌ Done | None |
| `/api/execution/*` | Already proxied via `ExecutionEndpoints.cs` | ❌ Done | None |
| `/api/results/*` | Reads TRX files — no DB | ❌ No change | None |
| `/api/agents/*` | Reads agent gRPC state — no DB | ❌ No change | None |
| `/api/watchlist/*` | Reads XML files — no DB | ❌ No change | None |
| `/api/health/*` | Mostly no DB; some diagnostics | ❌ No change | None |

---

## Part 3: Recommended Target Architecture

### Architectural Principle

```
┌─────────────────┐          ┌──────────────────────────────┐
│  React Client   │──HTTP──▶ │  Standalone WebApi (IIS)     │
│  (browser)      │          │  • Serves SPA from wwwroot/  │
└─────────────────┘          │  • Proxies auth/user/system  │
                             │    to Controller (port 5200) │
                             │  • Serves results/agents/    │
                             │    watchlist locally (no DB) │
                             │  • NO SQLite, NO migrations  │
                             └────────────┬─────────────────┘
                                          │ HTTP forward
                                          ▼
                             ┌──────────────────────────────┐
                             │  WPF Controller (Kestrel)    │
                             │  • Port 5200                 │
                             │  • Owns orchestrator.db      │
                             │  • Runs migrations           │
                             │  • Auth, Users, Audit, Mode  │
                             │  • Execution engine          │
                             └──────────────────────────────┘
```

### Recommended Split of `AddRbacFeature()`

| New method | Used by | Registers |
|---|---|---|
| `AddRbacControllerHost(config)` | WPF Controller + its embedded API | Everything currently in `AddRbacFeature()` — DB, migrations, session store, auth service, audit drain, user service, mode transition |
| `AddRbacWebApiHost(config)` | Standalone WebApi | Only: `RbacOptions` (config binding), `WritableOptions<RbacOptions>`, `PasswordHasher`, `SystemModeBroadcaster`. Registers `ThrowingDbContextFactory` as safety guard. Does NOT register `DatabaseInitializerService`, `AuditDrainWorker`, `SessionStore`, `AuthService`, `UserService`, `PipelineService`, or `RbacModeTransitionService`. |

The WebApi would replace the DB-backed controllers with proxy endpoints (same pattern as `LockEndpoints.cs`), using an expanded `ControllerProxyService` that supports POST/PUT with body forwarding.

---

## Part 4: Step-by-Step Implementation Plan

| # | Step | What it does | Test | Risk |
|---|------|-------------|------|------|
| 1 | **Implement `isPrimaryHost` guard in `AddRbacFeature()`** | When `isPrimaryHost=false`: skip `DatabaseInitializerService`, `AuditDrainWorker`, replace `IDbContextFactory` with `ThrowingDbContextFactory`, skip registering `AuthService`, `UserService`, `PipelineService`, `RbacModeTransitionService`. | WebApi starts without touching any `.db` file. Confirm no `SqliteException` at startup. | **Low** — existing param, additive logic. |
| 2 | **Add `ForwardPostWithBodyAsync` and `ForwardPutAsync` to `ControllerProxyService`** | Reads the request body, forwards it as JSON with auth header to the controller. | Unit test: mock HttpClient, verify body + header forwarded. | **Low** — additive method, no behavior change. |
| 3 | **Create `AuthProxyEndpoints.cs`** in `TestController.WebApi/Endpoints/` | Minimal API endpoints that proxy `api/auth/*` to the controller via `ControllerProxyService`. Same pattern as `LockEndpoints.cs`. | Call `/api/auth/login` → verify it hits controller on port 5200 and returns a token. | **Medium** — login is critical path. |
| 4 | **Create `UserProxyEndpoints.cs`** | Proxy `api/users/*` (GET list, GET by id, POST create, PUT update, DELETE, assignments, reset-password). | Verify CRUD round-trip through proxy. | **Low** — admin-only, same pattern. |
| 5 | **Create `SystemModeProxyEndpoints.cs`** | Proxy `api/system/mode`, `api/system/mode/secured`, `api/system/mode/default`. | Verify mode switch returns success from controller. | **Low** — rare operation. |
| 6 | **Create `SecurityProxyEndpoints.cs`** | Proxy `api/security/status`, `capabilities`, `whoami`, `readiness`. | Verify capabilities return matches direct controller call. | **Low** — read-only. |
| 7 | **Create `PipelineProxyEndpoints.cs`** | Proxy `api/pipelines/{id}/trigger` and `api/pipelines/{id}/cancel`. | Trigger a pipeline from WebClient → verify it executes on controller. | **Medium** — execution path. |
| 8 | **Replace `SessionAuthInterceptor` in WebApi with a proxy-based token validator** | Instead of opening the DB to validate tokens, forward the token to the controller's `/api/auth/me` endpoint. Cache the result briefly (5s). | Verify all WebApi endpoints still require valid auth. | **High** — changes auth for every request. Must handle controller-down gracefully. |
| 9 | **Remove shared controller mappings from WebApi for proxied routes** | Stop `UseControllerApi()` from mapping `AuthController`, `UserController`, `SystemModeController`, `PipelinesController`, `SecurityController` in the standalone WebApi. Keep the shared controllers in the WPF embedded API only. Or: exclude via `[ApiExplorerSettings]` or a host-conditional attribute. | Verify no 404s — proxy endpoints serve the same routes. | **Medium** — must ensure route parity. |
| 10 | **Deploy to IIS and validate** | Run the WebApi under IIS with no `orchestrator.db` in the deploy folder. Confirm all WebClient operations work through the proxy. | Full end-to-end: login, view users, trigger execution, view results. | **Medium** — integration test. |

---

## Part 5: Recommendation — Demo vs. Deferred

### Can we defer this refactor and demo using the Controller's embedded API directly?

**Yes.** Here's why:

The WPF Controller already exposes a fully functional REST API + SignalR hub on port 5200 (via `ControllerWebApiHost`). The React WebClient can be pointed directly at it by:

1. Setting `VITE_API_BASE_URL=http://<controller-machine>:5200` in the React `.env` file (or in the IIS-deployed build).
2. The Controller's embedded API already has CORS configured for localhost:3000/5173/8080/8081 + machine name.

**What works today against port 5200:**
- ✅ Login/logout/me
- ✅ User management (CRUD)
- ✅ System mode switching
- ✅ Pipeline authorization
- ✅ Execution triggering + real-time output via SignalR
- ✅ Lock operations
- ✅ Results browsing
- ✅ Agent monitoring

**What you lose by skipping the standalone WebApi for the demo:**
- No IIS hosting of the React SPA (serve it from a static file server, or just use `npm run preview`/Vite dev server).
- The Controller must be running for the web dashboard to work (no standalone mode).
- No rate limiting, no OpenTelemetry metrics endpoint (these are WebApi-only features).

### Recommendation

> **For a month-end demo: point the React WebClient directly at the Controller's embedded API (port 5200).** This requires zero code changes — only a configuration update in the React build. The full DB-decoupling refactor (steps 1–10 above) can be done post-demo when there's time to properly test the proxy layer.

**To do this:**
1. Build the React WebClient with `VITE_API_BASE_URL=http://<machine>:5200`
2. Serve the built files from any web server (IIS static, nginx, or `npx serve dist`)
3. Ensure the WPF Controller is running — it handles everything

This avoids the IIS/SQLite issue entirely because the Controller's embedded Kestrel has no file-permission problems (it runs as the logged-in user who already owns the database).
