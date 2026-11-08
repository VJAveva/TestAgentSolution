# 06 — Copilot Implementation Plan (Token-Efficient)

> **Purpose**: implement the RBAC + Default Mode design across Phases 0–10 using GitHub Copilot (Claude Opus 4.6) under a usage-based pricing plan, minimizing token cost without sacrificing code quality. The plan assumes Copilot has zero prior knowledge of your codebase and explicitly works around that.

> **Estimated total token cost** with this strategy: **~500-700K tokens** for full implementation across all phases. Without strategy (re-asking Copilot to "understand the project" every session): **~5M tokens**. The savings come from one-time discovery and persistent context.

---

## Part 1 — The Real Problem

Most Copilot waste on a large project comes from **repeated discovery**, not from generating code. Three patterns drain tokens fast:

1. **"Look at my project structure"** — Copilot scans dozens of files, summarizes them, and then you ask the actual question. Every session.
2. **"Follow our conventions"** — Copilot guesses at your patterns instead of being told them.
3. **"How should I implement X?"** — Copilot re-derives architecture decisions you've already made (and documented in docs 00–05).

The fix is a **three-layer context model**:
- **Layer 1**: facts loaded automatically every session (repo-level instructions file)
- **Layer 2**: phase-specific context pack loaded at start of each phase
- **Layer 3**: task-specific prompt (small, focused, references Layers 1 and 2)

When you ask Copilot to generate `SessionAuthInterceptor.cs`, the prompt is small but Copilot has everything it needs because Layers 1 and 2 are already in its context window.

---

## Part 2 — The Three-Layer Context Model

### Layer 1: `.github/copilot-instructions.md` (loaded automatically every session)

Create this file at the repo root. GitHub Copilot reads it on every interaction without you having to attach it. Keep it under 3 KB — it's a permanent tax on every prompt's context.

**Template** (customize the bracketed parts):

