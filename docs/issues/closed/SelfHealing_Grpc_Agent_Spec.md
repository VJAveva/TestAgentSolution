# Self-Healing gRPC Agent — Implementation Specification

| Field | Value |
|---|---|
| **Problem** | Agent gRPC listener goes `Unavailable` while the process stays alive (zombie listener); controller calls intermittently fail |
| **Component** | TestAgentGrpc (agent) + TestControllerGrpc.Core (controller-side retry) |
| **Goal** | Agent detects its own degraded listener and self-heals; controller rides through the brief recovery window |
| **Priority** | P0 (production reliability) |

---

## Instructions for Copilot

Implement a complete self-healing system for the TestAgentGrpc service across
four parts. Work through them in order. For each:

1. Create or modify the file as specified
2. Replace every `===== PLUG IN =====` with the REAL value from this codebase
   (actual namespaces, the real host setup, real channel-creation code)
3. Preserve existing functionality — these are additions/enhancements, not rewrites
4. After all four parts, the agent should self-detect a dead listener and
   restart cleanly, and the controller should tolerate the restart window

Be opinionated. If my skeleton conflicts with the real host setup, adapt it and
explain what you changed.

---

## Background: The Problem

The agent (`TestAgentGrpc`) sometimes enters a state where:

- The gRPC listener reports `Unavailable` to the controller
- BUT the process is still alive (it did not crash)
- The Windows service shows "Running"
- Controller calls reach the agent intermittently — some succeed, some fail

This is a **zombie listener**: the process holds its port and mutex but the
gRPC listener has silently stopped serving reliably.

### Why the obvious fix is insufficient

`sc.exe failure TestAgentService ... actions= restart/5000` only restarts the
service when the **process exits/crashes**. It cannot detect a zombie listener
because Windows sees a healthy running process and does nothing.

### The complete solution (4 parts)

| Part | What it does | Solves |
|---|---|---|
| 1. Self-healing watchdog | Agent checks its own listener, exits if dead | Detecting the zombie |
| 2. gRPC health service | Gives the watchdog something real to call | Reliable health signal |
| 3. sc.exe failure policy | Restarts the process when watchdog exits | Completing the heal |
| 4. Controller retry | Rides through the restart window | Pipelines don't fail on transient unavailability |

Parts 1+2 detect and trigger. Part 3 completes the restart. Part 4 makes the
controller tolerant. All four are needed.

---

## Part 1: Self-Healing Watchdog

The agent monitors its OWN gRPC listener from inside the process. Every 30s it
makes a loopback gRPC health call. If the listener fails the check 3 times in a
row, the watchdog exits the process cleanly so the service manager restarts a
fresh instance (which binds a clean port).

### File: `TestAgentGrpc/Services/GrpcListenerWatchdog.cs`

This extends the existing watchdog (which currently just has the cancellation
fix). Replace its contents with this fuller implementation.

