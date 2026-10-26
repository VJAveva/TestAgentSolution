---
name: windows-update-report
description: 'Read-only report of pending Windows updates and reboot state across the agent fleet. Never installs, downloads or hides updates: ManageEngine owns patching. Use for pending updates, patch status, which nodes need updates, reboot required, Windows Update report.'
argument-hint: 'Optional: node list (default the 9-node roster)'
---

# Windows Update Report (read-only)

**ManageEngine (UEMS agent) owns patching.** This skill only reads.

## Never
- Never call anything that installs, downloads, hides or approves updates: no `Install-WindowsUpdate`, `UsoClient`, `wuauclt /updatenow`, `CreateUpdateInstaller`, `CreateUpdateDownloader`.
- Never click **Install updates** in the Fleet tab, and never call `INodeUpdateInstaller.InstallAsync`. It is parked behind `MaintenanceOptions.AllowUpdateInstall=false`; don't flip that flag.
- Never reboot a node or change WU policy or registry.

## Sources (use in this order)
1. **Fleet posture** (no node contact): `GET http://JVGR22:5200/api/maintenance/updates` and `/api/maintenance/updates/{nodeId}`. Per node:
   - `ScanStatus` (Ok, Stale, Failed, Unknown), `PendingCount` (null if the scan was not proven), last scan time, reboot required.
   - **Only `ScanStatus=Ok` means the count is real.** Unknown, Failed or Stale is "not proven clean", never "0 pending".
   - These scans use the **local WU cache** (`searcher.Online=false`), so they can lag behind the server.
2. **Online, read-only search** when fresher data is needed. Use a WUA COM **search only**, run on the node. There is no `Get-OnlineUpdates` command; don't look for or invent one:
   ```powershell
   Invoke-Command -ComputerName <node> -ScriptBlock {
     $s = (New-Object -ComObject Microsoft.Update.Session).CreateUpdateSearcher(); $s.Online = $true
     $r = $s.Search("IsInstalled=0 and IsHidden=0")
     $r.Updates | ForEach-Object { [pscustomobject]@{ Title=$_.Title; KB=($_.KBArticleIDs -join ','); Severity=$_.MsrcSeverity; Reboot=$_.InstallationBehavior.RebootBehavior } } }
   ```
   This needs WinRM (5985), which is closed on some nodes. Report "not reachable"; don't fall back to anything that writes.
3. **Reboot state** (read-only registry):
   - `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending`
   - `...\WindowsUpdate\Auto Update\RebootRequired`
   - **PendingFileRenameOperations alone is NOT a reboot signal here.** AVEVA installers set it, and it was a fleet-wide false positive.
4. **Recent patch reboots** (context): System log Id **1074** whose initiator is `ManageEngine\UEMS_Agent\bin\dcmsghandler.exe` or `TrustedInstaller.exe`. Node clocks are **PST**; the controller is IST (+12:30).

## Report
| Node | Scan status | Pending (count / KBs, severity) | Reboot required (authoritative keys) | Last scan | Last ManageEngine reboot |
|---|---|---|---|---|---|

Then:
- nodes whose posture is unproven, and why
- nodes that are unreachable
- a one-line summary for the ManageEngine owner

No recommendations to install. Recommendations are for the patch owner only.