```markdown
# Project: TestAgentSolution

You are working on TestAgentSolution, a distributed gRPC test orchestration
platform for AVEVA System Platform QA automation. Two host processes (the WPF
Controller and the standalone WebApi), one React WebClient, a Windows Agent
service, and shared libraries.

## Solution Layout (actual projects)
- `TestControllerGrpc.Core/` — shared library: proto-generated gRPC types,
  domain models, service interfaces, XML parser, session manager, AppLogger.
- `TestController.Api/` — shared ASP.NET Core class library: controllers,
  SignalR hubs, middleware, AddMultiIdentitySecurity() and AddControllerApi()
  extension methods. Referenced by both hosts.
- `TestController.WebApi/` — standalone ASP.NET Core host: REST + SignalR for
  React client, proxies to agents via gRPC.
- `TestControllerGrpc/` — WPF Controller desktop app: hosts gRPC server +
  WebApi + file watchers + MVVM UI. The all-in-one host.
- `TestController.WebClient/` — React 18 + TypeScript + Vite + Tailwind +
  Zustand + SignalR.
- `TestAgentGrpc/` — Windows agent service: gRPC server + system tray.
- `TestAgentDisplay/`, `TestController.Dashboard/` — auxiliary WPF clients.
- `TestControllerGrpc.Tests/`, `TestController.WebApi.Tests/` — xUnit.

## RBAC Feature — New Persistence Project
The RBAC feature adds a NEW project `TestController.Persistence/` for EF Core +
SQLite (DbContext, entity configurations, migrations). Referenced by both
TestControllerGrpc (WPF host) and TestController.WebApi (web host). This is
the only structural addition; all other RBAC code lands in existing projects.

## Active Design Specifications (read before generating code)
- `docs/rbac/00_Master_Plan.md` — decisions and phase summary
- `docs/rbac/01_System_Design.md` — architecture and SQLite schema
- `docs/rbac/02_Implementation_Roadmap.md` — phase definitions, exit criteria
- `docs/rbac/03_Integration_With_Lock_Spec.md` — pipeline lock model (separate
  from existing AgentLockManager, which locks agents not pipelines)
- `docs/rbac/04_UI_Mockup_Catalog.md` — visual design rationale
- `docs/rbac/05_Default_Mode_Design.md` — Default vs Secured mode
- `docs/rbac/07_Implementation_Toolkit.md` — the practical handoff guide
- `docs/rbac/phase-packs/phase-N-context.md` — attach the current-phase pack

## Current Codebase Facts
See `docs/architecture/CURRENT_STATE.md` — captures existing types, services,
DI registrations, executor flow, WatchList schema, and load-bearing weirdness.
Always check this before assuming a pattern.

## Coding Conventions
See `docs/architecture/CONVENTIONS.md` — CommunityToolkit.Mvvm patterns,
Zustand stores, custom AppLogger (NOT Serilog), Singleton DI defaults,
Method_Should_Expected_When_State test naming, etc.

## Operating Principles (apply to every response)

1. **Output code, not explanations.** Unless explicitly asked, do not narrate
   your reasoning. Generate the requested artifact and stop.
2. **Reference, don't restate.** When a design doc covers something, cite it
   by section number ("per 01_System_Design.md §3.4") instead of paraphrasing.
3. **No speculative changes.** Generate exactly what was asked. Do not "while
   we're here" modify adjacent files.
4. **No clarifying questions for things in the docs.** If the answer is in
   docs 00-07 or CURRENT_STATE.md, just apply it.
5. **Match existing patterns.** Before introducing a new pattern, check
   CURRENT_STATE.md and CONVENTIONS.md.
6. **Default to small diffs.** When modifying existing files, output unified
   diffs or surgical changes, not full file replacements.
7. **Tests follow code structure** with Method_Should_Expected_When_State naming.
8. **Use Singleton DI lifetime** unless there's a specific reason for Scoped
   (per CONVENTIONS.md — the codebase is Singleton-heavy).
9. **Use IAppLogger, not Serilog** — `_logger.Info("Category", "message")`
   pattern. Only use ILogger<T> for framework-level logging in hosted services.
10. **Use CommunityToolkit.Mvvm** for any new WPF view models — `[ObservableProperty]`
    and `[RelayCommand]` source-generators. NO manual INotifyPropertyChanged.
11. **Use Zustand** for any new React state — one store per domain.
12. **Use the existing custom apiFetch wrapper** for new API calls, not raw fetch.
```

This file is read on every session. The cost: ~700 tokens per session, every session. Worth it.

### Layer 2: Phase Context Packs (one per implementation phase)

For each phase, create a small file under `docs/rbac/phase-packs/`:

- `phase-0-context.md` (~2 KB) — what Phase 0 delivers, file list, dependencies on Layer 1 facts
- `phase-0.5-context.md`
- `phase-1-context.md`
- ... etc.