```csharp
using Grpc.Net.Client;
using Grpc.Health.V1;                    // gRPC standard health service
using Grpc.Core;
using System.Net.NetworkInformation;

// ===== PLUG IN: your actual namespace =====
namespace TestAgentGrpc.Services;

/// <summary>
/// Monitors the agent's own gRPC listener and self-heals if it becomes a
/// "zombie" (process alive but listener not serving). On sustained failure it
/// exits the process cleanly so the Windows Service Manager restarts a fresh
/// instance with a clean port binding.
/// </summary>
public class GrpcListenerWatchdog : BackgroundService
{
    private readonly ILogger<GrpcListenerWatchdog> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly int _port;

    private int _consecutiveFailures = 0;

    // Tunables
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SelfCheckTimeout = TimeSpan.FromSeconds(5);
    private const int MaxFailuresBeforeHeal = 3;

    // Exit code 3 = "restart needed" (distinct from normal shutdown = 0)
    private const int ExitCodeRestartNeeded = 3;

    public GrpcListenerWatchdog(
        ILogger<GrpcListenerWatchdog> logger,
        IHostApplicationLifetime lifetime,
        IConfiguration config)
    {
        _logger = logger;
        _lifetime = lifetime;
        // ===== PLUG IN: your config key for the gRPC port =====
        _port = config.GetValue<int>("Agent:GrpcPort", 5200);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Let the host fully start before the first check
            await Task.Delay(StartupGrace, stoppingToken);

            _logger.LogInformation(
                "GrpcListenerWatchdog started. Monitoring port {Port} every {Sec}s.",
                _port, CheckInterval.TotalSeconds);

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(CheckInterval, stoppingToken);
                await CheckAndHealAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown — swallow, do NOT crash the host
            _logger.LogInformation("GrpcListenerWatchdog shutting down gracefully");
        }
        catch (Exception ex)
        {
            // Unexpected — log loudly but don't take down the host
            _logger.LogError(ex, "GrpcListenerWatchdog encountered an unexpected error");
        }
    }

    private async Task CheckAndHealAsync(CancellationToken ct)
    {
        bool healthy = await IsListenerHealthyAsync(ct);

        if (healthy)
        {
            if (_consecutiveFailures > 0)
                _logger.LogInformation(
                    "gRPC listener recovered after {N} failed check(s)",
                    _consecutiveFailures);
            _consecutiveFailures = 0;
            return;
        }

        _consecutiveFailures++;
        _logger.LogWarning(
            "gRPC listener health check FAILED ({N}/{Max}) on port {Port}",
            _consecutiveFailures, MaxFailuresBeforeHeal, _port);

        if (_consecutiveFailures >= MaxFailuresBeforeHeal)
            Heal();
    }

    /// <summary>
    /// Two-stage health check:
    ///   Stage 1 (cheap): is anything listening on the port at TCP level?
    ///   Stage 2 (real):  does the gRPC health service actually respond Serving?
    /// A TCP-only check can pass while gRPC is stuck, so stage 2 is essential.
    /// </summary>
    private async Task<bool> IsListenerHealthyAsync(CancellationToken ct)
    {
        // Stage 1 — TCP listener present?
        if (!IsPortListening(_port))
        {
            _logger.LogWarning("Port {Port} has no TCP listener", _port);
            return false;
        }

        // Stage 2 — gRPC health call to self
        try
        {
            using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{_port}");
            var client = new Health.HealthClient(channel);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(SelfCheckTimeout);

            var resp = await client.CheckAsync(
                new HealthCheckRequest(),
                cancellationToken: cts.Token);

            bool serving = resp.Status == HealthCheckResponse.Types.ServingStatus.Serving;
            if (!serving)
                _logger.LogWarning("gRPC health reports status: {Status}", resp.Status);
            return serving;
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(
                "gRPC self-check failed: {Code} — {Detail}",
                ex.StatusCode, ex.Status.Detail);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "gRPC self-check threw unexpectedly");
            return false;
        }
    }

    private static bool IsPortListening(int port)
    {
        var props = IPGlobalProperties.GetIPGlobalProperties();
        return props.GetActiveTcpListeners().Any(ep => ep.Port == port);
    }

    /// <summary>
    /// Self-heal by exiting cleanly. The Windows Service Manager (configured via
    /// sc.exe failure policy, see Part 3) restarts a FRESH process that binds a
    /// clean port. In-process listener rebinding is intentionally NOT attempted
    /// because Kestrel/gRPC port rebinding in the same process is unreliable and
    /// can cause "address already in use" crashes.
    /// </summary>
    private void Heal()
    {
        _logger.LogError(
            "gRPC listener unhealthy after {N} checks. Self-healing: exiting so " +
            "the service manager restarts a clean instance.",
            _consecutiveFailures);

        Environment.ExitCode = ExitCodeRestartNeeded;
        _lifetime.StopApplication();
    }
}
```

### Register the watchdog (if not already)

In `Program.cs`:

```csharp
// ===== PLUG IN: ensure this registration exists =====
builder.Services.AddHostedService<GrpcListenerWatchdog>();
```

---

## Part 2: gRPC Health Service (so the self-check has something to call)

The watchdog's stage-2 check calls the standard gRPC health service. The agent
must expose it. This uses the official `Grpc.HealthCheck` package.

### Add the package to TestAgentGrpc

```xml
<PackageReference Include="Grpc.AspNetCore.HealthChecks" Version="2.66.0" />
<!-- Adjust version to match your existing Grpc.AspNetCore version -->
```

### Register in `Program.cs`

```csharp
// ===== PLUG IN: add alongside your existing gRPC service registration =====

builder.Services.AddGrpcHealthChecks()
    .AddCheck("agent-listener", () =>
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy());

// ... after building the app, map the health endpoint ...
var app = builder.Build();

app.MapGrpcHealthChecksService();   // exposes the standard grpc.health.v1 service

// ... your existing app.MapGrpcService<YourAgentService>(); ...
```

This makes the agent respond to `grpc.health.v1.Health/Check` with `Serving`
when healthy. The watchdog's self-check calls exactly this. You can later make
the health check smarter (e.g., report `NotServing` if an internal queue is
stuck), and the watchdog will react automatically.

