# Fleet Maintenance — Workflow & Copilot Prompts

**Part 1:** revert a lab VM directly from the Agents ▸ Fleet panel, reusing the same PowerShell script that test plan actions call today.
**Part 2:** detect Windows updates on agent VMs, gate dispatch on them, and notify operators in the same panel.
**Target:** `TestControllerGrpc` (WPF) + `TestControllerGrpc.Core` + `TestController.WebApi`
**Companion:** `FleetRevert-UI-Spec.html` (sections 1–5 = Part 1, R1–R14; sections 6–10 = Part 2, R15–R24)

---

# Part 1 — Operator-Initiated Revert

---

## 1. The core problem, stated plainly

Today the revert script is reachable only through a test plan: `Revert Warm Machines ▸ Renamed (EVT) ▸ RevertingAgents (SEQ) ▸ RevertWarmAgents (REF)`. To revert one box an operator authors or opens a plan and runs it. That is a heavy path for a one-machine chore, and it produces plan history where maintenance history belongs.

The wrong fix is to add a second copy of the revert logic behind a fleet button. That gives two code paths that drift apart, two log formats, and two ways to forget the "take the node out of rotation first" rule.

The right fix is the pattern already load-bearing in this codebase: **one engine, another front door.** Extract the revert into a Core operation, then let both the plan action and the fleet panel call it.

```
BEFORE                                  AFTER

RevertWarmAgents (REF action)           RevertWarmAgents (REF action) ──┐
        │                                                              │
        └─► PowerShell script            Fleet panel ──────────────────┼──► IMachineRevertOperation
                                                                       │      (Core)
                                         Autopilot / scheduler ────────┘             │
                                                                                     ▼
                                                                       Revert-AgentVM.ps1
```

`MaintenanceOperation.TriggerSource` is the only thing that differs between callers, and it is exactly what the history table needs.

---

## 2. Where the revert actually runs

Worth stating before any code, because it inverts the usual direction of control in this system.

Every other agent command travels controller → gRPC → agent. A revert cannot. The agent is running *on* the machine being reverted; it will be destroyed mid-command and can never report success. So:

- The revert script runs **on the controller**, using vCloud/PowerCLI, targeting the VM by name.
- The agent's gRPC disconnect during a revert is **expected input**, not a fault. The health monitor must be told to suppress its usual "agent lost" alarm for a node in `Reverting` state.
- Success is confirmed by the agent **re-registering**, not by the script's exit code alone. Exit code 0 means "snapshot applied and power-on requested" — nothing more.

This means the readiness probe is a first-class part of the operation, not a nicety.

---

## 3. Node state model

Reuse the `MaintenanceState` concept already designed for the Windows Update detection work rather than inventing a parallel one.

```csharp
public enum MaintenanceState
{
    None = 0,        // normal, eligible for dispatch
    Reverting,       // snapshot revert in flight
    Rebooting,
    Updating,        // Windows Update (existing design)
    Quarantined      // failed maintenance, held out of rotation until cleared
}
```

**Dispatch eligibility becomes one predicate, used everywhere:**

```csharp
bool IsDispatchable(AgentNode n) =>
    n.ConnectionState == AgentConnectionState.Connected
    && n.MaintenanceState == MaintenanceState.None
    && n.ActiveSessions.Count == 0;
```

### The 409 wall

The known open gap applies directly here. Today `busy → 409 Conflict`. Once nodes can be pulled out of rotation for maintenance, a Patch Tuesday sweep plus a scheduled regression run produces a wall of 409s with no recovery path.

For this feature, the minimum acceptable behaviour is: **a dispatch request that finds no eligible node returns a typed "fleet unavailable" result naming the blocking nodes and their states**, rather than a bare 409. That is not a queue, but it is the seam a queue plugs into later. Do not let this feature ship a plain 409.

---

## 4. Operation state machine

Eight phases. The UI phase chips (R7) are a direct render of this enum — do not derive them by parsing log text.

| # | Phase | What happens | Cancellable |
|---|-------|--------------|-------------|
| 1 | `Precheck` | Node exists, not already in maintenance, snapshot name resolves, busy check | yes |
| 2 | `Quarantine` | `MaintenanceState = Reverting`, suppress disconnect alarm, abort session if forced | yes |
| 3 | `SnapshotRevert` | Invoke `Revert-AgentVM.ps1`, stream stdout/stderr | **no** |
| 4 | `PowerOn` | Script powers the VM on; wait for the power-state confirmation | no |
| 5 | `PingWait` | 120 s boot delay, then require 3 consecutive ping replies | yes |
| 6 | `AgentWait` | Wait for gRPC re-registration, default timeout 15 min | yes |
| 7 | `PostPrep` | Optional `Prepare-AgentNode.ps1`, then optional `Install-Build.bat` | yes |
| 8 | `Verify` | Agent reports expected version/build; `MaintenanceState = None` | — |

**Failure rule:** any failure past `Quarantine` leaves the node in `Quarantined`, never silently back in rotation. A machine whose revert half-finished is the most dangerous thing in the fleet — it looks healthy and runs stale bits. Clearing quarantine is an explicit operator action (R8, "Return to rotation").

**Cancel rule:** cancellation is honoured at phase boundaries only, and is refused during `SnapshotRevert`. Surface that in the tooltip (R13) rather than letting the button appear broken.

---

## 5. Contracts to generate

