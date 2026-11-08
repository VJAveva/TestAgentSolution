# TestAgentSolution — Feature Scope & Production-Readiness Roadmap

### From a single-user tool to a cross-team platform

| | |
|---|---|
| **Document owner** | Vinod Kumar |
| **Status** | Planning / Draft v1 |
| **Last updated** | June 2026 |
| **Applies to** | TestControllerGrpc (WPF), TestControllerGrpc.Core, TestController.WebApi, TestAgentGrpc, TestController.WebClient |

---

## 1. Why this document exists

Today, TestAgentSolution works — but it works for **one person who knows where all the bodies are buried.** You know which VM tends to misbehave, which script to re-run when a job hangs, and what a "zombie" agent actually means. That knowledge lives in your head, not in the system.

To let other teams use it, the platform has to stop depending on you being in the room. That comes down to three shifts:

- **"I can see what's wrong" → "anyone can see what's wrong."** The system explains itself.
- **"I restart it when it breaks" → "it recovers on its own."** The system heals itself.
- **"It's my tool" → "it's a shared service."** It has logins, permissions, fairness, and onboarding.

This document lists every gap between today and that goal, groups the work into eight areas (epics), and lays out the order to tackle them so each step stands on solid ground.

**How to read each work area below:** every epic has three parts —
- **Why it matters** — the plain-English reason, no jargon.
- **What's involved** — the technical work, in your stack (.NET 10, gRPC, SignalR, WPF, React).
- **Done when** — how you know it's finished.

---

## 2. Where we are today (honest snapshot)

| Area | Today |
|---|---|
| **Users** | Just you. No logins, no permissions. |
| **Controller** | The WPF app wears "three hats" at once — desktop app, web server, and agent coordinator in one process. The web client only works if the desktop app is open. |
| **Agents** | `TestAgentGrpc.exe` launched via a tray app + scheduled task. The "zombie" pattern (process alive, port dead) is handled reactively by scripts. |
| **When an agent dies mid-run** | The run likely hangs until someone notices. |
| **Visibility** | You diagnose by reading scattered logs and recognising patterns from experience. No single view of a run's journey or fleet health. |
| **Configuration** | Hostnames and ports live in many places across scripts, code, and VMs. They drift — which is why `jvkbak` behaves differently from `jvgr1`. |
| **Security** | `jvkbak` SSL/cleartext mismatch unresolved. Domain credentials (`wwAPPS` / `MAGELLANDEV2000`) likely embedded in scripts. |
| **Deployment** | You run PowerShell scripts by hand (`Deploy-TestController.ps1`, revert scripts, etc.). |
| **Docs** | Strong — the "Architect's Field Guide" eBook and the WebClient spec already exist. This is a real asset for onboarding. |

---

## 3. What "production-ready, cross-team" means

The bar we are aiming for:

1. **Self-explaining** — any engineer can open a dashboard, find a run, and see exactly where and why it failed, without asking you.
2. **Self-healing** — agents restart themselves; a dead agent's work is retried or cleanly failed, never left hanging.
3. **Accessible without the desktop app** — teams use the web client; the WPF app is optional, not a dependency.
4. **Secure and multi-user** — people log in, can only do what their role allows, and every sensitive action is recorded.
5. **Fair under load** — multiple teams can submit runs at once without stepping on each other.
6. **Operable by more than one person** — deploys are automated and repeatable; there's a runbook, not tribal knowledge.
7. **Consistent everywhere** — every VM is configured identically and survives reverts, because the config is baked into provisioning.

---

## 4. The work — feature scope

Eight work areas. Each is independently valuable, but they have a natural order (see the roadmap in Section 5).

### Epic A — Observability & Diagnostics

> **Why it matters:** Right now, when a run goes wrong, you're guessing where it broke. Before anyone else can use this, the system has to be able to explain itself. This is the single highest-payoff change.

