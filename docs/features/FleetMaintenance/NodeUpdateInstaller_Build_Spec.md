# Build Spec: Windows Update Installer + Snapshot Wiring for Golden-Image Refresh

| Field | Value |
|---|---|
| **Component** | TestControllerGrpc.Core (contracts) + TestAgentGrpc (agent-side installer) + DI wiring |
| **Type** | New component — the missing piece that makes golden-image refresh buildable |
| **Priority** | P1 (unblocks the whole feature) |
| **Risk** | HIGH — this installs updates onto the base image every agent reverts to. Safety rails are mandatory, not optional. |

---

## Instructions for Copilot

Build the one missing component that makes golden-image refresh reachable: a real
`INodeUpdateInstaller` that searches for and installs Windows updates on an agent.
Then register the snapshot provider and refresh operation so the chain is wireable.

Read the existing code first — especially `WindowsUpdateDetector.cs` on the agent,
which already talks to the Windows Update API. The installer REUSES that same API
surface for installing rather than only detecting. This is an extension of proven
code, not greenfield.

Replace every `PLUG IN` with real names from the codebase. **Do not** enable the
auto-rebaseline step — that stays behind a flag until the installer is proven on a
throwaway agent (see Safety Rails).

---

## The situation this fixes

Golden-image refresh is fully written (12-phase state machine, passing tests) but
**unreachable** — because `INodeUpdateInstaller` (declared in
`GoldenImageContracts.cs:21`) has **zero implementations**. Its three methods are
the entire point of the feature: search and install Windows updates. Wiring
everything else without this produces a feature that fails at the install step.

So the real work is: (1) implement the installer, (2) register the provider +
operation, (3) leave the dangerous rebaseline gated.

---

## Part 1 — Implement INodeUpdateInstaller (the missing piece)

### 1.1 The contract (already declared — implement it)

```csharp
// ===== EXISTING in GoldenImageContracts.cs — do not redeclare, implement =====
public interface INodeUpdateInstaller
{
    Task<bool> IsInstallSupportedAsync(string agentName, CancellationToken ct);
    Task<UpdateSearchResult> SearchAsync(string agentName, CancellationToken ct);
    Task<UpdateInstallResult> InstallAsync(string agentName, IReadOnlyList<string> updateIds, CancellationToken ct);
}
// ===== PLUG IN: confirm the exact method signatures + result types from the real file =====
```

### 1.2 Where the install actually runs — AGENT-SIDE, elevated

The controller cannot install updates on a remote machine directly. The install
must run **on the agent**, which already runs as **LocalSystem** (per
`Setup-AgentNode.ps1`), so it has the rights. So the installer is a controller-side
class that DISPATCHES an install command to the agent over the existing gRPC path,
and the agent executes it against the Windows Update API locally.

Two pieces:

**A. Agent-side update executor** (new, in TestAgentGrpc):
- Reuses the WUApi COM interface that `WindowsUpdateDetector.cs` already uses —
  but with `searcher.Online = true` (the detector uses `Online = false` / cache
  only; install needs an online search first, so this is a new call path, not a
  reuse of the cached one).
- Search: query `IsInstalled=0 and IsHidden=0 and Type='Software'` online.
- Install: use `IUpdateInstaller` (the WUApi installer) to download + install the
  selected update IDs. Report progress + reboot-required back.
- Runs elevated (LocalSystem) — already the case.
- ===== PLUG IN: the agent's gRPC service + how it currently receives commands
  (RunCommandStreamed / the dispatcher path). Add an install verb or a dedicated
  RPC. =====

**B. Controller-side installer** (new, implements INodeUpdateInstaller):
```csharp
// ===== PLUG IN: real dispatcher + agent-name resolution =====
public sealed class NodeUpdateInstaller : INodeUpdateInstaller
{
    private readonly IAgentGrpcDispatcher _dispatcher;   // existing
    private readonly ILogger<NodeUpdateInstaller> _logger;

    public async Task<bool> IsInstallSupportedAsync(string agentName, CancellationToken ct)
    {
        // Probe the agent: is it install-capable? (LocalSystem agents = yes;
        // interactive-user agents from Setup-InteractiveAgent.ps1 may NOT be.)
        // Ask the agent over gRPC; return false if it can't, never assume.
    }

    public async Task<UpdateSearchResult> SearchAsync(string agentName, CancellationToken ct)
    {
        // Dispatch an online WU search to the agent; return the pending updates.
    }

    public async Task<UpdateInstallResult> InstallAsync(
        string agentName, IReadOnlyList<string> updateIds, CancellationToken ct)
    {
        // Dispatch the install to the agent; stream progress; capture reboot-required
        // and per-update success/failure. Time out generously (updates are slow).
    }
}
```

### 1.3 Per-node capability probe (mandatory)

Not every agent can install. `Setup-InteractiveAgent.ps1` provisions some agents
under an auto-logon USER account, which may lack the rights. So:
- `IsInstallSupportedAsync` must actually **ask the agent**, never assume
  fleet-wide capability.
- If an agent reports it can't install, the refresh operation skips it explicitly
  and reports "unsupported", never silently.

---

## Part 2 — Register what already exists (small)

These are written but not in the DI container. Add them to
`FleetMaintenanceExtensions.cs` (where seven siblings are already registered):

```csharp
// ===== PLUG IN: match the existing registration style in FleetMaintenanceExtensions.cs =====
services.AddSingleton<INodeUpdateInstaller, NodeUpdateInstaller>();           // Part 1
services.AddSingleton<IVirtualizationProvider, ScriptBackedVirtualizationProvider>(); // snapshots
services.AddSingleton<IGoldenImageRefreshOperation, GoldenImageRefreshOperation>();   // the operation
```

That makes the operation constructible. Do NOT yet add the UI/endpoint to trigger
it — see Part 4.

