# Root-Cause Diagnosis: gRPC Zombie Listener

A staged plan to find WHY the TestAgentGrpc listener goes `Unavailable` while
the process stays alive. Move from cheap/passive to definitive/active.

---

## The Core Difficulty

The failure happens **inside the agent process**, but it's only observed from
**outside** (the controller sees `Unavailable`). And by the time anyone looks,
the watchdog may have restarted the agent, erasing the evidence.

So root-cause diagnosis requires capturing the **internal state in the minutes
leading up to the failure** — not a post-mortem snapshot. This document gives
three tiers of instrumentation, from passive (always-on, cheap) to active
(triggered at the moment of failure, definitive).

---

## The Suspects and Their Fingerprints

| Root cause | Mechanism | Fingerprint in data |
|---|---|---|
| Thread pool starvation | All threads blocked; gRPC can't accept | Thread count climbs, ThreadPool queue grows, everything slows first |
| Memory pressure / long GC | GC pause freezes the listener | Memory climbs, Gen2 GC count spikes, pause durations grow |
| Socket / handle leak | Resources exhausted, can't accept new conns | Handle count or CLOSE_WAIT climbs steadily over hours |
| Deadlock | Threads stuck on a lock forever | Many threads same wait reason, CPU near zero, no progress |
| Sync-over-async blocking | A blocking call holds a gRPC thread | One operation never completes; thread count creeps up |
| External dependency hang | Waiting on share/vCloud/DB that hung | Last log line is an outbound call with no completion |
| Unhandled handler exception | Exception corrupts state / kills thread | Exception in logs immediately before degradation |

Each leaves a distinct trail. The instrumentation below captures the trails.

---

## Tier 1: Mine Existing Logs (do this first, costs nothing)

