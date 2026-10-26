---
name: patch-agent-fleet
description: 'Deploy a new TestAgentGrpc build to the agent fleet with deploy/Invoke-FleetDeployment.ps1: canary on lpwin11test2 first, then the 9-node roster, verifying per node that the binary hash matches, the scheduled task runs, 5200 listens and the agent re-registers with the controller. Use for patch agents, deploy agent, fleet rollout, update TestAgentGrpc.'
argument-hint: 'Optional: subset of nodes (default all), skip canary only if user insists'
---

# Patch the Agent Fleet

Tool: `deploy\Invoke-FleetDeployment.ps1 -Component agents`. Per node it does: precheck, stop the `TestAgentGrpc Interactive` task, back up, robocopy `/MIR` excluding preserveFiles/preserveFolders, patch config from the inventory, start the task, verify. It doesn't need WinRM; it uses SMB + `schtasks /S`.

## Never
- Never deploy from a dirty tree, and never skip the canary unless the user explicitly says so.
- Never deploy while a pipeline is using the target nodes. Check `GET http://JVGR22:5200/api/execution/sessions` (activeCount) and `/api/execution/locks`.
- Never copy or overwrite **preserveFiles** (`*.bat`, `*.cmd`, `appsettings.json`, `commandpolicy.json`, `*.bak-*`, ...). Agent scripts such as `C:\TestAgentService\Run-WASTests.bat` need a **manual per-agent copy**. List them; don't push them.
- Rollout order: if the change alters what agents send (e.g. JSON in `ExecutionEvent.detail`), **the controller ships first** (skill `deploy-controller`). A new agent talking to an old controller can silently stop reporting.

## Procedure
1. **Preflight**: `git status --porcelain` is empty, `git log -1` for the report, and `(Get-PSDrive C).Free`.
2. **Build**:
   ```powershell
   dotnet publish TestAgentGrpc\TestAgentGrpc.csproj -c Release -o publish\agent
   ```
   Gate on exit code 0. Record the SHA256 of `publish\agent\TestAgentGrpc.dll`, `TestAgentGrpc.exe` and `TestControllerGrpc.Core.dll`.
3. **Canary, lpwin11test2.** It is deliberately **not** in `fleet-inventory.json`, so `-Only` cannot target it. Use a flat inventory file:
   ```powershell
   "lpwin11test2" | Set-Content "$env:TEMP\canary.txt" -Encoding ASCII
   .\deploy\Invoke-FleetDeployment.ps1 -Component agents -InventoryPath "$env:TEMP\canary.txt" -PatchTemplate deploy\fleet-inventory.json -DryRun
   ```
   Show the dry-run plan, get an OK, then re-run without `-DryRun`. Record the backup stamp.
4. **Verify the canary** (all must pass before going further):
   - SHA256 of the three files at `\\lpwin11test2\C$\TestAgentService\` equals the publish hashes.
   - `schtasks /Query /S lpwin11test2 /TN "TestAgentGrpc Interactive"` shows Running, and `Test-NetConnection lpwin11test2 -Port 5200` is True.
   - **It re-registers**: `GET http://JVGR22:5200/api/agents/fleet` lists lpwin11test2 as healthy/online with a fresh heartbeat. Allow about 60 s; a reachable port is not proof that the agent registered.
   - Optional smoke: a trivial `RunRemoteCommand` (e.g. `cmd /c ver`) completes.
   - If any check fails, **stop**: report and offer rollback for the canary only.
5. **Roster**: dry-run first, then wait for the user's OK.
   ```powershell
   .\deploy\Invoke-FleetDeployment.ps1 -Component agents -DryRun        # all 9, or -Only <nodes>
   .\deploy\Invoke-FleetDeployment.ps1 -Component agents [-Only <batch>]
   ```
   Batch by pool if the user wants (Sanity: jvgr1, jvgr2, jvhist, jvkpri, jvkbak; Warm: warmgr, warmpri, warmbak, warmhist), and only when that pool is idle.
6. **Verify every node** the same way as the canary: hash, task, port, **registration in Fleet**. Also note .NET runtime drift reported by the script's Pass 0c (some nodes run older 10.0.x patches).

## Report
| Node | Hash match | Task | 5200 | Registered | Notes |
|---|---|---|---|---|---|

Also report:
- the backup stamp(s), and the rollback command: `.\deploy\Invoke-FleetDeployment.ps1 -Rollback -BackupStamp <stamp> [-Only <node>]`
- the deployed commit
- manual follow-ups (per-agent `.bat` copies, runtime drift).