---

## Part 3 — Snapshots (the safe half you want now)

Snapshot create/revert already works via `ScriptBackedVirtualizationProvider` →
`Vm-Ops.vcloud.ps1`. The ONLY blocker is vCloud credentials on JVGR22, which are
environment variables, NOT code and NOT appsettings.

**This step is done by the user directly on JVGR22 — not by Copilot, not in code:**
```
setx RCLOUD_USER "<vcloud-username>" /M
setx RCLOUD_PASSWORD "<vcloud-password>" /M
REM RCLOUD_ORG optional (defaults to AppServerPool2)
REM Then restart the controller so it picks up the new machine environment block
REM (a WPF process caches env at start — same trap as IIS).
```
The `/M` (machine scope) matters because the controller may not run as your user.

Once set, snapshot revert works, and snapshot **create** (which golden-image
refresh needs) uses the same provider — already covered by registering
`IVirtualizationProvider` in Part 2.

> Copilot: do NOT put credentials anywhere in code or config. Assume they are set
> as machine environment variables and read by the existing adapter.

---

## Part 4 — Wire the trigger, but gate the dangerous step

Add the trigger path (so the feature is reachable) BUT keep the irreversible
rebaseline behind a flag until the installer is proven.

- `FleetMaintenanceService.StartRefreshAsync` — new method, mirrors
  StartRevertAsync/StartRebootAsync.
- `MaintenanceController` — new `POST /refresh`.
- `FleetVM.RefreshCommand` + a `GoldenImageRefreshDialog` (pattern:
  `RevertMachineDialog`).
- **The gate:** the refresh operation's final phase — replacing the baseline
  snapshot — must be controlled by a config flag, default OFF:
  ```
  "FleetMaintenance": { "GoldenImage": { "AllowBaselineReplace": false } }
  ```
  With the flag off, the operation runs revert → install → verify → **stops before
  replacing the baseline**, and reports what it WOULD have done. This lets you
  exercise the whole flow on a throwaway agent without ever touching a real
  baseline. Turn the flag on only after it's proven.

---

## Safety Rails (mandatory — this is the riskiest component in the system)

The installer writes Windows updates onto the base image every agent reverts to.
If it corrupts a baseline, every agent that reverts inherits the corruption. So:

1. **Test on ONE throwaway agent first.** Never point the first real run at an
   agent whose baseline matters. Use a spare node.
2. **Baseline replace stays flag-gated (Part 4)** until the install + verify steps
   are proven end-to-end on that throwaway.
3. **Verify before replace.** The operation's verify phase (health probe + optional
   smoke) must pass before the baseline is ever replaced. On single-snapshot
   platforms (vCloud) the old baseline is gone once replaced — there is no undo, so
   verification is non-negotiable.
4. **Capability-probe per node** (Part 1.3) — never assume an agent can install.
5. **Generous timeouts + reboot handling.** Windows updates are slow and often
   require a reboot; the install phase must wait for the reboot-required cycle to
   complete and the agent to re-register.
6. **Quarantine on failure.** A node that fails mid-refresh must be quarantined
   (the existing MaintenanceRecoveryService pattern), never returned to rotation in
   an unknown state.

---

## Acceptance criteria

| ID | Criterion |
|---|---|
| AC-1 | `INodeUpdateInstaller` has a real implementation; all three methods work against the WUApi |
| AC-2 | Install runs agent-side, elevated (LocalSystem), dispatched over the existing gRPC path |
| AC-3 | `IsInstallSupportedAsync` probes the actual agent; unsupported agents are skipped explicitly, never silently |
| AC-4 | Provider + operation + installer registered in FleetMaintenanceExtensions; the operation is constructible |
| AC-5 | Snapshot create/revert works once vCloud env vars are set (user-provided, not in code) |
| AC-6 | Refresh is triggerable (service method + endpoint + command + dialog) |
| AC-7 | Baseline replacement is flag-gated, default OFF; with it off, the flow runs but stops before replacing the baseline |
| AC-8 | Verify phase must pass before any baseline replace |
| AC-9 | A node that fails mid-refresh is quarantined, not silently returned to rotation |
| AC-10 | No credentials in code or appsettings; read from machine env vars |
| AC-11 | Existing reboot + revert features unchanged; all existing tests pass; new tests cover the installer (against a mock agent) and the flag-gated stop |

---

## Build order

1. **Part 3 (snapshots)** — user sets vCloud env vars on JVGR22 directly, restarts
   controller, verifies revert on a spare node. (No Copilot code — this just proves
   the snapshot half works.)
2. **Part 1 (installer)** — the real build. Agent-side update executor + controller
   -side INodeUpdateInstaller, reusing the WUApi path the detector already uses.
   Test against a mock agent.
3. **Part 2 (registrations)** — plug provider + operation + installer into DI.
4. **Part 4 (trigger, gated)** — add the trigger path with baseline-replace OFF.
5. **Prove on a throwaway agent** — run the full flow with the flag off, watch it
   revert → install → verify → stop-before-replace. Only after it works there,
   consider enabling the flag for a real baseline.

Start with Part 1: show me the existing `WindowsUpdateDetector.cs` and the agent's
gRPC command path, then implement the agent-side update executor that reuses that
WUApi surface for online search + install.

---

## Why this order and these gates

- **Snapshots first** because they're safe and already built — you get half your
  goal (create/revert snapshots) working with two env vars.
- **The installer is the real work** — it's the one unbuilt piece, and it's the
  riskiest. Build it carefully, test against a mock, prove on a throwaway.
- **The baseline-replace gate** is the difference between "a feature you can test
  safely" and "a feature that can corrupt every agent's baseline on its first run".
  It stays off until you've watched the flow work. That gate is the single most
  important line in this spec.
