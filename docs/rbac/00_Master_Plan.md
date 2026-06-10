# 00 — Master Plan

> **Companion to SRS-CN-RBAC-001 v1.0.** This document set turns the SRS into an actionable system design and a phased implementation plan. Read this file first; it tells you what the rest of the documents contain and in what order to read them.

---

## 1. Reality Check — The Scale of This Work

Before any planning, an honest assessment of what the SRS describes:

- **Four roles** with distinct permission profiles, enforced across every server entry point
- **Two clients** (WPF + Web), each with role-conditional UI
- **A new authentication layer** on top of a system that has none today
- **A new authorization layer** with permission catalog, role mapping, and audit
- **User management** including assignments, password lifecycle, deletion semantics
- **Pipeline state machine** (Idle/Running/Disabled) with atomic concurrent-safe transitions
- **Three-level retry hierarchy** (Event / ActionGroup / Action)
- **Two bulk operations** with race-safe per-pipeline outcomes
- **Two background workers** — flaky-detection analyzer + report consolidator
- **A notification subsystem** with cooldown windows and channel abstraction
- **Append-only audit log** with full retention, filtering, export
- **Six open technology decisions** to resolve before construction
- **Eight functional sub-areas** and twelve non-functional requirement categories

For a team of **2-3 engineers**, this is **5-6 months** of focused work (about 22-26 weeks).
For a team of **5-6 engineers** with clean parallelization, **3-4 months** (12-16 weeks).

It is **not** a single sprint, a single release, or a single PR. It must be decomposed into shippable phases where each phase leaves the system in a working, demo-able state. That decomposition is the subject of document **02 — Implementation Roadmap**.

---

## 2. The Document Set

| # | File | Purpose | Audience |
|---|---|---|---|
| 00 | `Master_Plan.md` (this file) | Navigation, decisions, top-down summary | Everyone |
| 01 | `System_Design.md` | Architecture, components, sequence flows, concrete schema | Engineers, tech reviewers |
| 02 | `Implementation_Roadmap.md` | 12 phases with exit criteria and dependencies | Engineering managers, planners |
| 03 | `Integration_With_Lock_Spec.md` | How this reconciles with `Pipeline_Lock_Coordination_Spec.md` | Engineers working on either feature |
| 04 | `UI_Mockup_Catalog.md` | Per-mockup design rationale paired to phases | UI engineers, designers |
| 05 | `Default_Mode_Design.md` | The no-auth operational mode + mode-switch flows | Engineers, ops |

**Recommended reading order:**

1. This file end-to-end (15 minutes)
2. Skim the SRS sections you haven't read recently (Section 5 functional reqs, Section 6 NFRs)
3. `01_System_Design.md` (45-60 minutes)
4. `03_Integration_With_Lock_Spec.md` (20 minutes)
5. `02_Implementation_Roadmap.md` (30 minutes)

---

## 3. Open Decisions — Resolved

The SRS deliberately left six decisions open (§10 OD-01 through OD-06). This section resolves each with a recommended choice and the rationale. Any of these can be overridden by the technical reviewer; the implementation plan in document 02 assumes these resolutions.

### OD-01 — Authentication Mechanism

**Decision: Server-side opaque session tokens, backed by a session store.**

The SRS lists four options. Evaluation against this system's needs:

| Option | Verdict | Why |
|---|---|---|
| JWT bearer tokens | Rejected | Revocation lag (refresh-token mechanics) conflicts with FR-USR-03 ("revoke a pipeline assignment from an Engineer") and Risk #2 ("cached assignments become stale"). Tolerable, but not the best fit. |
| **Server-side session store** | **Selected** | Instant revocation. Instant assignment refresh. Simple mental model. Operationally cheap for an internal system with <100 users. |
| Windows Integrated Auth | Rejected | No Guest path; Web Client deployed off-domain would not work; mixed-environment complexity. |
| Mutual TLS | Rejected | Certificate distribution and lifecycle is operationally expensive for human users; appropriate for service-to-service, not for an Engineer logging in from a laptop. |

**Implementation note:** Sessions are stored in SQLite (the same database file as the rest of persistence). A `Sessions` table holds session id, user id, created/last-used timestamps, ip, and revoked flag. Token is a 256-bit cryptographically random opaque string passed in gRPC metadata. Re-validation per request is a single indexed lookup (~1 ms).

### OD-02 — Transport Security for the gRPC Channel

**Decision: Option (a) — secure only the new client-facing endpoints. Controller-Agent remains as-is for this delivery.**

Rationale:
- Keeps blast radius small. Securing Controller-Agent is its own multi-week initiative with cert distribution to every agent.
- The new attack surface is the Web Client, which is exactly where TLS termination belongs.
- Risk #1 in the SRS acknowledges this trade-off and the mitigation is restricting the new service to the lab subnet, which is acceptable for a v1 internal tool.

Controller-Agent hardening is tracked as a follow-up project and is **out of scope** for this delivery.

### OD-03 — Persistence Engine

**Decision: SQLite with WAL mode.**

Rationale (revised from earlier draft that recommended SQL Server):