Each pack contains:
- 2-3 sentence phase goal
- The full file list for the phase (so Copilot doesn't have to think about what to create)
- Sequence number (which tasks must come first)
- "Watch out for" notes (e.g., "the SessionAuthInterceptor must handle the Default mode short-circuit before token validation")

Attach the pack at the start of each session for that phase. Detach it when you move to the next phase.

### Layer 3: Task Prompts (one per work session)

Each task = one focused outcome = one short prompt. The prompt template is in Part 5 below.

---

## Part 3 — One-Time Setup (do this once, before any feature work)

### Step 1: Run the Codebase Audit

This is the most important step in the entire plan. You spend ~50K tokens once, and save ~3 million tokens over the rest of the project.

The audit produces two files:
- `docs/architecture/CURRENT_STATE.md` — facts about your existing codebase
- `docs/architecture/CONVENTIONS.md` — patterns and conventions

**Audit prompt** (run this in a fresh Copilot session with `@workspace` enabled so it can read your repo):

```
@workspace I need to onboard a coding assistant to this codebase. Generate two
markdown files based on actual inspection of the code (not assumptions):

FILE 1: docs/architecture/CURRENT_STATE.md
Sections:
- Solution layout (every .csproj with its purpose in one sentence)
- Public interfaces in ControlNode.Core (if it exists; otherwise note "to be created")
- Existing gRPC service definitions (list .proto files and the services they define)
- Existing WPF view structure (XAML conventions, MVVM framework, navigation pattern)
- Existing React structure (state management, API client pattern, routing)
- Existing DI registrations in Program.cs (the AddX calls)
- Existing logging setup (which library, sink configuration)
- Existing test projects and frameworks
- Existing executor adapter — how WPF currently triggers pipelines
- WatchList.xml schema (paste current structure)
- Build commands (the actual dotnet/npm commands the team uses)
- Anything weird, hacky, or load-bearing that future code must not break

FILE 2: docs/architecture/CONVENTIONS.md
Sections:
- Naming: classes, interfaces, files, test classes
- File structure: one type per file? grouped namespaces?
- Async conventions: ConfigureAwait, cancellation tokens, naming
- Error handling: exceptions thrown vs Result<T> returned
- Logging: structured logging template, log levels per scenario
- WPF MVVM: how view models connect to views, command pattern used
- React: hooks-only, class components, state management library
- Testing: xUnit fixtures, Vitest patterns, test naming
- DI: constructor injection, service lifetimes, registration style

Use file paths and code snippets to support claims. Do NOT invent conventions —
if a section has no clear pattern in the codebase, write "No clear convention,
recommend: [your suggestion]".

Output: the two markdown files, nothing else.
```

This is the only time you spend tokens on broad discovery. After this, every prompt references these files.

**Sanity check the output.** Read both files yourself. Fix any errors before committing them — they become the source of truth for every future Copilot session.

### Step 2: Commit the design docs to the repo

Put `docs/rbac/00_Master_Plan.md` through `05_Default_Mode_Design.md` into the repo. Copilot can reference them by path. They're already token-optimized (you and Claude designed them carefully).

### Step 3: Create `.github/copilot-instructions.md`

Use the template in Part 2 above. Customize the bracketed parts.

### Step 4: Generate the phase context packs

For each phase you're about to start (don't do them all at once — generate when needed):

```
@workspace Read docs/rbac/02_Implementation_Roadmap.md "Phase 0" section and
generate docs/rbac/phase-packs/phase-0-context.md with:
- 2-sentence goal
- Numbered file list with each file's purpose in <10 words
- Sequence note: which files must come before which (e.g., "interfaces before implementations")
- "Watch out for" bullets: cross-cutting concerns specific to this phase
- Token budget guidance: how to attach context for each task

Keep under 2 KB. Reference docs, don't restate them.
```

Total one-time setup cost: ~80K tokens (50K audit + 30K for 6 phase packs).

---

## Part 4 — The Per-Task Workflow

Every implementation task follows the same loop. Master this loop and your token bill stays predictable.

```
1. Open a fresh Copilot session
2. Attach (or pin) the relevant phase context pack
3. Issue a task prompt using the template in Part 5
4. Review the output, ask for fixes if needed (max 1-2 revisions per session)
5. Commit
6. Close the session
```

**Critical**: close the session after each task. Long-running sessions accumulate context that's no longer relevant and re-bills you for it on every subsequent message.

Two anti-patterns that kill token efficiency:
- **The mega-session** — "let's just keep working in this chat" → context grows, every message costs more. Open fresh sessions per task.
- **The thinking-out-loud session** — "what do you think about how to handle…" → Copilot writes essays. If you need to think, think in your own head or in Claude.ai (not in Copilot, where every token bills against your project budget).

---

## Part 5 — Task Prompt Template

```
[SPEC]
- <doc path> §<section> — <one-line summary of what you need from it>
- <doc path> §<section> — <one-line summary>

[CURRENT STATE]
- <what exists already, e.g., "ControlNode.Core/Identity/IUserContext.cs already created in task 1">
- <what does not exist, e.g., "No SessionStore implementation yet — this task creates the first one">

[TASK]
Generate <file path>

[CONSTRAINTS]
- <specific requirement 1>
- <specific requirement 2>
- Follow patterns in docs/architecture/CONVENTIONS.md
- Do not modify any other file

[OUTPUT]
- The file content
- One line: where in Program.cs to register (if applicable)
- Nothing else
```

