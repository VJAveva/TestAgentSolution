# Patch-AgentFleet — Setup Guide

This guide gets you from zero to running `Patch-AgentFleet.ps1` against
your real agent fleet. **Estimated setup time: 15 minutes.**

---

## What This Replaces

```
Your old workflow (per agent):
  1. RDP into agent → 2 minutes
  2. Stop service → 30 seconds
  3. Copy file → 1 minute
  4. Start service → 30 seconds  
  5. Verify tray → 1 minute
  6. Reboot → 2 minutes
  7. Verify tray again → 1 minute
  8. Power off via vCloud → 1 minute
  9. Delete snapshot → 30 seconds
 10. Create snapshot → 1 minute
 Total: ~10 minutes per agent

  × 10 agents = 100 minutes
  × 100 agents = 16+ hours

New workflow (one command):
  .\Patch-AgentFleet.ps1 -BinaryPath .\TestAgentGrpc.exe -Reboot -Snapshot

  × 10 agents = ~5 minutes
  × 100 agents = ~15 minutes
```

---

## Prerequisites Checklist

### On the Controller machine (jvgr22)

#### 1. PowerShell 7+

The script uses `ForEach-Object -Parallel` which is only in PS 7.

Check your version:
```powershell
$PSVersionTable.PSVersion
```

If you see 5.x, install PS 7:
```powershell
winget install Microsoft.PowerShell
# OR download MSI from:
# https://github.com/PowerShell/PowerShell/releases/latest
```

After install, **launch `pwsh.exe`** (not `powershell.exe`) to run the script.

#### 2. VMware.PowerCLI (only if you'll use -Snapshot)

```powershell
Install-Module -Name VMware.PowerCLI -Scope CurrentUser -Force
Set-PowerCLIConfiguration -InvalidCertificateAction Ignore -Confirm:$false
```

Test connection to your vCloud:
```powershell
Connect-CIServer -Server vcloud.dev.wonderware.com -Org AppServerPool2
# Enter credentials when prompted
Get-CIVM -Name jvgr1   # should return your VM
Disconnect-CIServer -Confirm:$false
```

#### 3. Verify network access to all agents

```powershell
$agents = @("jvgr1", "jvkpri", "jvkbak", "jvhist")
$agents | ForEach-Object {
    $ping = Test-Connection $_ -Count 1 -Quiet
    $smb = Test-NetConnection $_ -Port 445 -InformationLevel Quiet `
        -WarningAction SilentlyContinue
    $winrm = Test-NetConnection $_ -Port 5985 -InformationLevel Quiet `
        -WarningAction SilentlyContinue
    $grpc = Test-NetConnection $_ -Port 5200 -InformationLevel Quiet `
        -WarningAction SilentlyContinue
    [PSCustomObject]@{
        Agent = $_
        Ping = $ping
        SMB445 = $smb
        WinRM5985 = $winrm
        GRPC5200 = $grpc
    }
} | Format-Table -AutoSize
```

All four columns should show `True`. If any are `False`, see the
troubleshooting section below.

---

### On each Agent machine (jvgr1, jvkpri, ...)

This is a **one-time setup per agent**. After this, you never touch
the agents directly again — everything goes through the script.

#### 1. Enable WinRM (PowerShell Remoting)

On each agent, **as Administrator**:
```powershell
# Enable WinRM listener and service
Enable-PSRemoting -Force -SkipNetworkProfileCheck

# Increase memory limit (for large file copies through WinRM)
Set-Item WSMan:\localhost\Shell\MaxMemoryPerShellMB 2048
Set-Item WSMan:\localhost\Plugin\Microsoft.PowerShell\Quotas\MaxMemoryPerShellMB 2048

# Restart WinRM
Restart-Service WinRM

# Verify
Get-Service WinRM         # should be Running
Test-WSMan -ComputerName localhost
```

#### 2. Firewall — allow WinRM and gRPC

```powershell
# WinRM (usually already exists)
Enable-NetFirewallRule -DisplayGroup "Windows Remote Management"

# Your gRPC port 5200
New-NetFirewallRule -Name "TestAgentGrpc" `
    -DisplayName "TestAgent gRPC port 5200" `
    -Direction Inbound `
    -Protocol TCP `
    -LocalPort 5200 `
    -Action Allow `
    -Profile Any