```csharp
// ---- Core: the shared engine both front doors call ----

public interface IMachineRevertOperation
{
    Task<MaintenanceOperation> ExecuteAsync(
        RevertRequest request,
        IProgress<MaintenanceProgress> progress,
        CancellationToken ct);
}

public sealed record RevertRequest
{
    public required string NodeId { get; init; }
    public required string SnapshotName { get; init; }
    public string? ScriptPath { get; init; }             // null = fleet default
    public bool WaitForAgent { get; init; } = true;
    public bool RunPrep { get; init; } = true;
    public bool InstallBuild { get; init; }
    public bool ForceIfBusy { get; init; }
    public TimeSpan AgentWaitTimeout { get; init; } = TimeSpan.FromMinutes(15);
    public required MaintenanceTriggerSource TriggerSource { get; init; }
    public string? TriggeredBy { get; init; }
    public string? Reason { get; init; }
    public Guid? LinkedRunId { get; init; }              // set when a plan action calls in
}

public enum MaintenanceTriggerSource { FleetPanel, PlanAction, Autopilot, WebApi }

public sealed record MaintenanceProgress(
    Guid OperationId,
    string NodeId,
    RevertPhase Phase,
    int StepNumber,
    int StepCount,
    string Message,
    TimeSpan Elapsed);

// ---- Fleet-facing service: concurrency guard + operation registry ----

public interface IFleetMaintenanceService
{
    Task<PrecheckResult> PrecheckAsync(IReadOnlyList<string> nodeIds, CancellationToken ct);
    Task<Guid> StartRevertAsync(RevertRequest request, CancellationToken ct);
    Task<bool> RequestCancelAsync(Guid operationId);
    Task ClearQuarantineAsync(string nodeId, string clearedBy);
    IReadOnlyCollection<MaintenanceOperation> ActiveOperations { get; }
    event EventHandler<MaintenanceProgress>? ProgressChanged;
    event EventHandler<MaintenanceOperation>? OperationCompleted;
}

// ---- Supporting seams ----

public interface IPowerShellScriptRunner
{
    Task<ScriptResult> RunAsync(
        ScriptInvocation invocation,
        IProgress<ScriptOutputLine> output,
        CancellationToken ct);
}

public interface INodeReadinessProbe
{
    Task<ReadinessResult> WaitForPingAsync(string host, PingOptions options, CancellationToken ct);
    Task<ReadinessResult> WaitForAgentAsync(string nodeId, TimeSpan timeout, CancellationToken ct);
}
```

**Concurrency:** `FleetMaintenanceService` holds a `ConcurrentDictionary<string, MaintenanceOperation>` keyed by `NodeId`. `TryAdd` failing *is* the duplicate-operation check — no lock, consistent with how `ExecutionSessionManager` already works.

---

## 6. Persistence

New table, one row per operation, serving both entry points:

```
MaintenanceOperations
  Id                  GUID   PK
  NodeId              TEXT
  Kind                INT     (Revert | Reboot | Prep | InstallBuild)
  SnapshotName        TEXT    null
  ScriptPath          TEXT
  State               INT     (Queued | Running | Succeeded | Failed | Cancelled)
  Phase               INT     last phase reached
  TriggerSource       INT
  TriggeredBy         TEXT    null
  Reason              TEXT    null
  LinkedRunId         GUID    null FK  ← plan-triggered reverts only
  StartedUtc          DATETIME
  CompletedUtc        DATETIME null
  ExitCode            INT      null
  FailurePhase        INT      null
  LogPath             TEXT
```

`LinkedRunId` follows the same nullable-FK convention as `TriggeredByPrId` / `MatchedRuleId` / `AutopilotRunId` in the PR pipeline design. Log text goes to a file, not the DB; the row carries the path.

---

## 7. Notification

Host-local, per the existing architecture — no cross-process singleton.

- **WPF host:** `IMaintenanceNotifier` → `Progress<T>` marshalled to the dispatcher → `FleetViewModel` updates the card in place.
- **WebApi host:** same interface, SignalR implementation. New events `maintenanceStarted`, `maintenanceProgress`, `maintenanceCompleted` on the existing hub — three additions to the documented ten.
- **Agent gRPC stream:** no new agent-side message is needed for revert. The agent is not a participant; it is the subject. (This differs from the Windows Update design, where the agent detects and reports.)

---

## 8. Credentials

`Revert-AgentVM.ps1` needs vCloud credentials. They must come from controller configuration or Windows Credential Manager and be passed as parameters — never embedded in the script.

Related and still outstanding: the live Azure DevOps PAT hardcoded in `GetBuildChanges.ps1`, `GetBuildChanges_OMI.ps1`, and `test1.ps1` still needs revoking and rotating. Do not let this feature add a second instance of the same problem.

---

## 9. Copilot prompts

Give these in order. Each builds on the last; each is scoped to one file or one region so the diff stays reviewable.

### Prompt 1 — Core contracts

```
In TestControllerGrpc.Core, create a new folder Maintenance containing:

- MaintenanceState enum: None, Reverting, Rebooting, Updating, Quarantined
- RevertPhase enum: Precheck, Quarantine, SnapshotRevert, PowerOn, PingWait,
  AgentWait, PostPrep, Verify
- MaintenanceOperationState enum: Queued, Running, Succeeded, Failed, Cancelled
- MaintenanceTriggerSource enum: FleetPanel, PlanAction, Autopilot, WebApi
- MaintenanceKind enum: Revert, Reboot, Prep, InstallBuild
- Records: RevertRequest, MaintenanceProgress, MaintenanceOperation,
  PrecheckResult, ScriptInvocation, ScriptResult, ScriptOutputLine,
  ReadinessResult, PingOptions
- Interfaces: IMachineRevertOperation, IFleetMaintenanceService,
  IPowerShellScriptRunner, INodeReadinessProbe, IMaintenanceNotifier

Use file-scoped namespaces, required init-only properties on records, nullable
reference types enabled. No implementations yet — contracts only.
Target .NET 10, C# latest.
```

### Prompt 2 — Script runner

```
Implement PowerShellScriptRunner : IPowerShellScriptRunner in
TestControllerGrpc.Core/Maintenance.

Requirements:
- Launch powershell.exe via ProcessStartInfo with
  -NoProfile -NonInteractive -ExecutionPolicy Bypass -File <script> <args>
- Redirect stdout and stderr; read both asynchronously without deadlocking
  (attach to OutputDataReceived/ErrorDataReceived, call BeginOutputReadLine
  and BeginErrorReadLine).
- Report every line through IProgress<ScriptOutputLine> with a UTC timestamp
  and a stream marker (Stdout or Stderr).
- Honour CancellationToken: on cancel, kill the process tree
  (Process.Kill(entireProcessTree: true)) and return a cancelled ScriptResult.
- Return ScriptResult with ExitCode, Duration, and whether it was cancelled.
- Never throw on a non-zero exit code; report it.
- Arguments must be escaped so a snapshot name containing spaces works.
```