**Worked example — Phase 0 Task 5 (SessionAuthInterceptor)**:

```
[SPEC]
- docs/rbac/01_System_Design.md §3.4 — Default mode short-circuit logic
- docs/rbac/01_System_Design.md §4 — auth flow, interceptor responsibilities
- docs/rbac/05_Default_Mode_Design.md §4 — short-circuit code shape

[CURRENT STATE]
- IUserContext, Role, ClientKind, Permission, IAuthorizationService all exist (tasks 1-4)
- ISessionStore exists (task 4)
- RbacOptions exists (task 4)
- No interceptors registered yet
- DI conventions: see docs/architecture/CONVENTIONS.md §DI

[TASK]
Generate src/ControlNode.Api/Interceptors/SessionAuthInterceptor.cs

[CONSTRAINTS]
- Inherit from Interceptor (Grpc.Core)
- Handle both UnaryServerHandler and ClientStreamingServerHandler (we use both)
- In Default mode (RbacOptions.Enabled = false): skip token validation, inject
  DefaultUser.ForClient(clientKind)
- Detect ClientKind from gRPC metadata header "x-client-kind" (values: "wpf", "web", "cli")
- In Secured mode: read "authorization: bearer <token>" header, look up session,
  inject IUserContext from session.User. Throw RpcException(Unauthenticated)
  on missing/invalid token.
- Use IOptionsMonitor<RbacOptions> so flag changes are picked up live without restart
- Async; no blocking calls
- Stash IUserContext into ServerCallContext.UserState["user"]
- Follow patterns in docs/architecture/CONVENTIONS.md

[OUTPUT]
- The .cs file content
- One line: the AddSingleton or AddScoped call to add in Program.cs
- Nothing else
```

This prompt is ~600 tokens. Output is ~1500 tokens (one focused file). Total cost: ~2K tokens for one delivered component. Compare to "implement the auth interceptor" with no context: easily 10-20K tokens of back-and-forth.

---

## Part 6 — Phase 0 & 0.5 Task Sequence (concrete)

Here's the ordered task list for Phase 0 and Phase 0.5. Run them in this order. Each task = one Copilot session.

### Phase 0 — Identity & Authorization Foundations (~20 tasks, ~100K tokens)

**Block A: Core types (do these first, no dependencies)**

| # | Task | File | Est. tokens |
|---|------|------|-------------|
| 1 | Generate `IUserContext` interface and `ClientKind` enum | `TestControllerGrpc.Core/Identity/IUserContext.cs`, `TestControllerGrpc.Core/Identity/ClientKind.cs` | 2K |
| 2 | Generate `Role` enum and `Permission` enum | `TestControllerGrpc.Core/Identity/Role.cs`, `TestControllerGrpc.Core/Authorization/Permission.cs` | 3K |
| 3 | Generate `AuthDecision` record and `IAuthorizationService` interface | `TestControllerGrpc.Core/Authorization/AuthDecision.cs`, `TestControllerGrpc.Core/Authorization/IAuthorizationService.cs` | 2K |
| 4 | Generate `User`, `PipelineAssignment`, `AuditEntry` entities (POCO, no logic) | `TestControllerGrpc.Core/Identity/User.cs`, `TestControllerGrpc.Core/Identity/PipelineAssignment.cs`, `TestControllerGrpc.Core/Audit/AuditEntry.cs` | 3K |
| 5 | Generate `DefaultUser` static and `SyntheticUserContext` | `TestControllerGrpc.Core/Identity/DefaultUser.cs`, `TestControllerGrpc.Core/Identity/SyntheticUserContext.cs` | 2K |
| 6 | Generate `RbacOptions` configuration class | `TestControllerGrpc.Core/Configuration/RbacOptions.cs` | 1K |

**Block B: Persistence + Infrastructure (depends on Block A)**

These land in a NEW `TestController.Persistence` project (the only structural addition).

