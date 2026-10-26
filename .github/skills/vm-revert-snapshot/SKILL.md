---
name: vm-revert-snapshot
description: 'Revert agent VMs on vCloud (rcloud) with the shared automation account, and take new baseline snapshots ONLY with the operator''s own vCloud credentials, with the VM powered off, after patching and verification. Use for revert VM, revert agent, restore snapshot, take snapshot, new golden image, baseline.'
argument-hint: 'revert | snapshot, and VM name(s)'
---

# VM Revert & Snapshot (vCloud)

Scripts (repo `Utilites\RevertAgents\`; deployed at `C:\TestSetup\RevertAgents\` on JVGR22):
- `Revert-RcloudMachines.ps1 -OrgName <org> -Machines a,b`: revert + power on + ping wait. Batch-capable over one PowerCLI session.
- `Vm-Ops.vcloud.ps1 -Verb State|PowerOn|PowerOff|List|Create|Revert|Delete -Vm a,b`: verb dispatcher. **Not deployed to JVGR22.** Run it from a machine that has PowerCLI.

Both read credentials from **`RCLOUD_USER` / `RCLOUD_PASSWORD`** (and `RCLOUD_ORG`, default `AppServerPool2`) environment variables. **Never pass credentials as arguments**: they show in the process table and in logs.

## vCloud facts
- **Exactly one snapshot per VM.** `Create` **replaces** it in one call. A new snapshot is the only rollback point from then on.
- `Revert` ignores snapshot names. The worker powers the VM on and waits for **ping only**; that is not proof the agent is up.
- A revert **wipes the event log**. Collect any diagnostics (skill `diagnose-agent-offline`) **before** reverting.

## Revert (automation account)
1. Check the node isn't in use: `GET http://JVGR22:5200/api/execution/sessions` / `/locks` and its Fleet card. A revert under a running pipeline is destructive; get an explicit OK.
2. The automation account's credentials live in the deployed pipeline config (`_VCloudUser` / `_VCloudPassword`). Run **on JVGR22** with the secret loaded into the remote session's environment, so the value never enters the transcript:
   ```powershell
   Invoke-Command -ComputerName JVGR22 -ScriptBlock {
     $cfg = Get-Content 'C:\TestControllerService\Parameters\<Release>\<file>.json' -Raw | ConvertFrom-Json
     $env:RCLOUD_USER = $cfg.global._VCloudUser; $env:RCLOUD_PASSWORD = $cfg.global._VCloudPassword
     & 'C:\TestSetup\RevertAgents\Revert-RcloudMachines.ps1' -OrgName AppServerPool2 -Machines <vm1>,<vm2>
     $LASTEXITCODE }
   ```
   Print only the exit code and the `[TAG]` summary lines. Never echo `$cfg`.
3. Exit 0 means every VM reverted and pings. Then confirm the **agent re-registered** in Fleet (`/api/agents/fleet`). Exit 1: report which VM failed, from the log in `C:\TestSetup\Logs\Revert-Rcloud_*.log`.

## Snapshot (operator's OWN credentials only)
**Preconditions (all must hold; refuse otherwise):**
- The node was patched (OS updates and/or agent build) **and verified**: agent registered and healthy, no pending reboot, smoke OK. A bad snapshot replaces the only good baseline.
- The user confirms they want to replace the baseline for exactly these VMs.
- **Do it on the canary `lpwin11test2` first** if it lives in the same vCloud org. The `Create` path has not been proven against live vCloud yet.

**Credentials:** the agent must never see or type them. Ask the user to run this **themselves** in their terminal, then continue in that same terminal session:
```powershell
$c = Get-Credential   # their own vCloud account
$env:RCLOUD_USER = $c.UserName; $env:RCLOUD_PASSWORD = $c.GetNetworkCredential().Password
```
Never use `setx` and never use the automation account for snapshots.

**Sequence** (stop on any failure):
1. `-Verb PowerOff -Vm <vm> -Graceful true`, then `-Verb State` must show **powered off**. Never snapshot a running VM.
2. `-Verb Create -Vm <vm> -Snapshot "<yyyyMMdd>-<reason>" -Description "<build/patch level, who>"`.
3. `-Verb List -Vm <vm>`: the new snapshot's created time is now.
4. `-Verb PowerOn -Vm <vm>`, then wait for the agent to re-register in Fleet.
5. Clear the credentials: `Remove-Item Env:RCLOUD_USER, Env:RCLOUD_PASSWORD`.

## Report
Per VM: action, exit code, before/after state, snapshot id and created time (snapshot), agent registered (Y/N), log path. Never include credential values.