Use `Diagnose-ZombieListener.ps1` against a known incident time (from the
controller's `Unavailable` log entry).

```powershell
.\Diagnose-ZombieListener.ps1 -IncidentTime "2026-06-15 21:56:00" -WindowMinutes 15
```

It pulls, for the window around the incident:
- Agent application log (and the "final words" before silence)
- Windows Application event log (.NET exceptions, EventID 1026)
- System event log (service + resource events)
- Current process state (if still degraded — run immediately)
- Port / connection state

**What you might already find:** an exception right before the failure, or a
"final words" log line showing the agent hung on an outbound call. If so, you
may have your answer without further tooling.

**What it can't tell you:** internal .NET state (thread pool, GC, locks) over
time. For that, go to Tier 2.

---

## Tier 2: Continuous Health Sampling (always-on, lightweight)

Run `Sample-AgentHealth.ps1` as a scheduled task on 2-3 "canary" agents that
experience the problem. It records one CSV row every 15s:

```powershell
.\Sample-AgentHealth.ps1 -IntervalSeconds 15
```

Captures over time: thread count, handle count, memory, connection states,
and a TCP health probe with latency.

**After the next incident, open the CSV and look at the rows leading up to the
failure.** The TREND is the diagnosis:

| What the trend shows | Root cause |
|---|---|
| Threads climbing 50 → 200 → 800 before failure | Thread pool starvation / blocking calls |
| PrivateMemMB climbing steadily, never dropping | Memory leak |
| Handles climbing steadily over hours | Handle leak |
| CloseWaitConns climbing | Socket leak (channels not disposed) |
| HealthProbeMs growing (50ms → 500ms → 3000ms → fail) | Gradual degradation — resource exhaustion |
| Everything flat, then probe suddenly fails | Sudden event — exception or deadlock (check logs at that timestamp) |

This is the single most valuable artifact for root cause. It turns "it
sometimes goes unavailable" into "memory climbed from 200MB to 1.8GB over 90
minutes, then it died" — which points straight at a leak.

---

## Tier 3: .NET Internal Diagnostics (definitive)

For the internal state the PowerShell sampler can't see, use Microsoft's
`dotnet-counters` and `dotnet-dump`. These are the definitive tools.

### Install the tools (one-time, on the agent)

```powershell
dotnet tool install --global dotnet-counters
dotnet tool install --global dotnet-dump
dotnet tool install --global dotnet-trace
# If dotnet SDK isn't on agents, download the single-file versions:
#   https://aka.ms/dotnet-counters/win-x64
#   https://aka.ms/dotnet-dump/win-x64
```

### 3a. dotnet-counters — live internal metrics

Run this against the agent process to watch the EXACT internal counters that
reveal thread pool and GC problems:

```powershell
# Find the PID
$pid = (Get-Process TestAgentGrpc).Id

# Watch the key counters live, logging to CSV
dotnet-counters monitor --process-id $pid --refresh-interval 5 `
    --counters System.Runtime,Microsoft.AspNetCore.Hosting,Grpc.AspNetCore.Server `
    --format csv --output agent-counters.csv
```

The counters that matter for THIS problem:

| Counter | Reveals |
|---|---|
| `ThreadPool Thread Count` | Thread pool starvation (climbs and doesn't recover) |
| `ThreadPool Queue Length` | Work piling up faster than it's processed |
| `ThreadPool Completed Work Item Count` | If this flatlines, the pool is wedged |
| `GC Heap Size` | Memory growth |
| `% Time in GC` | If high, GC pauses are freezing the listener |
| `Gen 2 GC Count` | Frequent Gen2 = memory pressure |
| `Monitor Lock Contention Count` | Lock contention / approaching deadlock |
| `Current Requests` (Hosting) | Requests in flight — if pinned high, they're stuck |

**Leave this running on a canary agent.** When the listener dies, the CSV shows
exactly which counter went abnormal first. That counter IS your root cause
category.

### 3b. dotnet-dump — capture the moment of failure

A memory dump at the moment of failure lets you see EXACTLY what every thread
is doing — the definitive deadlock/hang diagnosis.

**Capture a dump when the agent is degraded (before the watchdog restarts it):**

```powershell
$pid = (Get-Process TestAgentGrpc).Id
dotnet-dump collect --process-id $pid --output C:\TestAgentService\Logs\agent-hang.dmp
```

**Analyze it:**

```powershell
dotnet-dump analyze C:\TestAgentService\Logs\agent-hang.dmp
```

Then at the analysis prompt:

```
> threads              # list all threads
> pstacks              # parallel stacks — groups threads by what they're doing
> clrstack -all        # full managed stacks for every thread
> syncblk              # show locks and who holds them (DEADLOCK detection)
> dumpheap -stat       # heap by type — find what's leaking
```

The `pstacks` and `syncblk` commands are the deadlock-killers:
- `pstacks` — if 200 threads are all stuck at the same method, that method is the hang
- `syncblk` — if thread A holds a lock that thread B waits for, and vice versa, that's your deadlock

### 3c. Automate the dump capture (catch it before the restart)

The challenge: capturing the dump before the watchdog restarts. Hook the
capture into the watchdog itself — right before it heals, dump first:

```csharp
// ===== In the watchdog's Heal(), BEFORE exiting =====
private void Heal()
{
    _logger.LogError("Listener unhealthy — capturing diagnostic dump before restart");

    try
    {
        var pid = Environment.ProcessId;
        var dumpPath = $@"C:\TestAgentService\Logs\hang-{DateTime.Now:yyyyMMdd-HHmmss}.dmp";

        // Trigger dotnet-dump to capture THIS process
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet-dump",
            Arguments = $"collect --process-id {pid} --output \"{dumpPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var dumpProc = Process.Start(psi);
        dumpProc?.WaitForExit(30000);   // wait up to 30s for the dump

        _logger.LogInformation("Diagnostic dump written to {Path}", dumpPath);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Failed to capture diagnostic dump");
    }

    // ... then the existing exit logic ...
    Environment.ExitCode = 3;
    _lifetime.StopApplication();
}
```

Now every time the watchdog heals, it leaves a dump behind. After a few
incidents, you'll have several dumps — analyze them, and the common pattern
across them is your root cause. This is the killer combination: the watchdog
keeps the agent alive AND collects the evidence to fix the underlying bug.

---

## Recommended Sequence

1. **Tier 1 now** — mine existing logs for an incident you already had. You
   might get lucky and find an exception or hang in the "final words."

2. **Tier 2 this week** — deploy the health sampler to 2-3 canary agents. Wait
   for the next incident. The CSV trend will narrow it to a category
   (leak vs starvation vs sudden event).

3. **Tier 3 to confirm** — based on Tier 2's category, run dotnet-counters
   (for thread pool / GC) and capture a dump (for deadlock / hang). The dump's
   `pstacks` / `syncblk` give the definitive answer.

4. **Wire dump capture into the watchdog** — so future incidents
   auto-collect evidence even after you've deployed the self-healing fix.

---

## Decision Tree (once you have the data)

```
Did the health sampler show resources climbing before failure?
├── YES, memory climbing      → memory leak → dotnet-dump → dumpheap -stat
├── YES, threads climbing     → thread pool starvation → dotnet-counters
│                               (look for blocking sync-over-async calls)
├── YES, handles/CLOSE_WAIT   → socket/handle leak → find undisposed
│                               channels/connections in code
└── NO, flat then sudden death
    ├── Exception in logs at that timestamp? → that exception is the cause
    ├── Last log = outbound call, no completion? → external dependency hang
    │                               (add timeouts to that call)
    └── No exception, CPU near zero, threads stuck?
        → deadlock → dotnet-dump → syncblk + pstacks
```

---

## What "Good" Looks Like After You Find It

Once you identify the root cause, the fix is targeted and the self-healing
watchdog becomes a safety net rather than the primary mechanism:

| Root cause found | Targeted fix |
|---|---|
| Sync-over-async blocking | Make the call truly async; add timeout |
| External dependency hang | Add CancellationToken + timeout to the outbound call |
| Socket leak | Dispose gRPC channels / HttpClient properly (or pool them) |
| Memory leak | Fix the retained reference (dump shows what's held) |
| Deadlock | Reorder lock acquisition or use a single lock |
| Thread pool starvation | Remove blocking calls; raise min threads if justified |
| Unhandled exception | Catch and handle it in the gRPC handler |

The watchdog stays in place either way — but now it almost never fires, because
the underlying cause is gone.

---

**End of document.**