### Prompt 3 — Readiness probe

```
Implement NodeReadinessProbe : INodeReadinessProbe in
TestControllerGrpc.Core/Maintenance.

WaitForPingAsync:
- Wait PingOptions.BootDelay (default 120 seconds) before the first ping.
- Then ping the host on an interval (default 5 s) until PingOptions.RequiredConsecutiveReplies
  (default 3) consecutive successes, or the overall timeout elapses.
- A failed ping resets the consecutive counter to zero.
- Use System.Net.NetworkInformation.Ping. Honour the CancellationToken.

WaitForAgentAsync:
- Complete when the agent registry reports the node connected with an active
  gRPC session, having registered AFTER the operation began (pass a
  notBeforeUtc so a stale registration does not count as success).
- Poll the registry every 3 seconds up to the timeout.
- Return ReadinessResult with Succeeded, Elapsed, and a FailureReason string.

Do not use WinRM anywhere in this class.
```

### Prompt 4 — The operation itself

```
Implement MachineRevertOperation : IMachineRevertOperation in
TestControllerGrpc.Core/Maintenance.

Constructor dependencies: IAgentRegistry, IPowerShellScriptRunner,
INodeReadinessProbe, IExecutionSessionManager, IMaintenanceOperationStore,
ILogger<MachineRevertOperation>.

Execute the phases in this exact order, reporting each through
IProgress<MaintenanceProgress> with StepNumber and StepCount:

1. Precheck   — node exists; MaintenanceState is None; snapshot name non-empty.
                If the node has active sessions and ForceIfBusy is false, fail
                immediately with a clear message naming the running WatchItem.
2. Quarantine — set MaintenanceState = Reverting; suppress the disconnect alarm
                for this node; if ForceIfBusy, abort active sessions and mark
                them Cancelled.
3. SnapshotRevert — run the revert script. Ignore CancellationToken during this
                phase (log that cancellation was deferred).
4. PowerOn    — treat a zero exit code from the script as power-on requested.
5. PingWait   — skip entirely if WaitForAgent is false.
6. AgentWait  — skip if WaitForAgent is false.
7. PostPrep   — if RunPrep, run Prepare-AgentNode.ps1; if InstallBuild, then run
                Install-Build.bat. Skip both if WaitForAgent is false.
8. Verify     — set MaintenanceState = None only on full success.

Failure handling: on any failure after phase 2, set MaintenanceState =
Quarantined and record FailurePhase. Never return the node to rotation on a
failed or cancelled operation. Persist the operation row at start, on every
phase transition, and at completion.
```

### Prompt 5 — Fleet service

```
Implement FleetMaintenanceService : IFleetMaintenanceService in
TestControllerGrpc.Core/Maintenance.

- Hold ConcurrentDictionary<string, MaintenanceOperation> keyed by NodeId.
  StartRevertAsync uses TryAdd as the duplicate guard; on failure throw
  MaintenanceInProgressException naming the node and the existing operation.
- PrecheckAsync returns, per node: whether it is dispatchable, its current
  MaintenanceState, and the name of any WatchItem it is executing. The dialog
  uses this to render its busy warning.
- Run each operation on a background task; do not block the caller.
- Re-raise IProgress callbacks as the ProgressChanged event and remove the
  dictionary entry in a finally block before raising OperationCompleted.
- ClearQuarantineAsync sets MaintenanceState back to None and writes an audit row.
```

### Prompt 6 — Refactor the existing plan action

```
Find the existing test plan action that invokes the machine revert PowerShell
script (used by the "RevertWarmAgents" REF action). Refactor it so it no longer
launches PowerShell directly. Instead it should:

- Depend on IMachineRevertOperation.
- Build a RevertRequest with TriggerSource = PlanAction, TriggeredBy = the run's
  initiator, and LinkedRunId = the current execution session id.
- Forward progress into the existing action progress reporting.
- Preserve the action's current XML schema and parameter names exactly, so
  existing WatchList.xml files keep working without edits.

This is a behaviour-preserving refactor. Do not change the script, its
parameters, or the action's serialized shape.
```

### Prompt 7 — WPF, regions R1/R2/R3

```
In TestControllerGrpc, extend the Agents Fleet panel.

R3: add an IsSelectMode bool to FleetViewModel. When true, each agent card shows
a checkbox bound to AgentCardViewModel.IsSelected. Add a "Select" ToggleButton to
the fleet toolbar. Support shift-click range selection. Escape exits select mode
and clears the selection.

R1: add a "Maintenance" split-button to the fleet toolbar, right of the search
box. Its dropdown holds: Revert to snapshot…, Reboot machines…, Run agent prep…,
Install build…, separator, Maintenance defaults…. Commands operate on the current
selection and are disabled when the selection is empty, with a tooltip explaining
why.

R2: add a ⋮ overflow button to each agent card, and wire the same ContextMenu to
right-click on the card body. Items: Revert to snapshot… (Ctrl+R), Reboot machine…,
Run agent prep…, separator, Restart agent service, Open remote desktop, separator,
Maintenance history, Take out of rotation.

Disable rather than hide unavailable commands, and give each disabled item a
tooltip stating the reason. Match the existing dark theme resources — do not
introduce new brushes.
```

### Prompt 8 — WPF, revert dialog (R4/R9/R10)