### Optional: tie health status to real internal state

For a more meaningful signal, make the health check reflect whether the agent
can actually do work:

```csharp
// ===== PLUG IN: report real health based on agent internals =====
builder.Services.AddGrpcHealthChecks()
    .AddCheck("agent-listener", () =>
    {
        // Example: report unhealthy if the command executor is wedged
        // bool canExecute = _commandExecutor.IsResponsive;
        // return canExecute ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy();
        return HealthCheckResult.Healthy();
    });
```

---

## Part 3: sc.exe Failure Policy (restarts the process when the watchdog exits)

This is the piece that completes the heal: when the watchdog exits the process,
Windows restarts it. The critical detail most people miss is `failureflag 1` —
without it, Windows only treats a CRASH as a failure, not a clean exit.

### File: `Configure-AgentRecovery.ps1`

```powershell
<#
.SYNOPSIS
  Configures Windows Service Manager recovery for TestAgentService so it
  restarts after the self-healing watchdog exits the process.

.DESCRIPTION
  - Sets escalating restart delays (5s, 10s, 30s) to avoid tight crash loops.
  - Sets failureflag=1 so NON-ZERO exit codes (the watchdog's exit 3) count as
    failures, not just hard crashes. This is the key setting.
  - Resets the failure counter after 60s of healthy running.

.NOTES
  Run as Administrator. Run on every agent (bake into provisioning/revert
  scripts so it survives VM reverts).
#>

param(
    [string]$ServiceName = "TestAgentService"
)

$ErrorActionPreference = "Stop"

Write-Host "Configuring recovery policy for $ServiceName ..."

# Verify the service exists
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) {
    throw "Service '$ServiceName' not found. Install the agent service first."
}

# Set failure actions: reset counter after 60s; restart with escalating delays
# Delays are in milliseconds: 5s, 10s, 30s
& sc.exe failure $ServiceName reset= 60 actions= restart/5000/restart/10000/restart/30000
if ($LASTEXITCODE -ne 0) { throw "sc.exe failure returned $LASTEXITCODE" }

# CRITICAL: treat non-zero exit codes as failures (catches watchdog exit 3).
# Without this, a clean Environment.Exit(3) would NOT trigger restart.
& sc.exe failureflag $ServiceName 1
if ($LASTEXITCODE -ne 0) { throw "sc.exe failureflag returned $LASTEXITCODE" }

Write-Host "Recovery policy configured:"
Write-Host "  - Restart after 5s / 10s / 30s on successive failures"
Write-Host "  - Non-zero exit codes treated as failures (failureflag=1)"
Write-Host "  - Failure counter resets after 60s healthy"

# Show the resulting config for verification
Write-Host "`nCurrent configuration:"
& sc.exe qfailure $ServiceName
```

### Run it

```powershell
.\Configure-AgentRecovery.ps1 -ServiceName TestAgentService
```

### Bake into provisioning

Add this call to your VM provisioning / revert scripts (e.g.
`RevertRcloudMachine.ps1` flow) so the recovery policy survives VM reverts —
consistent with your "bake fixes into provisioning" principle.

---

## Part 4: Controller-Side gRPC Retry (ride through the restart window)

While the agent self-heals (exit + restart takes a few seconds), the controller
should retry transient `Unavailable` errors rather than failing the pipeline.

### File: where the controller creates agent gRPC channels

```csharp
// ===== PLUG IN: locate your channel creation in IAgentGrpcDispatcher =====
// (or wherever GrpcChannel.ForAddress is called for agents)

using Grpc.Net.Client;
using Grpc.Net.Client.Configuration;