| # | Task | File | Est. tokens |
|---|------|------|-------------|
| 7a | Create the `TestController.Persistence` .csproj and reference from both hosts (WPF + WebApi) | New project + .sln entry | 2K |
| 7b | Generate `OrchestratorDbContext` with all entity configurations | `TestController.Persistence/OrchestratorDbContext.cs` + `Configurations/*.cs` | 8K |
| 8 | Generate EF Core initial migration (`dotnet ef migrations add Initial -p TestController.Persistence`) | `TestController.Persistence/Migrations/*` | 3K |
| 9 | Generate `SessionStore` implementing `ISessionStore` | `TestController.Persistence/Identity/SessionStore.cs` | 4K |
| 10 | Generate `PasswordHasher` (bcrypt wrapper) | `TestController.Persistence/Identity/PasswordHasher.cs` | 2K |
| 11 | Generate `AuthorizationService` implementing `IAuthorizationService` with Default-mode short-circuit | `TestController.Persistence/Authorization/AuthorizationService.cs` | 5K |
| 12 | Generate `WritableOptions<RbacOptions>` that persists changes to appsettings.json | `TestControllerGrpc.Core/Configuration/WritableOptions.cs` | 4K |
| 13 | Generate `QueuedAuditWriter` + `AuditDrainWorker` (uses existing IAppLogger pattern) | `TestController.Persistence/Audit/QueuedAuditWriter.cs`, `AuditDrainWorker.cs` | 4K |
| 14 | Generate `RbacModeTransitionService` (Default↔Secured atomic transitions) | `TestController.Api/SystemMode/RbacModeTransitionService.cs` | 6K |

**Block C: API surface (depends on Blocks A and B)**

| # | Task | File | Est. tokens |
|---|------|------|-------------|
| 15 | Generate `SessionAuthInterceptor` (with Default mode short-circuit). Integrates with existing `AddMultiIdentitySecurity()` infrastructure — does not replace it. | `TestController.Api/Interceptors/SessionAuthInterceptor.cs` | 5K |
| 16 | Generate `AuditLoggingInterceptor` | `TestController.Api/Interceptors/AuditLoggingInterceptor.cs` | 2K |
| 17 | Generate `auth.proto` and the generated stubs | `TestControllerGrpc.Core/Protos/auth.proto` | 2K |
| 18 | Generate `AuthGrpcService` (LoginAsync, LogoutAsync, ChangePasswordAsync) | `TestController.Api/Services/AuthGrpcService.cs` | 4K |
| 19 | Generate `SystemModeGrpcService` (Get/SwitchToSecured/SwitchToDefault) | `TestController.Api/Services/SystemModeGrpcService.cs` | 3K |
| 20 | Generate DI registrations as a new extension method `AddRbacFeature()` and call it from both `TestControllerGrpc/App.xaml.cs` and `TestController.WebApi/Program.cs` | `TestController.Api/RbacFeatureExtensions.cs` + surgical diffs | 4K |

**Block D: Tests (depends on Blocks A-C)**

| # | Task | File | Est. tokens |
|---|------|------|-------------|
| 21 | Generate `AuthorizationServiceTests` (Default mode + Secured mode parametrized) | `TestControllerGrpc.Tests/Rbac/AuthorizationServiceTests.cs` | 5K |
| 22 | Generate `SessionAuthInterceptorTests` (both modes) | `TestController.WebApi.Tests/Rbac/SessionAuthInterceptorTests.cs` | 4K |
| 23 | Generate `RbacModeTransitionServiceTests` (atomic transitions) | `TestControllerGrpc.Tests/Rbac/RbacModeTransitionServiceTests.cs` | 4K |
| 24 | Generate end-to-end integration test for Login → authenticated RPC → logout | `TestController.WebApi.Tests/Rbac/AuthE2ETests.cs` | 5K |

**Phase 0 total: ~88K tokens.**

### Phase 0.5 — Default Mode UX (~10 tasks, ~50K tokens)

**Block A: WPF**