For this system's actual workload — single controller node, fewer than 100 users, fewer than 500 pipelines, peak write rate of a few rows per second across audit, runs, and lock-state changes — SQLite is the better operational fit. The prior SQL Server recommendation traded simplicity for institutional alignment with AVEVA SP; that alignment turns out to buy nothing concrete because the Control Node app does not share data with SP itself.

What SQLite gives us:
- **Zero ops.** No separate service to install, no port to firewall, no SA password to manage.
- **File-based.** Backup is `cp orchestrator.db orchestrator.db.bak`. Restore is the reverse.
- **Same database in dev, test, prod.** No "works on my machine" gaps.
- **Trivial unit tests.** In-memory mode for fast unit tests; file mode for integration.
- **WAL mode handles our concurrency.** Many concurrent readers plus one writer — comfortable for our load.
- **Fast at scale.** Handles millions of audit rows with the indexes specified in `01_System_Design.md` §7.1.
- **EF Core treats it identically.** Schema migrations, LINQ queries, transactions all work the same as on SQL Server.

What we accept:
- **Single writer at a time.** WAL mode permits many concurrent readers plus one writer. At our peak write load (a handful per second from audit, run state, and lock-state updates), this is invisible to users.
- **No multi-process sharing.** Everything must run in the controller process. The background workers (FlakyAnalyzer, NotificationDispatcher, ReportGenerator, LockExpirySweeper) already do, so nothing changes.
- **If we ever go multi-controller**, we will migrate to PostgreSQL or SQL Server. That decision is explicitly deferred, and the abstractions (`OrchestratorDbContext`, `ILockRegistry`, repository interfaces) are designed so this is a swap-out (change provider package and connection string, regenerate migrations) — not a rewrite.

**Setup specifics**:
- File path: `orchestrator.db` in the controller's working directory; configurable via `ConnectionStrings:Default` (e.g., `Data Source=C:\TestAgent\orchestrator.db`).
- Required pragmas, set on first open by `OrchestratorDbContext.OnConfiguring`:
  - `PRAGMA journal_mode = WAL;`
  - `PRAGMA synchronous = NORMAL;`
  - `PRAGMA foreign_keys = ON;`
- EF Core provider: `Microsoft.EntityFrameworkCore.Sqlite` (not `.SqlServer`).

### OD-04 — Web Client Framework

**Decision: React + TypeScript, served as static files by the API host.**

This aligns with the prior `WebClient_Enhancement_Spec.md` already in flight. The SRS proposes Blazor as an alternative; the case for staying on React:
- An existing React WebClient codebase is already being enhanced
- Doubling the team's UI tech surface (Blazor + React) costs more than the code-share saves
- gRPC-Web with TypeScript bindings is mature
- Toolchain (Vite, TanStack Query, SignalR client) is already in place

Models are shared via **OpenAPI/gRPC schema-generated TypeScript types**, not by sharing C# code. This achieves the practical goal Blazor would have offered (no manual DTO duplication) without changing the UI stack.

### OD-05 — Notification Delivery Channels

**Decision: Email only at v1.**

Microsoft Teams, in-app, and SMS are deferred. The Notification Dispatcher (§7.6 in the SRS) is built with a `INotificationChannel` abstraction so a Teams or webhook channel can be added later without changing the analyzer or the storage layer.

### OD-06 — Audit Log Storage

**Decision: Same SQLite database file, dedicated indexed table, 12-month retention via a scheduled job.**

A separate log-aggregation system (Seq, Splunk, Elastic) is overkill for an internal QA tool with the expected volume. If audit query performance degrades or volume exceeds expectations, the architecture supports moving the audit store behind the existing `IAuditWriter` interface to a different backing technology (or a separate SQLite file) without changing call sites.

### 3.7 — Operational Mode (Default vs Secured)

**Decision: support two operational modes — Default (no auth, single-operator) and Secured (full RBAC). New installs start in Default. Upgrade to Secured is a one-click Settings flow.**

This decision was not in the original SRS — it surfaced during design review as the path to (a) preserve the current pre-RBAC behavior of the application, (b) give operators a usable system in <30 seconds from install, and (c) let small/lab teams skip RBAC entirely without losing the option to enable it later.

**Default mode**: No login screens. WPF runs all actions as a synthetic "Default user". Web Client is read-only (sees pipelines, tree, live logs, dashboard; cannot trigger). Lock badges show `Locked by Default user (WPF)` on running pipelines. Audit log captures everything with the Default user as actor.

**Secured mode**: Full RBAC as designed across `00`-`04`. Login required on both clients. Four roles, per-pipeline assignments, lock badges show real user identity.

**Toggle mechanism**: A single boolean `RBAC:Enabled` in `appsettings.json`, flipped by the WPF Settings UI. Switching Default → Secured launches an initial-Admin-creation wizard. Switching Secured → Default requires typing `DISABLE RBAC` to confirm. Both transitions are live (no service restart).

**Permission catalog is identical in both modes.** Only `IAuthorizationService.CanAsync` short-circuits when RBAC is off (allow all from WPF, allow read-only from Web, deny writes from Web). This means feature code is written once, no per-mode branching, and every feature in Phases 0-10 must work in both modes (enforced by the test matrix).