```
Create RevertMachineDialog (Window + RevertMachineViewModel) in TestControllerGrpc.

Layout top to bottom:
- R4: an ItemsControl of removable agent chips, seeded from the selection.
      Removing the last chip disables the confirm button.
- R9: a warning panel, visible only when PrecheckAsync reports busy nodes. It
      names each busy node and the WatchItem it is running.
- A snapshot ComboBox and a read-only script path field.
- R10: four checkboxes — Wait until the agent reconnects (default on), Run agent
      prep after revert (default on), Install build after prep (default off),
      Force revert busy agents (default off).
      Unchecking "Wait until the agent reconnects" must disable and uncheck the
      two below it.
      Checking "Force revert busy agents" changes the confirm button text to
      "Force revert N agents" and its style to the destructive brush.
- A single-line Reason TextBox.
- Footer: node count, estimated duration, Cancel, and the confirm button.

Call IFleetMaintenanceService.PrecheckAsync when the dialog opens and bind the
result. On confirm, issue one StartRevertAsync per chip and close immediately —
do not wait for completion.
```

### Prompt 9 — WPF, live card state (R5/R6/R12/R13)

```
Extend AgentCardViewModel and the fleet panel to show maintenance in place.

R5: when MaintenanceState != None, the card spans the full panel width, uses the
amber card style, and shows: the phase message, a determinate ProgressBar bound to
StepNumber/StepCount, and a line reading "Step N of M · {elapsed} · snapshot {name}".
Elapsed updates on a DispatcherTimer at 1 s.

R6: group agent cards by status with headers in this order — Maintenance,
Available, Busy, Offline. Hide empty groups. Use a CollectionViewSource with
grouping rather than separate ItemsControls.

R12: the header counter line reads "{n} agents · {b} busy · {m} reverting · {f} free",
omitting the reverting term when m is zero and colouring it amber when present.

R13: show a Cancel button on maintenance cards, bound to
IFleetMaintenanceService.RequestCancelAsync. Disable it while Phase ==
SnapshotRevert with the tooltip "Cannot cancel while the snapshot is being applied".

Subscribe to IFleetMaintenanceService.ProgressChanged and marshal updates to the
UI thread with Dispatcher.InvokeAsync.
```

### Prompt 10 — WPF, Maintenance tab (R7/R8/R11/R14)

```
Add a fourth tab "Maintenance" to the Agents panel, beside Fleet, Monitor and
Registry, with a badge showing the count of active operations.

R7: an ItemsControl of active and recent operations. Each row shows node name,
phase, status message and elapsed time; below it a WrapPanel of phase chips
(Precheck, Out of rotation, Snapshot revert, Power on, Ping, Agent reconnect,
Prep, Verify) styled done / current / pending / failed from RevertPhase; below
that a monospace, auto-scrolling log view fed by the script output stream.
Chip state comes from the phase enum, never from parsing log text.

R14: under the operation detail, a horizontal provenance strip with cells:
Triggered from, By, Reason, Snapshot, Script, Linked run.

R11: below the active list, a DataGrid of completed operations with columns
Agent, Operation, Snapshot, Source, By, Started, Duration, Result. The Source
column renders MaintenanceTriggerSource as a pill; plan-triggered rows show the
plan name from LinkedRunId.

R8: on OperationCompleted, raise a toast through the existing notification
service — auto-dismiss after 8 s on success; sticky on failure, naming the failed
phase and offering "View log" and "Return to rotation" (the latter calls
ClearQuarantineAsync).
```

### Prompt 11 — Dispatch gate and the 409

```
Update node selection so a node is dispatchable only when:
  ConnectionState == Connected
  && MaintenanceState == MaintenanceState.None
  && ActiveSessions.Count == 0

Extract this into a single method IsDispatchable(AgentNode) and use it
everywhere a node is currently chosen for work — do not leave a second copy
of the condition anywhere.

When no node is dispatchable, do not return a bare 409. Return a
FleetUnavailableResult listing each candidate node with its blocking reason
(Busy / Reverting / Updating / Quarantined / Disconnected), and surface that
list in the WPF and WebApi error paths. Add a TODO comment marking this as the
insertion point for the future run queue.

Also: while a node's MaintenanceState is Reverting, the agent health monitor
must not raise its "agent lost" alarm for that node. Add that suppression.
```

### Prompt 12 — WebApi surface

```
In TestController.WebApi add a MaintenanceController with:

  POST   /api/maintenance/revert           → StartRevertAsync, returns operation id
  POST   /api/maintenance/precheck         → PrecheckAsync
  POST   /api/maintenance/{id}/cancel      → RequestCancelAsync
  POST   /api/maintenance/nodes/{nodeId}/clear-quarantine
  GET    /api/maintenance/active
  GET    /api/maintenance/history?nodeId=&from=&to=

Set TriggerSource = WebApi on requests arriving here.

Add a SignalR implementation of IMaintenanceNotifier emitting maintenanceStarted,
maintenanceProgress and maintenanceCompleted on the existing hub. Register it in
the WebApi host's DI only — the WPF host keeps its own dispatcher-based
implementation. Same interface, different concrete type per host.
```

---

## 10. Build order

Prompts 1–6 give a working revert with no UI, testable from a unit test or a
scratch console. Do that first and confirm a real revert of `JVKPRI` end to end,
including the failure path where the agent never comes back. Only then start
prompt 7.

This is the same phased discipline the PR pipeline work uses: prove the engine
against the real fleet before building the surface that will hammer it.

---

## 11. Test checklist

| Scenario | Expected |
|---|---|
| Revert an idle node, all options on | All 8 phases pass; node returns Available; one history row, source Fleet panel |
| Revert a busy node without force | Rejected at Precheck; node untouched; dialog shows the running WatchItem |
| Revert a busy node with force | Session aborted and marked Cancelled; revert proceeds |
| Agent never re-registers | Fails at AgentWait; node Quarantined; sticky toast; machine left powered on |
| Cancel during PingWait | Operation Cancelled; node Quarantined (not returned to rotation) |
| Cancel during SnapshotRevert | Button disabled; if invoked via API, returns refused with a reason |
| Two reverts on one node | Second rejected with MaintenanceInProgressException |
| Revert during a scheduled dispatch | Node excluded from selection; if no node is free, FleetUnavailableResult not 409 |
| Existing plan action still runs | Unchanged WatchList.xml executes; history row shows source Plan with LinkedRunId |
| Snapshot name with spaces | Passed to the script correctly quoted |

---
---

