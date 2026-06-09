<#
.SYNOPSIS
Patches the TestAgentGrpc.exe binary on multiple agent nodes
in parallel and verifies each agent comes back online.

.DESCRIPTION
Replaces your manual workflow:
  1. Stop TestAgentService on each agent
  2. Kill any orphan TestAgentGrpc.exe processes
  3. Copy new binary via admin share
  4. Start TestAgentService
  5. Wait for gRPC port 5200 to listen
  6. (Optional) reboot and re-verify
  7. (Optional) take vCloud snapshot
  
Designed for 10+ agents running in parallel. Reduces an
80-minute manual chore to under 5 minutes.

.PARAMETER BinaryPath
Full UNC or local path to the new TestAgentGrpc.exe.
Must be accessible from the Controller machine.

.PARAMETER Agents
Array of agent hostnames. Defaults to your standard 4.
For larger fleets, pass via -Agents (Get-Content fleet.txt).

.PARAMETER Reboot
Switch. If specified, reboots each agent after patching
and waits for it to come back online.

.PARAMETER Snapshot
Switch. If specified, requires vmware PowerCLI module,
powers off each agent, removes old snapshot, creates new
one, powers back on.

.PARAMETER MaxConcurrent
Maximum number of agents to patch simultaneously.
Default 5. Increase to 10 for faster fleet patching.

.PARAMETER LogPath
Folder for per-run log file. Default: C:\Patches\logs\

.EXAMPLE
.\Patch-AgentFleet.ps1 -BinaryPath \\jvgr22\share\TestAgentGrpc.exe

Patches the default 4 agents with no reboot or snapshot.

.EXAMPLE
.\Patch-AgentFleet.ps1 `
    -BinaryPath C:\Deployment\TestAgentGrpc\TestAgentGrpc.exe `
    -Agents (Get-Content C:\fleet.txt) `
    -Reboot `
    -Snapshot `
    -MaxConcurrent 10

Patches every agent listed in fleet.txt, reboots them, takes
snapshots, with 10 running in parallel.

.NOTES
Prerequisites on the Controller machine:
  - PowerShell 7.0+ (ForEach-Object -Parallel)
  - Admin credentials on the agent machines
  - WinRM enabled on agents (default in domain envs)
  - SMB admin share access (\\agent\C$)
  - For -Snapshot: VMware.PowerCLI module installed

Prerequisites on each agent:
  - TestAgentService Windows service installed
  - Binary at C:\TestAgentService\TestAgentGrpc.exe
  - WinRM listener on port 5985 (or 5986 for HTTPS)
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BinaryPath,
    
    [string[]]$Agents = @("jvgr1", "jvkpri", "jvkbak", "jvhist"),
    
    [switch]$Reboot,
    [switch]$Snapshot,
    
    [int]$MaxConcurrent = 5,
    
    [string]$LogPath = "C:\Patches\logs",
    
    [string]$ServiceName = "TestAgentService",
    
    [int]$GrpcPort = 5200,
    
    [int]$PortCheckTimeoutSeconds = 60,
    
    [int]$PortSustainSeconds = 30,
    
    [int]$RebootTimeoutSeconds = 300,
    
    [string]$vCloudServer = "vcloud.dev.wonderware.com",
    [string]$vCloudOrg = "AppServerPool2",
    
    [pscredential]$AgentCredential,
    
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

# ═══════════════════════════════════════════════════════
# SETUP: Logging, validation, prerequisites
# ═══════════════════════════════════════════════════════

if (-not (Test-Path $LogPath)) {
    New-Item -ItemType Directory -Path $LogPath -Force | Out-Null
}
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$logFile = Join-Path $LogPath "patch-$timestamp.log"

function Write-Log {
    param([string]$Message, [string]$Color = "White")
    $line = "[$(Get-Date -Format 'HH:mm:ss')] $Message"
    Write-Host $line -ForegroundColor $Color
    Add-Content -Path $logFile -Value $line
}

Write-Log "═══════════════════════════════════════════════════" "Cyan"
Write-Log "Agent Fleet Patch — $timestamp" "Cyan"
Write-Log "═══════════════════════════════════════════════════" "Cyan"
Write-Log "Binary:    $BinaryPath"
Write-Log "Agents:    $($Agents.Count) total"
Write-Log "Parallel:  $MaxConcurrent at a time"
Write-Log "Reboot:    $($Reboot.IsPresent)"
Write-Log "Snapshot:  $($Snapshot.IsPresent)"
Write-Log "Dry run:   $($DryRun.IsPresent)"
Write-Log "Log file:  $logFile"
Write-Log ""

# Validate PowerShell version
if ($PSVersionTable.PSVersion.Major -lt 7) {
    Write-Log "ERROR: PowerShell 7+ required (you have $($PSVersionTable.PSVersion))" "Red"
    Write-Log "Install: winget install Microsoft.PowerShell" "Yellow"
    exit 1
}

# Validate binary
if (-not (Test-Path $BinaryPath)) {
    Write-Log "ERROR: Binary not found: $BinaryPath" "Red"
    exit 1
}

$binaryInfo = Get-Item $BinaryPath
$sourceHash = (Get-FileHash $BinaryPath -Algorithm SHA256).Hash
Write-Log "Binary size: $([math]::Round($binaryInfo.Length / 1MB, 2)) MB"
Write-Log "Binary SHA256: $sourceHash"
Write-Log "Binary modified: $($binaryInfo.LastWriteTime)"
Write-Log ""

# Get credentials if not provided
if (-not $AgentCredential -and -not $DryRun) {
    Write-Log "Enter admin credentials for the agent machines:" "Yellow"
    $AgentCredential = Get-Credential -Message "Agent admin credentials"
}

# Validate vCloud module if -Snapshot
if ($Snapshot) {
    if (-not (Get-Module -ListAvailable VMware.PowerCLI)) {
        Write-Log "ERROR: VMware.PowerCLI module required for -Snapshot" "Red"
        Write-Log "Install: Install-Module VMware.PowerCLI -Scope CurrentUser" "Yellow"
        exit 1
    }
    Import-Module VMware.PowerCLI -ErrorAction SilentlyContinue | Out-Null
    Set-PowerCLIConfiguration -InvalidCertificateAction Ignore `
        -Confirm:$false | Out-Null
}

