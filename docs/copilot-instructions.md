# Project: TestAgentSolution

You are working on TestAgentSolution, a distributed gRPC test orchestration platform for AVEVA System Platform QA automation. Two host processes (the WPF Controller and the standalone WebApi), one React WebClient, a Windows Agent service, and shared libraries.

## Solution Layout (actual projects)

- `TestControllerGrpc.Core/` — shared library: proto-generated gRPC types, domain models, service interfaces, XML parser, session manager, AppLogger.
- `TestController.Api/` — shared ASP.NET Core class library: controllers, SignalR hubs, middleware, `AddMultiIdentitySecurity()` and `AddControllerApi()` extension methods. Referenced by both hosts.
- `TestController.WebApi/` — standalone ASP.NET Core host: REST + SignalR for React client, proxies to agents via gRPC.
- `TestControllerGrpc/` — WPF Controller desktop app: hosts gRPC server + WebApi + file watchers + MVVM UI. The all-in-one host.
- `TestController.WebClient/` — React 18 + TypeScript + Vite + Tailwind + Zustand + SignalR.
- `TestAgentGrpc/` — Windows agent service: gRPC server + system tray.
- `TestAgentDisplay/`, `TestController.Dashboard/` — auxiliary WPF clients.
- `TestControllerGrpc.Tests/`, `TestController.WebApi.Tests/` — xUnit.

## RBAC Feature — New Persistence Project

The RBAC feature adds a NEW project `TestController.Persistence/` for EF Core + SQLite (DbContext, entity configurations, migrations, repositories, authorization service implementation, audit drain worker). Referenced by both `TestControllerGrpc` (WPF host) and `TestController.WebApi` (web host). This is the only structural addition; all other RBAC code lands in existing projects.

## Active Design Specifications (read before generating code)

- `docs/rbac/00_Master_Plan.md` — decisions and phase summary
- `docs/rbac/01_System_Design.md` — architecture and SQLite schema
- `docs/rbac/02_Implementation_Roadmap.md` — phase definitions, exit criteria
- `docs/rbac/03_Integration_With_Lock_Spec.md` — pipeline lock model (separate from existing `AgentLockManager`, which locks agents not pipelines)
- `docs/rbac/04_UI_Mockup_Catalog.md` — visual design rationale
- `docs/rbac/05_Default_Mode_Design.md` — Default vs Secured mode
- `docs/rbac/06_Copilot_Implementation_Plan.md` — token-efficient workflow
- `docs/rbac/07_Implementation_Toolkit.md` — the practical handoff guide (start here)
- `docs/rbac/phase-packs/phase-N-context.md` — attach the current-phase pack to each session

## Current Codebase Facts

See `docs/architecture/CURRENT_STATE.md` — captures existing types, services, DI registrations, executor flow, WatchList schema, and load-bearing weirdness. Always check this before assuming a pattern.

## Coding Conventions

See `docs/architecture/CONVENTIONS.md` — CommunityToolkit.Mvvm patterns, Zustand stores, custom `AppLogger` (NOT Serilog), Singleton DI defaults, `Method_Should_Expected_When_State` test naming, etc.

## Operating Principles (apply to every response)

1. **Output code, not explanations.** Unless explicitly asked, do not narrate your reasoning. Generate the requested artifact and stop.

2. **Reference, don't restate.** When a design doc covers something, cite it by section number ("per `01_System_Design.md` §3.4") instead of paraphrasing.

3. **No speculative changes.** Generate exactly what was asked. Do not "while we're here" modify adjacent files.

4. **No clarifying questions for things in the docs.** If the answer is in docs 00-07 or `CURRENT_STATE.md`, just apply it. Ask only for genuinely missing context.

5. **Match existing patterns.** Before introducing a new pattern, check `CURRENT_STATE.md` and `CONVENTIONS.md`.

6. **Default to small diffs.** When modifying existing files, output unified diffs or surgical changes, not full file replacements.

7. **Tests follow code structure** with `Method_Should_Expected_When_State` naming per the existing convention.

8. **Use Singleton DI lifetime** unless there's a specific reason for Scoped (per `CONVENTIONS.md` — the codebase is Singleton-heavy). Only `OrchestratorDbContext` is Scoped (EF Core requirement); access it from Singleton services via `IDbContextFactory<OrchestratorDbContext>`.

9. **Use `IAppLogger`, not Serilog.** The pattern is `_logger.Info("Category", "message")` / `_logger.Warn(...)` / `_logger.Error("Category", "msg", ex)`. Only use `ILogger<T>` for framework-level logging in hosted services.

10. **Use CommunityToolkit.Mvvm** for any new WPF view models — `[ObservableProperty]` and `[RelayCommand]` source-generators. NO manual `INotifyPropertyChanged`.

11. **Use Zustand** for any new React state — one store per domain (`useXxxStore`). NO Redux, NO Context for global state.

12. **Use the existing custom `apiFetch<T>()` wrapper** in `src/lib/api.ts` for new API calls, not raw `fetch` or new axios instances.

13. **Existing `AddMultiIdentitySecurity()` stays.** The RBAC feature ADDS session-based auth via a gRPC interceptor; it does NOT replace the existing NTLM/negotiate + API-key + roles middleware. The two coexist on different request paths.

14. **`AgentLockManager` is a different lock layer** from the pipeline lock work. AgentLockManager locks remote agent machines per execution session. The pipeline lock (designed in `03_Integration_With_Lock_Spec.md`) locks WatchItems per user/client. Phase 0 does not touch AgentLockManager; Phase 3 adds the pipeline lock alongside it.

15. **All UUIDs are app-generated**, not DB-generated. SQLite has no `NEWID()`. Use `Guid.NewGuid()` in C# and store as lowercase hyphenated TEXT.

16. **Audit writes are fire-and-forget**, NOT awaited in the request path. Use the `IAuditWriter` queue pattern, never `await _auditWriter.WriteAsync(...)` inside `CanAsync` or any request-path code.

17. **Don't paste large doc sections into responses.** Cite by path and section; the reader can open the file.