# Part 2 — Windows Update Detection & Fleet Notification

**Companion sections:** `FleetRevert-UI-Spec.html` sections 6–10, regions R15–R24.

---

## 12. Why this is the mirror image of Part 1

| | Revert | Windows Update |
|---|---|---|
| Who initiates | Controller (operator, plan, autopilot) | The machine itself |
| Direction | Controller → PowerShell → VM | Agent → gRPC → controller |
| Agent's role | Subject — it gets destroyed | Participant — it reports |
| Confirmed by | Agent re-registering | Nothing; it is a claim about state |
| Failure mode | Machine half-reverted | Machine silently unfit to test on |

Both write to the same `MaintenanceState`, the same history table, and the same
dispatch predicate. That shared spine is what makes it one feature rather than two.

The important consequence: **an update notification is an entry point into the
revert engine.** A node reporting `RebootRequired` is most often answered by a
reboot or a revert, both of which run through `IFleetMaintenanceService`. Build
the notification so its primary action starts a `MaintenanceOperation` (R20), not
so it merely informs.

### The failure this actually prevents

An agent that installed updates and is waiting to reboot still answers pings, still
holds a gRPC session, and still looks green. It will happily accept a regression run
and produce results from a machine whose patch state no longer matches the baseline —
or reboot itself halfway through and orphan the session. That silent-green state is
the whole reason this feature exists; the notification is secondary to the dispatch gate.

---

## 13. Detection: hybrid edge + level

Neither mechanism alone is sufficient. Use both.

**Edge — `EventLogWatcher`.** Catches transitions the moment they happen, with the
KB list and per-update result in the payload.

Channel: `Microsoft-Windows-WindowsUpdateClient/Operational`
Provider: `Microsoft-Windows-WindowsUpdateClient`

| Event ID | Meaning | Maps to |
|---|---|---|
| 44 | Download started | `UpdatePending` |
| 43 | Installation started | `UpdateInstalling` |
| 19 | Installation successful | `UpdateInstalled` |
| 20 | Installation failure | `UpdateFailed` |

```
*[System[Provider[@Name='Microsoft-Windows-WindowsUpdateClient']
  and (EventID=19 or EventID=20 or EventID=43 or EventID=44)]]
```

**Level — registry polling.** Survives agent restarts, VM reverts, dropped
subscriptions, and events that fired while the agent was down. This is the source
of truth for "is a reboot pending right now".

| Key / value | Condition |
|---|---|
| `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired` | key exists |
| `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending` | key exists |
| `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager` → `PendingFileRenameOperations` | value present and non-empty |

Any one true ⇒ reboot required.

**Why hybrid:** the event log tells you *what happened and which KBs*; the registry
tells you *what is true now*. An agent that restarts after a Windows-initiated
reboot has no events to replay — only the registry can tell it the machine is clean
again. Conversely the registry cannot tell you that KB5044033 failed with 0x80073712.

**Startup snapshot.** On connect, the agent always sends a level-state report
regardless of whether anything changed, tagged `source = Startup`. Without it, the
controller's picture of a node is undefined between agent restart and the first
event. This is also what repopulates state after a VM revert.

**Permissions.** Reading the Operational channel requires the agent's service
account to be in the local **Event Log Readers** group. Add that to
`Prepare-AgentNode.ps1` as an idempotent step — do not apply it by hand on the four
VMs, or it will vanish at the next revert.

---

## 14. State model: three tiers, not two

`MaintenanceState` gains one member and the dispatch predicate gains one tier.

```csharp
public enum MaintenanceState
{
    None = 0,
    Draining,        // ← new: keeps its current run, accepts no new work
    Reverting,
    Rebooting,
    Updating,
    Quarantined
}

public enum WindowsUpdateState
{
    Unknown = 0,     // agent has not reported yet
    UpToDate,
    UpdatePending,   // downloaded, not installed
    UpdateInstalling,
    RebootRequired,
    Suppressed       // inside the post-revert grace window
}
```

Keep these **separate**. `WindowsUpdateState` is a fact about the machine;
`MaintenanceState` is a decision about scheduling. The policy table (R24) is the
mapping between them, and it is configurable precisely because the right answer
differs per fleet.

```csharp
public enum DispatchEligibility { Eligible, Draining, Blocked }

DispatchEligibility Evaluate(AgentNode n) =>
    n.ConnectionState != AgentConnectionState.Connected ? DispatchEligibility.Blocked
    : n.MaintenanceState is Reverting or Rebooting or Updating or Quarantined ? DispatchEligibility.Blocked
    : n.MaintenanceState is Draining ? DispatchEligibility.Draining
    : DispatchEligibility.Eligible;

bool IsDispatchable(AgentNode n) => Evaluate(n) == DispatchEligibility.Eligible;
```

`Draining` exists because killing an eight-step regression run to install a Defender
definition update is worse than the update waiting twenty minutes. A draining node
finishes what it holds, then transitions to `Blocked` and becomes rebootable.

Default policy (R24), all overridable:

| Update state | Effect |
|---|---|
| `UpdatePending` | Notify only, stays `Eligible` |
| `UpdateInstalling` | `Blocked` |
| `RebootRequired` | `Draining`, then `Blocked` on run completion |
| `UpdateFailed` | Notify only, stays `Eligible` |

**Auto-reboot defaults off.** An unattended reboot on a warm GR box is how a
carefully staged configuration silently disappears.

---

## 15. Post-revert suppression

A reverted VM restores a snapshot that is by definition behind on updates. Windows
Update will notice within minutes and the fleet will light up amber — for a machine
that is doing exactly what was asked of it.

So: for `SuppressionWindow` (default 60 min) after a `MaintenanceOperation`
completes on a node, update events are recorded but do not raise notifications or
change `MaintenanceState`. The node shows `Suppressed`.

**Surface the suppression as a feed entry anyway** (R23, section 7). A silently
swallowed alert is worse than a noisy one — the operator needs to see that the
system chose not to alarm and why. `RebootRequired` arising *inside* the window is
still suppressed but is re-evaluated the moment the window closes.

