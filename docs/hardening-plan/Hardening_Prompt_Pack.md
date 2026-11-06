# TestAgentSolution — Hardening, Performance & Architecture Prompt Pack

Use in the agent (Claude Code / Copilot agent mode), **one prompt at a time, in order**.
Rule of the pack: **audit first, fix in small proven batches, refactor last.** Never "refactor the whole
codebase" in one go — that is how working code breaks silently.

| Step | Prompt | Changes code? |
|---|---|---|
| 1 | Full audit → findings register | No |
| 2 | Triage → batch plan | No |
| 3 | Fix one batch (repeat) | Yes, small |
| 4 | Performance: measure, then fix | Yes, measured |
| 5 | Architecture: proposals, then staged refactor | Yes, staged |

---

## 0. Ground rules (paste once at the start of every session)

```
You are working on TestAgentSolution (.NET 10: WPF controller + ASP.NET Core WebApi, both over
TestControllerGrpc.Core; gRPC agents; EF Core/SQLite; React web client; PowerShell/batch deploy scripts).
Rules:
- Report what the code actually does, with file:line evidence. If unsure, mark UNVERIFIED - never guess.
- Do not change code unless the prompt says so. Never rewrite working code without a finding that justifies it.
- Every fix gets a test, and a red-proof: revert the fix, confirm exactly the expected test fails, restore it.
- Run the full test suite after every change; report pass/fail counts. Known flaky: VocabularyMonitorTests,
  LockPersistenceMigrationTests - rerun before blaming a change.
- Respect the Ui ratchets (DynamicResource only, AutomationProperties.Name on every interactive control,
  no hard-coded hex/FontSize, AutomationId count only goes up).
- Core stays dependency-light; host-specific types never enter Core.
- Never print, commit or log secrets. Never touch preserveFiles behaviour or deploy anything.
```

---

## 1. Full audit (report only)

```
Audit the whole solution. Do NOT change any code. Work folder by folder; finish one before the next.
Write the results to docs/Audit/AUDIT_<yyyy-MM-dd>.md as a findings register:

| ID | Severity | Lens | Area | File:Line | Evidence | Impact in production | Minimal fix | Effort S/M/L | Test to prove it |

Severity: Critical (security, data loss, run corruption) / High (wrong results, hangs, leaks) /
Medium (fragile, slow) / Low (cleanup). Max 60 findings - the ones that matter. Mark UNVERIFIED when unsure.

Lenses:
A. Security - secrets in code, scripts, configs, logs, TRX output and git history; credentials on command
   lines; unauthenticated or permission-unchecked endpoints; path traversal in file/results APIs.
B. Correctness - async void, .Result/.Wait(), fire-and-forget tasks, missing CancellationToken, undisposed
   IDisposable, UI-thread violations, races on shared state, exceptions swallowed.
C. Architecture - host types in Core, wrong DI lifetimes, static mutable state, duplicated logic between
   WPF and WebApi paths, code paths that bypass the shared funnels, dead code.
D. Performance - work on the UI thread, polling where events exist, N+1 EF queries, unbounded collections,
   per-call allocations that should be cached, large files read whole, sync-over-async.
E. Reliability/ops - missing timeouts/retries, lock or permit leaks on cancel/exception, file-watcher races,
   recovery after controller restart, behaviour when an agent goes offline mid-run.
F. Test health - tests that pass vacuously, missing red-proofs, flaky tests, untested critical paths.

Check these known hotspots explicitly and record a finding (or "verified OK") for each:
1. GetUserToken logging password + JWT (WASSmokeTest output, TRX files, Logs.zip); credentials in git history.
2. Two SecurityRedactor classes - which paths use which; any log path that bypasses redaction.
3. RBAC: Pipeline_Author and other permissions enforced server-side (service/API), not only in view models.
4. PipelineConcurrency / MaxParallelDegree: permit released on cancel and exception; nested groups never deadlock.
5. Agent and pipeline locks: released in finally; lock renewal; stale tokens cannot release newer locks.
6. DeepCloneNode / NodeId uniqueness on fan-out and template expansion (cross-pipeline progress bug).
7. ParameterResolver: every execution path (executor, 3 trigger paths, PreflightRunner, web node-run) uses the
   shared funnel; GlobalVariables rank 35 and release-match guard.
8. Memory growth in the controller (live logs, session history, SignalR buffers, caches without limits).
9. WPF CPU: animations/timers running when minimized or hidden (DesiredFrameRate), dispatcher floods.
10. FileWatcherManager: duplicate triggers, missed events, rename races, watcher disposal on WatchList reload.
11. Tag/identity: PipelineAssignments.PipelineId max 36 vs real tags; '+' and spaces in tags used in URLs.
12. Both hosts: services registered in both DI containers where the controller endpoints are not proxied.
13. Deploy scripts: dirty-tree guard, hash verification, preserveFiles side-effects (*.bat never updated).
14. FIR: cache key, workbook resolution + staleness guard, no secret in any report or .fir.json.

Finish with: top 10 by risk, and a one-paragraph verdict per lens.
```

## 2. Triage into batches (no code)

```
Read docs/Audit/AUDIT_<date>.md. Group the findings into fix batches of 3-6 related items each, ordered:
Critical security -> Critical/High correctness and reliability -> High architecture -> Medium -> Low.
Each batch must be independently shippable and testable. For each batch list: findings, files touched,
risk of regression, tests to add, and whether it needs a deploy-time step.
Write docs/Audit/FIX_PLAN_<date>.md. Do not change code.
```

## 3. Fix one batch (repeat per batch)

```
Implement batch <N> from docs/Audit/FIX_PLAN_<date>.md only. Nothing outside it.
For each finding: write the failing test first, make the minimal fix, red-proof it (revert -> exactly the
expected test fails -> restore), run the full suite. No behaviour change beyond what the finding states.
Update the finding's row in the register to FIXED with the commit-ready summary. Stop after this batch and
report: what changed, test counts before/after, anything you found but did not fix.
```

## 4. Performance — measure, then fix

```
Do not optimise anything without a number. For the performance findings in the register:
1. Measure the baseline: startup time, time to open Results for a large build, WatchList load, memory after
   1 hour idle and after a full pipeline run, CPU when minimized, API p95 for /results and /fir endpoints.
   Use Stopwatch logging or dotnet-counters / dotnet-trace; record exact commands and numbers.
2. Propose fixes ranked by expected gain / effort.
3. Implement one at a time, re-measure, keep it only if the number improves; report before/after.
Write docs/Audit/PERF_<date>.md.
```

## 5. Architecture — proposals first, staged refactor second

```
From the Architecture findings, write proposals as short ADRs in docs/Architecture/ADR-<nnn>-<title>.md:
context, problem (with file:line evidence), options, recommendation, migration steps, risk, how to verify.
Do NOT refactor yet.
After I approve an ADR: implement it in stages (strangler style) - each stage builds, passes the full suite,
and can ship alone. No big-bang rewrites; no renames of public APIs or persisted identifiers (tags, DB keys,
file names) without a migration.
```

---

## Done when

- Every Critical and High finding is FIXED or has an approved, dated exception.
- Each fix has a test and a red-proof.
- Performance changes have before/after numbers.
- The secrets list is cleared, the exposed passwords are **rotated**, and the git-history clean-up is done
  (that last one is a separate, coordinated task — the agent should not rewrite history on its own).
