# Fleet Update Orchestration — Findings & Design

**Status:** Draft for review · **Date:** 2026-09-16
**Scope:** Controller-driven Windows Update installation, golden-image (snapshot) refresh, and multi-hypervisor support (vCloud Director, vSphere, Hyper-V), at a fleet size of 50+ VMs.

**Related docs:** [FleetRevert-UI-Spec.html](FleetRevert-UI-Spec.html) (R1–R24) · [FleetRevert-Workflow-and-CopilotPrompts.md](FleetRevert-Workflow-and-CopilotPrompts.md)

---

## 1. The target workflow

The manual process to be automated, as described by the requester:

> If there are any updates → revert machine to snapshot → perform updates → power off → delete existing snapshot(s) → create a new one in the powered-off state → power on.

This is a **golden-image refresh**, not a patch run. Two properties of it are deliberate and worth preserving:

1. **Revert before patching.** The patched image is built from the known-good baseline, not from a machine that has drifted through weeks of test runs. Without this, every refresh cycle compounds drift.
2. **Snapshot while powered off.** No memory state is captured, the snapshot is smaller and faster, and the disk is quiescent. (On vCloud this also avoids the `memory=true` path entirely.)

### 1.1 The gap in the manual process

The sequence destroys the old snapshot **before** anything has confirmed the patched machine is usable:

```
… install updates → power off → DELETE old snapshot → create new snapshot → power on
                                 ▲
                                 └─ the rollback path is gone from here onward
```

If the update leaves the machine broken, there is no baseline to return to. On vSphere/Hyper-V that is recoverable (see §3 — they can hold multiple snapshots). **On vCloud it is not**, because a VM can hold exactly one snapshot.

**This design therefore inserts a verification gate before the destructive step**, and makes the gate mandatory on single-snapshot platforms:

```
revert → install → reboot → wait ready → VERIFY → power off → replace snapshot → power on → back to rotation
                                                 ▲
                                                 └─ failure here reverts to the still-intact baseline
```

---

## 2. What exists today (verified in code)

### 2.1 Windows Update — detection only

[`WindowsUpdateDetector.cs`](../../TestAgentGrpc/Services/WindowsUpdateDetector.cs) runs on the agent as a `BackgroundService` and feeds one reporter from two paths:

| Path | Mechanism | Interval |
|---|---|---|
| Registry poll | 3 reboot-pending keys (`RebootRequired`, `RebootPending`, `PendingFileRenameOperations`) | 300 s |
| WUApi COM search | Late-bound `Microsoft.Update.Session`, query `IsInstalled=0 and IsHidden=0 and Type='Software'` | 300 s |
| Event log | `Microsoft-Windows-WindowsUpdateClient/Operational`, event IDs 19/20/43/44 | real-time |

Posture travels to the controller as `ExecutionEventType.EVENT_WINDOWS_UPDATE = 11` with a JSON body in `ExecutionEvent.detail`, coalesced 30–300 s by `WindowsUpdateReporter`.

> **There is no install capability anywhere in the repo.** Searches for `Install-WindowsUpdate`, `PSWindowsUpdate`, `UsoClient`, `wuauclt`, `IUpdateInstaller` return zero hits.

Two facts that make adding it cheap:

- The detector's WUApi search runs with **`searcher.Online = false`** — local cache only. An install flow needs an *online* search first, so this is a new call path, not a reuse.
- The agent installs as **LocalSystem** by default ([`Setup-AgentNode.ps1`](../../Utilites/TestAgent_SetupScripts/Setup-AgentNode.ps1)), so it is already elevated enough to drive WUApi installs.

⚠️ **Not uniform:** `Setup-InteractiveAgent.ps1` provisions agents under an auto-logon *user* account for desktop-session work. Those nodes may not be able to install. Capability must be **probed per node**, never assumed fleet-wide.

### 2.2 Snapshot / revert — vCloud only, via PowerShell