---

## 16. Protobuf contract

Extend the existing bidirectional agent stream. No new service, no new connection.

```protobuf
enum MaintenanceEventKind {
  MAINTENANCE_EVENT_UNSPECIFIED = 0;
  UPDATE_PENDING     = 1;
  UPDATE_INSTALLING  = 2;
  UPDATE_INSTALLED   = 3;
  UPDATE_FAILED      = 4;
  REBOOT_REQUIRED    = 5;
  REBOOT_CLEARED     = 6;
}

enum MaintenanceEventSource {
  SOURCE_UNSPECIFIED = 0;
  EVENT_LOG          = 1;
  REGISTRY_POLL      = 2;
  STARTUP_SNAPSHOT   = 3;
}

message UpdateItem {
  string kb_id       = 1;   // "KB5041585"
  string title       = 2;
  string result      = 3;   // Installed | Failed | Pending
  string result_code = 4;   // "0x80073712", empty on success
}

message WindowsUpdateStatus {
  bool   reboot_required             = 1;
  int32  pending_count               = 2;
  repeated UpdateItem items          = 3;
  google.protobuf.Timestamp last_install_utc = 4;
}

message NodeMaintenanceEvent {
  string node_id                              = 1;
  MaintenanceEventKind kind                   = 2;
  MaintenanceEventSource source               = 3;
  WindowsUpdateStatus status                  = 4;   // always the full level state
  google.protobuf.Timestamp detected_utc      = 5;
  string agent_version                        = 6;
}
```

**Always send the complete `WindowsUpdateStatus`, not a delta.** The controller
must be able to reconstruct a node's state from a single message, because messages
are lost when connections drop and this is exactly the window in which updates
install. `kind` says what triggered the send; `status` says what is true.

Add `NodeMaintenanceEvent` as a new case in the existing agent→controller `oneof`.

---

## 17. Coalescing

Windows Update produces bursts — a cumulative update, three definition updates and
a servicing stack update fire within seconds. One notification per event is noise.

Agent side: buffer events for `CoalescingWindow` (default 30 s), then send one
`NodeMaintenanceEvent` carrying the merged item list and the highest-severity kind
(`REBOOT_REQUIRED` > `UPDATE_FAILED` > `UPDATE_INSTALLED` > `UPDATE_INSTALLING` >
`UPDATE_PENDING`). Reset the timer on each new event, cap the total buffer at 5 min
so a slow trickle still reports.

`REBOOT_CLEARED` is never coalesced — it is good news and should land immediately,
since it returns a node to rotation.

---

## 18. Patch Tuesday makes the queue gap urgent

On the second Tuesday of the month, plausibly all five agents reach
`RebootRequired` within the same hour. Every one drains, then blocks. Any scheduled
regression dispatching in that window finds nothing eligible.

Today that produces a wall of 409s with no recovery path. Part 1 (§3) specified the
minimum: return a typed `FleetUnavailableResult` naming each node and its blocking
reason instead of a bare 409. **This part is what makes that non-optional** — before
update detection, an empty fleet was a rare accident; after it, it is a monthly
certainty.

Two mitigations worth building now, both cheap:

1. **Stagger.** When auto-reboot is enabled, never reboot more than
   `MaxConcurrentReboots` (default 1) nodes at a time; queue the rest. A serial
   sweep of five VMs costs fifteen minutes and keeps four testable throughout.
2. **Maintenance window.** An optional daily window during which auto-reboot may
   run. Outside it, nodes drain and wait for an operator.

Neither is a run queue. Both narrow the hole until one exists.

---

## 19. Controller-side contracts

```csharp
public interface INodeUpdateStatusStore
{
    NodeUpdateStatus? Get(string nodeId);
    IReadOnlyCollection<NodeUpdateStatus> GetAll();
    void Apply(NodeMaintenanceEvent evt);              // idempotent, last-write-wins
    event EventHandler<NodeUpdateStatusChanged>? Changed;
}

public sealed record NodeUpdateStatus
{
    public required string NodeId { get; init; }
    public required WindowsUpdateState State { get; init; }
    public required MaintenanceEventSource LastSource { get; init; }
    public DateTimeOffset? LastEventUtc { get; init; }
    public DateTimeOffset LastReportUtc { get; init; }   // for staleness display
    public DateTimeOffset? LastInstallUtc { get; init; }
    public int PendingCount { get; init; }
    public IReadOnlyList<UpdateItemDto> Items { get; init; } = [];
    public DateTimeOffset? SuppressedUntilUtc { get; init; }
    public DateTimeOffset? SnoozedUntilUtc { get; init; }
    public bool Acknowledged { get; init; }
}

public interface IUpdatePolicyEvaluator
{
    MaintenanceState Evaluate(NodeUpdateStatus status, UpdatePolicy policy);
}

public interface IFleetNotificationService
{
    IReadOnlyList<FleetNotification> Notifications { get; }
    void Raise(FleetNotification n);
    void Acknowledge(Guid id);
    void Snooze(string nodeId, TimeSpan duration);
    void MarkAllRead();
    event EventHandler? NotificationsChanged;
}
```

`Apply` must be idempotent: the same startup snapshot arriving twice after a
reconnect must not produce two notifications. Key deduplication on
`(NodeId, Kind, DetectedUtc)`.

**Persistence:** update status is *live state*, not history — keep it in memory and
rebuild from startup snapshots on controller restart. Write to
`MaintenanceOperations` only when an update leads to an actual operation (a reboot
or revert), so the history table keeps meaning one thing.

---

## 20. Copilot prompts, 13–20

### Prompt 13 — Protocol and shared types