# ═══════════════════════════════════════════════════════
# PHASE 1: Patch agents in parallel
# ═══════════════════════════════════════════════════════

Write-Log "──── Phase 1: Patching binaries ────" "Cyan"
$phase1Start = Get-Date

$patchResults = $Agents | ForEach-Object -Parallel {
    $agent = $_
    $binPath = $using:BinaryPath
    $expectedHash = $using:sourceHash
    $svcName = $using:ServiceName
    $port = $using:GrpcPort
    $portTimeout = $using:PortCheckTimeoutSeconds
    $portSustain = $using:PortSustainSeconds
    $cred = $using:AgentCredential
    $dryRun = $using:DryRun
    
    function Log-Agent {
        param([string]$Message, [string]$Color = "Gray")
        $line = "[$(Get-Date -Format 'HH:mm:ss')] [$agent] $Message"
        Write-Host $line -ForegroundColor $Color
    }
    
    $result = [PSCustomObject]@{
        Agent = $agent
        Phase = "Patch"
        Status = "Pending"
        OldVersion = $null
        NewVersion = $null
        Duration = $null
        Error = $null
        Steps = @()
    }
    $start = Get-Date
    
    try {
        if ($dryRun) {
            Log-Agent "DRY RUN — would patch $agent" "Yellow"
            $result.Status = "DryRun"
            return $result
        }
        
        # STEP 1: Ping check before doing anything destructive
        Log-Agent "Checking reachability..."
        if (-not (Test-Connection -ComputerName $agent -Count 1 -Quiet)) {
            throw "Agent not reachable via ping"
        }
        $result.Steps += "Ping OK"
        
        # STEP 2: Get current version (best effort)
        try {
            $oldVer = Invoke-Command -ComputerName $agent `
                -Credential $cred -ScriptBlock {
                if (Test-Path "C:\TestAgentService\TestAgentGrpc.exe") {
                    (Get-Item "C:\TestAgentService\TestAgentGrpc.exe").VersionInfo.ProductVersion
                } else { "not-installed" }
            } -ErrorAction Stop
            $result.OldVersion = $oldVer
            Log-Agent "Current version: $oldVer"
        }
        catch {
            Log-Agent "Could not read current version (continuing)" "Yellow"
        }
        
        # STEP 3: Stop service and kill orphan processes
        Log-Agent "Stopping service and processes..."
        Invoke-Command -ComputerName $agent -Credential $cred -ScriptBlock {
            param($svc)
            # Stop service
            $service = Get-Service -Name $svc -ErrorAction SilentlyContinue
            if ($service -and $service.Status -ne 'Stopped') {
                Stop-Service -Name $svc -Force -ErrorAction Stop
                # Wait until truly stopped
                $deadline = (Get-Date).AddSeconds(30)
                while ((Get-Service -Name $svc).Status -ne 'Stopped') {
                    if ((Get-Date) -gt $deadline) {
                        throw "Service did not stop within 30s"
                    }
                    Start-Sleep -Milliseconds 500
                }
            }
            # Kill orphan tray processes
            Get-Process TestAgentGrpc -ErrorAction SilentlyContinue |
                Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 2
        } -ArgumentList $svcName -ErrorAction Stop
        $result.Steps += "Service stopped"
        
        # STEP 4: Verify file is not locked (try opening exclusively)
        $remotePath = "\\$agent\C$\TestAgentService\TestAgentGrpc.exe"
        for ($i = 0; $i -lt 5; $i++) {
            try {
                $fs = [System.IO.File]::Open(
                    $remotePath,
                    [System.IO.FileMode]::Open,
                    [System.IO.FileAccess]::ReadWrite,
                    [System.IO.FileShare]::None)
                $fs.Close()
                break
            }
            catch {
                if ($i -eq 4) {
                    throw "File still locked after 5 retries: $_"
                }
                Start-Sleep -Seconds 2
            }
        }
        $result.Steps += "File unlocked"
        
        # STEP 5: Backup existing binary
        Log-Agent "Backing up existing binary..."
        Invoke-Command -ComputerName $agent -Credential $cred -ScriptBlock {
            $bin = "C:\TestAgentService\TestAgentGrpc.exe"
            if (Test-Path $bin) {
                $backupDir = "C:\TestAgentService\backups"
                if (-not (Test-Path $backupDir)) {
                    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
                }
                $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
                Copy-Item $bin "$backupDir\TestAgentGrpc-$stamp.exe" -Force
                # Keep only last 5 backups
                Get-ChildItem "$backupDir\TestAgentGrpc-*.exe" |
                    Sort-Object LastWriteTime -Descending |
                    Select-Object -Skip 5 |
                    Remove-Item -Force -ErrorAction SilentlyContinue
            }
        } -ErrorAction Stop
        $result.Steps += "Backup created"
        
        # STEP 6: Copy new binary
        Log-Agent "Copying new binary..."
        Copy-Item -Path $binPath -Destination $remotePath -Force -ErrorAction Stop
        $result.Steps += "Binary copied"
        
        # STEP 7: Verify hash matches
        Log-Agent "Verifying hash..."
        $remoteHash = Invoke-Command -ComputerName $agent -Credential $cred -ScriptBlock {
            (Get-FileHash "C:\TestAgentService\TestAgentGrpc.exe" -Algorithm SHA256).Hash
        } -ErrorAction Stop
        if ($remoteHash -ne $expectedHash) {
            throw "Hash mismatch after copy. Expected $expectedHash, got $remoteHash"
        }
        $result.Steps += "Hash verified"
        
        # STEP 8: Start service
        Log-Agent "Starting service..."
        Invoke-Command -ComputerName $agent -Credential $cred -ScriptBlock {
            param($svc)
            Start-Service -Name $svc -ErrorAction Stop
            # Wait for Running state
            $deadline = (Get-Date).AddSeconds(30)
            while ((Get-Service -Name $svc).Status -ne 'Running') {
                if ((Get-Date) -gt $deadline) {
                    throw "Service did not start within 30s"
                }
                Start-Sleep -Milliseconds 500
            }
        } -ArgumentList $svcName -ErrorAction Stop
        $result.Steps += "Service started"
        
        # STEP 9: Wait for gRPC port to listen
        Log-Agent "Waiting for port $port to listen..."
        $portUp = $false
        $deadline = (Get-Date).AddSeconds($portTimeout)
        while ((Get-Date) -lt $deadline) {
            $test = Test-NetConnection -ComputerName $agent `
                -Port $port -InformationLevel Quiet -WarningAction SilentlyContinue
            if ($test) {
                $portUp = $true
                break
            }
            Start-Sleep -Seconds 3
        }
        if (-not $portUp) {
            throw "Port $port did not come up within $portTimeout seconds"
        }
        $result.Steps += "Port $port OK"
        
        # STEP 9b: Verify port STAYS up (catches crash-on-startup)
        if ($portSustain -gt 0) {
            Log-Agent "Verifying port $port sustained for ${portSustain}s..."
            $sustainStart = Get-Date
            while (((Get-Date) - $sustainStart).TotalSeconds -lt $portSustain) {
                Start-Sleep -Seconds 5
                $check = Test-NetConnection -ComputerName $agent `
                    -Port $port -InformationLevel Quiet -WarningAction SilentlyContinue
                if (-not $check) {
                    throw "Port $port came up but failed to remain listening (crashed within ${portSustain}s of start — check Event Log on $agent for port conflict or startup error)"
                }
            }
            $result.Steps += "Port sustained ${portSustain}s"
        }
        
        # STEP 10: Read new version
        try {
            $newVer = Invoke-Command -ComputerName $agent -Credential $cred -ScriptBlock {
                (Get-Item "C:\TestAgentService\TestAgentGrpc.exe").VersionInfo.ProductVersion
            } -ErrorAction Stop
            $result.NewVersion = $newVer
            Log-Agent "New version: $newVer" "Green"
        }
        catch { }
        
        $result.Status = "Success"
        Log-Agent "PATCH SUCCESS" "Green"
    }
    catch {
        $result.Status = "Failed"
        $result.Error = $_.Exception.Message
        Log-Agent "FAILED: $($_.Exception.Message)" "Red"
    }
    finally {
        $result.Duration = [math]::Round(((Get-Date) - $start).TotalSeconds, 1)
    }
    
    $result
} -ThrottleLimit $MaxConcurrent

$phase1Duration = [math]::Round(((Get-Date) - $phase1Start).TotalSeconds, 0)
Write-Log ""
Write-Log "Phase 1 complete in $phase1Duration seconds" "Cyan"

# Summary table
Write-Log ""
Write-Log "Patch results:" "Cyan"
$patchResults | Format-Table Agent, Status, OldVersion, NewVersion, 
    Duration, Error -AutoSize | Out-String |
    ForEach-Object { Add-Content -Path $logFile -Value $_; Write-Host $_ }

$succeeded = ($patchResults | Where-Object Status -eq "Success").Agent
$failed = ($patchResults | Where-Object Status -eq "Failed").Agent

if ($failed.Count -gt 0) {
    Write-Log "═══════════════════════════════════════════════════" "Red"
    Write-Log "$($failed.Count) agent(s) FAILED to patch:" "Red"
    $failed | ForEach-Object { Write-Log "  - $_" "Red" }
    Write-Log "═══════════════════════════════════════════════════" "Red"
}

if ($succeeded.Count -eq 0) {
    Write-Log "All patches failed. Aborting." "Red"
    exit 1
}

# ═══════════════════════════════════════════════════════
# PHASE 2: Reboot (optional)
# ═══════════════════════════════════════════════════════

if ($Reboot -and $succeeded.Count -gt 0 -and -not $DryRun) {
    Write-Log ""
    Write-Log "──── Phase 2: Rebooting agents ────" "Cyan"
    $phase2Start = Get-Date
    
    $rebootResults = $succeeded | ForEach-Object -Parallel {
        $agent = $_
        $port = $using:GrpcPort
        $rebootTimeout = $using:RebootTimeoutSeconds
        $cred = $using:AgentCredential
        
        function Log-Reboot {
            param([string]$Message, [string]$Color = "Gray")
            Write-Host "[$(Get-Date -Format 'HH:mm:ss')] [$agent] $Message" `
                -ForegroundColor $Color
        }
        
        $result = [PSCustomObject]@{
            Agent = $agent
            Phase = "Reboot"
            Status = "Pending"
            Duration = $null
            Error = $null
        }
        $start = Get-Date
        
        try {
            Log-Reboot "Initiating reboot..."
            Invoke-Command -ComputerName $agent -Credential $cred -ScriptBlock {
                shutdown.exe /r /t 5 /f
            } -ErrorAction Stop
            
            # Wait for agent to go offline (5 minute max)
            Log-Reboot "Waiting for offline..."
            $offlineDeadline = (Get-Date).AddSeconds(120)
            while ((Get-Date) -lt $offlineDeadline) {
                Start-Sleep -Seconds 5
                if (-not (Test-Connection -ComputerName $agent -Count 1 -Quiet `
                    -ErrorAction SilentlyContinue)) {
                    Log-Reboot "Offline confirmed"
                    break
                }
            }
            
            # Wait for agent to come back online + gRPC port (5 minute max)
            Log-Reboot "Waiting for online + port $port..."
            $onlineDeadline = (Get-Date).AddSeconds($rebootTimeout)
            $back = $false
            while ((Get-Date) -lt $onlineDeadline) {
                Start-Sleep -Seconds 10
                if (Test-Connection -ComputerName $agent -Count 1 -Quiet `
                    -ErrorAction SilentlyContinue) {
                    # Ping works; now check port
                    $portTest = Test-NetConnection -ComputerName $agent `
                        -Port $port -InformationLevel Quiet `
                        -WarningAction SilentlyContinue
                    if ($portTest) {
                        $back = $true
                        break
                    }
                }
            }
            
            if (-not $back) {
                throw "Did not come back online within $rebootTimeout seconds"
            }
            
            $result.Status = "Success"
            Log-Reboot "REBOOT SUCCESS" "Green"
        }
        catch {
            $result.Status = "Failed"
            $result.Error = $_.Exception.Message
            Log-Reboot "FAILED: $($_.Exception.Message)" "Red"
        }
        finally {
            $result.Duration = [math]::Round(((Get-Date) - $start).TotalSeconds, 1)
        }
        
        $result
    } -ThrottleLimit $MaxConcurrent
    
    $phase2Duration = [math]::Round(((Get-Date) - $phase2Start).TotalSeconds, 0)
    Write-Log "Phase 2 complete in $phase2Duration seconds" "Cyan"
    
    $rebootResults | Format-Table Agent, Status, Duration, Error -AutoSize |
        Out-String |
        ForEach-Object { Add-Content -Path $logFile -Value $_; Write-Host $_ }
    
    # Update succeeded list to only those that survived reboot
    $rebootFailed = ($rebootResults | Where-Object Status -eq "Failed").Agent
    if ($rebootFailed.Count -gt 0) {
        $succeeded = $succeeded | Where-Object { $_ -notin $rebootFailed }
        Write-Log "Excluding $($rebootFailed.Count) agent(s) from snapshot phase" "Yellow"
    }
}

# ═══════════════════════════════════════════════════════
# PHASE 3: Snapshot (optional)
# ═══════════════════════════════════════════════════════

if ($Snapshot -and $succeeded.Count -gt 0 -and -not $DryRun) {
    Write-Log ""
    Write-Log "──── Phase 3: Taking snapshots ────" "Cyan"
    $phase3Start = Get-Date
    
    # Connect to vCloud
    try {
        $vc = Connect-CIServer -Server $vCloudServer -Org $vCloudOrg `
            -ErrorAction Stop
        Write-Log "Connected to vCloud: $vCloudServer / $vCloudOrg"
    }
    catch {
        Write-Log "Cannot connect to vCloud: $_" "Red"
        Write-Log "Skipping snapshot phase" "Yellow"
        $Snapshot = $false
    }
    
    if ($Snapshot) {
        $snapName = "post-patch-$timestamp"
        Write-Log "Snapshot name: $snapName"
        
        # Note: vCloud cmdlets are NOT thread-safe — run serially
        # but still much faster than manual
        $snapResults = foreach ($agent in $succeeded) {
            $result = [PSCustomObject]@{
                Agent = $agent
                Phase = "Snapshot"
                Status = "Pending"
                Duration = $null
                Error = $null
            }
            $start = Get-Date
            
            try {
                Write-Log "[$agent] Looking up VM..."
                $vm = Get-CIVM -Name $agent -ErrorAction Stop
                
                # Power off
                if ($vm.Status -ne "PoweredOff") {
                    Write-Log "[$agent] Shutting down..."
                    try {
                        # Try graceful first
                        Stop-CIVMGuest -VM $vm -Confirm:$false `
                            -ErrorAction Stop | Out-Null
                    }
                    catch {
                        # Fallback to hard power off
                        Stop-CIVM -VM $vm -Confirm:$false `
                            -ErrorAction Stop | Out-Null
                    }
                    
                    $offDeadline = (Get-Date).AddMinutes(5)
                    while ((Get-CIVM -Name $agent).Status -ne "PoweredOff") {
                        if ((Get-Date) -gt $offDeadline) {
                            throw "Did not power off within 5 min"
                        }
                        Start-Sleep -Seconds 10
                    }
                }
                
                # Remove existing snapshot
                Write-Log "[$agent] Removing old snapshot..."
                try {
                    $existing = Get-CIVMSnapshot -VM $vm `
                        -ErrorAction SilentlyContinue
                    if ($existing) {
                        Remove-CIVMSnapshot -VM $vm -Confirm:$false `
                            -ErrorAction Stop | Out-Null
                    }
                }
                catch {
                    Write-Log "[$agent] Could not remove old snapshot: $_" "Yellow"
                }
                
                # Create new snapshot
                Write-Log "[$agent] Creating snapshot..."
                New-CIVMSnapshot -VM $vm -Name $snapName `
                    -Confirm:$false -ErrorAction Stop | Out-Null
                
                # Power back on
                Write-Log "[$agent] Powering on..."
                Start-CIVM -VM $vm -Confirm:$false `
                    -ErrorAction Stop | Out-Null
                
                $result.Status = "Success"
                Write-Log "[$agent] SNAPSHOT SUCCESS" "Green"
            }
            catch {
                $result.Status = "Failed"
                $result.Error = $_.Exception.Message
                Write-Log "[$agent] FAILED: $_" "Red"
            }
            finally {
                $result.Duration = [math]::Round(
                    ((Get-Date) - $start).TotalSeconds, 1)
            }
            
            $result
        }
        
        Disconnect-CIServer -Server $vc -Confirm:$false `
            -ErrorAction SilentlyContinue | Out-Null
        
        $phase3Duration = [math]::Round(
            ((Get-Date) - $phase3Start).TotalSeconds, 0)
        Write-Log "Phase 3 complete in $phase3Duration seconds" "Cyan"
        
        $snapResults | Format-Table Agent, Status, Duration, Error -AutoSize |
            Out-String |
            ForEach-Object { Add-Content -Path $logFile -Value $_; Write-Host $_ }
    }
}

# ═══════════════════════════════════════════════════════
# FINAL SUMMARY
# ═══════════════════════════════════════════════════════

$totalDuration = [math]::Round(
    ((Get-Date) - $phase1Start).TotalSeconds, 0)

Write-Log ""
Write-Log "═══════════════════════════════════════════════════" "Cyan"
Write-Log "FLEET PATCH COMPLETE" "Cyan"
Write-Log "═══════════════════════════════════════════════════" "Cyan"
Write-Log "Total agents:     $($Agents.Count)"
Write-Log "Patched:          $($succeeded.Count)" "Green"
Write-Log "Failed:           $($failed.Count)" $(if($failed.Count -gt 0) {"Red"} else {"Green"})
Write-Log "Total time:       $totalDuration seconds"
Write-Log "Log saved to:     $logFile"
Write-Log "═══════════════════════════════════════════════════" "Cyan"

if ($failed.Count -gt 0) {
    exit 1
} else {
    exit 0
}
