---
name: diagnose-agent-offline
description: 'Read-only diagnosis of a TestAgentGrpc agent that dropped offline or lost its listener: ping vs port 5200, scheduled task, firewall rules, reboot initiators (event 1074), controller Fleet heartbeat and logs. Reports the cause with evidence before changing anything. Use for agent offline, unavailable, actively refused, did not recover, not in Fleet.'
argument-hint: 'Agent hostname (e.g. jvkpri)'
---

# Diagnose Agent Offline

**Read-only until the user approves a fix.** Do not restart the task or service, edit firewall rules, reboot, or revert the node.
**Collect node evidence BEFORE any revert**: a snapshot revert wipes the event log.

## Facts
- The agent runs as scheduled task **`TestAgentGrpc Interactive`** (not a Windows service) and listens on **TCP 5200**. The controller listens on 5100/5200.
- Firewall rules: `TestAgent gRPC Inbound (TCP 5200)` and `TestAgent to Controller Outbound (TCP 5100)`.
- Agent logs: `C:\TestAgentSolution\Logs\audit\audit_<date>.jsonl` (live). `C:\TestAgentService\Logs` is empty. `C:\TestControllerService\Logs\agent_crash.log`. Viewer: `.\deploy\Get-AgentLog.ps1 -ComputerName <node> -ErrorsOnly -Tail 50`.
- Agent nodes run on **Pacific Standard Time**; the controller is **IST (+12:30)**. Convert times before lining up events.
- WinRM (5985) may be closed on some nodes. `schtasks /S`, SMB `\\node\C$` and `Get-WinEvent -ComputerName` work without it.

## Procedure
1. **Network discriminator** (from the controller or a workstation):
   - `Test-Connection <node> -Count 2` and `Test-NetConnection <node> -Port 5200`.
   - Ping OK and 5200 **refused** (TCP RST) means the host is up and only the listener is gone; go to step 2.
   - Ping and 5200 both **time out** means the host is off, rebooting or unreachable; go to step 4.
   - Ping fails but 5200 works means ICMP is blocked, which is harmless.
2. **Task / process**:
   - `schtasks /Query /S <node> /TN "TestAgentGrpc Interactive" /V /FO LIST`: check Status, Last Run Time and Last Result.
   - `Get-Process -ComputerName <node> TestAgentGrpc` (or tasklist /S). Record the PID and start time.
3. **Firewall** (needs WinRM):
   - `Invoke-Command <node> { Get-NetFirewallRule -DisplayName 'TestAgent*' | Select DisplayName,Enabled,Action,Profile }`.
   - Check the active network profile too: a rule scoped to Domain does nothing if the NIC fell to Public.
4. **Reboots and initiators**:
   - `Get-WinEvent -ComputerName <node> -FilterHashtable @{LogName='System'; Id=1074,1076,6005,6006,6008; StartTime=<window>}`.
   - **Id 1074 names the process that requested the restart.** Known culprits: `ManageEngine\UEMS_Agent\bin\dcmsghandler.exe` (patch agent) and `TrustedInstaller.exe` ("Operating System: Upgrade"). 6008 means an unexpected shutdown.
   - Pending-reboot flags: CBS `RebootPending`, WU `Auto Update\RebootRequired`. PendingFileRenameOperations from AVEVA installers is a known false positive.
5. **Controller view**:
   - `GET http://JVGR22:5200/api/agents/fleet`: status, last heartbeat, locked, maintenance state. A node can be **Quarantined** but healthy; that is a different problem from offline.
   - Grep `\\JVGR22\C$\TestControllerService\Logs\app_<date>.log` for the node name with `UNAVAILABLE|actively refused|did not recover|Registered|Unregistered|ControllerLost`.
   - Also check the agent audit log for `AgentStopped`, `ControllerLost` and the last `HeartbeatAcked`.
6. **Runtime drift** (crash loops): look at `\\<node>\C$\Program Files\dotnet\shared\Microsoft.NETCore.App`. Nodes on an older .NET 10 patch have crashed before.

## Report (before proposing any change)
- **Cause**, with a confidence level and the evidence lines that show it (timestamps in both time zones).
- A timeline: last heartbeat, then the event(s), then the current state.
- What was ruled out, and how.
- A proposed fix, which needs approval (e.g. `schtasks /Run /S <node> /TN "TestAgentGrpc Interactive"`, return the node to rotation, exclude it from patch-agent reboot windows).
- If the evidence is gone (the node was already reverted), say so. Don't guess.