```
Extend the existing agent gRPC contract in the shared .proto file.

Add: MaintenanceEventKind enum (UNSPECIFIED, UPDATE_PENDING, UPDATE_INSTALLING,
UPDATE_INSTALLED, UPDATE_FAILED, REBOOT_REQUIRED, REBOOT_CLEARED),
MaintenanceEventSource enum (UNSPECIFIED, EVENT_LOG, REGISTRY_POLL,
STARTUP_SNAPSHOT), messages UpdateItem, WindowsUpdateStatus, and
NodeMaintenanceEvent as specified in section 16 of this document.

Add NodeMaintenanceEvent as a new case in the existing agent-to-controller oneof.
Do not create a new service or a new stream.

In TestControllerGrpc.Core/Maintenance add: WindowsUpdateState enum, records
NodeUpdateStatus, UpdateItemDto, UpdatePolicy, FleetNotification,
NodeUpdateStatusChanged; and interfaces INodeUpdateStatusStore,
IUpdatePolicyEvaluator, IFleetNotificationService.

Add Draining to the existing MaintenanceState enum, positioned after None.
```

### Prompt 14 — Agent-side detector

```
In TestAgentGrpc create WindowsUpdateDetector : IHostedService.

Two detection paths feeding one merge point.

1. EventLogWatcher on channel
   "Microsoft-Windows-WindowsUpdateClient/Operational" with query:
   *[System[Provider[@Name='Microsoft-Windows-WindowsUpdateClient']
     and (EventID=19 or EventID=20 or EventID=43 or EventID=44)]]
   Map 44 -> UPDATE_PENDING, 43 -> UPDATE_INSTALLING, 19 -> UPDATE_INSTALLED,
   20 -> UPDATE_FAILED. Parse the KB id, title and result code from the event
   properties into UpdateItem. If the subscription throws (access denied, channel
   missing), log a warning once and continue with registry polling only — never
   crash the agent.

2. A registry poller on a configurable interval (default 5 minutes) checking:
   - HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired (key exists)
   - HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending (key exists)
   - HKLM\SYSTEM\CurrentControlSet\Control\Session Manager -> PendingFileRenameOperations (non-empty)
   Any one true means reboot required. Emit REBOOT_REQUIRED on a false-to-true
   transition and REBOOT_CLEARED on true-to-false.

Expose the current level state as a WindowsUpdateStatus property so the agent can
send a STARTUP_SNAPSHOT on every connect, changed or not.

Use RegistryView.Registry64 explicitly. Never write to the registry.
```

### Prompt 15 — Agent-side reporting and coalescing

```
In TestAgentGrpc add WindowsUpdateReporter, sitting between WindowsUpdateDetector
and the existing gRPC stream client.

- Buffer incoming detections for a coalescing window (default 30 seconds),
  resetting the timer on each new event, with a hard cap of 5 minutes.
- On flush, emit one NodeMaintenanceEvent with the merged UpdateItem list and the
  highest-severity kind, ranked REBOOT_REQUIRED > UPDATE_FAILED >
  UPDATE_INSTALLED > UPDATE_INSTALLING > UPDATE_PENDING.
- REBOOT_CLEARED bypasses coalescing and sends immediately.
- Always populate the full WindowsUpdateStatus level state on every message, never
  a delta.
- On stream connect or reconnect, send a STARTUP_SNAPSHOT event before anything
  else.
- If the stream is down, hold only the most recent merged state (do not queue a
  backlog) and send it on reconnect.

Make the coalescing window, poll interval and enable flag configurable through the
agent's existing appsettings binding.
```

### Prompt 16 — Controller-side store and policy

```
In TestControllerGrpc.Core/Maintenance implement NodeUpdateStatusStore :
INodeUpdateStatusStore and UpdatePolicyEvaluator : IUpdatePolicyEvaluator.

NodeUpdateStatusStore:
- ConcurrentDictionary<string, NodeUpdateStatus>, in memory only.
- Apply(NodeMaintenanceEvent) is idempotent: deduplicate on
  (NodeId, Kind, DetectedUtc) and ignore an event whose DetectedUtc is older than
  the stored LastEventUtc from the same source.
- Always overwrite the level fields (RebootRequired, PendingCount, Items) from the
  incoming status.
- Set SuppressedUntilUtc when a MaintenanceOperation completes on the node,
  using UpdatePolicy.SuppressionWindow (default 60 minutes). While suppressed,
  record the event and raise a Suppressed-kind notification but do not change
  MaintenanceState.
- Raise Changed on every effective state transition.

UpdatePolicyEvaluator maps WindowsUpdateState to MaintenanceState using UpdatePolicy:
  UpdatePending    -> configured, default None
  UpdateInstalling -> configured, default Updating
  RebootRequired   -> configured, default Draining
  UpdateFailed     -> None
  Suppressed       -> None

Wire the store into the existing gRPC stream handler where agent messages are
dispatched. When the evaluator returns a different MaintenanceState than the node
currently holds, apply it through the same path the revert operation uses — do not
mutate AgentNode directly from the stream handler.
```

### Prompt 17 — Draining and the dispatch gate

```
Extend node selection with three-tier eligibility.

Add DispatchEligibility enum (Eligible, Draining, Blocked) and a single method
Evaluate(AgentNode) implementing the rules in section 14. Keep IsDispatchable as a
thin wrapper returning Evaluate(n) == Eligible, and replace every existing
eligibility check with one of these two — leave no second copy of the condition.

Draining semantics:
- A Draining node keeps its ActiveSessions and runs them to completion.
- It is never selected for new work.
- When its last session completes, transition it from Draining to the blocking
  state its update status implies (normally Updating for RebootRequired), and raise
  a notification that it is now ready to reboot.

Add MaxConcurrentReboots (default 1) to UpdatePolicy and enforce it in
FleetMaintenanceService: a reboot requested while that many are already running is
queued, not rejected. Add an optional daily maintenance window during which
auto-reboot may run; outside it, nodes drain and wait.

Extend FleetUnavailableResult so each listed node reports its DispatchEligibility
and, when Blocked or Draining, the update state responsible.
```

### Prompt 18 — WPF: banner, bell, badge, flyout (R15–R18, R23)

