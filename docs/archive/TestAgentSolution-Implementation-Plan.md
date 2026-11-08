# TestAgentSolution — Implementation Plan (Code-Validated)

| | |
|---|---|
| **Companion to** | `TestAgentSolution-Production-Readiness-Scope-and-Roadmap (1).md` |
| **Purpose** | Validate the roadmap against the *actual* code, then give a buildable plan |
| **Method** | Full walkthrough of all 5 projects + scripts + config (June 2026) |
| **Tone** | Brutally honest. No credit for code that doesn't run in anger. |

---

## 0. The headline you need to hear first

**The roadmap is wrong about where you are — it undersells you, then mis-prices the remaining work.**

It reads as if you are at zero: "single-user tool, no logins, agents are tray apps, web only works when the desktop is open." That snapshot is **6–9 months stale.** The code tells a different story: you have already built the *scaffolding* for almost every epic. `TestController.WebApi` is a real standalone host. RBAC has a full DbContext, 19 permissions, interceptors, and an audit drain. Polly retry + circuit breakers are wired on both dispatchers. There's a heartbeat, a health endpoint, a gRPC listener watchdog, and a Prometheus `/metrics` endpoint.

So the good news: you are roughly **50–70% of the way into the *framework*** for Epics A–F.

The brutal news: **the remaining 30–50% is the part that actually makes it production-ready, and it's all the unglamorous last-mile** — turning things *on*, threading them *through*, supervising the process, and validating under real multi-team load. Scaffolding that is `Enabled: false` is not a feature; it's a liability with good intentions. **The hard 20% is what's left, and the roadmap treats it like the easy 80%.**

---

## 1. Roadmap vs. Reality — epic-by-epic verdict

Legend: 🟢 done · 🟡 built-but-not-finished · 🔴 genuinely absent

| Epic | Roadmap says | Code actually shows | Real status |
|---|---|---|---|
| **A — Observability** | Build it from scratch | `AppLogger` already has `RunId`/`CorrelationId`/`Agent`/`Pipeline` fields; OpenTelemetry + Prometheus `/metrics` live in WebApi; heartbeat + `/api/health` exist | 🟡 **Framework there, not joined up** |
| **B — Agent reliability** | Build it from scratch | Full Polly stack (retry + breaker + 24h timeout) on both hosts; `GrpcListenerWatchdog` (TCP + `grpc.health.v1`); `MapGrpcHealthChecksService`; orphan *detection* | 🟡 **Strong — except hosting & reassignment** |
| **C — Standalone host** | "Web only works with desktop open" | `TestController.WebApi` is a fully standalone host (own gRPC clients, executor, lock registry); `TestController.Api` shared lib with 15 controllers + 3 hubs, referenced by both hosts | 🟡 **Mostly done — last mile is the client URL + deploy story** |
| **D — Config/TLS/secrets** | Fix drift, fix jvkbak, vault secrets | TLS already consistent (plaintext h2c everywhere). BUT a live config typo and plaintext creds | 🟡 **Half done, and already biting you** |
| **E — Security/RBAC** | Build auth + RBAC + audit | DbContext, roles, permissions, `AuthorizationService.CanAsync`, 403/409, 2 interceptors, `QueuedAuditWriter` + drain, login UI — **all present but `RBAC.Enabled: false`** | 🟡 **Built and dormant — the scariest state to be in** |
| **F — Concurrency/queue** | Finish ActiveSessions, add queue | `ExecutionSessionManager` concurrent model **already done**; `AgentLockManager` atomic locking. No queue, no fairness | 🟡 **Concurrency done; scheduling absent** |
| **G — Ops/CI/CD** | Automate deploys | GitHub Actions build + nightly load test; `deploy/*.bat`; `RUNBOOK.md`; smoke + pre-deploy checks | 🟡 **CI yes, CD no** |
| **H — Docs** | Onboarding + API docs | Strong arch/RBAC/runbook docs already | 🟡 **Reference docs strong; no published API/onboarding** |

