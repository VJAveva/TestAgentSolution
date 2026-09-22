# Fleet Maintenance — Pre-Production Hardening Audit

**Scope:** reboot, revert, quarantine, crash recovery, dispatch gating, and the (unreachable) update/golden-image work.
**Status of this document:** Stage 1 (error & exception audit) complete with evidence, plus the prioritised implementation plan. Stages 2–4 detail follows on request.
**No code was changed for this audit.**

---

## Read this first — two corrections to the brief

**1. The exception handling is better than the brief assumes.** The brief expects "swallowed exceptions… loophole #1". I read every `catch` in `TestControllerGrpc.Core/Maintenance/` (28 across 11 files). Almost all are **deliberate, narrow, and commented with their reasoning**. Examples:

- `PowerShellScriptRunner.cs:73` — `catch { /* process already gone */ }` after a kill, draining output to read the real exit code.
- `NodeReadinessProbe.cs:99` — bare `catch` while polling a rebooting agent, with a comment explaining that a gRPC deadline surfaces as `OperationCanceledException` and **must not** be mistaken for operator cancellation. That is a subtle trap, already handled.
- `MachineRevertOperation.cs:366` — best-effort operation-log write, explicitly "never fail a revert because a log could not be written".

I am not going to manufacture findings to fill a table. **There is no P0 swallowed-exception problem here.** The real risks are elsewhere, and they are about *state*, not *exceptions*.

**2. "Free while offline" does not occur.** The brief cites it as already found. It isn't in the code: `FleetVM.cs` sets `card.Status`/`offline++` and `free++` in **mutually exclusive** `else if`/`else` branches, so an offline agent is never counted free. The state-lie that *does* exist is the inverse — a **quarantined node reporting red while its agent is healthy** — which was fixed on 2026-09-22 but only at the display layer. The underlying stickiness remains (F-1 below).

---

## Stage 1 — Failure map

| # | Failure point | Caught? | Handled correctly? | Blast radius | Sev |
|---|---|---|---|---|---|
| **F-1** | Operation quarantines a node, agent later recovers | n/a | **No** — quarantine never re-evaluated | Node blocked from all pipeline work until an operator manually clears it | **P1** |
| **F-2** | Exception escapes an operation into `FleetMaintenanceService.RunAsync:221` | Yes | **No** — marks the operation Failed but **never touches `IMaintenanceStateStore`** | Node stranded in `Rebooting`/`Reverting`; `DispatchGate` blocks it; no recovery until controller restart | **P1** |
| **F-3** | Controller crashes mid-operation | Yes, by `MaintenanceRecoveryService` | Partially — **runs once at startup only** (`ExecuteAsync => RecoverAsync`) | Orphans created *while running* (see F-2) are never reconciled | **P1** |
| **F-4** | `AgentWait` exceeds timeout on a slow boot | Yes | Quarantines — but timeout is a **hard-coded 15 min** (`RebootRequest.AgentWaitTimeout`), never set by any caller | False quarantine on slow machines; manual recovery each time | **P1** |
| **F-5** | Revert script exits non-zero (e.g. **exit 2 = missing vCloud creds**) | Yes | Quarantines the node — but the node was **already quarantined before the script ran**, so a pure config error costs a node | Node out of rotation for a misconfiguration | **P2** |
| **F-6** | Agent command returns failure | Yes | `ActionResult` is `(Success, ExitCode, ErrorMessage)` — **no stdout**, so detail cannot cross the boundary | Diagnosability, not correctness | **P2** |
| **F-7** | Script runner process races / cannot read exit code | Yes | `exitCode = -1`, reported not thrown | Correct | ✅ |
| **F-8** | Operation-store write fails | Yes | Best-effort, logged (`:205`, `:307`) | History gap only | ✅ |
| **F-9** | Agent unreachable during readiness poll | Yes | Retries until timeout; distinguishes cancel from deadline | Correct | ✅ |
| **F-10** | Two operations on one node | Yes | `_active.TryAdd` guard + `DispatchGate` | Correct | ✅ |
| **F-11** | vCloud snapshot replace (single-snapshot platform) | n/a | **Irreversible; no verify gate reachable** — but `IGoldenImageRefreshOperation` is unregistered, so unreachable | None today; **blocks enabling** | **Gate** |