```
In TestControllerGrpc extend the Agents panel for update awareness.

R15: a banner stack directly below the fleet toolbar, one banner per severity, not
per node. Amber banner when any node is RebootRequired, listing the node names and
the sentence "no new work will be sent to them once their current run finishes".
Blue informational banner when nodes have pending updates, noting they still accept
work. Each banner has a Review action that switches to the Maintenance tab's
updates section. Banners hide when their condition clears.

R16: a notification bell in the Agents title bar with an unread badge, opening a
FleetNotificationsFlyout bound to IFleetNotificationService. Each entry shows node
name, description, relative time, detection source, and inline action buttons.
Include Mark all read. Failed updates appear here only — they never set a card badge.

R17: a shield badge on the agent card. Blue "N pending" when UpdatePending; amber
"Reboot required" when RebootRequired. The badge is a button that opens R18.
Cards in RebootRequired use the amber card style; cards in Draining use a distinct
grey-blue style with the state line "Draining — finishing {watchItemName}, step N of M".

R18: NodeUpdateFlyout showing the KB list from NodeUpdateStatus.Items with per-item
result and failure code, a footer of actions (Reboot now, Revert instead, Snooze 4h,
Acknowledge), and R23: a provenance line reading "Detected by {source} at {time},
confirmed by {source} at {time} · last agent report {relative}".

Bind everything to INodeUpdateStatusStore.Changed marshalled to the UI thread.
Match existing dark theme resources; add no new brushes beyond the shield styles.
```

### Prompt 19 — WPF: rollup, actions, toasts (R19–R22)

```
R19: add a Windows updates section to the Maintenance tab. A four-box rollup
(agents reporting, reboot required, updates pending, install failed) above a
DataGrid with columns Agent, Update state, Last install, Pending, Detected by,
Last report, Dispatch, Actions.

Render one row per registered agent including nodes with nothing to report — show
their last check-in time, because "no alert" and "agent has not reported in six
hours" must not look identical. Show relative last-report time in amber when older
than twice the poll interval.

Add a "Reboot all ready" button that queues a reboot operation for every node in
RebootRequired that is not Draining, respecting MaxConcurrentReboots.

R20: wire Reboot now and Revert instead to IFleetMaintenanceService. Reboot now
opens a confirmation; Revert instead opens the existing RevertMachineDialog with
that node pre-selected and Reason pre-filled with the update context. Disable
rather than hide actions that are unavailable, with a tooltip giving the reason
("Available when {watchItemName} finishes").

R22: raise a toast on new update notifications through the existing notification
service, carrying the same two primary actions. Reboot-required toasts are sticky;
pending-update toasts auto-dismiss after 8 seconds.
```

### Prompt 20 — Policy settings and API surface (R24)

```
R24: add an update policy settings view under the Maintenance tab with rows for:
behaviour when updates are pending / installing / reboot required (each a combo of
Notify only, Drain then block, Block new work); Reboot automatically (toggle,
default off); registry poll interval; suppress-alerts-after-revert window;
event coalescing window; max concurrent reboots; and an optional auto-reboot
maintenance window.

Persist UpdatePolicy in the controller's existing settings store and push the
agent-relevant values (poll interval, coalescing window, enable flag) down to
agents through the existing configuration channel so a change does not require an
agent restart.

In TestController.WebApi add to MaintenanceController:
  GET  /api/maintenance/updates                    → all NodeUpdateStatus
  GET  /api/maintenance/updates/{nodeId}
  POST /api/maintenance/updates/{nodeId}/snooze
  POST /api/maintenance/updates/{nodeId}/acknowledge
  POST /api/maintenance/reboot                     → reboot one or more nodes
  GET/PUT /api/maintenance/policy

Add SignalR events updateStatusChanged and fleetNotificationRaised. Register the
SignalR IFleetNotificationService implementation in the WebApi host only; the WPF
host keeps its dispatcher-based one. Same interface, different concrete type per
host.
```

---

## 21. Build order for Part 2

Prompts 13–17 give detection and gating with no UI. Verify on a single agent before
building any panel work:

1. Deploy the detector to `jvkbak` only. Watch the controller log for a startup
   snapshot on connect.
2. Force a state change without waiting for Patch Tuesday — install any pending
   update, or create the `RebootRequired` key manually on the test VM to exercise
   the registry path. Confirm one coalesced event arrives, not five.
3. Confirm the node moves to `Draining`, that a running WatchItem completes, and
   that no new work is dispatched to it in the meantime.
4. Revert the node and confirm the suppression window holds the resulting pending
   alerts for 60 minutes.

Only then start prompt 18. Same phased discipline as everywhere else: the engine
proves itself against the real fleet before the surface exists to hammer it.

---

## 22. Test checklist additions

| Scenario | Expected |
|---|---|
| Agent connects with a clean machine | Startup snapshot received; state `UpToDate`; no notification |
| Agent connects with a pending reboot | Startup snapshot sets `RebootRequired`; node goes `Draining`; banner appears |
| Cumulative + 3 definition updates install together | One coalesced notification, not four; kind is the highest severity |
| Update installs while the node is running a WatchItem | Node goes `Draining`; run completes; node then `Blocked`; notification says ready to reboot |
| New dispatch while a node is `Draining` | Node not selected; if it was the only candidate, `FleetUnavailableResult` names it with reason |
| Node reverted, then reports pending updates 5 min later | Suppressed; feed shows a suppression entry; `MaintenanceState` unchanged |
| Suppression window expires with reboot still pending | State re-evaluated; notification raised at that point |
| Update install fails (0x80073712) | Feed entry with the code; no card badge; node stays `Eligible` |
| Agent restarts mid-install | Registry poll re-establishes the level state; no duplicate notification from the replayed snapshot |
| Event log channel access denied | Warning logged once; registry polling continues; agent does not crash |
| Reboot required clears after a Windows-initiated reboot | `REBOOT_CLEARED` sent immediately, not coalesced; node returns to `Eligible`; banner clears |
| All five agents reach `RebootRequired` at once | Reboots serialized to `MaxConcurrentReboots`; no wall of 409s |
| Same startup snapshot delivered twice on reconnect | Deduplicated; exactly one notification |
| Agent stops reporting for 30 min | Rollup row shows a stale last-report time in amber |