| # | Task | File | Est. tokens |
|---|------|------|-------------|
| 1 | Generate `SystemModeClient` (gRPC client wrapper) | `WpfClient/Services/SystemModeClient.cs` | 2K |
| 2 | Generate `UserIdentityBadge` user control (handles Admin/Engineer/SrMgr/Guest/Observer/Default user) | `WpfClient/Controls/UserIdentityBadge.xaml(.cs)` | 5K |
| 3 | Generate `DefaultModeBanner` user control | `WpfClient/Controls/DefaultModeBanner.xaml(.cs)` | 3K |
| 4 | Generate `SecurityModePanel` (Mockup 10 — Settings page) | `WpfClient/Views/Settings/SecurityModePanel.xaml(.cs)` + ViewModel | 8K |
| 5 | Generate `InitialAdminWizard` modal (Default → Secured) | `WpfClient/Views/Settings/InitialAdminWizard.xaml(.cs)` | 6K |
| 6 | Generate `DisableRbacConfirmDialog` (Secured → Default) | `WpfClient/Views/Settings/DisableRbacConfirmDialog.xaml(.cs)` | 4K |

**Block B: Web Client**

| # | Task | File | Est. tokens |
|---|------|------|-------------|
| 7 | Generate `useSystemMode` hook | `WebClient/src/hooks/useSystemMode.ts` | 2K |
| 8 | Generate `UserIdentityBadge.tsx` component (handles all variants) | `WebClient/src/components/header/UserIdentityBadge.tsx` | 4K |
| 9 | Generate `DisabledTriggerButton.tsx` component | `WebClient/src/components/common/DisabledTriggerButton.tsx` | 3K |
| 10 | Update existing trigger button locations to use the new disabled component (surgical diffs) | Multiple existing files | 5K |

**Block C: Integration test**

| # | Task | File | Est. tokens |
|---|------|------|-------------|
| 11 | Generate `ModeSwitchE2ETests` covering both directions | `Tests/Phase0_5/ModeSwitchE2ETests.cs` | 6K |

**Phase 0.5 total: ~48K tokens.**

### Aggregate token budget through Phase 10

| Phase | Effort | Est. Copilot tokens |
|---|---|---|
| One-time setup (audit + packs) | 1-2 days | 80K |
| Phase 0 | 2 weeks | 85K |
| Phase 0.5 | 1 week | 48K |
| Phase 1 | 3 weeks | 110K |
| Phase 2 | 3 weeks | 90K |
| Phase 3 | 2 weeks | 70K |
| Phase 4 | 2 weeks | 50K |
| Phase 5 | 1 week | 25K |
| Phase 6 | 1 week | 20K |
| Phase 7 | 2 weeks | 50K |
| Phase 8 | 3 weeks | 80K |
| Phase 9 | 3 weeks | 80K |
| Phase 10 | 2 weeks | 60K |
| **Total** | **25 weeks (sequential)** | **~850K tokens** |

At Claude Opus pricing (~$15/M input + ~$75/M output, roughly 30:70 split): **~$50-60 total** for full implementation across 25 weeks. Compare to "ad-hoc Copilot use without strategy" at ~$300-500 for the same outcome.

---

## Part 7 — Anti-Patterns to Avoid

These habits silently 10x your token bill:

| Anti-pattern | Cost | Fix |
|---|---|---|
| "Look at my repo and tell me what's there" every session | 20-40K per session | Run the codebase audit once. Reference `CURRENT_STATE.md` instead. |
| Pasting the SRS into every prompt | 5-15K per prompt | The SRS is replaced by docs 00-05. Reference them by path. |
| Asking for "the right way to do auth" | 10K of essay-writing | The right way is in `01_System_Design.md` §3-4. Just say "implement per §3.4". |
| Letting Copilot generate full file replacements when you wanted a 3-line change | 2-5K wasted per edit | Ask explicitly: "Output a unified diff, not the full file." |
| Long-running sessions where context accumulates | Grows quadratically | Close the session after each task. |
| Accepting verbose Copilot explanations | 1-3K of unread text per session | Add "Output only the code, no explanation" to every prompt. The instructions file should enforce this but it slips. |
| Asking Copilot to "review this design" | 20K of opinionated essays | If you want a design review, do it with Claude in claude.ai (separate billing) or with a human teammate. |
| Generating a file with Copilot, then asking it to test the file in a different session | Double-paying for context | Generate the test in the same session as the file, while context is hot. |
| Re-prompting because the first answer "wasn't quite right" without telling Copilot specifically what was wrong | 3-5x amplification on each iteration | Be specific: "Move the null check to line 42" not "fix the bug". |