**There is not a single 🔴 epic.** Every epic is mid-flight. That changes the whole strategy: this is **not a year of greenfield building — it's a campaign of *finishing things you started and abandoned at 60%*.**

---

## 2. Where the codebase is beating you (the honest part)

These are the things that will keep hurting until you confront them. Each is real, with the file that proves it.

### 2.1 You're maintaining the same logic twice (the dual-host tax) — **biggest structural debt**
You have two of everything on the hot path:

- Executors: [ActionPipelineExecutor.cs](TestControllerGrpc/Services/ActionPipelineExecutor.cs) (WPF) **vs** [StandalonePipelineExecutor.cs](TestController.WebApi/Services/StandalonePipelineExecutor.cs) (WebApi)
- Dispatchers: [AgentGrpcDispatcher.cs](TestControllerGrpc/Services/AgentGrpcDispatcher.cs) **vs** [StandaloneAgentDispatcher.cs](TestController.WebApi/Services/StandaloneAgentDispatcher.cs)
- Locks: [AgentLockManager.cs](TestControllerGrpc.Core/Services/AgentLockManager.cs) **vs** the WebApi `LockRegistry`

Every fix you make now has to be made twice or it drifts. The earlier email-card I/O fix, the deadlines, the Polly tuning — each is a coin-flip on whether both hosts got it. **This is the tax you pay for shipping the standalone host *in parallel* with the WPF host instead of *extracting* it.** Until the WPF host becomes a thin client of the same core execution path, drift is structural, not accidental.

