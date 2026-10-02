# Fleet update → snapshot: audit

**Date:** 2026-10-02 · **Scope:** "Install updates → reboot → verify → create snapshot" from the Agent Fleet UI
**Method:** read of the current source on branch `Rbsl`, plus live checks against JVGR22. No code was changed.

---

## 1. The short version

The common assumption — "the code exists but nothing is wired" — is **half right, and the wrong half is the dangerous one.**

Two separate things are going on:

**The update installer is fully wired and reachable today.** `NodeUpdateInstaller` is registered in DI
([FleetMaintenanceExtensions.cs](TestController.Api/FleetMaintenanceExtensions.cs#L32)) and the Fleet UI has a live
button bound to it ([MaintenanceView.xaml](TestControllerGrpc/Views/AgentWorkspace/MaintenanceView.xaml#L219)).
Anyone looking at the Agent Fleet screen right now can check for updates and install them on a production node.

**The snapshot half is deliberately switched off.** `GoldenImageRefreshOperation` exists and is tested, but it is
not registered, there is no `StartRefreshAsync` on the service interface, and no UI can reach it. The comment at
[FleetMaintenanceExtensions.cs](TestController.Api/FleetMaintenanceExtensions.cs#L31) says so explicitly:
*"Inert until something resolves it: IGoldenImageRefreshOperation is deliberately NOT registered."*

So the risk is not that nothing works. It is that **the destructive half works and the safety half does not.**

The single most important finding in this audit:

> The UI "Install updates" path calls the installer **directly**. It does not create a maintenance operation, does
> not set the node's maintenance state, and does not take a lock. Every safety mechanism this codebase has for
> fleet maintenance — the dispatch gate, the one-operation-per-node guard, the concurrency cap, and crash
> recovery — keys off a maintenance operation that this path never creates. A node can therefore be handed test
> work in the middle of installing Windows updates, and a controller crash mid-install leaves no record at all.

The orchestrated version of this flow (`GoldenImageRefreshOperation`) gets all of that right. It is simply
unreachable. **Most of the work below is wiring and gating, not new capability.**

---

## 2. Inventory

| Component | Exists? | Wired (DI / service / API / UI) | Tested? | What it does |
|---|---|---|---|---|
| `INodeUpdateInstaller` | Yes — [GoldenImageContracts.cs:21](TestControllerGrpc.Core/Maintenance/GoldenImageContracts.cs#L21) | — | — | Probe / Search / Install contract |
| `NodeUpdateInstaller` | Yes — [NodeUpdateInstaller.cs:23](TestControllerGrpc.Core/Maintenance/NodeUpdateInstaller.cs#L23) | **DI yes** ([:32](TestController.Api/FleetMaintenanceExtensions.cs#L32)). No API endpoint. **UI yes** | `NodeUpdateInstallerTests.cs` | Sends WUApi PowerShell inline as `-EncodedCommand` over the existing `RunRemoteCommand` path; parses a `##TCWU##`-delimited JSON line off `OutputReceived`, filtered by agent name |
| "Check for updates" UI path | Yes — `UpdateActionCommand` [FleetUpdatesVM.cs:317](TestControllerGrpc/ViewModels/AgentWorkspace/FleetUpdatesVM.cs#L317) | **Bound** at [MaintenanceView.xaml:219](TestControllerGrpc/Views/AgentWorkspace/MaintenanceView.xaml#L219) | `NodeUpdateActionTests.cs` | One button: Check → Install. Probes `IsInstallSupportedAsync` first |
| `MachineRebootOperation` | Yes | **DI yes** ([:27](TestController.Api/FleetMaintenanceExtensions.cs#L27)); API `POST /reboot`; UI FleetVM + Fleet grid | `FleetMaintenanceServiceTests`, concurrency tests | Remote `shutdown` dispatched to the agent, then waits for re-registration |
| `MachineRevertOperation` | Yes | **DI yes** ([:26](TestController.Api/FleetMaintenanceExtensions.cs#L26)); API `POST /revert`; UI `RevertMachineViewModel` | Yes | Reverts to baseline snapshot via `Revert-AgentVM.ps1` |
| `GoldenImageRefreshOperation` | Yes — [:16](TestControllerGrpc.Core/Maintenance/GoldenImageRefreshOperation.cs#L16) | **NOT registered**; no `StartRefreshAsync` on [IFleetMaintenanceService](TestControllerGrpc.Core/Maintenance/MaintenanceContracts.cs#L37); **no UI** | `GoldenImageRefreshOperationTests.cs` | The full 12-phase state machine. Unreachable at runtime |
| `BaselineReplacer` | Yes — [:53](TestControllerGrpc.Core/Maintenance/BaselineReplacer.cs#L53) | **NOT registered** anywhere | `BaselineReplacerTests.cs` | Chooses a safe snapshot-replacement ordering per platform |
| `IVirtualizationProvider` / `ScriptBackedVirtualizationProvider` | Yes | **DI yes**, via factory ([:29](TestController.Api/FleetMaintenanceExtensions.cs#L29), [:41](TestController.Api/FleetMaintenanceExtensions.cs#L41)) — but its only consumers are unregistered, so it is **inert** | `VirtualizationProviderRegistrationTests.cs` | Snapshot/power verbs via `Vm-Ops.<platform>.ps1` |
| `Vm-Ops.vcloud.ps1` | Yes — `Utilites/RevertAgents/` | **Deployed on JVGR22** — verified 14,891 bytes, 2026-09-17 | — | Verb-dispatched create/delete/revert/power/list |
| `MaintenanceRecoveryService` | Yes — [:50](TestControllerGrpc/Services/MaintenanceRecoveryService.cs#L50) | **DI yes**, hosted service | `MaintenanceRecoveryServiceTests.cs` | On restart, closes unfinished operations as Failed and quarantines their nodes |
| Quarantine | Yes — `MaintenanceState.Quarantined` | API `POST /nodes/{id}/clear-quarantine`; UI "Return to rotation" | Yes | Sticky hold-out until an operator clears it |
| `DispatchGate` | Yes — [:26](TestControllerGrpc.Core/Maintenance/DispatchGate.cs#L26) | Used at `ExecutionController` :402/:858 and `MainViewModel.Execution` :82 | `DispatchGateTests.cs` | Blocks dispatch for Reverting / Rebooting / **Updating** / Quarantined |
| `UpdatePostureCoordinator` | Yes | **DI yes** ([App.xaml.cs:153](TestControllerGrpc/App.xaml.cs#L153)) | `UpdatePostureCoordinatorTests.cs` | Drains on posture, moves idle+RebootRequired nodes to `Updating`, optional auto-reboot inside a policy window |
| `MaintenanceController` | Yes | revert / reboot / precheck / cancel / clear-quarantine / active / history / updates / policy | Yes | **No install endpoint and no refresh endpoint** |
| Agent-side update execution | Yes | `WindowsUpdateDetector` is **detect-only** (`Online = false`); installation happens through the inline script | `WindowsUpdateSettingsDefaultsTests.cs` | Detect posture; install arrives as a remote command |

---

## 3. End-to-end flow

| # | Step | Status | Evidence |
|---|---|---|---|
| 1 | Drain node | **Partial** | `UpdatePostureCoordinator.OnNodeIdle` drains and sets `Updating` — but only when posture already says RebootRequired. The UI install button drains nothing |
| 2 | Search updates (online) | **Works** | `NodeUpdateInstaller.SearchAsync`; deliberately online, unlike the detector's cached scan |
| 3 | Install (agent, elevated) | **Works** | `InstallAsync`; `IsInstallSupportedAsync` probes first, because auto-logon agents cannot install |
| 4 | Reboot | **Partial** | `MachineRebootOperation` works and is in production use, but nothing chains it to the install. The UI reports "a reboot is required" and waits for a human |
| 5 | Agent re-registers | **Works** (inside operations) | `NodeReadinessProbe.WaitForAgentAsync` |
| 6 | Verify health + update status | **Partial** | Implemented as phase 9 of `GoldenImageRefreshOperation` — unreachable. No verification at all on the UI path |
| 7 | Create snapshot | **Missing** | `BaselineReplacer` unregistered; no caller |
| 8 | Return to rotation | **Partial** | Works for reboot/revert; the UI install path never left rotation, so there is nothing to return |
| 9 | Report in Fleet UI | **Partial** | Per-node transient action state only. No operation row, so nothing in history and nothing after a restart |

---

## 4. Integrity risks

### 4.1 A node receives test work while updating — **NO GUARD on the live path**

`DispatchGate` does block `Updating` ([DispatchGate.cs:36](TestControllerGrpc.Core/Maintenance/DispatchGate.cs#L36)).
But [`InstallUpdatesAsync`](TestControllerGrpc/ViewModels/AgentWorkspace/FleetUpdatesVM.cs#L372) never sets that
state and never takes a lock — it calls `_installer.InstallAsync` directly. A node is only protected if it happened
to be `Draining`/`Updating` already from posture. **Click "Install updates" on an idle, healthy node and it stays
fully dispatchable for the entire install.**

### 4.2 Reboot or crash mid-install; controller crash mid-operation — **guarded only for operations**

`MaintenanceRecoveryService` closes unfinished operations and quarantines their nodes on restart
([:50–75](TestControllerGrpc/Services/MaintenanceRecoveryService.cs#L50)). That is solid — but it reads
`GetUnfinishedAsync`, and the UI install path **creates no operation row**. A controller crash during a UI-initiated
install leaves no record, no quarantine, and a half-patched node back in rotation.

### 4.3 Snapshot taken before verification, or with a reboot pending — **guarded, in unreachable code**

`GoldenImageRefreshOperation` puts Verify at phase 9, before PowerOff (10) and ReplaceBaseline (11), and on
verification failure reverts to the intact baseline rather than promoting
([:170–182](TestControllerGrpc.Core/Maintenance/GoldenImageRefreshOperation.cs#L170)). It also reboots and re-waits
whenever `install.RebootRequired`. The ordering is right; nothing can currently run it.

### 4.4 vCloud single snapshot — delete-then-create window — **guarded by default**

`BaselineReplacer.ReplaceAsync` uses atomic replace on vCloud and create-then-prune on tree platforms.
Delete-then-create is reachable **only** when `ReclaimSpaceBeforeReplace` is set, and it defaults to `false`
([BaselineRetentionOptions](TestControllerGrpc.Core/Maintenance/BaselineReplacer.cs#L33)). The distinct
`FailedBaselineMissing` status exists so a lost baseline is reported as such. This is the best-guarded area in the
whole feature.

### 4.5 Two operations on one node; a whole pool at once — **guarded for operations only**

One-per-node is enforced by `_active.TryAdd` ([FleetMaintenanceService.cs:98](TestControllerGrpc.Core/Maintenance/FleetMaintenanceService.cs#L98)).
Concurrency is kind-aware ([CapFor :144](TestControllerGrpc.Core/Maintenance/FleetMaintenanceService.cs#L144)):
Reboot and GoldenImageRefresh are capped, everything else is `int.MaxValue`. Two gaps: `InstallUpdates` is **not**
capped, and the UI install path bypasses both guards entirely — nothing stops an operator from starting installs on
every node in the pool at once.

### 4.6 Agent does not come back after reboot — **generic guard only**

`NodeReadinessProbe.WaitForAgentAsync` plus quarantine on timeout (default `AgentWaitTimeout` 15 min). There is
**no** specific detection for firewall Block rules, WinRM state, or a network profile flipping to Public — all of
those present identically as "agent did not reconnect". Diagnosis is left to a human.

### 4.7 Credentials, exit codes, timeouts, partial success

- **Credentials: broken on the target today.** `RCLOUD_USER`, `RCLOUD_PASSWORD`, `RCLOUD_ORG` are all **unset at
  Machine scope on JVGR22** (verified during this audit). Both `Revert-AgentVM.ps1` and `Vm-Ops.vcloud.ps1` fail
  with exit 2 and a clear message — but **nothing checks this at precheck**, so the failure surfaces only after the
  node has already been quarantined.
- **Exit codes:** `Vm-Ops.vcloud.ps1` returns 0/1/2(creds)/3(PowerCLI) and emits a JSON envelope; an unmentioned VM
  is recorded as failed rather than assumed fine.
- **Timeouts:** install is capped at 3 hours (`InstallTimeout`); agent waits at 15 min.
- **Partial success:** `UpdateInstallResult` carries `FailedCount` separately from `InstalledCount`, but the UI
  path only branches on `Ok` — a run that installed 4 of 9 updates is reported simply as "Installed 4 update(s)".

### 4.8 Verification of the facts you flagged

| Claim | Verdict |
|---|---|
| Reboot works in production | **Confirmed in code** — registered, API + UI reachable, concurrency-tested. Not re-run live during this audit |
| Revert needs `RCLOUD_USER` / `RCLOUD_PASSWORD` | **Confirmed, and they are missing on JVGR22** |
| Reboot-required should use `Auto Update\RebootRequired` only | **Code does not match the brief** — it ORs `Auto Update\RebootRequired` **and** `Component Based Servicing\RebootPending` into one boolean used for both drain and reporting. `PendingFileRenameOperations` is counted and debug-logged but gated behind `TreatPendingFileRenamesAsRebootRequired`, default `false` ([WindowsUpdateDetector.cs:152](TestAgentGrpc/Services/WindowsUpdateDetector.cs#L152)). Resolved by D7 — see §7.2 |
| Posture reports Ok/Failed/Stale/Unknown | **Confirmed** — `UpdateScanStatus` ([WindowsUpdateEnums.cs:24](TestControllerGrpc.Core/Maintenance/WindowsUpdateEnums.cs#L24)) |

One correction to earlier notes: `Vm-Ops.vcloud.ps1` **is** deployed on JVGR22. A previous note claiming it was
missing was wrong; measured this session.

---

## 5. Gaps — build vs wire

| # | Gap | Build or wire | Size |
|---|---|---|---|
| G1 | UI install does not set `Updating`, take a lock, or create an operation | **Wire** | **S** |
| G2 | `InstallUpdates` has no concurrency cap in `CapFor` | **Wire** | **S** |
| G3 | No precheck for vCloud credentials before a destructive phase | **Build** (small) | **S** |
| G4 | `BaselineReplacer` not registered in DI | **Wire** | **S** |
| G5 | No `StartRefreshAsync` on `IFleetMaintenanceService` | **Build** (mirror `StartRebootAsync`) | **M** |
| G6 | `IGoldenImageRefreshOperation` not registered | **Wire** | **S** |
| G7 | No API endpoint and no UI entry for refresh | **Build** | **M** |
| G8 | Fleet grid shows no operation/phase for update work | **Build** | **M** |
| G9 | Partial-install outcome not surfaced or acted on | **Build** | **S** |
| G10 | No canary/throwaway-node concept | **Build** | **M** |
| G11 | Reboot not chained to install on the UI path | **Build** | **M** |
| G12 | No post-reboot agent-return diagnostics (firewall/WinRM/profile) | **Build** | **L** |

Nine of twelve are S/M, and five are pure wiring. **The expensive part of this feature is already written.**

---

## 6. Proposed plan

Smallest safe slice first. Each phase is independently shippable and independently revertible.

**Phase 0 — stop the bleeding (S).** Make the existing UI install path honest: set `MaintenanceState.Updating`
before `InstallAsync` and clear it after, create a `MaintenanceKind.InstallUpdates` operation row so crash recovery
can see it, and add `InstallUpdates` to `CapFor`. No new capability — this just puts the already-live button inside
the safety framework. *Fixes G1, G2 and closes risks 4.1, 4.2, 4.5.*

**Phase 1 — credential + environment precheck (S).** Fail at precheck with a clear message when `RCLOUD_USER` /
`RCLOUD_PASSWORD` are unset, before anything quarantines a node. *Fixes G3, closes 4.7.*

**Phase 2 — register the refresh machinery, still unreachable (S).** Register `BaselineReplacer` and
`IGoldenImageRefreshOperation`. Nothing calls them yet; this is purely so the object graph resolves and a DI test
can prove it. *Fixes G4, G6.*

**Phase 3 — `StartRefreshAsync`, API only, flag default OFF (M).** Add the service method and a
`POST /maintenance/refresh` endpoint behind a feature flag that defaults to **off**, one node at a time, capped by
`MaxConcurrentRefreshes`. **Baseline replace stays behind its own flag, also default off** — so the first runs
install, reboot and verify, then stop short of touching the snapshot. *Fixes G5, G7.*

**Phase 4 — canary on a throwaway agent (M).** Run the full flow end to end against a disposable VM, with baseline
replace enabled only for that node. This is the first time a snapshot is ever written. Record what the node looked
like before and after.

**Phase 5 — Fleet grid surfacing (M).** Phase chips and operation state for update work in the Fleet grid, so an
operator can see which nodes are mid-update and why one is quarantined. *Fixes G8, G9.*

**Phase 6 — enable for the real pool, one node at a time (M).** Only after Phase 4 has been clean on a throwaway
node more than once.

Throughout: **quarantine on any failure** (already the behaviour of every phase in the state machine), and
**verify before snapshot** (already phases 9 → 11).

---

## 7. Decisions needed

| # | Decision | Options | My recommendation |
|---|---|---|---|
| D1 | Phase 0 changes behaviour of a button that is live today — a node will start refusing work during an install | Do it / leave as is | **Do it.** It is currently possible to run tests on a node mid-install and get results nobody can trust. That is worse than a node being briefly unavailable |
| D2 | Should the UI install path auto-reboot when `RebootRequired`? | Auto / prompt / leave manual | **Prompt, don't auto.** `AutoReboot` already exists for posture-driven reboots inside a window; reuse that policy rather than adding a second rule |
| D3 | Who may trigger a refresh? | Any operator / admin only | **Admin only.** It is the only irreversible operation in the system |
| D4 | `ReclaimSpaceBeforeReplace` | Leave off / allow per-run | **Leave off, permanently, on vCloud.** The disk saving is not worth the window with no baseline. Revisit only if storage actually bites |
| D5 | Set `RCLOUD_*` on JVGR22 now, or keep refresh blocked? | Set now / defer | **Defer until Phase 3.** While they are unset, no snapshot operation can run at all — that is currently a useful safety interlock, not a bug |
| D6 | Canary node | `jvhist` proposed | **Answered `jvhist` — but the premise does not hold. See 7.1.** `jvhist` is not spare; recommend picking a different node |
| D7 | Reboot-required signal | Keep / drop CBS | **Answered: split the signal. See 7.2.** Dispatch/drain uses `Auto Update\RebootRequired` only; the update flow reboots on `Auto Update` **OR** CBS; PFRO stays informational |
| D8 | Partial install (4 of 9) | Success / quarantine / flag | **Flag, don't quarantine.** Partial patching is normal; it should be visible and retried, not treated as a fault |

### 7.1 D6 — `jvhist` is not a spare node (decision needs revisiting)

The brief described `jvhist` as "spare, not in the WARM/Sanity pools". The deployed configuration says otherwise:

- It is **`_Agent3` in the Sanity profile** of `Parameters\SP2023R2SP2\pipeline-config.json` on JVGR22:
  `_AgentsGroup = "jvgr1,jvgr2,jvhist,jvkpri,jvkbak"`.
- It appears **15 times as an `AgentName`** in the deployed `WatchList.xml`, including real work:
  `Install-Build.bat`, `Prepare-Agent.bat`, a `shutdown /r` reboot action tagged *"Restart jvhist"*,
  `Copy-DependentBinaries.bat`, and `Run-WASTests.bat ... Set3` with a 7200-second timeout.

So it is an active Sanity-pool member running a two-hour smoke suite. Using it as a throwaway canary would take a
production pool node out of rotation, and — once Phase 4 writes a snapshot — would replace the baseline the Sanity
revert pipeline depends on.

**I could not confirm whether it has a snapshot to fall back to.** That query needs `RCLOUD_USER` /
`RCLOUD_PASSWORD`, which are unset on JVGR22 (§4.7). Once set, this answers it without changing anything:

```powershell
& C:\TestSetup\RevertAgents\Vm-Ops.vcloud.ps1 -Verb List -Vm jvhist
```

**Options, in order of preference:**

1. **A VM outside the 9-node roster** — nothing in `appsettings.json`, `WatchList.xml` or `pipeline-config.json`
   references it. Safest, and the only option that makes "throwaway" literally true. None exists today.
2. **`jvkbak`** — `_Agent5`, last in the Sanity group; still a pool member, but the least load-bearing of the five
   by WatchList reference count. Requires temporarily removing it from `_AgentsGroup`.
3. **`jvhist` as asked** — only with Phase 4's baseline-replace flag left **off**, so the canary proves
   install → reboot → verify and never touches the snapshot.

Option 3 is compatible with the first slice below, which writes no snapshot at all.

### 7.2 D7 — the signal has to be split in two

The decision is clear, but today **one boolean serves both purposes**, so this is a code change, not a config one.

`WindowsUpdateDetector.IsRebootRequired()`
([:152](TestAgentGrpc/Services/WindowsUpdateDetector.cs#L152)) ORs `Auto Update\RebootRequired` **and**
`Component Based Servicing\RebootPending` into a single `RebootRequired` field. That one field then drives the
**dispatch/drain** decision all the way through:

> `WindowsUpdateStatusDto.RebootRequired` → `NodeUpdateStatusStore.DeriveState`
> ([:75](TestControllerGrpc.Core/Maintenance/NodeUpdateStatusStore.cs#L75)) → `WindowsUpdateState.RebootRequired`
> → `UpdatePolicyEvaluator` ([:13](TestControllerGrpc.Core/Maintenance/UpdatePolicyEvaluator.cs#L13)) →
> `policy.RebootRequiredEffect` (default `Draining`) → `DispatchGate` blocks.

So **drain currently fires on CBS too**, which the decision says it should not.

Meanwhile the update flow's reboot decision uses neither registry key: `UpdateInstallResult.RebootRequired` comes
from the WUApi installer's own answer, `$result.RebootRequired`
([NodeUpdateInstaller.cs:201](TestControllerGrpc.Core/Maintenance/NodeUpdateInstaller.cs#L201)).

Implementing D7 therefore means:

1. Report the two registry keys as **separate fields** rather than one OR'd boolean.
2. Point the drain/dispatch path at the `Auto Update` field only.
3. Make the update flow reboot when the installer says so **or** either registry key is set.
4. Leave `PendingFileRenameOperations` exactly as it is — counted, debug-logged, gated off by default.

**Rollout order is load-bearing.** The posture payload is a wire contract
(`WindowsUpdatePayload`, carried as JSON in `ExecutionEvent.detail`). A new agent emitting a field an older
controller cannot parse makes `TryMap` return null, and that node **silently stops reporting posture entirely**.
Ship controller/web first, agents last — the same ordering recorded for the earlier posture change.


---

## 8. What I did not verify

- Reboot "works in production" is confirmed **from code and wiring**, not re-run live in this audit.
- No part of the snapshot path has ever been executed against live vCloud — `CreateSnapshot` via
  `ExtensionData.CreateSnapshot($params)` is still unproven against the real API.
- `MaintenanceController.PutPolicy` constructing a fresh policy (and so resetting unlisted fields) was flagged in
  earlier notes; I did not re-read it this pass. Worth checking before adding a policy field.
- Whether `jvhist` currently holds a snapshot — blocked on the missing vCloud credentials (§7.1).

---

## 9. First slice — install + reboot + verify on one node, no snapshot

**Goal:** make the install button that is *already live* safe, and chain reboot and verification behind it.
Stop before anything irreversible. No snapshot is written, so there is no path to losing a baseline.

### 9.1 Why this is controller-only

D7's signal split needs an agent-side change, a new field on the posture wire contract, and an agents-last
rollout across nine nodes. That is too much for a first slice, and it is not needed to make install safe.

This slice therefore reboots on **`UpdateInstallResult.RebootRequired`** — the WUApi installer's own answer,
already returned today ([NodeUpdateInstaller.cs:201](TestControllerGrpc.Core/Maintenance/NodeUpdateInstaller.cs#L201)),
requiring no agent change. The D7 registry split becomes **slice 2**.

**Accepted consequence:** until slice 2 ships, drain/dispatch still fires on `CBS\RebootPending` as well as
`Auto Update\RebootRequired`. That is the behaviour running in production today — this slice does not make it
worse, it just does not fix it yet.

### 9.2 Scope

| # | Change | Why |
|---|---|---|
| 1 | `StartInstallUpdatesAsync` on `IFleetMaintenanceService`, mirroring `StartRebootAsync`, creating a `MaintenanceKind.InstallUpdates` operation | Puts install inside the framework. `_active.TryAdd` then gives one-operation-per-node for free |
| 2 | Add `InstallUpdates` to `CapFor` with a `MaxConcurrentUpdates` policy value, default **1** | Stops a whole pool being patched at once (§4.5) |
| 3 | Set `MaintenanceState.Updating` on entry; `None` on success, `Quarantined` on any failure | `DispatchGate` already blocks `Updating` — this is the line that closes §4.1 |
| 4 | Phase sequence: `Precheck → Quarantine → SearchUpdates → InstallUpdates → RebootWait → Verify` | **Every one of these `RevertPhase` members already exists.** No enum change, so the append-only persistence rule is untouched |
| 5 | Reboot when `install.RebootRequired`, then `NodeReadinessProbe.WaitForAgentAsync` | Reuses the proven reboot + re-registration path |
| 6 | Verify = agent re-registered **and** a fresh posture report with `ScanStatus == Ok` | A node that comes back but cannot scan is not verified |
| 7 | `FleetUpdatesVM` calls the service instead of the installer directly | One front door; the operation row makes it visible in history and recoverable after a crash |

### 9.3 Explicitly out of scope

`BaselineReplacer`, `IVirtualizationProvider`, `GoldenImageRefreshOperation` and every snapshot verb stay
**unregistered and unreachable**. No vCloud call is made, so `RCLOUD_USER` / `RCLOUD_PASSWORD` can stay unset —
the interlock in §7.1 holds for free.

### 9.4 Tests

| Test | Proves |
|---|---|
| `StartInstallUpdatesAsync_Should_Throw_When_NodeAlreadyHasAnOperation` | One-per-node (§4.5) |
| `StartInstallUpdatesAsync_Should_Queue_When_CapIsOne` | Pool-wide cap (§4.5) |
| `InstallUpdates_Should_SetUpdating_When_OperationStarts` | §4.1 — the headline fix |
| `InstallUpdates_Should_Quarantine_When_InstallFails` | Failure policy |
| `InstallUpdates_Should_Quarantine_When_AgentDoesNotReturn` | §4.6 |
| `InstallUpdates_Should_SkipReboot_When_InstallerReportsNoRebootRequired` | No gratuitous reboots |
| `MaintenanceRecovery_Should_Quarantine_When_InstallOperationInterrupted` | §4.2 |
| `DispatchGate_Should_Block_When_NodeIsUpdating` | Already exists — assert it still holds |

Each must be **red-proved** against the unfixed path before being counted.

### 9.5 Canary

Per §7.1 option 3, `jvhist` is acceptable **for this slice specifically**, because no snapshot is written and the
node is only ever left in `None` or `Quarantined`. Sequence:

1. Confirm `jvhist` is idle and the Sanity pipeline is not queued.
2. Trigger install from the Fleet UI. Expect the node to show `Updating` and refuse dispatch.
3. Confirm a `MaintenanceKind.InstallUpdates` row appears in history.
4. Let it reboot and verify; expect return to `None` and dispatch to resume.
5. Re-run with the controller killed mid-install; expect `Quarantined` on restart and a history row marked Failed.

Step 5 is the one that matters most — it is the §4.2 gap, and it cannot be proved any other way.

### 9.6 Rollback

Controller-only, so the standard patch path applies with a single rollback stamp. No agent redeploy, no config
change, no schema change. Worst case the button returns to its current behaviour.