private GrpcChannel CreateAgentChannel(string agentUrl)
{
    var retryPolicy = new RetryPolicy
    {
        MaxAttempts = 5,
        InitialBackoff = TimeSpan.FromSeconds(1),
        MaxBackoff = TimeSpan.FromSeconds(10),
        BackoffMultiplier = 2.0,
        RetryableStatusCodes = { StatusCode.Unavailable }
    };

    return GrpcChannel.ForAddress(agentUrl, new GrpcChannelOptions
    {
        ServiceConfig = new ServiceConfig
        {
            MethodConfigs =
            {
                new MethodConfig
                {
                    Names = { MethodName.Default },
                    RetryPolicy = retryPolicy
                }
            }
        },

        // Keepalive helps detect dead connections faster
        HttpHandler = new SocketsHttpHandler
        {
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            EnableMultipleHttp2Connections = true,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
        }
    });
}
```

### Important caveats

1. **Retries only apply to idempotent calls.** A "reboot the machine" command
   is NOT safe to auto-retry. Mark recovery/destructive calls as non-retryable,
   or give them their own channel without the retry policy.

2. **Re-resolve dead channels.** If a channel has been failing, dispose and
   recreate it rather than reusing — a channel pinned to a dead connection may
   not recover on its own.

3. **Recovery commands bypass the circuit breaker.** This connects to the prior
   defect: if your circuit breaker is open, recovery/reboot commands must STILL
   be dispatchable. Ensure the retry + circuit-breaker interaction does not
   block the very commands meant to recover the agent.

```csharp
// ===== PLUG IN: mark recovery commands as bypass =====
// Example pattern:
// if (command.IsRecoveryAction)
//     return await DispatchWithoutCircuitBreaker(agent, command, ct);
```

---

## How It All Fits Together

```
┌─────────────────────────────────────────────────────────────┐
│ TestAgentGrpc process                                       │
│   gRPC listener (5200) + grpc.health.v1 health service       │
│                    ▲                                         │
│                    │ loopback health check every 30s         │
│   GrpcListenerWatchdog                                       │
│     3 consecutive failures → Environment.ExitCode=3 + exit   │
└────────────────────┬────────────────────────────────────────┘
                     │ process exits (code 3)
                     ▼
        Windows Service Manager (failureflag=1)
          treats exit 3 as failure → restart after 5s
                     │
                     ▼
        Fresh TestAgentGrpc process → clean port bind → healthy

Meanwhile, on the controller:
   gRPC call hits Unavailable during the ~5-10s restart window
     → retry policy retries with backoff (1s, 2s, 4s...)
     → call succeeds once the fresh agent is up
     → pipeline continues, no failure
```

---

## Implementation Order

| # | Part | Effort | Depends on |
|---|---|---|---|
| 1 | gRPC health service (Part 2) | 30 min | — (do first; watchdog needs it) |
| 2 | Self-healing watchdog (Part 1) | 1 hr | Part 2 |
| 3 | sc.exe recovery script (Part 3) | 30 min | — |
| 4 | Controller retry (Part 4) | 1 hr | — |

Do Part 2 before Part 1 — the watchdog's self-check needs the health service to
exist, or it will always report unhealthy and restart-loop.

---

## Verification Steps

### Verify the health service works
```powershell
# Install grpcurl, then on the agent:
grpcurl -plaintext localhost:5200 grpc.health.v1.Health/Check
# Should return: { "status": "SERVING" }
```

### Verify the watchdog detects a dead listener
1. Start the agent, confirm watchdog logs "Monitoring port 5200"
2. Simulate a zombie: suspend the gRPC handler thread (or block the health check)
3. Within ~90s (3 × 30s), watchdog should log failures and exit with code 3
4. Confirm the service restarts (check Event Log + service status)

### Verify the recovery policy
```powershell
sc.exe qfailure TestAgentService
# Confirm: RESET_PERIOD 60, actions restart/5000/10000/30000, FAILURE_ACTIONS_FLAG TRUE
```

### Verify controller tolerance
1. Trigger a pipeline
2. While running, kill the agent process (force the restart)
3. Pipeline should NOT fail — the controller retry rides through the restart

---

## Tuning Guidance

| Setting | Default | When to change |
|---|---|---|
| Check interval | 30s | Lower (15s) for faster detection; higher (60s) to reduce overhead |
| Max failures before heal | 3 | Lower (2) for faster heal; higher (5) to avoid false positives |
| Self-check timeout | 5s | Raise if the agent is on a slow/loaded machine |
| Restart delays | 5/10/30s | Raise if restarts are expensive; the escalation avoids crash loops |
| Controller retry attempts | 5 | Match to expected worst-case restart time |

Start conservative (the defaults). If you see false-positive restarts (healthy
agents restarting), raise the failure threshold and self-check timeout.

---

## What This Does and Doesn't Solve

**Solves:**
- Zombie listener (process alive, gRPC not serving) — now self-detected and healed
- Clean restart with fresh port bind — no "address already in use"
- Controller pipelines surviving the brief restart window

**Does NOT solve (separate concerns):**
- Root cause of WHY the listener zombies in the first place — this is a safety
  net, not a root-cause fix. Still investigate why it degrades (resource leak?
  thread pool exhaustion? gRPC server bug?).
- Hardware/network failures of the VM itself — that's the VM revert/reboot path.

The self-healing system makes the agent resilient to the symptom while you
investigate the underlying cause separately.

---

**End of specification.**