### 2.2 RBAC is built, wired, and switched OFF — comfortable, and dangerous
[TestControllerGrpc/appsettings.json](TestControllerGrpc/appsettings.json#L100-L101) has `"RBAC": { "Enabled": false }`. The entire auth/permission/audit surface — `AuthorizationService.CanAsync`, `SessionAuthInterceptor`, 403/409 paths — **has never run in Secured mode in production.** Scaffolding that compiles gives false confidence. The day you flip it on is the day you discover which of the 15 controllers forgot a `Can` check, which client call lacks a bearer token, and which 409 should've been a 403. **The gate to "second team onboarded" is not *writing* RBAC — it's *surviving the first week with it on*.**

### 2.3 Config drift already shipped the exact bug the roadmap warns about
The roadmap dedicates Epic D to "jvkbak drift." It's already in your repo, in production config:

```jsonc
// TestController.WebApi/appsettings.Production.json line 28
{ "Name": "jvkbak", "Address": "http://jvbak:5200" },   // Name says jvkbak, address says jvbak
```

A name/address mismatch in [appsettings.Production.json](TestController.WebApi/appsettings.Production.json#L28). This is not hypothetical future drift — it's a live latent outage waiting for the day traffic routes by that address. **The doc treats config drift as a future risk; it's a present defect.**

### 2.4 Correlation IDs exist in the schema but aren't threaded — half-wired traceability
`AppLogEntry` carries `CorrelationId` and `RunId` ([IAppLogger.cs](TestControllerGrpc.Core/Services/IAppLogger.cs)), and the gRPC runner can attach an `x-correlation-id` header ([RemoteCommandStreamRunner.cs](TestControllerGrpc.Core/Services/RemoteCommandStreamRunner.cs)). But the proto carries only `execution_id`, the session manager doesn't propagate the id, and **agent logs don't carry the controller's `RunId`.** So the promise — "one run reads as a single story" — is *plumbed but not connected*. This is worse than absent, because the fields *look* populated in the controller and mysteriously empty on the agent.

### 2.5 The agent is still a tray app — the OS can't supervise it
[TestAgentGrpc/Program.cs](TestAgentGrpc/Program.cs) runs Kestrel on a background thread + a WinForms tray context on an STA thread. There is **no `UseWindowsService()`.** Your `GrpcListenerWatchdog` does the right thing — it exits with a restart code on zombie detection — but *something has to restart it*, and that something is a Windows Service Recovery policy that only exists if provisioning set it. **Your self-healing depends on a config step that VM reverts erase.** The zombie problem you keep fighting is downstream of this one decision.

### 2.6 Orphan detection without reassignment turns "hung run" into "lost run"
[LockRecoveryService.cs](TestController.Api/Services/LockRecoveryService.cs) detects orphaned locks and releases them via `AgentLockManager.FindOrphanedLocks`. Good — no more indefinite hang. But the abandoned *work* is never requeued. You traded one failure mode (hang) for a quieter one (silent loss). For a single user who notices, fine. For another team, a run that vanishes with no result and no error is a trust-killer.

### 2.7 Co-located mode has two event authorities
When `ControllerProxyUrl` is set, [TestController.WebApi/Program.cs](TestController.WebApi/Program.cs) runs `ControllerEventRelayService`/`ControllerProxyService` to bridge to the WPF host's authoritative data — *and* the WPF host runs its own `ControllerWebApiHost` on the same port 5200. **Which process is the source of truth depends on a config string.** That's a debugging trap: the same `/api/...` call can hit either backend, and event ordering across the relay is not guaranteed.

### 2.8 No run queue — multi-team is currently "collide and 409"
[ExecutionController.cs](TestController.Api/Controllers/ExecutionController.cs) dispatches with `_ = Task.Run(...)` (fire-and-forget) and returns `409 Conflict` when agents are busy. That's fine for one operator who retries. Two teams = a busy-wait race over four shared VMs. **The system doesn't queue work; it rejects it.**

### 2.9 Secrets are in plaintext
`ServicePassword` flows as a plaintext script parameter into `ConvertTo-SecureString -AsPlainText` ([Setup-AgentNode.ps1](Utilites/TestAgent_SetupScripts/Setup-AgentNode.ps1)), and SMTP/domain identifiers sit in `appsettings.json`. No DPAPI, no Credential Manager, no vault. One repo leak = domain-account exposure.

---

## 3. The plan — corrected priorities

The roadmap's *sequence* (Reliable → Decoupled → Secure → Scalable → Operable) is sound. But because the framework already exists, the **unit of work is "finish + validate," not "build."** Re-prioritised by *risk you're carrying right now*:

### P0 — Stop the bleeding (days, not months)
These are live defects and false-confidence traps. Do them before any new feature.

| # | Action | Files | Exit criteria |
|---|---|---|---|
| P0-1 | Fix the `jvkbak`/`jvbak` address typo | [appsettings.Production.json](TestController.WebApi/appsettings.Production.json#L28) | Name and address agree for all 4 agents; diff'd against WPF [appsettings.json](TestControllerGrpc/appsettings.json) |
| P0-2 | Reconcile agent lists across the 3 appsettings (UPPERCASE vs lowercase, jvgr2 only in prod) | all `appsettings*.json` | One canonical agent roster; documented source of truth |
| P0-3 | Move `ServicePassword` + SMTP creds out of plaintext | [Setup-AgentNode.ps1](Utilites/TestAgent_SetupScripts/Setup-AgentNode.ps1), `appsettings.json` × N | No secret literal in any tracked file; runtime reads DPAPI/Credential Manager |

### P1 — Make self-healing actually self-heal (Epic B finish)
| # | Action | Files | Exit criteria |
|---|---|---|---|
| P1-1 | Run the agent as a Windows Service (`UseWindowsService()`), keep tray as optional attach | [TestAgentGrpc/Program.cs](TestAgentGrpc/Program.cs) | `sc query` shows the service; kill the process → SCM restarts it with **zero** human action |
| P1-2 | Bake SCM recovery policy + service registration into provisioning so it survives reverts | [Setup-AgentNode.ps1](Utilites/TestAgent_SetupScripts/Setup-AgentNode.ps1), [Patch-AgentFleet.ps1](Utilites/Patch-AgentFleet.ps1) | Fresh/reverted VM auto-configures recovery; verified by revert test |
| P1-3 | Decide orphaned-work policy: **clean-fail with a visible result** (minimum) or reassign (stretch) | [LockRecoveryService.cs](TestController.Api/Services/LockRecoveryService.cs), `ExecutionSessionManager` | A mid-run agent death produces a terminal `Failed` session + SignalR event within a bounded time — never a silent disappearance |
| P1-4 | Audit every Controller→agent gRPC call for an explicit deadline | [AgentGrpcDispatcher.cs](TestControllerGrpc/Services/AgentGrpcDispatcher.cs), [StandaloneAgentDispatcher.cs](TestController.WebApi/Services/StandaloneAgentDispatcher.cs) | No unbounded call remains; deadlines sourced from `ControllerTimeoutOptions` |

### P2 — Join up observability (Epic A finish)
| # | Action | Files | Exit criteria |
|---|---|---|---|
| P2-1 | Thread `RunId`/`CorrelationId` end-to-end: controller → gRPC metadata → agent log scope | [RemoteCommandStreamRunner.cs](TestControllerGrpc.Core/Services/RemoteCommandStreamRunner.cs), `ExecutionSessionManager`, agent log writes | Given a `RunId`, you can pull controller **and** agent log lines for that one run |
| P2-2 | Ship metrics from the agent + WPF host, not just WebApi | [AppMetrics.cs](TestController.WebApi/Services/AppMetrics.cs) pattern → agent | `/metrics` (or push) covers all hosts |
| P2-3 | Build the fleet-health screen in the WebClient off existing heartbeat + `/api/health` | [HealthController.cs](TestController.Api/Controllers/HealthController.cs), `ConnectionHealthMonitor`, WebClient | Killing an agent shows red on screen within seconds, before a run hangs on it |
| P2-4 | Add agent-down + run-overrun alerting (reuse existing SMTP path) | `NotificationDispatcher`, `ConnectionHealthMonitor` events | Email/Teams fires on `ConnectionLost` and on run > expected duration |
| P2-5 | (Optional but high-value) central log sink so logs aren't trapped per-VM | `AppLogger` sink abstraction | One query surface across the fleet (Seq/ELK) — without ripping out `AppLogger` |

### P3 — Finish decoupling (Epic C last mile)
| # | Action | Files | Exit criteria |
|---|---|---|---|
| P3-1 | Make the WebClient backend URL deploy-time configurable (honour `VITE_API_BASE_URL`) | [vite.config.ts](TestController.WebClient/vite.config.ts), [src/lib/api.ts](TestController.WebClient/src/lib/api.ts) | One build runs against WPF-embedded *or* standalone WebApi by env, no rebuild |
| P3-2 | Define one authoritative deployment topology; make `ControllerProxyUrl` co-located mode an explicit, documented choice — not an accident | [TestController.WebApi/Program.cs](TestController.WebApi/Program.cs), `RUNBOOK.md` | A team runs submit→watch entirely on standalone WebApi with **no WPF running anywhere** |
| P3-3 | Attack the dual-host tax: extract shared execution path so WPF becomes a client of the core, not a parallel implementation | `ActionPipelineExecutor` / `StandalonePipelineExecutor` → shared base | New hot-path logic is written once; the two executors collapse toward one |

### P4 — Turn the lock on and survive it (Epic E activation)
| # | Action | Files | Exit criteria |
|---|---|---|---|
| P4-1 | Audit all 15 controllers for a `Can`/permission check on every mutating action | [TestController.Api/Controllers/](TestController.Api/Controllers) | Coverage matrix: every endpoint maps to a permission; gaps closed |
| P4-2 | Run the full app in **Secured mode** in a staging fleet; fix every 401/403/409 surprise | [RbacFeatureExtensions.cs](TestController.Api/RbacFeatureExtensions.cs), interceptors | A scripted run as each of the 4 roles behaves exactly per the capability matrix |
| P4-3 | Decide identity provider: keep Negotiate/API-key, or integrate Entra/OIDC as the roadmap wants | [SecurityServiceExtensions.cs](TestController.Api/Security/SecurityServiceExtensions.cs) | Documented decision; if OIDC, login flow validated end-to-end |
| P4-4 | Only after P4-2 passes: flip `RBAC.Enabled = true` as the default and onboard team #2 | `appsettings.json` | First external team completes a run under Secured mode with a clean audit trail |

### P5 — Fairness & ops (Epics F, G, H)
| # | Action | Files | Exit criteria |
|---|---|---|---|
| P5-1 | Add a run queue in front of the fire-and-forget dispatch | [ExecutionController.cs](TestController.Api/Controllers/ExecutionController.cs) | Two simultaneous submissions queue and both complete; no 409 race |
| P5-2 | Per-team fair-share + queue-depth visibility on the dashboard | scheduler + WebClient | One team can't starve another; depth/usage visible |
| P5-3 | Automate deploy + rollback on top of existing CI | [.github/workflows/](.github/workflows), [deploy/](deploy) | Someone other than you deploys and rolls back from `RUNBOOK.md` |
| P5-4 | Publish Swagger/OpenAPI + onboarding guide | WebApi host, `docs/` | A new team goes no-access → first run on docs alone |

---

## 4. Corrected sequencing rationale

- **P0 is non-negotiable and first.** You have a live config defect and plaintext secrets. Fixing a typo costs minutes; the outage it prevents does not.
- **P1 before P2** still holds — but note P1 is *mostly finishing*, not building. The single highest-leverage line of code in this whole plan is `UseWindowsService()` plus a recovery policy that survives reverts. It retires most of your zombie firefighting.
- **P4 (RBAC on) is gated by validation, not by writing.** The code is done. The work is *proving it* in Secured mode. Do **not** onboard a second team on the strength of "the code exists." Onboard them on the strength of "it ran in Secured mode for a week without surprises."
- **P3-3 (dual-host extraction) is the strategic one.** It won't show on a demo, but every month you defer it, you pay the drift tax twice. Schedule it deliberately or it never happens.

---

## 5. Revised "where we are today" snapshot (replaces roadmap §2)

| Area | Roadmap §2 said | Truth in the code |
|---|---|---|
| Users | "Just you, no logins" | Login UI + sessions + 4 roles exist; **switched off** |
| Controller | "WPF wears three hats" | Still true **and** a standalone WebApi exists in parallel — now you have a *fourth* hat and a dual-authority mode |
| Agents | "tray app + scheduled task" | True — still no Windows Service; watchdog compensates fragilely |
| Mid-run death | "hangs until noticed" | Now detected + lock-released, but **work silently lost** |
| Visibility | "scattered logs" | Structured logger + metrics + heartbeat + health exist; **not joined into one view** |
| Config | "drifts" | Confirmed: live `jvbak`/`jvkbak` mismatch in prod |
| Security | "unresolved" | Hybrid auth + full RBAC scaffold present; **dormant** |
| Deployment | "by hand" | CI builds; deploy still manual `.bat` |
| Docs | "strong" | Still strong; no published API/onboarding |

---

## 6. The one-paragraph brutal summary

You are not a year of greenfield work away from production — you are a *finishing* campaign away, and finishing is harder than starting because there's no dopamine in it. Your biggest enemies are **self-inflicted**: a standalone host shipped *beside* the WPF host instead of *under* it (doubling your maintenance forever), an RBAC system that compiles but has never defended a real request, correlation IDs that are populated on one side of the wire and blank on the other, and an agent the OS can't supervise. None of these are visible in a demo — which is exactly why they've survived. Fix the live config typo today, make the agent a real service this week, turn RBAC on in staging and *survive it* before you let anyone else in, and pay down the dual-host tax before it compounds further. The scaffolding is genuinely good. Now make it load-bearing.

---

*Validated against the working tree on 2026-06-26. Update §1 verdicts as each P-item closes.*