### The one that matters most — F-2

`MachineRebootOperation` restores the state invariant in its own handler:

```csharp
catch (Exception ex)
{
    if (op.Phase != RevertPhase.Precheck)
        _stateStore.Set(request.NodeId, MaintenanceState.Quarantined);
```

The outer net in `FleetMaintenanceService.RunAsync:221` does **not**:

```csharp
catch (Exception ex)
{
    _logger.Error(...);
    result = running.Operation with { State = MaintenanceOperationState.Failed, ... };
    try { await _operationStore.SaveAsync(result, ...); } catch { /* best-effort */ }
}
```

The operation record says Failed; the **node still says `Rebooting`**. `DispatchGate` blocks `Rebooting`, and `MaintenanceRecoveryService` won't look again until restart. The node is silently unusable.

In plain language: **the safety net doesn't do the one thing the thing it's catching for does.** The trigger is narrow (an exception escaping an already broad handler), which is why this is P1 not P0 — but the effect is a node lost with no signal.

---

## Implementation plan

### Wave 0 — Prove the failure paths before changing them
You cannot harden what you cannot verify. No production code changes in this wave.

| Test to add | Proves |
|---|---|
| Operation throws → assert node state is **not** left `Rebooting` | F-2 (will **fail** today — that is the point) |
| Recovery pass invoked with a mid-run orphan | F-3 |
| `AgentWait` timeout → quarantine, then agent returns | F-1 |
| Script exit 2 → node quarantined, reason surfaced | F-5 |
| Cancellation mid-phase → quarantined, not stranded | F-2/F-4 |

**Done when:** each test exists and the F-2 test is **red** against current code.

### Wave 1 — Close the state loopholes (P1)

| Fix | Effort | Prevents |
|---|---|---|
| `RunAsync`'s catch sets `MaintenanceState.Quarantined` (mirroring the operation handlers) | **S** | A node silently stranded in `Rebooting` forever |
| `MaintenanceRecoveryService` reconciles **periodically**, not once at startup | **M** | Mid-run orphans surviving until the next restart |
| Reboot-only: clear quarantine when the quarantine came from an `AgentWait` timeout **and** the agent is now healthy | **M** | The node the operator must hand-clear after every slow boot |

The third is a **policy change** and should be reboot-only. "Agent came back" *is* reboot's success criterion; it is not revert's, where a half-reverted VM is genuinely unknowable.

### Wave 2 — Timeouts and config (P1/P2)

| Fix | Effort | Prevents |
|---|---|---|
| Make `AgentWaitTimeout` configurable via `MaintenanceOptions` | **S** | False quarantines on machines that boot slower than 15 min |
| Validate vCloud credentials at **precheck**, before quarantining | **S** | Losing a node to a config error |

### Wave 3 — Contract hardening (P2, tidiness-adjacent)

| Fix | Effort | Prevents |
|---|---|---|
| Carry stdout on `ActionResult`, or formalise the sentinel-envelope pattern | **M** | Every future agent-side feature re-inventing the `OutputReceived`+sentinel workaround |

### Wave 4 — Gate for the unbuilt work (not debt — a precondition)

Golden-image refresh must satisfy **all** of these before it is ever registered:

- [ ] Verify phase passes before any baseline replace
- [ ] On vCloud (`MaxSnapshotsPerVm = 1`, `CanRetainPreviousBaseline = false`) the replace is **irreversible** — must be gated behind an explicit config flag, default off
- [ ] Proven end-to-end on a throwaway agent with the flag off
- [ ] `INodeUpdateInstaller` exercised against a real agent, not only mocks

---

## The five things to fix first