[`MachineRevertOperation.cs`](../../TestControllerGrpc.Core/Maintenance/MachineRevertOperation.cs) is an 8-phase state machine (`Precheck → Quarantine → SnapshotRevert → PowerOn → PingWait → AgentWait → PostPrep → Verify`) that shells out through `IPowerShellScriptRunner`:

```csharp
var revert = await _scriptRunner.RunAsync(
    new ScriptInvocation { ScriptPath = revertScript, Arguments = [request.NodeId, request.SnapshotName] },
    log, CancellationToken.None);
```

The script contract is **positional `(vmName, snapshotName)`, success signalled by exit code 0**.

[`Revert-AgentVM.ps1`](../../Utilites/RevertAgents/Revert-AgentVM.ps1) adapts that onto [`Revert-RcloudMachines.ps1`](../../Utilites/RevertAgents/Revert-RcloudMachines.ps1), which uses VMware PowerCLI against vCloud Director:

```powershell
$snapSection = $vm.ExtensionData.GetSnapshotSection()
if (-not $snapSection -or -not $snapSection.Snapshot) { Fail $s "No snapshot exists"; break }
$vm.ExtensionData.RevertToCurrentSnapshot() | Out-Null
```

Two consequences, both load-bearing for this design:

1. **`.Snapshot` is singular.** vCloud Director permits exactly one snapshot per VM. The adapter header says so outright: *"vCloud VMs keep a single snapshot"*.
2. **`SnapshotName` is decorative.** The precheck requires it and the API accepts it, but the vCloud worker ignores it — there is nothing to name. This is a latent lie in the current contract and should be resolved (§4.4).

**vSphere and Hyper-V support does not exist in any form today.** This is greenfield.

### 2.3 Fleet maintenance engine

[`FleetMaintenanceService.cs`](../../TestControllerGrpc.Core/Maintenance/FleetMaintenanceService.cs) owns in-flight operations in a `ConcurrentDictionary<string, RunningOperation>` keyed by node, with a `ConcurrentQueue<string>` for reboots beyond the concurrency cap. `MaintenanceKind` is `{ Revert, Reboot, Prep, InstallBuild }`; `MaintenanceState` is `{ None, Draining, Reverting, Rebooting, Updating, Quarantined }`.

Crash recovery exists: `MaintenanceRecoveryService` closes orphaned operations on startup and **quarantines** the node rather than returning it to rotation, because it cannot know whether the operation finished.

---

## 3. Platform capability matrix

This table is the foundation of the design. The platforms are **not** interchangeable, and hiding the difference behind a uniform interface would silently lose rollback safety on vCloud.

| Capability | vCloud Director | vSphere | Hyper-V |
|---|---|---|---|
| Snapshots per VM | **Exactly 1** | Many (tree) | Many (tree) |
| Named snapshots | ❌ No | ✅ Yes | ✅ Yes |
| Create | `CreateSnapshot()` — *replaces* existing | `New-Snapshot -Name` | `Checkpoint-VM -SnapshotName` |
| Revert | `RevertToCurrentSnapshot()` | `Set-VM -Snapshot` | `Restore-VMSnapshot` |
| Delete | `RemoveAllSnapshots()` | `Remove-Snapshot` | `Remove-VMSnapshot` |
| Power on / off | `Start-CIVM` / `Stop-CIVM` | `Start-VM` / `Stop-VM` | `Start-VM` / `Stop-VM` |
| Module | PowerCLI (`*-CIVM`) | PowerCLI (`*-VM`) | `Hyper-V` module |
| **Can keep previous baseline?** | ❌ **No** | ✅ Yes | ✅ Yes |

### 3.1 The rollback strategy must differ by platform

**Answered 2026-09-16:** on vCloud, creating a snapshot **does supersede the existing one in a single call** —
deleting first is only needed to reclaim disk, not to make room. That removes the dangerous ordering from the
default path entirely.

**Hyper-V and vSphere can hold a tree, but the fleet is operated with ONE golden image** (occasionally two or
three for specific use cases). Retention therefore defaults to `Keep = 1`.

