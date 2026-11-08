# Tier 1 Reliability & Observability — Command Execution Hardening Spec

**Status:** Proposed
**Scope:** `TestAgentGrpc`, `TestControllerGrpc`, `TestController.WebApi`, `TestControllerGrpc.Core`
**Branch target:** `Rbsl`
**Author:** Copilot-assisted design
**Related review:** Tier 1 feasibility assessment (session review — ~70% of Tier 1 already shipped)

---

## 0. Motivation

A prior codebase review confirmed most of the proposed Tier 1 reliability work already ships:

| Capability | Existing implementation |
|---|---|
| Self-healing gRPC listener watchdog (zombie-listener detection) | `TestAgentGrpc/Services/GrpcListenerWatchdog.cs` |
| Stuck-execution safety net | `TestAgentGrpc/Services/StuckExecutionWatchdog.cs` |
| Prometheus / OpenTelemetry metrics (all hosts) | `AgentMetrics.cs`, `AppMetrics.cs`, `ControllerHostMetrics.cs` |
| Correlation-ID propagation over gRPC | `RemoteCommandStreamRunner` (`x-correlation-id` metadata), boundary re-stamp in `TestAgentGrpcService` |
| Live fleet-health UI | `HealthMetricsStrip.xaml` + `HealthMetricsVM.cs` |
| Post-exit stream drain timeout | `CommandExecutor.cs` (30s drain guard) |

This spec covers **only the three genuine remaining gaps**:

- **Work Item B1 — stdin redirected from NUL** at every process launch (kills interactive-prompt blocking).
- **Work Item B2 — silence detector** (last-output-age "likely hung" signal, flags in seconds instead of burning the full timeout).
- **Work Item C — error-unmasking audit** (`GrpcGuard` wrapper so the real server-side exception always names itself before rethrow).

**Option A** = deliver B1 + B2 + C together. **Option B** = B1 + B2 only (highest ROI). **Option C** = C only.
This document specifies **all three** so any option can be executed from the same source of truth.

---

## 1. Work Item B1 — Redirect `stdin` from NUL at every process launch

### 1.1 Problem
`ProcessStartInfo` in the command-execution paths redirects stdout/stderr but **not** stdin. A child process that prompts for interactive input (e.g. a `[Y/N]` confirm, a credential prompt) blocks forever on a handle that is never closed, producing a hung batch that only the timeout/watchdog eventually clears.

### 1.2 Affected launch sites
All three must be changed identically for fleet-wide coverage:

| # | File | Approx. line | Notes |
|---|------|-------------|-------|
| 1 | `TestAgentGrpc/Services/CommandExecutor.cs` | ~300 (`psi`), ~317 (`Process.Start`) | Primary agent execution path |
| 2 | `TestControllerGrpc/Services/AgentGrpcDispatcher.cs` | ~1278 (`new Process()`), ~1289 (`Start`) | WPF host in-proc dispatch |
| 3 | `TestController.WebApi/Services/StandaloneAgentDispatcher.cs` | ~388 (`Process.Start`) | Web host dispatch |

> Exclude non-command launches (`explorer.exe`, `notepad.exe`, help/URL launches, `UseShellExecute = true`) — those are user-facing shell opens, not redirected pipes.

### 1.3 Required change
For each `ProcessStartInfo` used for command execution:

```csharp
var psi = new ProcessStartInfo
{
	FileName               = resolvedFile,
	Arguments              = resolvedArgs,
	UseShellExecute        = false,
	RedirectStandardOutput = true,
	RedirectStandardError  = true,
	RedirectStandardInput  = true,   // NEW
	CreateNoWindow         = true,
};
```

Immediately after a successful `Process.Start`:

```csharp
// EOF on stdin: any interactive prompt fails fast instead of blocking the batch.
try { process.StandardInput.Close(); }
catch (Exception ex) { _logger.LogDebug(ex, "stdin close no-op"); }
```

