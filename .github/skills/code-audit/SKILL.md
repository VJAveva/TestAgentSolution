---
name: code-audit
description: 'Five-pass code audit of ONE folder in TestAgentSolution: secrets, correctness, architecture, performance, hardening. Report only, never rewrite. Follows docs/hardening-plan/CodeAudit-Copilot-Prompts.md. Use for audit, code review, find bugs, hardening review, security review of a folder.'
argument-hint: 'One folder (e.g. TestControllerGrpc.Core/Maintenance) and optionally a single pass number'
---

# Code Audit (5 passes, one folder)

Source prompts: `docs/hardening-plan/CodeAudit-Copilot-Prompts.md`. That file is the authority; this skill is the runbook.

## Ground rules
1. **Report only.** No edits, no rewritten files. A fix is a *minimal* suggested diff in the table.
2. **One folder per run.** If given more, ask which folder to do first.
3. Label anything you inferred but didn't see **`ASSUMPTION:`**, and anything you suspect but couldn't confirm **`UNVERIFIED`**. Never invent an API.
4. No style opinions. Defects only, ranked by production impact.
5. **Never print a secret value.** Mask the middle and give file:line only.
6. **Check before you claim.** Before naming any existing helper, script or test in a fix, grep for it.

## Passes (run all five in this order, then report everything together)
| # | Pass | Look for (summary; full lists in the prompts doc) |
|---|---|---|
| 1 | **Secrets & leakage** (1.1-1.3) | hardcoded PATs, passwords, connection strings; secrets reaching logs, exceptions, gRPC status or command lines; config hygiene |
| 2 | **Correctness** (2.1-2.4) | async void, `.Result`/`.Wait()`, unobserved tasks, dropped CancellationTokens, unsafe shared state; WPF dispatcher misuse and leaks; gRPC deadlines, retries, reconnect; EF/SQLite N+1, long write txns, non-atomic `ExecuteDelete`+`SaveChanges` |
| 3 | **Architecture** (3.1-3.3) | WPF/ASP.NET types in Core; DI lifetime mismatches between the two hosts; captive deps; state machines with no crash recovery or timeout |
| 4 | **Performance** (4.1-4.2) | per-event dispatcher hops, O(n²) loops, unbounded collections, per-call regex or channel construction. Mark **NEEDS MEASUREMENT** where a profiler is required |
| 5 | **Hardening** (5.1-5.3) | missing validation at trust boundaries (gRPC, REST, XML/JSON parsing, paths, anything reaching a shell); observability gaps; missing test seams |

If a single pass number is given, run only that pass.

## Repo-specific checks
- Logging: the codebase uses `IAppLogger` (`_logger.Info("Category", ...)`), **not** `ILogger<T>` (except in hosted services). Suggest fixes in that style, even though the prompts doc says ILogger.
- DI defaults to Singleton. `OrchestratorDbContext` must be accessed via `IDbContextFactory`.
- Audit writes are fire-and-forget (`IAuditWriter`). An awaited audit write on the request path is a finding.
- Known traps worth checking:
  - Optional ctor params defaulting to null that silently disable a feature (e.g. `ILockRegistry? = null`).
  - Interfaces implemented in Core but never registered or called (a "decorative feature").
  - `Directory.Exists`/`File.Exists` treating access-denied as missing.
  - CSV parameter parser pointed at `.json`.
- Persisted enums (`Permission`, `MaintenanceKind`, `RevertPhase`, `ActionOutcome`): a removal or insertion is Critical.

## Output per pass
`| Severity | File:Line | Issue | Why it bites in production | Minimal fix |`
Severity is Critical, High, Medium or Low. Sort by severity, **at most 15 rows** per pass. Under each table, one line: "Pass N: X findings (C/H/M/L)". Keep a separate table for each pass in the final report; don't merge them.

## Final summary
Top 5 findings across all passes, what was out of scope or unverifiable, and suggested next folder.