| | vSphere / Hyper-V | vCloud |
|---|---|---|
| Default strategy | **Create, then prune** to `Keep`. A failure leaves the old baseline intact. | **Create only.** The new snapshot supersedes the old one atomically. |
| Is there ever "no snapshot"? | No | No |
| Rollback after a bad patch | Revert to the retained baseline (when `Keep` > 1) | Revert to the superseded snapshot until it is collapsed |
| Verification gate | Strongly recommended | Strongly recommended |
| Disk cost | Bounded by `Keep` | Higher — the superseded snapshot's space is not reclaimed immediately |

**Delete-then-create is now opt-in only**, behind `BaselineRetentionOptions.ReclaimSpaceBeforeReplace`. It is
the manual practice used today (to reclaim space), and it is the *one* ordering that can leave a VM with no
baseline — so it is off by default and must be chosen deliberately.

> **Design rule:** the provider abstraction exposes `SupportsAtomicSnapshotReplace` and `MaxSnapshotsPerVm`,
> and the orchestrator branches on them. The default branch on every platform is one where the VM always has a
> snapshot.

---

## 4. Proposed design

### 4.1 `IVirtualizationProvider` — a capability-aware seam

The current script contract (two positional args, exit-code only) cannot carry six operations across three platforms, and cannot return snapshot metadata. Replace it with a C# seam in Core:

```csharp
public interface IVirtualizationProvider
{
    string PlatformId { get; }                       // "vcloud" | "vsphere" | "hyperv"
    VmPlatformCapabilities Capabilities { get; }

    Task<VmPowerState> GetPowerStateAsync(IReadOnlyList<string> vmNames, CancellationToken ct);
    Task<VmOpResult> PowerOnAsync(IReadOnlyList<string> vmNames, CancellationToken ct);
    Task<VmOpResult> PowerOffAsync(IReadOnlyList<string> vmNames, bool graceful, CancellationToken ct);

    Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(string vmName, CancellationToken ct);
    Task<VmOpResult> CreateSnapshotAsync(IReadOnlyList<string> vmNames, string name, string description, CancellationToken ct);
    Task<VmOpResult> RevertSnapshotAsync(IReadOnlyList<string> vmNames, string? snapshotName, CancellationToken ct);
    Task<VmOpResult> DeleteSnapshotAsync(string vmName, string snapshotId, CancellationToken ct);
}

public sealed record VmPlatformCapabilities(
    bool SupportsNamedSnapshots,
    int MaxSnapshotsPerVm,              // vCloud = 1
    bool RequiresPowerOffForSnapshot,
    bool SupportsBatchOperations);
```

**Every method is plural-capable** because batching is where the fleet-scale win lives (§5.2).

### 4.2 Implementation: orchestration in C#, platform calls in PowerShell

Keep retry, phases, cancellation, progress and persistence in C# where they are testable. Keep PowerCLI / Hyper-V module calls in PowerShell where those modules live.

One **verb-dispatched script per platform**, not six scripts × three platforms:

```
Vm-Ops.vcloud.ps1   -Verb Revert  -Vm "a,b,c" [-Snapshot <name>]
Vm-Ops.vsphere.ps1  -Verb Create  -Vm "a,b,c"  -Snapshot "baseline-2026-09-16"
Vm-Ops.hyperv.ps1   -Verb List    -Vm "a"
```

**Contract change: structured JSON on stdout**, not exit-code-only. The orchestrator needs snapshot ids, creation dates and per-VM outcomes, none of which an exit code can carry:

```json
{ "ok": true, "platform": "vcloud", "results": [
    { "vm": "jvgr1", "ok": true,  "snapshotId": "urn:vcloud:snapshot:…", "createdUtc": "2026-09-16T10:02:11Z" },
    { "vm": "jvgr2", "ok": false, "error": "BUSY_ENTITY", "retryable": true } ] }
```