### 1.4 Acceptance criteria
- All three launch sites set `RedirectStandardInput = true` and close stdin post-start.
- A command that prompts for input (test: `cmd /c "set /p x=continue? "`) returns/terminates instead of hanging.
- No regression in stdout/stderr streaming or exit-code capture.

---

## 2. Work Item B2 — Silence detector (last-output-age hung signal)

### 2.1 Problem
The existing heartbeat loop in `CommandExecutor.cs` (~lines 347–397) reports **elapsed** time only. A process that is alive but producing **zero output** is indistinguishable from healthy progress until the full timeout expires. We want to flag "likely hung" within a configurable short window (default 90s) — advisory, not fatal.

### 2.2 Design
Track the UTC timestamp of the last stdout/stderr line and compare it inside the existing heartbeat loop. This composes with (does not replace) `StuckExecutionWatchdog`, which remains the nuclear fallback.

#### 2.2.1 New state on `CommandExecutor`
```csharp
private long _lastOutputTicks = DateTime.UtcNow.Ticks;

/// <summary>Records that output was observed (called on every streamed line).</summary>
private void MarkOutput() =>
	Interlocked.Exchange(ref _lastOutputTicks, DateTime.UtcNow.Ticks);

private TimeSpan SinceLastOutput =>
	DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastOutputTicks), DateTimeKind.Utc);
```

- Initialize `_lastOutputTicks` at process start (reset per execution).
- Call `MarkOutput()` from `StreamOutputAsync` on every line received (both stdout and stderr).

#### 2.2.2 Heartbeat-loop escalation
Inside the existing loop, after the current progress `EmitEvent` (~line 381):

```csharp
var silence = SinceLastOutput;
if (silence > _settings.SilenceHungThreshold)
{
	var flagged = _silenceFlagged; // one-shot per silent stretch (see 2.2.3)
	if (!flagged)
	{
		_silenceFlagged = true;
		EmitEvent(executionId, ExecutionEventType.EventProgress,
			detail: $"\u26A0 No output for {silence:mm\\:ss} \u2014 command may be hung (PID {cachedPid})",
			progressPct: percent, perCallChannel: perCallChannel);

		_logger.LogWarning(
			"Execution {Id}: silent for {Silence}s \u2014 likely hung (PID {Pid})",
			executionId, (int)silence.TotalSeconds, cachedPid);
	}
}
```

#### 2.2.3 One-shot flag reset
Add `private bool _silenceFlagged;`. Reset to `false` in `MarkOutput()` so a fresh silent stretch re-arms the warning:

```csharp
private void MarkOutput()
{
	Interlocked.Exchange(ref _lastOutputTicks, DateTime.UtcNow.Ticks);
	_silenceFlagged = false;
}
```
Reset both `_lastOutputTicks` and `_silenceFlagged` at the start of each execution.

### 2.3 New settings (`AgentSettings.cs`)
Follow the existing pattern (see `WatchdogGraceMinutes`, `MaxExecutionTimeoutMinutes`). Expose seconds for config friendliness and derive the `TimeSpan`:

```csharp
/// <summary>
/// Seconds of zero stdout/stderr before the agent flags an execution as
/// "likely hung" (advisory only; does not kill the process). The
/// StuckExecutionWatchdog remains the nuclear fallback. Default: 90.
/// Set to 0 to disable the silence detector.
/// </summary>
public int SilenceHungSeconds { get; set; } = 90;

/// <summary>Derived threshold; TimeSpan.Zero disables the check.</summary>
public TimeSpan SilenceHungThreshold =>
	SilenceHungSeconds > 0 ? TimeSpan.FromSeconds(SilenceHungSeconds) : TimeSpan.Zero;
```

Guard the loop check with `_settings.SilenceHungThreshold > TimeSpan.Zero`.