| # | Fix | The one test that proves it |
|---|---|---|
| 1 | `RunAsync` catch sets Quarantined | Operation throws → node is Quarantined, not Rebooting |
| 2 | Periodic recovery reconciliation | Orphan created mid-run is closed without a restart |
| 3 | Configurable `AgentWaitTimeout` | Configured value reaches `WaitForAgentAsync` |
| 4 | Reboot quarantine auto-clears on healthy return | Timeout → quarantine → agent healthy → state returns to None |
| 5 | Credential precheck before quarantine | Missing creds → precheck failure, node **never** quarantined |

---

## Verdict

**Is Fleet Maintenance production-ready today?** Reboot is — it is in daily use with a clean history. Revert is wired but blocked on credentials. The update/golden-image path is unreachable and should stay that way.

**The single biggest surprise risk** is not an unhandled exception — it is **a node silently leaving the pool**. F-2 and F-1 both end the same way: a machine that looks fine, is healthy, and quietly accepts no work. On a 9-node fleet, losing two to sticky quarantine is a 22% capacity loss that shows up as "the pipeline is slow", not as an error.

**Highest-value hardening action:** Wave 1's first item. It is a handful of lines, it mirrors logic that already exists one layer down, and it closes the only path where a node can be lost with no operator signal at all.

**One open question I could not resolve:** the Maintenance grid currently shows **"0 agents reporting"** on a fleet of 9 online agents. Either the deployed agents predate Windows Update detection or it is disabled in their config. Until that is understood, every update-posture-driven behaviour in this feature — including `RebootRequiredEffect` gating dispatch — is running on data nobody is sending.

---

# Stage 2 — State integrity & loopholes

| # | What can go wrong | How | Current guard | Sev |
|---|---|---|---|---|
| **L-1** | Node quarantined and **never** recovered | Quarantine has exactly one exit: an operator calling `ClearQuarantineAsync`. Nothing re-evaluates it — not health, not time, not a later successful operation | None by design. `MaintenanceRecoveryService` *adds* quarantine, never removes it | **P1** |
| **L-2** | Node stranded in a transient state | F-2 — `RunAsync`'s catch never writes the state store | None | **P1** |
| **L-3** | Orphan created while the controller keeps running | `MaintenanceRecoveryService.ExecuteAsync` is a **single pass at startup** | None until restart | **P1** |
| **L-4** | Two operations on one node | — | ✅ **Sound.** `_active.TryAdd` is atomic and *is* the check — no TOCTOU. The code says so explicitly | ✅ |
| **L-5** | Crash recovery misses an operation kind | — | ✅ **Sound.** Recovery iterates `GetUnfinishedAsync` and quarantines **kind-agnostically**, so a new kind is covered automatically | ✅ |
| **L-6** | State that lies: healthy node shown as failed | Quarantine is independent of health; both rendered with `StatusFailedBg` | ⚠️ **Display fixed 2026-09-22** (amber + explicit hint). The underlying stickiness is L-1 | **P2** |
| **L-7** | Irreversible snapshot replace strands a node | vCloud: `MaxSnapshotsPerVm = 1`, `CanRetainPreviousBaseline = false` — replace destroys the only rollback point | Unreachable today (`IGoldenImageRefreshOperation` unregistered). `TryRevertToBaselineAsync` exists but has **0% coverage** | **Gate** |

**The quarantine invariant, stated plainly:** a node can enter quarantine from five paths (precheck failure past phase 1, any operation exception, `AgentWait` timeout, cancellation, crash recovery) and leave by exactly one (a human). That asymmetry is deliberate for revert — a half-reverted VM is genuinely unknowable — but it is what turns a slow boot into a lost node.

---

# Stage 3 — Design & implementation debt