```

#### 3. (Recommended) Trust the controller for WinRM

If the controller and agents are NOT in the same domain, you need to
add the controller to the agent's WinRM TrustedHosts list:

**On each agent**, as Administrator:
```powershell
# Replace with your controller hostname
Set-Item WSMan:\localhost\Client\TrustedHosts -Value "jvgr22" -Force
```

If in the same domain, this step is not needed.

#### 4. Verify TestAgentService is installed

```powershell
Get-Service -Name "TestAgentService"
# Should show Status: Running, StartType: Automatic
```

The service name in the script is `TestAgentService`. If yours has a
different name, pass it via `-ServiceName "YourName"`.

---

## Quick Validation Run

Once prerequisites are set up, do a **dry run** on a single agent to
make sure everything works:

```powershell
# Dry run on one agent — does NOT change anything
.\Patch-AgentFleet.ps1 `
    -BinaryPath C:\Deployment\TestAgentGrpc\TestAgentGrpc.exe `
    -Agents @("jvgr1") `
    -DryRun
```

If that succeeds, do a real patch on one agent (no reboot/snapshot):

```powershell
.\Patch-AgentFleet.ps1 `
    -BinaryPath C:\Deployment\TestAgentGrpc\TestAgentGrpc.exe `
    -Agents @("jvgr1")
```

Verify jvgr1 came back online, then scale up to your full fleet.

---

## Real-World Usage Patterns

### Pattern 1: Quick patch (no reboot, no snapshot)

For minor bug fixes when you trust the binary works:

```powershell
.\Patch-AgentFleet.ps1 `
    -BinaryPath C:\Deployment\TestAgentGrpc\TestAgentGrpc.exe
```

### Pattern 2: Full patch + reboot + snapshot

For releases where you want a clean snapshot post-deployment:

```powershell
.\Patch-AgentFleet.ps1 `
    -BinaryPath C:\Deployment\TestAgentGrpc\TestAgentGrpc.exe `
    -Reboot `
    -Snapshot `
    -MaxConcurrent 10
