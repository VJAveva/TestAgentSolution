# Hardening Audit & Plan: Agent Fleet Maintenance (the USP feature)

| Field | Value |
|---|---|
| **Component** | Fleet Maintenance — reboot, revert, quarantine, recovery, (and the update/golden-image work) |
| **Goal** | Production-harden the project's flagship feature: no surprises, every error/exception handled, design debt fixed, core logic concretized |
| **Deliverable** | One document: evidence first, then a prioritized action plan |
| **Coverage stance** | Target HIGH coverage of core logic and EVERY failure path — NOT a flat 95% everywhere. Report the number as information; never write filler tests to inflate it. |

---

## Instructions (read first)

Fleet Maintenance is the project's biggest selling point. Before it goes to
production I need it hardened: every error and exception handled, every loophole
closed, design and implementation debt identified, and the core logic made robust.

Act as a senior engineer doing a **pre-production hardening audit** of this one
feature. Read the actual code. Produce ONE document (`HARDENING.md`) with two
halves: **evidence** (what you found, cited to real files) then a **prioritized
action plan** (what to fix, in what order, with a definition of done).

Do NOT change code in this task — this is analysis and planning. The deliverable
is the document.

**On coverage — important:** the goal is NOT a flat 95% line coverage. That number
can be reached with weak tests while real failure paths stay untested — a false
sense of safety. Instead:
- Measure and report current coverage as INFORMATION.
- Identify which parts are **core logic** (the state machines, orchestration,
  recovery, error handling) vs **glue** (DI, DTOs, simple mappers).
- Target HIGH coverage on core logic and, above all, **every failure path** —
  missing credentials, agent crash mid-operation, script exit codes, partial
  batch failures, timeouts, cancellation, quarantine/recovery.
- Never recommend writing filler tests just to move the percentage.
Plain language first, technical detail second, on every finding.

---

## Scope — what "Fleet Maintenance" covers here

Audit all of it, but note the differing maturity (from prior review):
- **Reboot** — in production use, proven. Harden, don't rebuild.
- **Revert** — fully wired; fails without vCloud env creds. Harden the failure and
  recovery paths.
- **Quarantine + crash recovery** — MaintenanceRecoveryService. Central to "no
  surprises" — a node must never be silently lost or silently returned to rotation.
- **Update installer / golden-image** — partly built/unreachable. Audit for what
  MUST be true before it's ever enabled, but don't treat unbuilt code as debt in
  the same way as shipped code — flag it separately.

===== PLUG IN: the real file set — FleetMaintenanceService, MachineRebootOperation,
MachineRevertOperation, MaintenanceRecoveryService, the provider/adapter, the
controller, FleetVM. =====

---

## Stage 1 — Error & exception handling audit (the core of "no surprises")

For every operation (reboot, revert, quarantine, recovery, dispatch), find and
tabulate:

1. **Every failure point** — every place a call can throw, time out, return a bad
   exit code, or return partial success. For each: is it caught? Is it handled
   correctly, or swallowed / logged-and-ignored / left to bubble?
2. **Swallowed exceptions** — every `catch { }` or `catch (Exception) { log }` that
   hides a fault. These are loophole #1 in a USP feature. List each with its file
   and what it hides.
3. **Unhandled paths** — exceptions that can escape and crash the operation or the
   host. (You have prior incidents here — dispatcher faults, mutex-on-exit, etc.)
4. **Partial failure** — operations that do several things (revert N machines,
   copy to controller, delete-then-create snapshot): what happens if step 3 of 5
   fails? Is the node left consistent, or half-done and lying about its state?
5. **Timeout & cancellation** — does every long operation have a bounded timeout?
   Is cancellation handled cleanly, or does it corrupt state / leak a quarantine?

Output: a table of **failure point -> caught? -> handled correctly? -> blast radius
-> severity (P0/P1/P2)**. This table is the backbone of the plan.

---

## Stage 2 — State-integrity & loophole audit

The nightmare for a fleet feature is a node in a wrong or unknown state. Check:

1. **The quarantine invariant** — can a node ever be quarantined and never
   recovered? Or returned to rotation while actually broken? Trace every path in
   and out of quarantine.
2. **Crash mid-operation** — if the controller dies during a revert/reboot, does
   MaintenanceRecoveryService correctly close the orphan and quarantine, for EVERY
   operation kind? Any operation it doesn't know how to recover?