---

## Part 8 — When NOT to Use Copilot (use Claude.ai or your own head)

Some work is better done outside Copilot, either because it's cheaper elsewhere or because Copilot adds friction:

| Task | Better venue | Why |
|---|---|---|
| Design discussions, architecture trade-offs | Claude.ai chat | Conversational mode, your subscription, no per-token billing |
| One-off "explain this stack trace" | Claude.ai or your IDE's built-in error explainer | Single Q&A; doesn't need codebase context |
| Drafting commit messages, PR descriptions | Claude.ai or a free model | Small text generation; Copilot is overkill |
| Researching a third-party library's API | The library's docs + Claude | Copilot doesn't always have current API knowledge |
| Refactoring a single file you understand well | Your IDE's refactoring tools | Faster, deterministic, no token cost |
| Generating boilerplate that has a template | Snippets or a custom Yeoman/dotnet template | Free, instant |
| Writing the design docs themselves | Claude.ai | Already done — see docs 00-05 |
| Code review of generated code | Your own eyes + xUnit | Copilot is bad at reviewing its own output |
| Bulk renaming a class across many files | IDE refactoring | Mechanical, deterministic |
| Generating mock/stub data fixtures | Claude.ai chat | Conversational refinement |

**Heuristic**: use Copilot when the work is "generate this specific file given this specific spec." Use Claude.ai when the work is "think with me." Use your IDE when the work is mechanical.

---

## Part 9 — Practical Workflow Per Day

Here's what a productive day looks like under this plan:

**Morning (30 min, no Copilot)**
1. Open today's task in the phase context pack
2. Read the relevant design doc section (max 5 min)
3. Sketch the task prompt using the template in Part 5
4. Identify what files Copilot needs to know exist already (CURRENT_STATE.md handles most of this)

**Mid-day (focused work, Copilot sessions)**
5. Open a fresh Copilot session per task
6. Run the task prompt
7. Review the output for sanity (security, naming, error handling)
8. If revision needed: be surgical ("change line 42 to do X") — never "fix it" or "improve it"
9. Save, commit, close the session

**End of day (10 min, no Copilot)**
10. Update the phase context pack: which tasks are done, which discovered changes
11. Note any patterns Copilot got wrong consistently — add them to CONVENTIONS.md as explicit rules

This workflow makes Copilot a tool for *execution*, not for *thinking*. Thinking is your job; Copilot just types faster than you do.

---

## Part 10 — Maintaining the Context Files

The context files (Layer 1 and Layer 2) decay if you don't tend them. Two maintenance habits:

1. **End-of-phase update**: when a phase completes, update `CURRENT_STATE.md` with new types and patterns introduced. Costs ~2K tokens.
2. **CONVENTIONS.md adjustments**: if you notice Copilot consistently violating a convention, add an explicit rule to CONVENTIONS.md. Future sessions will follow it.

Avoid the temptation to bloat these files. Both should stay under 5 KB each. If they get bigger, you're putting things in them that should be in the design docs.

---

## Part 11 — Getting Started Tomorrow

Concrete first-day plan:

1. **Hour 1**: create `.github/copilot-instructions.md` using the template in Part 2.
2. **Hour 2**: run the codebase audit prompt from Part 3. Review the generated `CURRENT_STATE.md` and `CONVENTIONS.md`. Fix anything wrong. Commit.
3. **Hour 3**: commit the 6 design docs (`00`–`05`) under `docs/rbac/`.
4. **Hour 4**: generate `phase-0-context.md` using the prompt in Part 3 Step 4.
5. **Hour 5**: run Phase 0 Task 1 using the template in Part 5. Verify the output looks right.
6. **End of day 1**: you have your full context model set up and your first generated file in main.