```

### Pattern 3: Use the fleet list from your registry

```powershell
# Pull agent list from your Controller's API
$fleet = (Invoke-RestMethod `
    "http://rcloud.dev.wonderware.com/api/agents/fleet").agents.name

.\Patch-AgentFleet.ps1 `
    -BinaryPath C:\Deployment\TestAgentGrpc\TestAgentGrpc.exe `
    -Agents $fleet `
    -Reboot `
    -Snapshot `
    -MaxConcurrent 10
```

### Pattern 4: From a file

Create `C:\fleet.txt`:
```
jvgr1
jvkpri
jvkbak
jvhist
warmpsr
warmpri
warmbak
warmhist
```

Then:
```powershell
.\Patch-AgentFleet.ps1 `
    -BinaryPath C:\Deployment\TestAgentGrpc\TestAgentGrpc.exe `
    -Agents (Get-Content C:\fleet.txt) `
    -Reboot `
    -Snapshot
```

---

## Troubleshooting

### "Access is denied" or "Cannot connect"

**Cause:** WinRM not enabled, or credentials wrong.

```powershell
# On the controller, test connection:
Test-WSMan -ComputerName jvgr1 -Credential (Get-Credential)

# If that fails, on the AGENT machine, check:
winrm quickconfig
Get-Service WinRM
```

### "The WinRM client cannot process the request"

**Cause:** Not in the same domain AND TrustedHosts not configured.

```powershell
# On each agent (replace jvgr22 with your controller):
Set-Item WSMan:\localhost\Client\TrustedHosts -Value "jvgr22" -Force
```

### "Cannot copy file ... access denied"

**Cause:** Admin share `\\agent\C$` not accessible.

Check that:
1. Your user is an Administrator on the agent machine
2. File and Printer Sharing is enabled on the agent
3. UAC remote token filtering isn't blocking (in domain environments
   this is usually fine; in workgroups you may need to set this
   registry key on each agent):

```powershell
# On each agent (workgroup only):
Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" `
    -Name "LocalAccountTokenFilterPolicy" -Value 1 -Type DWord
Restart-Service LanmanServer
```

### "Service did not start within 30s"

**Cause:** New binary has a bug, or .NET runtime missing.

```powershell
# Connect to the failing agent
Enter-PSSession -ComputerName jvgr1 -Credential (Get-Credential)

# Check the service status and event log
Get-Service TestAgentService
Get-EventLog -LogName Application -Newest 20 -Source "TestAgent*"

# If the service crashed on startup, restore from backup:
Stop-Service TestAgentService -Force
Copy-Item "C:\TestAgentService\backups\TestAgentGrpc-YYYYMMDD.exe" `
    "C:\TestAgentService\TestAgentGrpc.exe" -Force
Start-Service TestAgentService
```

### "Port 5200 did not come up within 60 seconds"

**Cause:** Service started but the gRPC listener is failing.

```powershell
# On the agent, check what's listening:
Get-NetTCPConnection -LocalPort 5200 -ErrorAction SilentlyContinue

# Check the agent's own log
Get-Content "C:\TestAgentService\Logs\agent-*-$(Get-Date -Format yyyyMMdd).log" `
    -Tail 50

# Check firewall
Get-NetFirewallRule -Name TestAgentGrpc | Format-List Name, Enabled
```

### "Did not power off within 5 min" (snapshot phase)

**Cause:** VMware Tools not installed in guest, so graceful shutdown
times out.

The script falls back to hard power off automatically. If you want
graceful shutdown, install VMware Tools on the agent guest OS.

### Parallel execution seems slow

If 5 parallel agents take longer than 1 agent × 1.5, your bottleneck
is probably:
- **Network bandwidth** to all agents from the controller (file copy)
- **WinRM throttle** (default max 25 concurrent shells per source)

```powershell
# On the controller, raise WinRM throttle:
Set-Item WSMan:\localhost\MaxShellsPerUser 50
Set-Item WSMan:\localhost\MaxConcurrentUsers 50
Restart-Service WinRM
```

---

## What the Script Does (Detailed)

For each agent, in parallel:

```
1. Test-Connection — verify ping
2. Read current binary version (best effort)
3. Stop-Service TestAgentService + kill orphan processes
4. Verify file isn't locked (5 retries with 2s delay)
5. Backup existing binary to C:\TestAgentService\backups\
   (keeps last 5 backups, auto-rotates)
6. Copy new binary via SMB
7. Verify SHA256 hash matches source
8. Start-Service TestAgentService
9. Test-NetConnection on port 5200 (up to 60s)
10. Read new binary version
11. Report success or failure

If -Reboot:
12. shutdown /r /t 5
13. Wait until pingable AGAIN (up to 5 min)
14. Verify port 5200 listening again

If -Snapshot (after reboot phase):
15. Stop-CIVMGuest (graceful) or Stop-CIVM (hard fallback)
16. Wait until PoweredOff (up to 5 min)
17. Remove existing snapshot
18. Create new snapshot named "post-patch-<timestamp>"
19. Start-CIVM
```

Logs every step to a per-run file at `C:\Patches\logs\patch-<timestamp>.log`.

---

## Safety Features

- **Dry run mode** (`-DryRun`) shows what would happen without
  changing anything
- **Hash verification** after every copy — script fails if hash
  doesn't match source
- **Backup before replace** — last 5 versions kept on each agent
- **Service stop timeout** — if service won't stop within 30s, script
  fails for that agent (others continue)
- **Port verification before declaring success** — script doesn't
  say "Success" unless gRPC port is actually listening
- **Per-agent failures don't stop the fleet** — one bad agent doesn't
  block the others
- **Reboot-failed agents are excluded from snapshot phase** — prevents
  snapshotting a broken machine

---

## Next Steps After This Works

Once you have this running smoothly, consider:

1. **Schedule it as a CI step** — when your build completes, automatically
   patch a "staging" subset of agents
2. **Wire to your Controller UI** — add a "Patch All Agents" button in
   the WPF Agent Workspace that calls this script via `Process.Start`
3. **Self-update RPC** — eventually, build the in-process update feature
   so you don't need SMB shares at all (the controller serves the binary
   via HTTP, agents pull it themselves)

Files saved to `/mnt/user-data/outputs/`:
- `Patch-AgentFleet.ps1` — the script
- `AGENT_FLEET_PATCH_SETUP.md` — this guide