3. **Concurrency** — can two operations hit the same node at once? Is the
   in-flight map (ConcurrentDictionary) actually race-safe on the check-then-act
   paths, or is there a TOCTOU window?
4. **State that lies** — anywhere the reported state (idle/free/busy/quarantined)
   can diverge from reality. (You already found one: "free" while offline.)
5. **Irreversible steps** — any operation with a point of no return (e.g. vCloud
   single-snapshot delete-then-create): is it gated behind a verify? Can it strand
   a node with no baseline?

Output: each loophole as **what can go wrong -> how -> current guard (if any) ->
severity**.

---

## Stage 3 — Design & implementation debt

Separate genuine debt from cosmetic. For each item: is it a **risk** (can cause a
production surprise) or **tidiness** (annoying but safe)? Only risks block
production.

1. **Inconsistent patterns** — the same operation done differently in different
   places (a top source of hidden bugs).
2. **Missing abstractions / leaks** — e.g. credentials from env vars with no
   validation, scripts with positional contracts, exit-code-only signalling that
   can't carry error detail.
3. **Untested core logic** — core state machines or recovery paths with no test
   (see Stage 4).
4. **Unreachable / half-built** — golden-image refresh, the update installer:
   flag as "not production debt, but must satisfy X before enabling".
5. **Config fragility** — anything that fails silently on missing/wrong config
   (the vCloud creds are the known one — are there others?).

Output: **debt item -> risk or tidiness -> file -> what fixing it prevents**.

---

## Stage 4 — Test coverage of what matters (not a flat number)

1. **Measure current coverage** and report it — overall, and split core-logic vs
   glue. State the number as information, not a target.
2. **Map tests to the failure table from Stage 1** — which failure paths have a
   test that actually asserts correct handling, and which have NONE. The gaps are
   the real risk.
3. **List the missing high-value tests** — specifically for: missing creds, agent
   crash mid-op, script exit codes 2/non-zero, partial batch failure, timeout,
   cancellation, quarantine-and-recover, the concurrency window, and the
   state-lies cases from Stage 2.
4. **Recommend the target**: high coverage on core logic + a test for every failure
   path in the Stage 1 table. If that lands the overall number near 95%, fine — but
   the number follows from covering what matters, it is not the goal.

Output: **failure path -> has a real test? (yes/no) -> the test that should exist**.

---

## Stage 5 — The hardening action plan (the payoff)

Pull it together into one prioritized plan a non-expert can drive:

1. **A single ranked table**: finding -> severity -> effort (S/M/L) -> plain-language
   "what surprise this prevents" -> the fix -> the test that proves it.
2. **Sequenced into waves:**
   - **Wave 0 — Test the failure paths that already exist but are untested.** Do
     this first: you cannot safely harden what you cannot verify. (Ties to Stage 4.)
   - **Wave 1 — Close P0 loopholes** (swallowed exceptions on critical paths, state
     that can lie, quarantine that can strand, irreversible steps without a verify).
   - **Wave 2 — P1 error handling + partial-failure + timeout/cancellation gaps.**
   - **Wave 3 — Design debt that is a real risk** (config validation, contract
     hardening, pattern consistency).
   - **Wave 4 — Tidiness** (safe to defer).
3. **A definition of done per wave** that includes: the failure path has a passing
   test, the full existing suite still passes, and — for anything touching layout
   or a live operation — it was exercised against a real/spare node, not just a
   green build.
4. **The pre-production gate:** a short checklist that must ALL be true before Fleet
   Maintenance ships — e.g. "no swallowed exception on any P0 path", "every
   operation has a recovery path tested", "no state can report free-while-offline",
   "every irreversible step is verify-gated", "core logic + all failure paths
   covered".
5. **The 5 things to fix first**, with why, and the one test that proves each.

End with a plain-language verdict: is Fleet Maintenance production-ready today, what
is the single biggest surprise risk, and what is the highest-value hardening action.

---

## Format rules

- Plain language first, code detail second, every finding.
- Every finding cites a real file/method.
- Tables for the failure map, loopholes, debt, coverage gaps, and the plan.
- Separate "risk" (blocks production) from "tidiness" (doesn't).
- Coverage is reported as information and targeted at core-logic + failure paths —
  never a flat 95% chased with filler tests.
- No code changed in this task; analysis + document only.
- Where unsure, say so and list an open question. Never invent.

---

## Start

Begin with **Stage 1** — the error & exception handling audit. Build the failure
table, append it to `HARDENING.md`, show me what you found, and pause for my
"continue". Cite real files so I can trust it.
