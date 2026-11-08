# 07 — Implementation Toolkit (start here)

> **Read this first.** This is the practical handoff guide that ties together the design docs, the codebase audit results, and the Copilot workflow. Reading order, loading order, day-by-day plan, copy-paste prompts.

> **Audience**: you, the senior engineer about to implement this feature. You know how to write code. You don't want to wade through 6 design docs to figure out what to do on Monday morning. This doc tells you.

> **Time to read**: 15-20 minutes.

---

## 1. What you now have

After this conversation, your `docs/` folder contains:

```
docs/
├── architecture/
│   ├── CURRENT_STATE.md          (from your audit run — facts about TestAgentSolution)
│   └── CONVENTIONS.md            (from your audit run — coding patterns the team uses)
└── rbac/
    ├── 00_Master_Plan.md         executive overview, all decisions resolved
    ├── 01_System_Design.md       architecture, SQLite schema, AuthZ logic
    ├── 02_Implementation_Roadmap.md   12 phases with exit criteria
    ├── 03_Integration_With_Lock_Spec.md  pipeline lock model + AgentLockManager coexistence
    ├── 04_UI_Mockup_Catalog.md   visual design rationale paired to phases
    ├── 05_Default_Mode_Design.md no-auth mode + Default↔Secured switch flows
    ├── 06_Copilot_Implementation_Plan.md  token-efficient workflow + per-phase task lists
    ├── 07_Implementation_Toolkit.md  THIS FILE — practical guide
    └── phase-packs/
        └── phase-0-context.md    attach this to every Phase 0 Copilot session

And at the repo root:
.github/copilot-instructions.md   (paste from copilot-instructions.md in deliverables)
```

Plus the standalone audit prompt at `docs/architecture/audit-prompt.md` for future re-audits.

---

## 2. What changed after the codebase audit

The design docs were written before the audit; the audit revealed truths that changed three things:

1. **No `ControlNode.*` projects exist.** The actual projects are `TestControllerGrpc.Core`, `TestController.Api`, `TestController.WebApi`, `TestControllerGrpc` (WPF host), etc. All Phase 0 file paths have been updated in docs 01, 02, 06, and the phase-0 pack to use the actual names.

2. **`AddMultiIdentitySecurity()` already exists.** The codebase already has NTLM/negotiate + API-key + roles authentication. Phase 0 ADDS session-based auth alongside it (via a gRPC interceptor). It does NOT replace the existing security middleware. The two coexist on different request paths.

3. **`AgentLockManager` already exists.** This locks remote agent *machines* per execution session. The pipeline lock in Phase 3 is a different layer — it locks *WatchItems* per user/client. Both coexist. Doc 03 §1.1 explains the distinction in detail; engineers must not confuse them.

One structural addition: a new `TestController.Persistence` project for EF Core + SQLite. Everything else lands in existing projects.

---

## 3. Reading order — for you (the human)

If you only have 30 minutes, read in this order:

1. **`07_Implementation_Toolkit.md`** (this file) — 15 min
2. **`00_Master_Plan.md` §1, §3, §4** — 10 min (skip §6, §7 unless you want context)
3. **`02_Implementation_Roadmap.md` Phase 0 section** — 5 min

That's it. You can start Phase 0 work after these three.

If you have 90 minutes and want the full picture before starting:

4. **`06_Copilot_Implementation_Plan.md` Parts 1, 2, 5, 7** — 15 min (the strategy + task template + anti-patterns)
5. **`01_System_Design.md` §1, §3, §4** — 15 min (architecture, AuthZ, auth flow)
6. **`05_Default_Mode_Design.md` §1-4** — 10 min (the mode switch concept)
7. **`phase-packs/phase-0-context.md`** — 5 min (what you'll attach to Copilot)

Skip for now (read when relevant):
- `04_UI_Mockup_Catalog.md` — for Phase 0.5 (UI work)
- `03_Integration_With_Lock_Spec.md` — for Phase 3 (locks)
- Phases 1-10 details in `02` — for those specific phases when you reach them

---

## 4. Loading order — for Copilot (per session)

Every Copilot session loads context in this order:

| Layer | What | Loaded by | Cost per session |
|---|---|---|---|
| 1 | `.github/copilot-instructions.md` | GitHub Copilot, automatic | ~700 tokens (every session) |
| 2 | `docs/architecture/CURRENT_STATE.md` + `CONVENTIONS.md` | Pin via `@workspace` or explicit reference in prompt | ~2K tokens when referenced |
| 3 | `docs/rbac/phase-packs/phase-N-context.md` for the current phase | Attach at start of session | ~2K tokens |
| 4 | Your task prompt referencing specific doc sections | You type it | ~500 tokens |

**Layer 1 is the most important — it makes everything else cheap.** Copy `copilot-instructions.md` from the deliverables to `.github/copilot-instructions.md` in your repo root. Commit it. After that, every Copilot session automatically has the project context.

When you start a phase, attach the corresponding phase pack to your first session and re-attach it each time you open a new session for that phase. (Some Copilot clients remember pinned files across sessions; others don't.)

---

## 5. The 30-minute one-time setup

Do this once, today or tomorrow. After this, every implementation task is fast.

### Step 1 (5 min): commit the docs to your repo

```powershell
cd $YOUR_REPO_ROOT
mkdir -Force docs/architecture, docs/rbac, docs/rbac/phase-packs

# Copy all the .md files from the deliverables zip into the matching locations
# CURRENT_STATE.md and CONVENTIONS.md from your audit run go in docs/architecture/
# 00 through 07 .md files go in docs/rbac/
# phase-0-context.md goes in docs/rbac/phase-packs/
# audit-prompt.md goes in docs/architecture/

git add docs/
git commit -m "Add RBAC feature design docs and architecture audit"
```

### Step 2 (2 min): drop in the copilot-instructions

```powershell
mkdir -Force .github
# Copy copilot-instructions.md from the deliverables to .github/copilot-instructions.md
git add .github/copilot-instructions.md
git commit -m "Add Copilot instructions for RBAC feature work"
```

### Step 3 (5 min): create the new TestController.Persistence project skeleton

```powershell
cd $YOUR_REPO_ROOT
dotnet new classlib -n TestController.Persistence -f net8.0
dotnet sln TestAgentSolution.sln add TestController.Persistence/TestController.Persistence.csproj

# Add EF Core SQLite packages
cd TestController.Persistence
dotnet add package Microsoft.EntityFrameworkCore.Sqlite --version 8.*
dotnet add package Microsoft.EntityFrameworkCore.Design --version 8.*
cd ..

# Reference from both hosts
dotnet add TestControllerGrpc/TestControllerGrpc.csproj reference TestController.Persistence/TestController.Persistence.csproj
dotnet add TestController.WebApi/TestController.WebApi.csproj reference TestController.Persistence/TestController.Persistence.csproj

# Also reference from TestController.Api (which contains the interceptors and services)
dotnet add TestController.Api/TestController.Api.csproj reference TestController.Persistence/TestController.Persistence.csproj

# Verify it builds
dotnet build TestAgentSolution.sln

git add .
git commit -m "Add empty TestController.Persistence project for RBAC feature"
```

### Step 4 (3 min): add the bcrypt package to Persistence

```powershell
cd TestController.Persistence
dotnet add package BCrypt.Net-Next --version 4.*
cd ..
git add .
git commit -m "Add bcrypt package for password hashing"
```

### Step 5 (15 min): sanity-check `CURRENT_STATE.md` and `CONVENTIONS.md`

Read them. Confirm they match what you actually know about the codebase. The audit was thorough but it's worth one human review pass. Fix any errors. Re-commit.

This is the most important step in the setup. If `CURRENT_STATE.md` is wrong, every future Copilot session generates wrong code referencing things that don't exist.

**Total time: ~30 min. You are now ready for Phase 0 Task 1.**

---

## 6. Day 1 walkthrough

Here's hour by hour what your first implementation day looks like.

### 09:00 — Open Phase 0 Task 1

You've decided to start with the foundation: `IUserContext` and `ClientKind`.

Open the relevant docs:
- `docs/rbac/phase-packs/phase-0-context.md` — see Block A, task #1
- `docs/rbac/01_System_Design.md` — read §3 (Authorization Design) and §4 (Authentication Flow)

You spend 10 minutes reading. You now know what `IUserContext` should expose.

### 09:15 — Open a fresh Copilot Chat session

Important: **fresh** session, not "continuing yesterday's conversation."

Attach (pin) `docs/rbac/phase-packs/phase-0-context.md` to the session.

### 09:20 — Paste your first task prompt

```
[SPEC]
- docs/rbac/01_System_Design.md §3 — IUserContext is the identity contract flowing through every gRPC request
- docs/rbac/01_System_Design.md §4 — SessionAuthInterceptor populates IUserContext from session token

[CURRENT STATE]
- No types in TestControllerGrpc.Core/Identity/ folder yet (folder doesn't exist)
- No types in TestControllerGrpc.Core/Authorization/ folder yet
- Convention: PascalCase classes, `I` prefix on interfaces, file-scoped namespaces (see CONVENTIONS.md)

[TASK]
Generate two files:
1. TestControllerGrpc.Core/Identity/IUserContext.cs
2. TestControllerGrpc.Core/Identity/ClientKind.cs

[CONSTRAINTS]
- IUserContext exposes: UserId (string), Username (string), Email (string?),
  Role (Role enum — note Role enum will be generated in task 2; declare as
  Role? and add a TODO comment), ClientKind (ClientKind enum), 
  AssignedPipelineIds (IReadOnlyList<string>)
- ClientKind enum has values: Wpf, Web, Cli
- Both file-scoped namespaces
- XML doc comments on every public member
- No implementation classes in this task — interfaces and enums only

[OUTPUT]
- The two .cs file contents
- Nothing else
```

### 09:25 — Review Copilot's output

Two short files. Quick visual check:
- ✅ namespace is `TestControllerGrpc.Core.Identity`
- ✅ `IUserContext` interface, not class
- ✅ XML docs present
- ✅ ClientKind has the three values
- ❌ Copilot added `[Flags]` attribute to ClientKind — it shouldn't be flags (a client is one kind, not multiple). Ask for surgical fix:

```
Remove the [Flags] attribute from ClientKind. A connection is exactly one
client kind, not a combination.
```

### 09:28 — Save files and close session

Copy the corrected files to disk. Save. Build:

```powershell
dotnet build TestControllerGrpc.Core
```

Build succeeds. Commit:

```powershell
git add TestControllerGrpc.Core/Identity/
git commit -m "Phase 0 task 1: IUserContext interface and ClientKind enum"
```

**Close the Copilot Chat session.** Don't keep it open. The context for the next task is different.

### 09:30 — Open Task 2: Role and Permission enums

New session. Attach phase pack. Paste new prompt:

```
[SPEC]
- docs/rbac/01_System_Design.md §3.1 — full Permission catalog (19 entries)

[CURRENT STATE]
- IUserContext and ClientKind from Task 1 are committed
- TestControllerGrpc.Core/Identity/Role.cs does not exist yet
- TestControllerGrpc.Core/Authorization/Permission.cs does not exist yet
- Authorization/ folder needs to be created

[TASK]
Generate two files:
1. TestControllerGrpc.Core/Identity/Role.cs
2. TestControllerGrpc.Core/Authorization/Permission.cs

[CONSTRAINTS]
- Role enum values: Administrator, SeniorManager, Engineer, Guest
- Permission enum: copy ALL 19 entries from 01_System_Design.md §3.1
  table. Use exact naming (Pipeline_View, Pipeline_Trigger,
  Pipeline_Cancel, Pipeline_Retry, Pipeline_TriggerAll,
  Pipeline_CancelAll, Pipeline_Enable, Pipeline_Disable,
  Pipeline_ForceRelease, User_Create, User_Update, User_Delete,
  User_Assign, User_Revoke, Report_View, Report_Generate, Audit_View,
  Audit_Export, Notification_Mute)
- XML doc on each value referencing its row in the table
- Explicit integer values: Role starting at 0, Permission starting at 0

[OUTPUT]
- The two .cs file contents
- Nothing else
```

You've now generated two files in 5 minutes for ~3K tokens. Repeat the pattern.

### 10:00 onward — Tasks 3, 4, 5, 6

By lunch, you should have all of Block A complete (6 tasks). That's the foundation — every later task depends on it being solid.

### After lunch — Update IUserContext

Now that `Role` exists (from Task 2), go back to `IUserContext` and replace the `Role?` placeholder with the real `Role` type:

```
[TASK]
Update TestControllerGrpc.Core/Identity/IUserContext.cs

Change the Role property from "Role?" placeholder to "Role" (now that
TestControllerGrpc.Core/Identity/Role.cs exists). Remove the TODO comment.

[OUTPUT]
- Unified diff
- Nothing else
```

This is 200 tokens. Trivial.

### End of Day 1

You've completed Phase 0 Block A (6 files), spent ~15K tokens, and the Core project compiles. Tomorrow: Block B.

---

## 7. The five workflow rules that matter most

1. **One task = one Copilot session = one closed chat.** This is the single most important habit. Long-running sessions accumulate context you re-pay for on every message.

2. **Reference docs by path; don't paste them.** Copilot has read them in Layer 1/2. Saying "per `01_System_Design.md` §3.1" is enough.

3. **Demand small diffs when editing existing files.** "Output a unified diff, not the full file" saves tokens and makes review fast.

4. **Reject verbosity immediately.** If Copilot starts explaining its approach, paste: `Output only the code. No explanation.` It will learn from the in-session correction.

5. **Update `CURRENT_STATE.md` at the end of each phase.** A 5-minute habit. Adds the new types Copilot needs to know about for the next phase. Without this maintenance, Phase 1 starts re-asking questions Phase 0 answered.

---

## 8. Per-phase loading checklist (use this every time)

Before starting any task:

- [ ] Fresh Copilot Chat session (not continuing yesterday's chat)
- [ ] `.github/copilot-instructions.md` is present in the repo (Layer 1 auto-loads)
- [ ] `docs/rbac/phase-packs/phase-N-context.md` for the current phase is attached/pinned
- [ ] You know which doc section the task spec lives in (so you can cite it)
- [ ] You know what files this task creates/modifies (from the phase pack)
- [ ] You have the task prompt template ready (Part 5 of doc `06`)

After each task:

- [ ] Review the output for sanity (security, naming, error handling)
- [ ] Apply max 2 revisions in-session ("change line 42 to do X"); after that, just edit by hand
- [ ] Save, build, test, commit
- [ ] Close the Copilot session
- [ ] Move to the next task in a fresh session

---

## 9. When you finish a phase

- [ ] All phase exit criteria pass (per `02_Implementation_Roadmap.md` for that phase)
- [ ] Update `CURRENT_STATE.md`: add the new types and patterns introduced
- [ ] Update `CONVENTIONS.md` only if Copilot consistently violated something — add an explicit rule so the next phase doesn't fight the same battle
- [ ] Generate `phase-packs/phase-(N+1)-context.md` for the next phase using the prompt in `06_Copilot_Implementation_Plan.md` Part 3 Step 4
- [ ] Commit, tag (`phase-0-complete`), notify stakeholders

---

## 10. When things go wrong — quick lookup

| Symptom | First thing to try |
|---|---|
| Copilot generates code with wrong namespace/project paths | Verify `copilot-instructions.md` is in `.github/`, not elsewhere. Re-attach the phase pack. |
| Copilot keeps asking what `IUserContext` should look like | Cite the doc section explicitly: "per `01_System_Design.md` §3, the interface exposes UserId, Username, Email, Role, ClientKind, AssignedPipelineIds." |
| Copilot generates a `Microsoft.Extensions.Logging.ILogger<T>` when you wanted `IAppLogger` | Add to your task prompt's [CONSTRAINTS]: "Use IAppLogger from TestControllerGrpc.Core.Services, NOT Microsoft.Extensions.Logging." Update `CONVENTIONS.md` to make this rule more visible. |
| Copilot generates a Serilog template string | Same as above. The team uses `_logger.Info("Category", "message text")` with string interpolation. |
| Copilot uses `[INotifyPropertyChanged]` manual implementation in a WPF VM | Reject and reprompt: "Use CommunityToolkit.Mvvm. ObservableObject base class. [ObservableProperty] attribute." |
| Copilot uses TanStack Query for a new React hook | Reject and reprompt: "Use Zustand. One store per domain. See useAgentStore for the pattern." |
| Copilot generates code that breaks existing `AddMultiIdentitySecurity()` | The new RBAC interceptor is a gRPC interceptor, not an ASP.NET Core middleware. They live in different pipelines and shouldn't conflict. If Copilot tries to register an authentication scheme in `AddAuthentication(...)`, stop — that's the wrong layer. |
| A task takes 5+ revisions to get right | Stop. The spec is ambiguous. Update either the design doc or the phase pack with the missing constraint. Resume with a fresh session. |
| You realize you missed a file dependency | Generate the missing file FIRST in a new session, then come back and complete the original task. Don't try to do both in one session. |
| Token usage way over estimate for a phase | Audit your session logs — are you running long sessions? Are you pasting large docs? Refresh on Part 7 (workflow rules). |
| `dotnet ef migrations add` fails with "no DbContext" | The startup project doesn't know about your context. Run with `-s TestController.WebApi` (or your host project) explicitly. |
| Migration generated but doesn't create the WAL pragma settings | Pragmas are runtime settings, not part of the migration. They're set in `OrchestratorDbContext.OnConfiguring`. Verify task 7b output includes them. |

---

## 11. Cost projection summary

If you follow this plan:

| Item | Tokens | At Opus pricing |
|---|---|---|
| One-time setup (audit + Layer 1/2 prep) | ~80K | ~$5 |
| Phase 0 (24 tasks) | ~88K | ~$5-6 |
| Phase 0.5 (11 tasks) | ~48K | ~$3 |
| Phase 1 (~20 tasks) | ~110K | ~$7 |
| Phases 2-10 combined | ~530K | ~$32 |
| **Total project** | **~860K** | **~$52** |

Compare to ad-hoc Copilot use without strategy: ~5M tokens, ~$300+. Your savings come from not re-explaining the codebase every session.

---

## 12. What this toolkit does NOT cover

Be honest about scope:

- **It does not write the code for you.** It makes Copilot efficient at writing the code from your specs. You still review, integrate, debug.
- **It does not validate the design.** The design docs (00-05) are your specs; this toolkit assumes they're correct. If you discover during implementation that a design decision was wrong, update the relevant design doc FIRST, then resume.
- **It does not test the system on real infrastructure.** Generated code might compile, pass unit tests, and still misbehave when deployed. Budget time for staging environment validation per phase exit criteria.
- **It does not replace human security review for auth code.** Phase 0 is auth-critical. Get a second pair of eyes on `SessionAuthInterceptor`, `PasswordHasher`, `AuthorizationService`. Copilot writes plausible-looking code that may have subtle vulnerabilities.

---

## 13. Cross-references

- **For workflow strategy**: `06_Copilot_Implementation_Plan.md` (deeper version of Parts 5, 7, 11 of this doc)
- **For the architecture**: `01_System_Design.md`
- **For the phase definitions**: `02_Implementation_Roadmap.md`
- **For the Default mode design** (the recent addition): `05_Default_Mode_Design.md`
- **For UI rationale** (when you reach Phase 0.5): `04_UI_Mockup_Catalog.md`
- **For pipeline lock design** (when you reach Phase 3): `03_Integration_With_Lock_Spec.md`
- **For codebase facts**: `docs/architecture/CURRENT_STATE.md`
- **For coding conventions**: `docs/architecture/CONVENTIONS.md`

---

## 14. The one-line summary

**Drop `copilot-instructions.md` into `.github/`, commit the 8 design docs to `docs/rbac/`, attach `phase-0-context.md` to your first Copilot session, and start with Phase 0 Task 1 using the prompt template in `06_Copilot_Implementation_Plan.md` Part 5. Close the session after each task. That's it.**