**What's involved**
- **Correlation IDs (a "tracking number" per run).** Generate a `RunId` (and per-test `ExecutionId`) at the Controller and thread it through every gRPC call to the agents and back. One run then reads as a single, traceable story end to end.
- **Structured logging** via **Serilog** across all five projects, writing to a central sink (Seq or Elasticsearch) instead of per-machine text files. Every log line carries the `RunId`, agent name, and machine.
- **Metrics & tracing** via **OpenTelemetry** — execution duration, pass/fail rate per agent, agent availability, queue depth. Exported to a dashboard (Grafana or Application Insights).
- **Live fleet health view** in the web client — a screen showing each of the four agents (`jvgr1`, `jvkpri`, `jvkbak`, `jvhist`) as green/amber/red, with last-heartbeat time, so a dead agent is visible *before* a run hangs on it.
- **Proactive alerting** — notify (email/Teams) when an agent goes dark or a run exceeds an expected duration.

**Done when**
- Any failed run can be diagnosed from one dashboard using its `RunId`, with no machine login required.
- The fleet health screen reflects reality within seconds of an agent dying.

**Depends on:** nothing — start here.

---

### Epic B — Agent Reliability & Self-Healing

> **Why it matters:** The "zombie" problem you keep fighting comes from *how* the agent is launched. Make the agent run itself properly and the babysitting scripts mostly disappear.

**What's involved**
- **Run the agent as a Windows Service**, not a tray app + scheduled task. Use `Microsoft.Extensions.Hosting.WindowsServices` with `UseWindowsService()`. Windows then monitors it and auto-restarts it on failure (recovery actions), replacing the manual `taskkill` / relaunch logic.
- **A real health contract.** The agent exposes a liveness/readiness check (gRPC health protocol or a lightweight HTTP `/health`) that confirms *the port is actually listening and accepting work* — catching the exact zombie pattern (process alive, mutex held, port silent) automatically.
- **Mid-run failure handling on the Controller:**
  - **Deadlines** on every gRPC call so a silent agent can't hang a run forever.
  - **Polly** retry + circuit-breaker policies for transient agent failures.
  - **Orphan-run detection** — if an agent drops mid-test, the Controller reassigns the work or fails it cleanly, rather than leaving it dangling.
- **Bake it into provisioning.** Service registration, recovery settings, and health config go into the revert/provisioning scripts so they survive the frequent VM reverts.

**Done when**
- Killing `TestAgentGrpc.exe` on a VM results in automatic restart with no human action.
- A run on an agent that dies mid-test ends in a clear pass/fail/retry within a bounded time — never an indefinite hang.

**Depends on:** Epic A (you want the health signals feeding the dashboard).

---

### Epic C — Architecture: Stand-Alone Controller Host

> **Why it matters:** Your web page only works when the desktop app is open. For teams who don't have (or want) the WPF app, that's a blocker. Splitting the jobs apart fixes this and is exactly the direction your architect roadmap is heading.

