<#
.SYNOPSIS
  Continuous lightweight sampler for the TestAgentGrpc process. Records key
  health metrics every N seconds to a CSV so that WHEN the zombie-listener
  failure happens, you have the full lead-up data to find the root cause.

  This is the diagnostic that actually catches the root cause, because it
  records the TREND leading to failure, not just a post-mortem snapshot.

.DESCRIPTION
  Samples: thread count, handle count, memory, GC, port/connection state,
  and whether the gRPC listener answers a health probe. Writes one CSV row
  per sample. Run this as a scheduled task on a few "canary" agents that
  experience the problem.

.PARAMETER IntervalSeconds
  Sampling interval (default 15s). Lower = finer resolution, more rows.

.PARAMETER OutputCsv
  Where to write samples.

.EXAMPLE
  .\Sample-AgentHealth.ps1 -IntervalSeconds 15
#>

param(
    [int]$IntervalSeconds = 15,
    [string]$OutputCsv = "C:\TestAgentService\Logs\health-samples.csv",
    [int]$Port = 5200
)

$ErrorActionPreference = "Continue"

# Write CSV header if file doesn't exist
if (-not (Test-Path $OutputCsv)) {
    "Timestamp,PID,Threads,Handles,WorkingSetMB,PrivateMemMB,GCGen0,GCGen1,GCGen2,TotalCPUsec,Responding,ListenerUp,EstablishedConns,CloseWaitConns,TimeWaitConns,HealthProbeMs,HealthProbeOk" |
        Out-File -FilePath $OutputCsv -Encoding utf8
}

Write-Host "Sampling TestAgentGrpc every ${IntervalSeconds}s -> $OutputCsv"
Write-Host "Press Ctrl+C to stop.`n"

# Track previous CPU to compute deltas (optional future enhancement)
while ($true) {
    $ts = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    $proc = Get-Process TestAgentGrpc -ErrorAction SilentlyContinue

    if ($proc) {
        $pid_      = $proc.Id
        $threads   = $proc.Threads.Count
        $handles   = $proc.HandleCount
        $wsMB      = [math]::Round($proc.WorkingSet64/1MB,1)
        $privMB    = [math]::Round($proc.PrivateMemorySize64/1MB,1)
        $cpu       = [math]::Round($proc.TotalProcessorTime.TotalSeconds,1)
        $responding= $proc.Responding

        # GC collection counts (process-wide via performance counters is heavy;
        # approximate with .NET CLR counters if available, else leave blank)
        $gc0 = ""; $gc1 = ""; $gc2 = ""
        try {
            $gc0 = [System.GC]::CollectionCount(0)  # NOTE: this is THIS ps process,
            $gc1 = [System.GC]::CollectionCount(1)  # not the agent. For agent GC,
            $gc2 = [System.GC]::CollectionCount(2)  # use dotnet-counters (see notes).
            $gc0 = ""; $gc1 = ""; $gc2 = ""          # blanked - use dotnet-counters instead
        } catch { }
    } else {
        $pid_=""; $threads=""; $handles=""; $wsMB=""; $privMB=""; $cpu=""
        $responding="ProcessNotRunning"; $gc0=""; $gc1=""; $gc2=""
    }

    # Port + connection state
    $conns = Get-NetTCPConnection -LocalPort $Port -ErrorAction SilentlyContinue
    $listenerUp = [bool]($conns | Where-Object State -eq 'Listen')
    $established = ($conns | Where-Object State -eq 'Established').Count
    $closeWait   = ($conns | Where-Object State -eq 'CloseWait').Count
    $timeWait    = ($conns | Where-Object State -eq 'TimeWait').Count

    # Health probe — can we actually reach the listener via TCP?
    $probeOk = $false
    $probeMs = -1
    try {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $tcp = New-Object System.Net.Sockets.TcpClient
        $iar = $tcp.BeginConnect("127.0.0.1", $Port, $null, $null)
        $probeOk = $iar.AsyncWaitHandle.WaitOne(3000, $false)
        $sw.Stop()
        $probeMs = $sw.ElapsedMilliseconds
        if ($probeOk) { $tcp.EndConnect($iar) }
        $tcp.Close()
    } catch { $probeOk = $false }

    # Append row
    "$ts,$pid_,$threads,$handles,$wsMB,$privMB,$gc0,$gc1,$gc2,$cpu,$responding,$listenerUp,$established,$closeWait,$timeWait,$probeMs,$probeOk" |
        Out-File -FilePath $OutputCsv -Append -Encoding utf8

    # Console heartbeat (compact)
    $flag = if (-not $listenerUp -or -not $probeOk) { " <-- UNHEALTHY" } else { "" }
    Write-Host "$ts  thr=$threads hnd=$handles memMB=$privMB conn=$established cw=$closeWait probe=${probeMs}ms$flag"

    Start-Sleep -Seconds $IntervalSeconds
}