Exit code stays as a coarse fallback so the existing `IPowerShellScriptRunner` keeps working unchanged.

### 4.3 The golden-image refresh operation

New `MaintenanceKind.GoldenImageRefresh`, driven by the existing `IFleetMaintenanceService` engine so it inherits quarantine semantics, progress events, persistence and crash recovery.

| # | Phase | Notes |
|---|---|---|
| 1 | `Precheck` | Node registered, not busy, provider reachable, baseline snapshot exists, agent install-capable. **No state changed — safe to fail.** |
| 2 | `Quarantine` | `MaintenanceState.Updating`. Stops dispatch before anything destructive. |
| 3 | `RevertToBaseline` | Start clean. Point of no return for the *run*, not for the baseline. |
| 4 | `PowerOn` + `PingWait` + `AgentWait` | Reuses `INodeReadinessProbe`. |
| 5 | `SearchUpdates` | **Online** WUApi search on the agent (detector's offline cache is insufficient). |
| 6 | `InstallUpdates` | Agent-side, elevated. Streams progress. |
| 7 | `RebootAndWait` | Until `RebootRequired` clears and the agent re-registers. |
| 8 | **`Verify`** | **The gate.** Health probe + optional smoke pipeline. Failure → revert to still-intact baseline, quarantine, stop. |
| 9 | `PowerOff` | Graceful, with forced fallback. |
| 10 | `ReplaceBaseline` | Branches on `MaxSnapshotsPerVm` — see §3.1. |
| 11 | `PowerOn` + readiness | |
| 12 | `Complete` | `MaintenanceState.None`, back in rotation. |

Phase 10, the only irreversible step, in pseudocode:

```csharp
if (caps.MaxSnapshotsPerVm == 1)          // vCloud
{
    // Verified in phase 8; no way to hold both. Delete then create.
    await provider.DeleteSnapshotAsync(vm, existing.Id, ct);
    await provider.CreateSnapshotAsync([vm], newName, desc, ct);
}
else                                       // vSphere / Hyper-V
{
    // Create first, so a failure here still leaves the old baseline intact.
    await provider.CreateSnapshotAsync([vm], newName, desc, ct);
    await PruneBeyondRetentionAsync(vm, _options.BaselineRetention, ct);
}
```

### 4.4 Resolving the `SnapshotName` lie

`RevertRequest.SnapshotName` is required by precheck but ignored on vCloud. Fix by making it **optional and capability-checked**: required when `SupportsNamedSnapshots` is true, ignored-with-a-warning when false. This removes a silent no-op from the API.

---

## 5. Scaling to 50+ VMs

The current design does not fit. Four measured bottlenecks, in priority order.

### 5.1 Reboots are serial

`MaxConcurrentReboots` defaults to **1** ([`WindowsUpdateModels.cs`](../../TestControllerGrpc.Core/Maintenance/WindowsUpdateModels.cs)). Each reboot is quarantine + reboot + ping-wait + agent-wait. At ~7 min/node, 50 nodes ≈ **6 hours**.

A golden-image refresh is far longer than a reboot (revert + patch + verify + snapshot), so at concurrency 1 a full-fleet refresh is measured in **days**. Concurrency must be configurable and enforced per *platform*, since the real constraint is how much of the fleet may be offline simultaneously.

### 5.2 Per-VM PowerCLI sessions — the biggest available win

The adapter passes a single machine (`-Machines $VmName`), but the worker is explicitly built for batch: it issues stop/revert/power-on asynchronously across all machines and polls in one loop, so **10 VMs cost roughly the same wall time as 1**.

At 50 nodes the current path pays 50 × (PowerCLI module load + `Connect-CIServer` + full stop/revert/poweron/ping-settle cycle). Batching at the engine boundary requires **no new technology** — the capability is already written and unused.

### 5.3 `FleetUpdatesVM` rebuilds everything, per event, with no debounce

`OnStatusChanged → Refresh()` → `Rows.Clear()` + full rebuild + banners + notifications ([`FleetUpdatesVM.cs`](../../TestControllerGrpc/ViewModels/AgentWorkspace/FleetUpdatesVM.cs)).

`FleetVM` received a 250 ms debounce whose comment reads *"At 200 agents with heartbeats every 5s, up to 40 events/second can arrive"* — **that fix was never applied to `FleetUpdatesVM`.** Two O(N²) loops compound it: the orphan scan (`Rows.All(...)` inside a `Where`) and `SyncCardBadges` (`Cards × Rows.FirstOrDefault`).

At 50 agents × 12 registry polls/hour = 600 full rebuilds/hour, each O(N²).

### 5.4 A 5-second probe of every agent, forever

[`FleetVM.cs`](../../TestControllerGrpc/ViewModels/AgentWorkspace/FleetVM.cs) probes every registered agent every 5 s — ~10 gRPC calls/sec at 50 agents, sustained. `AgentWorkspaceVM` constructs `FleetVM` eagerly, so this runs whether or not the tab is visible. Offline VMs make it worse, as each probe waits out a timeout.

### 5.5 UI at 50 rows

- Maintenance `DataGrid` is capped at `MaxHeight="200"` — fine for 9 rows, unusable for 50. (It *is* virtualized; `EnableRowVirtualization` defaults true.)
- Banners inline every agent name via `string.Join(", ", …)` — a 50-name wall of text.
- No pagination, grouping or status filter on the maintenance list.

---

## 6. Phased plan

Every phase has an exit criterion. A phase without a number drifts.

### Phase 0a — Answer the platform mix *(gate, not an open question)*

Inventory the 50+ VMs: how many are vCloud vs vSphere vs Hyper-V. This decides whether the single-snapshot
constraint (§3) is the common case or the exception, and therefore whether Phase 4 outranks Phase 3.

**This gates Phases 1 and 4 only.** Phases 0b and 0c are platform-independent work on the existing vCloud
path and should run in parallel — do not serialise them behind an inventory.

*Done when:* every node in the fleet inventory carries a platform tag.

### Phase 0b — Batching *(zero-risk win, ship on its own first)*

Batch provider calls through the worker's already-written multi-machine support. No new technology, no new
abstraction, biggest available scale win, independently shippable.

*Done when:* a 10-VM revert takes roughly the same wall time as a 1-VM revert. **Measure it** — the worker's
"10 VMs ≈ same wall time as 1" claim is a code comment, not an observation.

### Phase 0c — Make 50 nodes viable *(the rest of §5)*

- Configurable reboot/refresh concurrency.
- Debounce `FleetUpdatesVM`; remove both O(N²) loops.
- Scale the health-probe interval with fleet size; suspend it when the tab is hidden.
- Maintenance list: group by state, add a status filter, drop the 200 px cap.

*Done when:* a full-fleet reboot of 50 nodes completes inside **90 minutes**. At ~7 min/node that implies
sustained concurrency of ~4; the real cap is set by §8.4 (how much of the fleet may be offline at once).

### Phase 1 — Provider abstraction

- `IVirtualizationProvider` + `VmPlatformCapabilities` in Core.
- `ScriptBackedVirtualizationProvider` with the JSON contract (§4.2).
- `Vm-Ops.vcloud.ps1` refactored from the existing worker — **behaviour-identical**, proven by re-running the
  current revert tests unchanged.
- `SnapshotName` capability check (§4.4) — with a **logged warning** when the name is ignored, never a silent
  no-op.

*Done when:* the existing revert tests pass against the new provider with no test edits.

### Phase 2 — Controller-driven update install

- `MaintenanceKind.InstallUpdates`; agent-side online search + install; per-node capability probe.
- Progress streamed through the existing maintenance progress channel.

*Done when:* a node with pending updates is patched end-to-end from the controller, and a node running under
an interactive account reports **unsupported** rather than failing opaquely.

### Phase 3 — Golden-image refresh

- `MaintenanceKind.GoldenImageRefresh` (§4.3) with the verification gate.
- Platform-branched baseline replacement; retention policy on multi-snapshot platforms.
- **Partial-batch policy implemented** (§6.1) — not left to runtime chance.
- Own RBAC permission — more destructive than a reboot.

*Done when:* a deliberately-broken patch is caught by the gate and the node returns to its intact baseline.

### Phase 4 — vSphere and Hyper-V providers

Priority set by Phase 0a's answer.

- `Vm-Ops.vsphere.ps1`, `Vm-Ops.hyperv.ps1`.
- Per-node platform binding in the fleet inventory.
- Retention-based rollback enabled on both.

*Done when:* the same refresh operation runs unmodified against a node of each platform.

### 6.1 Partial-batch policy *(decided, not deferred)*

The JSON contract returns per-VM outcomes, so a batch of 10 can return "7 ok, 3 failed". At 50 nodes this is
the **normal case**, not an edge case.

**Decision: continue-and-quarantine.** Successful VMs proceed through their remaining phases; failed VMs are
quarantined individually and the batch continues.

Rationale: abort-the-batch lets one transient `BUSY_ENTITY` poison nine healthy machines, and it contradicts
the engine's existing per-node quarantine semantics. The operation is reported successful only if every node
succeeded, so a partial result is still a visible failure — it just is not a contagious one.

This applies to the verification gate too: baselines are replaced **per VM**, only for the nodes that passed.

---

## 7. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| Baseline destroyed by a bad patch on vCloud | **Critical** — no rollback | Mandatory verification gate before delete; refuse refresh if fleet health is degraded |
| **vCloud replace window — no snapshot exists between delete and create** | **Resolved 2026-09-16.** vCloud's create supersedes the existing snapshot atomically, so the default path never deletes first and the window does not occur. It remains reachable only via the opt-in `ReclaimSpaceBeforeReplace`, which is off by default | Default to atomic replace. When space reclamation is explicitly enabled, keep the gap as narrow as possible (nothing between the two calls), make the create non-cancellable, and report `FailedBaselineMissing` so an exposed VM is named rather than buried in a generic failure |
| **Superseded snapshots consume disk on vCloud** | Storage growth over repeated refreshes | Monitor datastore usage; enable `ReclaimSpaceBeforeReplace` per environment if growth outpaces capacity, accepting the exposure window as a deliberate trade |
| **Partial batch failure** ("7 ok, 3 failed") | Normal case at 50 nodes, not an edge case | Continue-and-quarantine per §6.1; operation reported successful only when every node succeeded |
| Mixed agent identities (LocalSystem vs interactive) | Install fails on some nodes | Probe capability per node; report unsupported explicitly, never silently skip |
| Long operation window (~30–60 min/node) vs crash recovery | Node stuck mid-refresh | Persist phase; `MaintenanceRecoveryService` quarantines on restart (already the behaviour) |
| Too much of the fleet offline at once | No capacity for test runs | Concurrency cap expressed as *max fraction of fleet offline*, not a raw count |
| vCloud `BUSY_ENTITY` under batch load | Spurious failures | Existing worker already retries with backoff; preserve it |
| Snapshot storage growth on vSphere/Hyper-V | Datastore exhaustion | Retention policy with a hard cap; surface snapshot size in the UI |

---

## 8. Open questions

1. ~~**Platform mix**~~ — **answered: vCloud first.** Hyper-V second; vSphere last. Phase 4 is scoped to Hyper-V
   before vSphere.

2. **What does "verified" mean?** *Recommendation: start with ping + agent re-register + a lightweight health
   probe.* Make the smoke-pipeline gate a later, opt-in option per pipeline. A heavy gate makes refreshes so
   slow that people disable it — and a gate that gets switched off protects nothing. It must be fast enough
   that skipping it is never tempting.

3. **Automatic or explicit?** *Recommendation: explicit initially*, automation behind a flag once the
   verification gate has proven itself.

4. **Concurrency budget** — what fraction of the fleet may be offline simultaneously during a refresh window?
   This sets the concurrency cap, and therefore Phase 0c's 90-minute exit criterion.

5. ~~**Retention depth**~~ — **answered: one baseline.** `Keep = 1` on every platform. Hyper-V and vSphere can
   hold more, and occasionally two or three are used for specific cases, but one is the operating default.

6. **Update selection** — install all pending updates, or security-only / a KB allow-list?

7. ~~**Does vCloud `CreateSnapshot` replace atomically?**~~ — **answered: yes.** It supersedes the existing
   snapshot in one call; deleting first is only a space optimisation. The replace window is therefore absent
   from the default path (§3.1, §7).

8. **Cohort or per-node?** — see §9.5. Needs deciding before refresh is wired.

---

## 9. Implementation status and pending work

As of 2026-09-16. Everything below that is marked *built* is unit-tested against fakes; nothing has been run
against a live hypervisor or a real patched node.

### 9.1 Built and verified

| Component | Notes |
|---|---|
| Fleet UI scale fixes (Phase 0c 1–3, 5) | Debounced rows rebuild, both O(N²) loops removed, adaptive probe cadence + tab suspension, status filter, severity ordering, capped banner name list |
| Reboot concurrency proof (Phase 0c 4) | Mechanism was already correct; `FleetMaintenanceConcurrencyTests` pins it at 50 nodes across caps of 1/4/10 |
| `IVirtualizationProvider` + `VmPlatformCapabilities` | Capability-aware seam; `CanRetainPreviousBaseline` drives the rollback strategy |
| `VmOpsEnvelope` + parser | Last well-formed JSON line wins; an **unmentioned VM is recorded as failed**, never silently passed |
| `ScriptBackedVirtualizationProvider` | Batches by default, serial fallback, exit-code fallback, warns when a snapshot name is dropped (§4.4) |
| `BaselineReplacer` | Platform-branched ordering; distinguishes `FailedBaselineIntact` from `FailedBaselineMissing` |
| `GoldenImageRefreshOperation` | 12-phase state machine with the verification gate (§4.3) |

### 9.2 Not built — blocked on live systems or a decision

| Item | Blocked by |
|---|---|
| `Vm-Ops.vcloud.ps1` / `.vsphere.ps1` / `.hyperv.ps1` | Needs live vCloud / vSphere / Hyper-V to write and verify |
| Agent-side `INodeUpdateInstaller` (online WUApi search + install) | Needs a real node with pending updates |
| Phase 0a platform inventory | Human task |
| Phase 0b batching measurement | Needs a live 10-VM revert to time |
| Replace-window mitigation | Open question 7 |

### 9.3 Built but deliberately NOT wired

`GoldenImageRefreshOperation` and `ScriptBackedVirtualizationProvider` have **no DI registration, no façade
method, and no UI entry point**. They are reachable only from tests.

This is intentional: making a destructive operation callable before its provider scripts exist — and before
the replace window is understood — invites exactly the accident this design exists to prevent. Wiring is the
first task *after* §9.2 clears, not before.

Wiring will need: DI registration, `IFleetMaintenanceService.StartRefreshAsync`, a per-node platform binding
in the fleet inventory, a confirmation dialog naming what gets destroyed, and its own RBAC permission.

### 9.4 Known gaps in what *is* built

1. ~~**Refresh operations are not concurrency-capped.**~~ **CLOSED 2026-09-16.**
   `FleetMaintenanceService` now throttles per *kind*: `CapFor(kind)` reads `MaxConcurrentReboots` for reboots and
   the new `MaxConcurrentRefreshes` (default **1**) for refreshes, with a **separate queue per kind** so a
   saturated refresh queue cannot head-of-line block a reboot that has a free slot. The count-then-start
   decision is taken under `_startGate`; previously two callers could both read "under cap" and both start,
   so the reboot cap could be exceeded under race. Kinds not listed in `CapFor` are uncapped, which preserves
   revert's existing behaviour exactly.

2. **`BaselineMissing` is only a return status.** When a vCloud refresh dies between delete and create, the VM
   has no snapshot at all — and today that fact lives only in an operation history row. It needs to be a
   visible node condition (badge, filter, banner) so an exposed VM cannot go unnoticed.

3. **Crash recovery does not understand the new phases.** `MaintenanceRecoveryService` quarantines orphaned
   operations, which is right, but a node interrupted at `ReplaceBaseline` needs stronger handling than a
   generic quarantine — recovery should check whether a snapshot actually exists.

4. **The refresh does not use the provider's batching** — see §9.5.

5. **No RBAC permission.** Refresh is more destructive than reboot and currently has no distinct gate.

### 9.5 Windows Update is a per-node unit — and that has a consequence

**Yes, the update is applied per agent, as a single unit.** `INodeUpdateInstaller` takes one `nodeId` for
`IsInstallSupportedAsync` / `SearchAsync` / `InstallAsync`, and `GoldenImageRefreshOperation` runs one node per
invocation.

That is correct for the *install itself*: it executes on the agent via WUApi, so there is no shared
controller-side session to amortise — sending one gRPC call to ten agents saves nothing, and each node
patches at its own pace.

**But it leaks into the VM operations.** A per-node refresh calls the provider with a single-element list, so
a 50-node fleet refresh would open **50 separate PowerCLI sessions** — precisely the waste Phase 0b exists to
remove. The batching capability is built and would go unused.

**Proposed fix (future work): a cohort model.** Run the fleet through the phases together rather than as N
independent state machines:

```
all revert (1 batched call) → each patches independently → all verify
    → all power off (1 batched call) → all snapshot (1 batched call) → all power on
```

That turns ~50 PowerCLI sessions into ~4, while leaving the genuinely per-node step — the patch — per-node.
It also makes the partial-batch policy (§6.1) load-bearing: a cohort of 10 that returns "7 ok, 3 failed"
carries the 7 forward and quarantines the 3.

The trade-off is that a cohort moves at the pace of its slowest node, so cohort size becomes a tuning knob
against the concurrency budget (§8.4).

### 9.6 DECIDED 2026-09-16: per-agent, not cohort

**The cohort model in §9.5 is deferred. Refresh runs one agent at a time.** Rationale, per the decision: lower
risk now, revisit later.

What this buys:

- **Blast radius is one machine.** A cohort shares a batched call, so one malformed provider response can
  mis-sequence every VM in the cohort. Per-agent, a bad response strands exactly one node.
- **Recovery stays comprehensible.** Crash recovery already reasons per node (`_active` is keyed by node,
  `MaintenanceOperation` carries one `NodeId`). A cohort would need a new unit of recovery spanning nodes
  that are in *different* phases after a partial batch failure.
- **No new partial-batch policy.** §6.1 ("7 ok, 3 failed") stops being load-bearing; every call is 1-of-1, so
  the envelope's per-VM results collapse to a single unambiguous outcome.

What it costs, stated plainly:

- **PowerCLI sessions are not amortised.** A 50-node refresh opens ~50 sessions rather than ~4. With
  `MaxConcurrentRefreshes = 1` these are sequential, so the cost is *wall-clock*, not load: roughly one
  connect (~6 s observed) per node, negligible beside a full revert-patch-snapshot cycle.
- **`ScriptBackedVirtualizationProvider`'s batching stays built but unused.** It is not removed — the plural
  signatures are what a future cohort needs, and calling them with one-element lists costs nothing.
- **A 50-node fleet refresh is a long serial operation.** If that becomes the constraint, the lever is
  `MaxConcurrentRefreshes`, not the cohort model — raise it once the provider script is trusted.

This reverses §9.5's "decide before wiring" advice in the safe direction: per-agent is the model that is
*harder to retrofit away from*, but it is also the one that cannot silently damage ten machines at once.