**What's involved**
- **Lift the server out of WPF.** Because `TestControllerGrpc.Core` is already shared, move the ASP.NET Core API + SignalR + gRPC server into a **standalone host** (a headless ASP.NET Core service). WPF becomes a **pure client** that talks to that host like any other client.
- **The web client stops depending on the desktop.** The React/Zustand WebClient connects to the standalone host directly — no WPF process required.
- **IIS deployment becomes a real service**, not a side effect of running the desktop app. Your existing `Deploy-TestController.ps1` / `appcmd.exe` flow now deploys an actual server.
- **WPF and Web reach parity** by both consuming the same API + SignalR events (the 23-endpoint / 10-event contract you've already documented).

**Done when**
- A team member can submit and watch a test run entirely from the web client with no WPF app running anywhere.
- The Controller runs headless on a server and survives a desktop logout.

**Depends on:** Epic A (tracing makes the refactor safe to verify). **Blocks:** Epic E (you want to attach auth to a real standalone API, not one bolted to WPF).

---

### Epic D — Configuration & Environment Consistency

> **Why it matters:** Settings scattered across scripts and VMs quietly drift apart — that's literally why one VM works and another doesn't. One source of truth ends that whole category of bug.

**What's involved**
- **One source of truth for endpoints, hostnames, and ports** — `appsettings.json` plus environment-specific overrides, with the PowerShell scripts reading from the *same* source rather than hard-coding values.
- **Resolve the `jvkbak` TLS/cleartext mismatch once, for all four VMs.** Pick one transport model and standardise it:
  - *Preferred:* TLS everywhere, with a self-signed cert (or a small internal CA) distributed by the revert script; **or**
  - cleartext (h2c) everywhere, with explicit h2c config on both Controller and agents so scheme and transport always agree.
  - Bake the chosen model into provisioning so it persists through reverts.
- **Secrets management.** Move the `wwAPPS` / `MAGELLANDEV2000` credentials out of plaintext scripts into a proper store (Windows Credential Manager, DPAPI, or a secrets vault), referenced at runtime.

**Done when**
- All four agents are byte-for-byte identical in transport config, and the `jvkbak` handshake error cannot recur after a revert.
- No credential appears in plaintext in any script or config file.

**Depends on:** can run alongside Epic C.

---

### Epic E — Security & Multi-User (the gate for cross-team use)

> **Why it matters:** You cannot let other teams in without knowing who they are and controlling what they can do. This is the door you don't open until it has a lock.

**What's involved**
- **Authentication.** Add identity to the API + SignalR endpoints — ideally integrate with your corporate identity provider (Azure AD / Entra ID via OIDC) so people use existing logins.
- **RBAC** — implement the four-role model already on your roadmap, with a `Can(user, permission, resource)` capability matrix, and the **403-vs-409** rules (forbidden vs. conflict). Enforce it at the API layer so both WPF and Web clients inherit it.
- **Audit log** — record who ran what, where, and when, for every sensitive action. (This also pairs naturally with Epic A's central logging.)

**Done when**
- Every API/SignalR call is authenticated; unauthorised actions return a correct 403.
- A complete, queryable audit trail exists for runs and admin actions.

**Depends on:** Epic C (auth attaches to the standalone host). **This epic gates onboarding any second team.**

---

### Epic F — Concurrency, Queueing & Fair Scheduling

> **Why it matters:** Once several teams submit runs at the same time, they'll collide over four shared agents. You need a queue and a fair way to share the machines.

**What's involved**
- **A run queue** on the Controller — submitted runs are queued, not run immediately on a first-come free-for-all.
- **Resource-aware scheduling** — assign runs to free agents, respect per-agent capacity, and avoid two teams' tests landing on the same VM at once (test isolation).
- **Prioritisation & fairness** — optional per-team priority or fair-share so one team can't starve another.
- **Per-WatchItem concurrent execution** — finish the `ActiveSessions` model (replacing the global `IsExecuting` flag) you've already scoped, so a single client can run multiple items safely too.
- **Visibility** — queue depth and per-team usage surfaced on the dashboard from Epic A.

**Done when**
- Two teams submitting runs simultaneously both get correct, isolated results with no manual coordination.
- No run silently overwrites another's agent or results.

**Depends on:** Epic B (reliability) and Epic E (you need identity to schedule *per team*).

---

### Epic G — Operations & Deployment

> **Why it matters:** Right now deploys happen because *you* run scripts. For a shared service, deploys have to be repeatable and not dependent on one person.

**What's involved**
- **CI/CD for the platform itself** — a pipeline (Azure DevOps / GitHub Actions) that builds, tests, versions, and deploys the Controller, agents, and web client.
- **Versioning & rollback** — tagged releases and a clean way to roll back a bad deploy.
- **Automated, repeatable provisioning** — your revert/provisioning scripts wrapped so a fresh VM joins the fleet correctly every time, unattended.
- **Runbooks** — short written procedures for the common operational tasks (deploy, roll back, add an agent, rotate credentials).

**Done when**
- A release can be deployed and rolled back by someone other than you, following a runbook.
- A reverted or brand-new VM rejoins the fleet fully configured with no manual fix-ups.

**Depends on:** Epics C and D (you need a real deployable server and consistent config first).

---

### Epic H — Onboarding & Documentation

> **Why it matters:** For other teams to *self-serve*, they need to understand the platform without booking time with you. You already have most of the raw material.

**What's involved**
- **An onboarding guide** — how a new team requests access, submits a run, and reads results (web-client-first).
- **Keep the Architect's Field Guide and WebClient spec current** as the platform changes — they're already strong; the work is keeping them in sync.
- **API documentation** — published, browsable docs for the 23 endpoints / 10 SignalR events (e.g. via Swagger/OpenAPI from the standalone host).
- **A support path** — where teams report issues and find status.

**Done when**
- A new team can go from "no access" to "first successful run" using docs alone, without your direct involvement.

**Depends on:** lands last, once the platform is stable enough to document confidently.

---

## 5. Roadmap (phased)

The order matters: each phase makes the next one safe. The headline progression is **Reliable → Decoupled → Secure → Scalable → Operable.**

> **Estimates assume largely solo / part-time capacity** and are planning guides, not commitments. Compress them with more hands.

| Phase | Theme | Epics | Rough window | Outcome |
|---|---|---|---|---|
| **1** | **Stand on solid ground** | A, B | Months 1–2 | You can see what's happening, and agents heal themselves. *Reduces your own daily pain immediately.* |
| **2** | **Break the desktop dependency** | C, D | Months 3–5 | The web client works without WPF; every VM is configured identically; `jvkbak` is fixed for good. |
| **3** | **Make it safe to share** 🚪 | E | Months 6–8 | Logins, roles, and audit are in place. **This is the gate — the first other team can be onboarded after this.** |
| **4** | **Make it fair under load** | F | Months 9–10 | Multiple teams run tests at once without colliding. |
| **5** | **Make it run without you** | G, H | Months 11–12 | Deploys are automated and documented; teams self-onboard. The platform no longer has a single point of failure (you). |

**Sequencing rationale**
- **A is first** because you can't safely change — or operate — a system you can't see.
- **B follows A** so agent health signals have somewhere to report.
- **C before E** because authentication belongs on a real standalone API, not one fused to the desktop app.
- **E before F** because fair scheduling needs to know *who* a run belongs to.
- **G and H last** because you can only automate and document a platform once it's stable.

```
Phase 1        Phase 2          Phase 3        Phase 4        Phase 5
[A] Observe    [C] Decouple     [E] Secure 🚪  [F] Schedule   [G] Operate
[B] Self-heal  [D] Consistency                                [H] Onboard
   │              │                │              │              │
 see &         web client       open the       fair under     runs without
 recover       without WPF      doors          load           you
```

---

## 6. What success looks like (metrics)

Track these to know the journey is working — not just that features shipped:

- **Mean time to diagnose a failed run** — from "minutes of log-reading by Vinod" to "seconds on a dashboard by anyone."
- **Unattended agent recovery rate** — % of agent failures resolved with zero human action (target: ~100%).
- **Hung-run rate** — runs that hang indefinitely (target: zero; all runs reach pass/fail/retry within a bounded time).
- **Desktop-free usage** — % of runs initiated entirely from the web client.
- **Config drift incidents** — "works on one VM, fails on another" occurrences (target: zero post-Phase 2).
- **Teams onboarded** — number of teams self-serving beyond you.
- **Onboarding time** — "no access" → "first successful run" without your involvement.

---

## 7. Risks & watch-outs

- **VM reverts erase fixes.** Every change in Phases 1–2 must be baked into provisioning/revert scripts, or it won't survive. (You already treat this as a first principle — keep it.)
- **The architecture split (Epic C) is the riskiest single change.** Do it *after* observability (A) so you can verify behaviour before/after with real traces, and keep WPF working against the new host throughout.
- **Don't open the doors early.** Resist onboarding a second team before Phase 3 (auth/RBAC) is genuinely done — retrofitting security after the fact is far more painful.
- **Solo bandwidth is the binding constraint.** This roadmap is a year of part-time work for one person. The phasing is designed so that *even if you stop after any phase*, what you've shipped is independently valuable.

---

## 8. Quick reference — gap → epic

| Original gap (from review) | Epic |
|---|---|
| Can't easily see what's happening / no health view | A |
| Agent "zombie" / fragile launch | B |
| Runs hang when an agent dies mid-test | B |
| WPF wears three hats; web needs desktop open | C |
| `jvkbak` TLS/cleartext mismatch | D |
| Settings scattered, config drift | D |
| No logins / permissions for multiple teams | E |
| Teams colliding over shared agents | F |
| Manual, person-dependent deploys | G |
| Self-serve onboarding for other teams | H |

---

*This is a living document. As epics complete, update Section 2 (snapshot) and tick the metrics in Section 6.*