| # | Item | Risk or tidiness | Where | What fixing it prevents |
|---|---|---|---|---|
| **D-1** | Safety net doesn't restore the state invariant its inner handlers do | **RISK** | `FleetMaintenanceService.cs:221` | A node lost with no signal (F-2/L-2) |
| **D-2** | **Two different script contracts for one feature** — `Revert-AgentVM.ps1` takes *positional* `<VmName> <Snapshot>`; `Vm-Ops.<platform>.ps1` is *verb-dispatched* returning a JSON envelope | **RISK** | `Utilites/RevertAgents/` | Silent breakage when someone edits the wrong contract; two parsing paths to maintain |
| **D-3** | `ActionResult` is `(Success, ExitCode, ErrorMessage)` — **no stdout** | **RISK** (diagnosability) | `ServiceTypes.cs:4` | Every agent-side feature re-inventing a workaround. `NodeUpdateInstaller` already had to use `OutputReceived` + a sentinel envelope to get data back |
| **D-4** | `AgentWaitTimeout` hard-coded 15 min; **no caller ever sets it** | **RISK** | `MaintenanceModels.cs:58`, `FleetVM.cs:459` | False quarantines on slow-booting machines |
| **D-5** | vCloud credentials read from env vars, validated only when the script runs — *after* the node is quarantined | **RISK** | `Revert-AgentVM.ps1` exit 2 | Losing a node to a config error |
| **D-6** | `VirtualizationProviderOptions.ScriptPath` derives from `RevertScriptPath`'s directory when unset | **Tidiness** (my own addition, 2026-09-22) | `FleetMaintenanceExtensions.cs` | Couples two configs; convenient but implicit |
| **D-7** | Per-view duplicated styles (`Fld` ×4, `SmBtn` ×4, no shared DataGrid style) | **Tidiness** — *and outside this feature's scope* | Views layer | Visual drift, not maintenance risk |

---

# Stage 4 — Coverage of what actually matters

**Measured**, not estimated: maintenance suite (204 tests) with `XPlat Code Coverage`.

### The headline is not the line number

| Type | Line % | **Branch %** |
|---|---|---|
| `MachineRebootOperation` | 86 | **46** |
| `MachineRevertOperation` | 83 | **47** |
| `MachineRevertOperation.ExecuteAsync` | 68 | 58 |
| `FleetMaintenanceService.RunAsync` | **52** | **50** |

Line coverage of 83–86% on the two shipped state machines looks healthy. **Branch coverage under 50% says more than half the decision points are never exercised** — and in a state machine the decision points *are* the failure paths: precheck rejections, quarantine conditions, cancellation checks, timeout branches.

This is precisely the false sense of safety the brief warned about. The happy path is well covered. The failure paths largely are not.

`FleetMaintenanceService.RunAsync` at 52%/50% is the sharpest signal: **the uncovered half contains the F-2 catch block.**

### Core logic with zero coverage

| Type | Line % | Why it matters |
|---|---|---|
| `PowerShellScriptRunner` | **0** | The component that actually launches every revert. Its process-race and exit-code defences — which I praised in Stage 1 — are **entirely unverified** |
| `MaintenanceOperationStore` (all methods) | **0** | Persistence. **Crash recovery reads `GetUnfinishedAsync` from this** — the recovery path depends on a wholly untested store |
| `MachineRebootOperation.CancelAsync` | **0** | The cancellation path, which writes quarantine |
| `NodeReadinessProbe.WaitForPingAsync` | **0** | One of the two readiness strategies |
| `GoldenImageRefreshOperation.TryRevertToBaselineAsync` | **0** | The rollback of the irreversible operation |
| `UpdatePostureHostedService`, `MaintenanceHubBridge` | **0** | Posture plumbing |

**Caveat, stated honestly:** `MaintenanceController` also reports 0%, but I only ran the *maintenance* suite. Its endpoints are plausibly covered by `TestController.WebApi.Tests` (735 tests), which I did not include. **I have not verified that** — treat the controller as unmeasured, not untested.

### Failure path → has a real test?