### 2.4 Acceptance criteria
- A long-running command with a >90s silent stretch emits exactly one `EventProgress` warning + one `LogWarning`, then re-arms after the next output line.
- `SilenceHungSeconds = 0` fully disables the detector (no warnings emitted).
- No new allocations in the hot streaming path beyond the interlocked timestamp write.
- Existing elapsed/progress heartbeat behaviour is unchanged.

---

## 3. Work Item C — Error-unmasking audit (`GrpcGuard`)

### 3.1 Problem
gRPC handlers can swallow or generically translate exceptions so the **real** server-side cause never appears in logs (the class of bug that cost real time on the compressed-flag issue). We want every handler boundary to log the actual exception with its correlation ID **before** rethrowing a sanitized `RpcException`.

### 3.2 Current state (verified)
The two sampled handlers in `TestAgentGrpcService.cs` (`RunCommandStreamed`, `SubscribeAgentEvents`) only catch `OperationCanceledException` (benign client disconnect, correctly logged as `LogInformation`). No blanket swallow was found there — but there is **no systematic guard**, so the next unhandled type surfaces as an opaque `Internal` with no server-side detail. This work item adds the guard and audits every handler across hosts.

### 3.3 Design — reusable wrapper in `TestControllerGrpc.Core`
New file: `TestControllerGrpc.Core/Services/GrpcGuard.cs`

```csharp
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace TestControllerGrpc.Services;

/// <summary>
/// Wraps a gRPC handler body so the REAL server-side exception is always logged
/// (with the inbound correlation id) before a sanitized RpcException is returned
/// to the caller. Benign cancellations and already-structured RpcExceptions pass
/// through untouched.
/// </summary>
public static class GrpcGuard
{
	private static string? Corr(ServerCallContext ctx) =>
		ctx.RequestHeaders.GetValue("x-correlation-id");

	public static async Task<T> RunAsync<T>(
		IAppLogger log, string category, ServerCallContext ctx, Func<Task<T>> body)
	{
		try { return await body(); }
		catch (OperationCanceledException) { throw; }   // benign disconnect
		catch (RpcException) { throw; }                  // already structured
		catch (Exception ex)
		{
			log.Log(LogLevel.Error, category,
				$"Unhandled in {ctx.Method}: {ex.GetType().Name}: {ex.Message}",
				Corr(ctx), ex: ex);
			throw new RpcException(new Status(StatusCode.Internal, ex.Message));
		}
	}

	public static async Task RunAsync(
		IAppLogger log, string category, ServerCallContext ctx, Func<Task> body)
	{
		try { await body(); }
		catch (OperationCanceledException) { throw; }
		catch (RpcException) { throw; }
		catch (Exception ex)
		{
			log.Log(LogLevel.Error, category,
				$"Unhandled in {ctx.Method}: {ex.GetType().Name}: {ex.Message}",
				Corr(ctx), ex: ex);
			throw new RpcException(new Status(StatusCode.Internal, ex.Message));
		}
	}
}
```

> `IAppLogger.Log(LogLevel, string, string, string? correlationId, long, Exception?)` already exists (see `IAppLogger.cs`), so no interface change is required.

### 3.4 Adoption pattern
Wrap the body of each unary/streaming handler. Example (`TestAgentGrpcService`):

```csharp
public override Task<RunCommandReply> RunCommand(RunCommandRequest request, ServerCallContext context)
	=> GrpcGuard.RunAsync(_appLogger, "AgentGrpc.RunCommand", context, () =>
	{
		// ... existing body ...
		return Task.FromResult(reply);
	});
```

For streaming handlers that already have targeted `catch (OperationCanceledException)`, keep the inner catch (it stays benign) and wrap the outer body so any *other* exception is logged.

### 3.5 Handler audit checklist
Produce and complete an audit table for every `TestAgentServiceBase` / gRPC service override across:
- `TestAgentGrpc/Services/TestAgentGrpcService.cs`
- Any gRPC service in `TestControllerGrpc` and `TestController.WebApi`