Full design in `05_Default_Mode_Design.md`. Mockups 10 and 11 in `04_UI_Mockup_Catalog.md`. Implementation lands in Phase 0 (mechanics, ~2 days of the 2-week phase) and Phase 0.5 (UX, 1-week mini-phase between Phase 0 and Phase 1).

---

## 4. Phase Summary (the full plan is in document 02)

Eleven phases. Each ends in a demo-able state. Phases 0-3 are mandatory foundations; phases 4-10 can be reordered by business priority within the constraints noted.

| # | Phase | Duration (2 eng) | Demo-able outcome |
|---|---|---|---|
| 0 | Identity & AuthZ foundations | 2 weeks | `Can()` API returns correct allow/deny for hard-coded users |
| 1 | User management (Admin only) | 3 weeks | Admin creates users via WPF; login works on both clients |
| 2 | Pipeline trigger + cancel + AuthZ enforcement | 3 weeks | Engineer triggers assigned pipeline; non-assigned is rejected |
| 3 | Lock coordination integration | 2 weeks | WPF and Web see each other's locks; conflicts return 409 |
| 4 | Retry hierarchy (Event / ActionGroup / Action) | 2 weeks | Engineer retries a failed Action |
| 5 | Bulk operations (Trigger All / Cancel All) | 1 week | Admin runs "Trigger All Idle" with mixed outcomes reported |
| 6 | Enable / disable pipelines | 1 week | Admin disables a pipeline; bulk skips it |
| 7 | Read paths and Guest access | 2 weeks | Guest browses results, write attempts rejected |
| 8 | Notifications + flaky detection | 3 weeks | Three consecutive failures triggers an email |
| 9 | Reports (weekly / monthly, PDF + CSV) | 3 weeks | Manager downloads a weekly consolidation |
| 10 | Audit log viewer + hardening | 2 weeks | Admin filters/exports the audit log; rate limiting in place |

**Total wall-clock: ~24 weeks for 2 engineers. ~14 weeks for 5 engineers with parallelization on independent phases (e.g., 8 and 9 can run in parallel after phase 7).**

---

## 5. Sequencing Constraints

A few hard "you cannot do X before Y" rules:

- Nothing else starts until Phase 0 is complete. Identity is the bedrock.
- Phase 2 (trigger/cancel) requires Phase 1 (you need users before you can authorize them).
- Phase 3 (Lock coordination) requires Phase 2 (locks operate on the trigger path).
- Phase 4 (retry) and Phase 5 (bulk) both require Phase 2 — they can run in parallel after Phase 2.
- Phase 7 (read paths + Guest) requires Phase 0 only, so it can start early in parallel with Phase 1.
- Phase 8 (notifications) and Phase 9 (reports) require Phases 2, 4 to have generated data worth analyzing/reporting on.
- Phase 10 (audit viewer + hardening) can start early on the viewer (the audit log itself is written from Phase 0 onward), but full hardening happens last.

---

## 6. Relationship to the Pipeline Lock Coordination Spec

The previously-delivered `Pipeline_Lock_Coordination_Spec.md` introduced a `LockRegistry` that gates pipeline triggers. That spec assumed:
- An `IUserContext` interface (defined here in Phase 0)
- A `pipeline:force-release` capability (defined here in the permission catalog)
- Both WPF and Web identities (defined here in the AuthN layer)

The Lock spec **becomes Phase 3** of this larger plan. It is no longer freestanding — it is the third increment in the RBAC delivery, sitting on top of the identity and authorization foundations.

Document `03_Integration_With_Lock_Spec.md` walks through exactly which parts of the Lock spec absorb into this plan, which adjustments are needed, and which sections of the Lock spec remain unchanged.

---

## 7. What This Plan Deliberately Does Not Promise

- **It does not promise multi-tenancy.** SRS §13 explicitly excludes it.
- **It does not promise external identity provider integration** (Azure AD, Okta). Listed as a follow-up in SRS §13.
- **It does not promise mobile clients.** Out of scope.
- **It does not promise hardening of the Controller-Agent gRPC channel.** Separate follow-up project (per OD-02 resolution).
- **It does not promise high availability beyond 99% during business hours** (per NFR-AVA-01). Failover, multi-region, and disaster-recovery are not in this delivery.
- **It does not promise self-service password reset.** Admin-mediated only in v1 (SRS §13).

If any of those constraints are unacceptable to the reviewer, they need to be re-opened and budgeted before construction starts.

---

## 8. What Success Looks Like

The system is "done" when all eight items in SRS §12 (Acceptance Criteria) demonstrate end-to-end on staging, **plus** the integrated lock behavior from `Pipeline_Lock_Coordination_Spec.md` works correctly across both clients, **plus** the NFR performance targets in SRS §6 are measured and met under realistic load.

---

## 9. Next Action

Read `01_System_Design.md`. That document contains the architecture, the concrete component decomposition, the sequence flows for the high-traffic operations, and the relational schema that the SRS describes only conceptually.