By end of week 1 you should have Phase 0 Block A complete (~6 files). Week 2: Block B. Week 3: Blocks C and D plus Phase 0.5 starts. Phase 0 + 0.5 ships at end of week 3.

---

## Part 12 — When Things Go Wrong

| Symptom | Probable cause | Fix |
|---|---|---|
| Copilot generates code using a pattern your team doesn't use | CONVENTIONS.md doesn't capture the pattern | Add explicit rule to CONVENTIONS.md |
| Copilot keeps asking clarifying questions about basic project facts | `.github/copilot-instructions.md` not being loaded, or too vague | Verify file is at repo root; tighten language |
| A task takes 5+ revisions to get right | Spec is ambiguous, OR prompt missing key constraints | Stop, write down what's ambiguous, update the design doc or prompt template |
| Generated code passes review but breaks integration tests | Phase context pack missing a "watch out for" item | Add the gotcha to the phase pack and to CONVENTIONS.md |
| Token usage way over estimate for a phase | Long-running sessions, or too much back-and-forth | Audit your session logs; enforce "one task = one session" |
| Copilot generates excellent code for one component then terrible code for the next | Hot session is reading from outdated context | Close the session, start fresh for each task |

---

## Part 13 — What This Plan Does Not Solve

Be honest about the limits:

- **It does not eliminate thinking time.** You still own the architecture, the trade-offs, the review. Copilot is a fast typist, not an engineer.
- **It does not catch security bugs.** Auth, authz, audit — review these by hand or with a security-focused colleague. Copilot generates plausible-looking code that may have subtle vulnerabilities.
- **It does not handle WPF visual design well.** XAML for complex layouts often needs iteration; budget extra tokens for Phase 0.5 UI work.
- **It does not write good tests by itself.** It writes tests that look like the patterns it has seen. You must verify they actually exercise the behavior you care about.
- **It does not replace integration testing on real infrastructure.** Generated code might compile, pass unit tests, and still misbehave when deployed. Budget time for staging environment validation.

Use Copilot for what it's good at (typing code from specs), and keep your engineering judgment for everything else.

---

## Part 14 — Single-Page Cheat Sheet

For your wall, your monitor, or `docs/COPILOT_CHEAT_SHEET.md`:

```
EVERY SESSION:
  - Fresh chat per task (close after)
  - Output code, not essays
  - Reference docs by path, don't restate

EVERY PROMPT (use template):
  [SPEC] → which docs and sections
  [CURRENT STATE] → what exists, what doesn't
  [TASK] → one specific file/change
  [CONSTRAINTS] → explicit rules
  [OUTPUT] → exactly what you want back

NEVER:
  - Paste the whole SRS
  - "Look at my codebase and tell me…"
  - Long-running sessions
  - "Fix it" without saying what's wrong
  - Generate full file when you wanted a 3-line diff

CONTEXT LAYERS:
  Layer 1: .github/copilot-instructions.md (auto-loaded)
  Layer 2: docs/rbac/phase-packs/phase-N-context.md (attach when starting phase)
  Layer 3: your task prompt (small, focused)

WHEN STUCK:
  - Re-read CURRENT_STATE.md and CONVENTIONS.md — is the answer there?
  - Re-read the relevant design doc section — is the spec ambiguous?
  - If genuinely missing context: update the design doc OR the pack, then resume
```

---

## Part 15 — Cross-References

- `00_Master_Plan.md` — the decisions Copilot will implement
- `02_Implementation_Roadmap.md` — the phase definitions Copilot will execute
- `04_UI_Mockup_Catalog.md` — the visual targets for Phase 0.5 and beyond
- `docs/architecture/CURRENT_STATE.md` *(to be generated by audit)* — the codebase facts
- `docs/architecture/CONVENTIONS.md` *(to be generated by audit)* — the coding rules
- `.github/copilot-instructions.md` *(to be created from Part 2 template)* — Layer 1 context