| Handler | Host | Catches today | Masks real exception? | Wrapped with GrpcGuard? |
|---|---|---|---|---|
| `RunCommand` | Agent | none | opaque on throw | ☐ |
| `RunCommandStreamed` | Agent | `OperationCanceledException` | no | ☐ |
| `SubscribeAgentEvents` | Agent | `OperationCanceledException` | no | ☐ |
| `GetExecutionHistory` | Agent | none | opaque on throw | ☐ |
| `GetState` / `GetAgentSnapshot` / `GetAuditLog` | Agent | none | opaque on throw | ☐ |
| _(enumerate remaining during implementation)_ | | | | ☐ |

### 3.6 DI note
`TestAgentGrpcService` currently takes `ILogger<TestAgentGrpcService>`. `GrpcGuard` needs `IAppLogger`. Inject `IAppLogger` into the handler classes (it is already registered as a Singleton in `TestControllerGrpc.Core`); confirm the agent host registers `IAppLogger` and add the registration if missing.

### 3.7 Acceptance criteria
- `GrpcGuard.cs` compiles in `TestControllerGrpc.Core` with no new dependencies.
- Every enumerated handler is either wrapped or explicitly justified in the audit table.
- A handler that throws a non-Rpc exception logs an `Error` with the correlation id and returns `StatusCode.Internal` (verified by a unit test).
- Benign `OperationCanceledException` still logs as information (no error noise on client disconnect).

---

## 4. Cross-cutting concerns

- **Conventions:** Use `IAppLogger` (`_logger.Info/Warn/Error` or the `Log(...)` overload) for app-level logging; keep `ILogger<T>` only for framework/hosted-service logging, per `CONVENTIONS.md`. Singleton DI defaults apply.
- **No behavioural change to watchdogs/metrics** — those are already shipped and out of scope here.
- **Redaction:** Continue routing any command text through `SecurityRedactor` before it reaches logs/events (already used in `CommandExecutor` / `TestAgentGrpcService`).

---

## 5. Test plan

### 5.1 Unit tests (`TestControllerGrpc.Tests`)
- `GrpcGuard_Should_LogAndRethrowInternal_When_BodyThrows`
- `GrpcGuard_Should_PassThrough_When_BodyThrowsRpcException`
- `GrpcGuard_Should_NotLogError_When_OperationCanceled`
- `CommandExecutor_Should_FlagSilence_When_NoOutputBeyondThreshold`
- `CommandExecutor_Should_ReArmSilenceFlag_When_OutputResumes`
- `CommandExecutor_Should_NotFlagSilence_When_SilenceHungSecondsIsZero`

### 5.2 Manual / integration
- Interactive-prompt command hangs no longer (B1) — `cmd /c "set /p x=go? "`.
- Long silent command produces exactly one hung warning within `SilenceHungSeconds` (B2).
- Force a handler exception and confirm the real type/message + correlation id appear server-side (C).

### 5.3 Build gate
- `run_build` succeeds (solution-wide).
- Existing `TestControllerGrpc.Tests` and `TestController.WebApi.Tests` remain green.

---

## 6. Rollout order (recommended)

1. **B1** (stdin from NUL) — smallest, highest ROI, kills interactive-prompt hangs.
2. **B2** (silence detector) — early hung signal; depends on B1 being in `CommandExecutor`.
3. **C** (`GrpcGuard` + handler audit) — makes the next mystery error name itself.

**Option A** ships all three in one reviewed PR; **Option B** stops after step 2; **Option C** performs step 3 only.

---

## 7. Out of scope (already implemented — do not re-add)

- gRPC listener watchdog / zombie-listener detection (`GrpcListenerWatchdog`).
- Stuck-execution nuclear fallback (`StuckExecutionWatchdog`).
- Prometheus/OTel metrics on any host.
- Correlation-ID propagation plumbing over gRPC metadata.
- Fleet-health UI strip.
- Post-exit stdout/stderr drain timeout.