| Failure path (Stage 1) | Real test today? | The test that should exist |
|---|---|---|
| F-2 exception escapes into `RunAsync` | **No** | Operation throws → node is Quarantined, not left `Rebooting` |
| F-3 orphan created mid-run | **No** | Recovery pass closes it without a restart |
| F-1 quarantine then healthy return | **No** | Timeout → quarantine → agent healthy → state observable |
| F-5 script exit 2 (missing creds) | **No** | Exit 2 → quarantined with the reason surfaced |
| Cancellation mid-phase | **No** (`CancelAsync` 0%) | Cancel → quarantined, not stranded |
| Script runner kill / unreadable exit code | **No** (0%) | Timeout kills the tree; exit code reported as -1 |
| `GetUnfinishedAsync` returns orphans | **No** (0%) | Store round-trip: unfinished rows are found after a restart |
| Duplicate operation on one node | **Yes** | ✅ covered |
| Reboot happy path | **Yes** | ✅ covered |

**Recommended target:** every row above marked *No* gets a test, and branch coverage on `MachineRebootOperation` / `MachineRevertOperation` / `FleetMaintenanceService` rises above **80%**. If overall line coverage lands near 95% as a side effect, fine — but branch coverage on the state machines is the number that actually means something here, and it is the one to hold people to.

---

## Plan update following Stages 2–4

Wave 0 now has concrete, measurable exits rather than a list of intentions:

- **Wave 0 exit criteria:** the eight *No* rows above have tests; the F-2 test is **red** before the fix; branch coverage on the three core types exceeds 80%.
- **New Wave 1 item (was not visible from Stage 1):** `MaintenanceOperationStore` has 0% coverage and crash recovery depends on it. A store round-trip test is a prerequisite for trusting *any* recovery work.
- **New Wave 2 item:** `PowerShellScriptRunner` at 0% — its timeout/kill/exit-code paths need tests before the revert failure paths can be trusted.
- **D-2 (two script contracts)** is promoted to Wave 3 as a genuine risk, not tidiness.

---

# Progress log

## 2026-09-22 — Wave 0 started, Wave 1 item 1 complete

**F-2 / L-2 — fixed, red-proved.**

1. Wrote `FleetMaintenanceServiceTests.RunAsync_Should_NotLeaveNodeInATransientState_When_TheOperationThrows`.
   The fake operation mirrors the real phase 2 — marks the node `Rebooting`, then throws.
2. **Confirmed RED against unfixed code:** `Assert.NotEqual() Failure: Values are equal`. The node really was
   left claiming `Rebooting`. The defect was reproduced, not assumed.
3. Fixed `FleetMaintenanceService.RunAsync`'s catch to convert a **transient** state to `Quarantined`:

   ```csharp
   if (_stateStore.Get(nodeId) is MaintenanceState.Rebooting
       or MaintenanceState.Reverting or MaintenanceState.Updating)
   ```

   Only transient states are converted, deliberately. Quarantining unconditionally would punish a node whose
   operation faulted at precheck, where no state was ever set — trading a stranded node for a false quarantine.
4. **Green**, and the whole suite with it.

**`MaintenanceOperationStore` — 0% → covered.** This was the Stage 4 prerequisite: crash recovery reads
`GetUnfinishedAsync` from a store that had no tests at all. Added 7 tests over a shared SQLite in-memory
connection, covering the round trip, the **upsert** path (the engine saves the same id once per phase, so a
second insert would throw), orphan retrieval, and — importantly — that terminal operations are **not** returned
as orphans. That last one matters: a finished operation resurfacing would quarantine a healthy node on every
controller restart.

### Measured effect

| Type | Before | After |
|---|---|---|
| `FleetMaintenanceService.RunAsync` | 52% line / **50% branch** | **100% line / 83% branch** |
| `FleetMaintenanceService` (type) | — | 90% line / 68% branch |
| `MaintenanceOperationStore` | **0%** | covered by 7 tests |

### Suites after the change

| Suite | Result |
|---|---|
| Maintenance + Ui + Rbac | **431 passed** |
| `TestController.WebApi.Tests` | **735 passed** |

### Still open in Wave 0

`PowerShellScriptRunner` (0%), `MachineRebootOperation.CancelAsync` (0%), `NodeReadinessProbe.WaitForPingAsync`
(0%), and the F-1 / F-5 failure-path tests. Branch coverage on `MachineRebootOperation` (46%) and
`MachineRevertOperation` (47%) is unchanged and remains the Wave 0 exit criterion.

