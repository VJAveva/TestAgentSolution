using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using TestAgentGrpc;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Dispatches commands to agent nodes via gRPC.
/// Maintains a pool of channels keyed by agent name → address.
/// Supports command execution with timeout, polling, and reboot handling.
/// </summary>
public sealed class AgentGrpcDispatcher : IAgentGrpcDispatcher
{
    private readonly ILogger<AgentGrpcDispatcher> _logger;
    private readonly IAppLogger _appLogger;
    private readonly IEventAggregator _events;
    private readonly IAgentTelemetryCache? _telemetryCache;
    private readonly ControllerTimeoutOptions _timeouts;
    private readonly ConcurrentDictionary<string, AgentEndpoint> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AgentHealthState> _healthStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _activeExecutions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when execution output arrives.</summary>
    public event Action<string, string, string>? OutputReceived;  // agentName, line, kind

    /// <summary>Raised when execution state changes.</summary>
    public event Action<string, string>? StatusChanged;  // agentName, status

    public AgentGrpcDispatcher(ILogger<AgentGrpcDispatcher> logger, IAppLogger appLogger, IEventAggregator events,
        IAgentTelemetryCache? telemetryCache = null, ControllerTimeoutOptions? timeouts = null)
    {
        _logger = logger;
        _appLogger = appLogger;
        _events = events;
        _telemetryCache = telemetryCache;
        _timeouts = timeouts ?? new ControllerTimeoutOptions();
    }

    /// <summary>
    /// Registers an agent name → gRPC address mapping.
    /// Normalizes the address to ensure a valid URI scheme is present.
    /// </summary>
    public void RegisterAgent(string agentName, string grpcAddress)
    {
        grpcAddress = NormalizeAddress(grpcAddress);

        // Dispose old endpoint if re-registering same name
        if (_agents.TryRemove(agentName, out var old))
            old.Dispose();
        _agents[agentName] = new AgentEndpoint(agentName, grpcAddress, _timeouts);
        _healthStates[agentName] = new AgentHealthState { AgentName = agentName };
        _logger.LogInformation("Registered agent {Name} \u2192 {Address}", agentName, grpcAddress);
        _events.Publish(new AgentRegisteredEvent(agentName, grpcAddress));
    }

    /// <summary>
    /// Removes a registered agent and disposes its gRPC channel.
    /// </summary>
    public bool UnregisterAgent(string agentName)
    {
        _healthStates.TryRemove(agentName, out _);
        _telemetryCache?.Remove(agentName);
        if (_agents.TryRemove(agentName, out var ep))
        {
            ep.Dispose();
            _logger.LogInformation("Unregistered agent: {Name}", agentName);
            _events.Publish(new AgentUnregisteredEvent(agentName));
            return true;
        }
        return false;
    }

    /// <summary>
    /// Tests connectivity to an agent by calling GetAgentSnapshot.
    /// Returns the snapshot on success, or null + error message on failure.
    /// </summary>
    /// <summary>Returns true if the given agent currently has a streaming command in progress.</summary>
    public bool IsAgentExecuting(string agentName) => _activeExecutions.ContainsKey(agentName);

    public async Task<(AgentSnapshot? Snapshot, string? Error)> TestConnectionAsync(
        string agentName, CancellationToken ct = default)
    {
        if (!_agents.TryGetValue(agentName, out var endpoint))
            return (null, $"Agent '{agentName}' not registered");

        // ── CRITICAL: Do NOT poll the agent via gRPC while a command is actively
        // streaming. Concurrent HTTP/2 calls on the same channel can trigger
        // GOAWAY/RST_STREAM on agents with unstable HTTP/2 (e.g., HTTP_1_1_REQUIRED),
        // which kills the in-flight streaming call and cancels the execution.
        if (_activeExecutions.TryGetValue(agentName, out var activeCmd))
        {
            return (new AgentSnapshot
            {
                AgentName = agentName,
                State = AgentState.Running,
                CurrentCommand = activeCmd,
                CurrentActivity = $"Executing: {activeCmd}",
            }, null);
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_timeouts.TestConnectionTimeoutSeconds));

            var client = endpoint.GetClient();
            var snapshot = await client.GetAgentSnapshotAsync(
                new Empty(), cancellationToken: cts.Token);

            RecordSuccess(agentName);
            _telemetryCache?.Update(agentName, snapshot, snapshot.Metrics);
            StatusChanged?.Invoke(agentName, $"Online \u2014 {snapshot.State}");
            return (snapshot, null);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            RecordFailure(agentName);
            StatusChanged?.Invoke(agentName, "Unreachable");
            return (null, $"Agent '{agentName}' unreachable at {endpoint.Address}");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
        {
            // Agent running older version without GetAgentSnapshot — fallback to GetState
            try
            {
                using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts2.CancelAfter(TimeSpan.FromSeconds(_timeouts.TestConnectionTimeoutSeconds));
                var client = endpoint.GetClient();
                var state = await client.GetStateAsync(new Empty(), cancellationToken: cts2.Token);
                RecordSuccess(agentName);
                StatusChanged?.Invoke(agentName, $"Online \u2014 {state.State} (legacy)");
                var legacySnapshot = new AgentSnapshot { AgentName = agentName, State = state.State };
                _telemetryCache?.Update(agentName, legacySnapshot, null);
                return (legacySnapshot, null);
            }
            catch (Exception ex2)
            {
                RecordFailure(agentName);
                return (null, $"Fallback GetState also failed: {ex2.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            RecordFailure(agentName);
            return (null, $"Connection to '{agentName}' timed out ({_timeouts.TestConnectionTimeoutSeconds}s)");
        }
        catch (Exception ex)
        {
            RecordFailure(agentName);
            return (null, $"Connection error: {ex.Message}");
        }
    }

    /// <summary>
    /// Performs a comprehensive multi-step diagnostic of agent connectivity.
    /// Returns a list of (step, passed, detail) tuples for display.
    /// </summary>
    public async Task<List<DiagnosticStep>> DiagnoseAgentAsync(
        string agentName, CancellationToken ct = default)
    {
        var steps = new List<DiagnosticStep>();

        // ── SAFEGUARD: Do not make gRPC/network calls during active execution ──
        // Diagnostics steps 3-6 (TCP, HTTP, gRPC×2) would risk HTTP/2 RST_STREAM
        // on the same channel that is streaming command output.
        if (IsAgentExecuting(agentName))
        {
            steps.Add(new("Registration", true, $"Agent '{agentName}' is registered"));
            steps.Add(new("Execution Guard", true,
                "Diagnostics skipped — agent is currently executing. " +
                "Network/gRPC calls suppressed to protect active command stream.",
                IsFatal: false));
            return steps;
        }

        // Step 0: Check registered
        if (!_agents.TryGetValue(agentName, out var endpoint))
        {
            steps.Add(new("Registration", false, $"Agent '{agentName}' not found in registry"));
            return steps;
        }
        steps.Add(new("Registration", true, $"Registered → {endpoint.Address}"));

        // Step 1: Parse URI
        Uri? uri;
        try
        {
            uri = new Uri(endpoint.Address);
            steps.Add(new("Address Parse", true, $"Host: {uri.Host}, Port: {uri.Port}, Scheme: {uri.Scheme}"));
        }
        catch (Exception ex)
        {
            steps.Add(new("Address Parse", false, $"Invalid URI: {ex.Message}"));
            return steps;
        }

        // Step 2: DNS resolution
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(uri.Host, ct);
            if (addresses.Length == 0)
            {
                steps.Add(new("DNS Lookup", false, $"No addresses found for '{uri.Host}'"));
                return steps;
            }
            steps.Add(new("DNS Lookup", true,
                $"{uri.Host} → {string.Join(", ", addresses.Select(a => a.ToString()))}"));
        }
        catch (Exception ex)
        {
            steps.Add(new("DNS Lookup", false, $"DNS resolution failed for '{uri.Host}': {ex.Message}"));
            return steps;
        }

        // Step 3: TCP port reachability
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            using var tcpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            tcpCts.CancelAfter(TimeSpan.FromSeconds(_timeouts.DiagnosticsTcpTimeoutSeconds));
            await tcp.ConnectAsync(uri.Host, uri.Port, tcpCts.Token);
            steps.Add(new("TCP Connect", true, $"Port {uri.Port} is open"));
        }
        catch (OperationCanceledException)
        {
            steps.Add(new("TCP Connect", false, $"Port {uri.Port} connection timed out ({_timeouts.DiagnosticsTcpTimeoutSeconds}s) — firewall?"));
            return steps;
        }
        catch (Exception ex)
        {
            steps.Add(new("TCP Connect", false, $"Port {uri.Port} refused: {ex.Message}"));
            return steps;
        }

        // Step 4: HTTP/2 check via plain HTTP endpoint
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(_timeouts.DiagnosticsHttpTimeoutSeconds) };
            var response = await http.GetAsync($"{uri.Scheme}://{uri.Host}:{uri.Port}/", ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            var preview = body.Length > 80 ? body[..80] + "…" : body;
            steps.Add(new("HTTP Endpoint", true, $"Status: {(int)response.StatusCode} — \"{preview}\""));
        }
        catch (Exception ex)
        {
            // Not fatal — gRPC may still work on HTTP/2 even if GET / fails
            steps.Add(new("HTTP Endpoint", false,
                $"GET / failed (may be normal for pure gRPC): {ex.Message}", IsFatal: false));
        }

        // Step 5: gRPC GetState (simplest RPC)
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_timeouts.DiagnosticsGrpcTimeoutSeconds));
            var client = endpoint.GetClient();
            var state = await client.GetStateAsync(new Empty(), cancellationToken: cts.Token);
            steps.Add(new("gRPC GetState", true, $"Agent state: {state.State}"));
        }
        catch (RpcException ex)
        {
            steps.Add(new("gRPC GetState", false,
                $"gRPC error [{ex.StatusCode}]: {ex.Status.Detail}"));
            return steps;
        }
        catch (Exception ex)
        {
            steps.Add(new("gRPC GetState", false, $"Failed: {ex.Message}"));
            return steps;
        }

        // Step 6: gRPC GetAgentSnapshot (full snapshot with metrics)
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_timeouts.DiagnosticsGrpcTimeoutSeconds));
            var client = endpoint.GetClient();
            var snapshot = await client.GetAgentSnapshotAsync(new Empty(), cancellationToken: cts.Token);
            var metricsStr = snapshot.Metrics is not null
                ? $"CPU: {snapshot.Metrics.CpuUsagePct:F0}% | Mem: {snapshot.Metrics.MemoryUsedMb:F0}MB | Disk: {snapshot.Metrics.DiskFreeGb:F1}GB"
                : "No metrics";
            steps.Add(new("gRPC Snapshot", true,
                $"State: {snapshot.State} | {metricsStr}"));
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
        {
            steps.Add(new("gRPC Snapshot", false, "GetAgentSnapshot not implemented (legacy agent)"));
        }
        catch (Exception ex)
        {
            steps.Add(new("gRPC Snapshot", false, $"Failed: {ex.Message}"));
        }

        return steps;
    }

    /// <summary>
    /// Quick ping — just checks if GetState responds within 3 seconds.
    /// </summary>
    public async Task<bool> PingAsync(string agentName, CancellationToken ct = default)
    {
        if (!_agents.TryGetValue(agentName, out var endpoint))
            return false;

        // Don't ping agents with active streaming commands
        if (_activeExecutions.ContainsKey(agentName))
            return true; // Known alive — it's executing

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_timeouts.PingTimeoutSeconds));
            var client = endpoint.GetClient();
            await client.GetStateAsync(new Empty(), cancellationToken: cts.Token);
            return true;
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Ping failed for {Agent}", agentName); return false; }
    }

    /// <summary>Gets the address of a registered agent.</summary>
    public string? GetAgentAddress(string agentName)
        => _agents.TryGetValue(agentName, out var ep) ? ep.Address : null;

    /// <summary>
    /// Progress callback for long-running remote actions: logs and surfaces a
    /// status update. Shared by the initial stream and the busy-retry stream.
    /// </summary>
    private void OnLongRunningProgress(string agent, string command, TimeSpan elapsed)
    {
        _logger.LogInformation(
            "Long-running action on {Agent}: {Command} running for {Elapsed}",
            agent, SecurityRedactor.Redact(command), elapsed.ToString(@"hh\:mm\:ss"));
        StatusChanged?.Invoke(agent,
            $"Running: {SecurityRedactor.Redact(command)} ({elapsed:hh\\:mm\\:ss})");
    }

    /// <summary>
    /// Streams a single remote command attempt, enforcing the action's Timeout
    /// (in SECONDS; 0 = no limit) via a CancellationTokenSource linked to <paramref name="ct"/>.
    /// </summary>
    private async Task<RemoteCommandStreamResult> StreamWithTimeoutAsync(
        TestAgentService.TestAgentServiceClient client,
        string agentName,
        ActionConfig resolved,
        CancellationToken ct,
        Action<string, string, TimeSpan>? onProgressTick,
        string? correlationId)
    {
        CancellationTokenSource? timeoutCts = resolved.Timeout > 0
            ? new CancellationTokenSource(TimeSpan.FromSeconds(resolved.Timeout))
            : null;
        using var _timeoutCtsDisposable = timeoutCts;
        using var linked = timeoutCts is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(ct);

        return await RemoteCommandStreamRunner.StreamAsync(
            client, agentName, resolved, linked.Token,
            outputReceived: (a, l, k) => OutputReceived?.Invoke(a, l, k),
            onProgressTick: onProgressTick,
            correlationId: correlationId);
    }

    /// <summary>
    /// Runs a single command attempt inside the endpoint's resilience pipeline,
    /// re-resolving the client per Polly attempt. Used by the recovery and
    /// post-reboot retry paths (no progress ticks on these retries).
    /// </summary>
    private async Task<RemoteCommandStreamResult> RunResilientStreamAsync(
        AgentEndpoint endpoint,
        string agentName,
        ActionConfig resolved,
        string? correlationId,
        CancellationToken ct)
        => await endpoint.Resilience.ExecuteAsync(
            async resilienceCt => await StreamWithTimeoutAsync(
                endpoint.GetClient(), agentName, resolved, resilienceCt,
                onProgressTick: null, correlationId: correlationId),
            ct);

    /// <summary>
    /// Executes a RunRemoteCommand on the specified agent.
    /// Streams stdout/stderr events back in real-time.
    /// Handles reboot actions (waits for agent to come back).
    /// Supports long-running actions (Timeout=0 means no limit).
    /// </summary>
    public async Task<ActionResult> ExecuteRemoteCommandAsync(
        ActionConfig action, PipelineExecutionContext ctx, CancellationToken ct)
    {
        var resolved = ParameterResolver.ResolveAction(action, ctx);
        var agentName = resolved.AgentName.Trim();
        var correlationId = ctx.SessionId ?? Guid.NewGuid().ToString("N")[..8];
        var cmdShort = SecurityRedactor.RedactCommandLine(resolved.Command, resolved.Parameters).Trim();
        if (cmdShort.Length > 80) cmdShort = cmdShort[..80] + "…";

        if (!_agents.TryGetValue(agentName, out var endpoint))
        {
            var msg = $"Agent '{agentName}' not registered";
            _logger.LogWarning(msg);
            _appLogger.Warn("Dispatch", $"[{agentName}] {msg}");
            return new ActionResult(false, -1, msg);
        }

        _appLogger.Log(LogLevel.Information, "Dispatch",
            $"[{agentName}] → {cmdShort} (timeout={resolved.Timeout}s, isReboot={resolved.IsReboot})",
            correlationId);
        StatusChanged?.Invoke(agentName, $"Executing: {SecurityRedactor.Redact(resolved.Command)}");
        var startTimestamp = Stopwatch.GetTimestamp();

        // Wait for agent to become free if it's still cleaning up from a previous command.
        // This prevents "Agent is busy" errors when the pipeline sends commands in sequence
        // and the previous command's process tree hasn't fully exited yet.
        await WaitForAgentFree(endpoint.GetClient(), agentName, ct);

        // Mark this agent as actively executing so Monitor polling is suppressed.
        // This prevents concurrent HTTP/2 streams from interfering with the command stream.
        _activeExecutions[agentName] = cmdShort;
        try
        {
            // Reboot/recovery commands bypass the circuit breaker. The entire purpose
            // of a reboot is to recover from the failed state that opened the breaker.
            // Without this bypass, the dispatcher blocks its own recovery path.
            if (resolved.IsReboot)
            {
                return await ExecuteRebootBypassingCircuitBreakerAsync(
                    endpoint, agentName, resolved, cmdShort, correlationId, startTimestamp, ct);
            }

            return await endpoint.Resilience.ExecuteAsync(async resilienceCt =>
            {
                // Re-read endpoint on each Polly attempt: if a channel reset replaced
                // the endpoint between retries, we must use the fresh (non-disposed) one.
                var currentEndpoint = _agents.TryGetValue(agentName, out var ep) ? ep : endpoint;
                var client = currentEndpoint.GetClient();

                // Timeout is in SECONDS in the WatchList XML. 0 = no limit.
                CancellationTokenSource? timeoutCts = resolved.Timeout > 0
                    ? new CancellationTokenSource(TimeSpan.FromSeconds(resolved.Timeout))
                    : null;
                using var _timeoutCtsDisposable = timeoutCts;
                using var linked = timeoutCts is not null
                    ? CancellationTokenSource.CreateLinkedTokenSource(resilienceCt, timeoutCts.Token)
                    : CancellationTokenSource.CreateLinkedTokenSource(resilienceCt);

                // Phase 2.15 spike: streaming + result synthesis is shared with
                // the WebApi dispatcher. We keep the resilience wrapper, the
                // long-running progress log (WPF-only), reboot wait, exit-code
                // classification, and health tracking outside the helper.
                var streamResult = await RemoteCommandStreamRunner.StreamAsync(
                    client, agentName, resolved, linked.Token,
                    outputReceived: (a, l, k) => OutputReceived?.Invoke(a, l, k),
                    onProgressTick: OnLongRunningProgress,
                    correlationId: correlationId);

                // If agent rejected the command because it's still busy (e.g. draining
                // stdout from a long install), wait and retry up to configured recovery time.
                if (streamResult.ExitCode == -1 &&
                    streamResult.ErrorMessage.Contains("busy", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "Agent {Agent} rejected command (busy). Waiting for agent to become free before retrying...",
                        agentName);
                    _appLogger.Warn("Dispatch",
                        $"[{agentName}] BUSY — agent rejected '{cmdShort}'. Starting {_timeouts.BusyRecoveryMaxSeconds}s recovery wait...");
                    StatusChanged?.Invoke(agentName, "Agent busy \u2014 waiting to retry...");

                    // Wait up to configured time in configured intervals for the agent to become Ready
                    var maxAttempts = _timeouts.BusyRecoveryMaxSeconds / _timeouts.BusyRecoveryIntervalSeconds;
                    bool becameFree = false;
                    for (int retryWait = 0; retryWait < maxAttempts; retryWait++)
                    {
                        await Task.Delay(_timeouts.BusyRecoveryIntervalSeconds * 1000, resilienceCt);
                        try
                        {
                            var state = await client.GetStateAsync(new Empty(),
                                deadline: DateTime.UtcNow.AddSeconds(_timeouts.TestConnectionTimeoutSeconds),
                                cancellationToken: resilienceCt);
                            if (state.State != AgentState.Running)
                            {
                                _appLogger.Info("Dispatch",
                                    $"[{agentName}] Agent became free after {(retryWait + 1) * _timeouts.BusyRecoveryIntervalSeconds}s — retrying command");
                                StatusChanged?.Invoke(agentName, "Agent free \u2014 retrying command");
                                becameFree = true;
                                break;
                            }
                            StatusChanged?.Invoke(agentName,
                                $"Agent still busy \u2014 waiting ({retryWait + 1}/{maxAttempts})");
                        }
                        catch (RpcException) { becameFree = true; break; } // agent unreachable, proceed to retry
                    }

                    // If the agent is still busy after recovery time, it's likely permanently
                    // stuck. Force-reset it before retrying.
                    if (!becameFree)
                    {
                        _logger.LogWarning(
                            "Agent {Agent} still busy after {Secs}s wait — calling ForceReady to recover stuck state",
                            agentName, _timeouts.BusyRecoveryMaxSeconds);
                        _appLogger.Warn("Dispatch",
                            $"[{agentName}] STUCK — still busy after {_timeouts.BusyRecoveryMaxSeconds}s. Calling ForceReady to recover...");
                        try
                        {
                            await client.ForceReadyAsync(new Empty(),
                                deadline: DateTime.UtcNow.AddSeconds(5),
                                cancellationToken: resilienceCt);
                            await Task.Delay(1_000, resilienceCt); // brief settle
                            _appLogger.Info("Dispatch",
                                $"[{agentName}] ForceReady succeeded — agent state reset");
                        }
                        catch (RpcException frEx) when (frEx.StatusCode == StatusCode.Unimplemented)
                        {
                            // Agent is running older code without ForceReady.
                            // Fallback: try TerminateExecution which kills the process
                            // and lets the agent's normal cleanup restore Ready state.
                            _appLogger.Warn("Dispatch",
                                $"[{agentName}] ForceReady not available (old agent). Trying TerminateExecution fallback...");
                            try
                            {
                                await client.TerminateExecutionAsync(new Empty(),
                                    deadline: DateTime.UtcNow.AddSeconds(5),
                                    cancellationToken: resilienceCt);

                                // Agent's TerminateExecution has an internal 5s delay before
                                // force-resetting state. Wait longer than that, then verify.
                                await Task.Delay(6_000, resilienceCt);

                                // Verify the agent actually transitioned out of Running
                                try
                                {
                                    var verifyState = await client.GetStateAsync(new Empty(),
                                        deadline: DateTime.UtcNow.AddSeconds(5),
                                        cancellationToken: resilienceCt);
                                    if (verifyState.State == AgentState.Running)
                                    {
                                        _appLogger.Warn("Dispatch",
                                            $"[{agentName}] Agent still Running after TerminateExecution + 6s — state reset may have failed");
                                    }
                                    else
                                    {
                                        _appLogger.Info("Dispatch",
                                            $"[{agentName}] TerminateExecution succeeded — agent state: {verifyState.State}");
                                    }
                                }
                                catch (RpcException)
                                {
                                    // Agent unreachable after terminate — may have crashed, proceed with retry anyway
                                    _appLogger.Warn("Dispatch",
                                        $"[{agentName}] Agent unreachable after TerminateExecution — proceeding with retry");
                                }
                            }
                            catch (Exception termEx)
                            {
                                _appLogger.Error("Dispatch",
                                    $"[{agentName}] TerminateExecution also FAILED: {termEx.Message}. " +
                                    "Agent service may need manual restart.", termEx);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "ForceReady call failed for {Agent}", agentName);
                            _appLogger.Error("Dispatch",
                                $"[{agentName}] ForceReady FAILED: {ex.Message}", ex);
                        }
                    }

                    // Retry the command
                    _appLogger.Info("Dispatch",
                        $"[{agentName}] Retrying: {cmdShort}");
                    streamResult = await RemoteCommandStreamRunner.StreamAsync(
                        client, agentName, resolved, linked.Token,
                        outputReceived: (a, l, k) => OutputReceived?.Invoke(a, l, k),
                        onProgressTick: OnLongRunningProgress);
                }

                // Reboot handling: wait for agent to come back.
                // Only wait if the command was actually accepted (not rejected as busy).
                if (resolved.IsReboot && streamResult.ExitCode != -1)
                {
                    _appLogger.Info("Dispatch",
                        $"[{agentName}] REBOOT initiated — waiting up to 5 min for agent to come back online");
                    StatusChanged?.Invoke(agentName, "Rebooting\u2026 waiting for agent");
                    await WaitForAgentReady(client, agentName, TimeSpan.FromMinutes(5), resilienceCt);
                    _appLogger.Info("Dispatch",
                        $"[{agentName}] REBOOT complete — agent back online");
                }

                var exitCode = streamResult.ExitCode;
                var errorMessage = streamResult.ErrorMessage;

                var exitDetail = ClassifyExitCode(exitCode, resolved.Command, errorMessage);
                if (!string.IsNullOrEmpty(exitDetail))
                    errorMessage = $"{exitDetail} {errorMessage}";

                var success = exitCode == 0 && string.IsNullOrEmpty(errorMessage);
                var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
                var statusMsg = success ? "Ready" : $"Failed (exit {exitCode}): {Truncate(errorMessage, 100)}";
                StatusChanged?.Invoke(agentName, statusMsg);

                if (success)
                {
                    _appLogger.LogStructured(LogLevel.Information, "Dispatch",
                        $"[{agentName}] ✓ OK: {cmdShort}",
                        agent: agentName, runId: correlationId,
                        elapsedMs: (long)elapsed.TotalMilliseconds);
                }
                else
                {
                    _appLogger.LogStructured(LogLevel.Error, "Dispatch",
                        $"[{agentName}] ✗ FAILED (exit {exitCode}): {Truncate(errorMessage, 150)}",
                        agent: agentName, runId: correlationId,
                        elapsedMs: (long)elapsed.TotalMilliseconds);
                }

                RecordSuccess(agentName);
                return new ActionResult(success, exitCode, errorMessage);
            }, ct);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            if (ct.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Action cancelled by user on {Agent} after {Elapsed}: {Command}",
                    agentName, elapsed.ToString(@"hh\:mm\:ss"), SecurityRedactor.Redact(resolved.Command));
                _appLogger.Warn("Dispatch",
                    $"[{agentName}] Cancelled by user after {elapsed:hh\\:mm\\:ss}: {cmdShort}");
                return new ActionResult(false, -1,
                    $"Cancelled by user after {elapsed:hh\\:mm\\:ss}");
            }
            // Cancellation not from the user token — likely a timeout CTS
            RecordFailure(agentName);
            _appLogger.Error("Dispatch",
                $"[{agentName}] gRPC call cancelled after {elapsed:hh\\:mm\\:ss}: {cmdShort}");
            return new ActionResult(false, -1,
                $"gRPC call cancelled after {elapsed:hh\\:mm\\:ss}");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            RecordFailure(agentName);
            if (resolved.IsReboot)
            {
                // Agent gRPC service was already down — use out-of-band reboot
                _appLogger.Warn("Dispatch",
                    $"[{agentName}] Agent gRPC service unreachable during reboot — attempting out-of-band remote shutdown");
                StatusChanged?.Invoke(agentName, "gRPC down \u2014 attempting remote reboot\u2026");

                var oobSuccess = await TryOutOfBandRebootAsync(agentName, resolved, ct);
                if (oobSuccess)
                {
                    _appLogger.Info("Dispatch",
                        $"[{agentName}] Out-of-band remote shutdown issued — waiting for agent to come back online");
                    StatusChanged?.Invoke(agentName, "Rebooting (out-of-band)\u2026 waiting for agent");
                    var client = endpoint.GetClient();
                    await WaitForAgentReady(client, agentName, TimeSpan.FromMinutes(5), ct);
                    _appLogger.Info("Dispatch",
                        $"[{agentName}] REBOOT complete — agent back online (out-of-band recovery)");
                    RecordSuccess(agentName);
                    return new ActionResult(true, 0, "Reboot completed (out-of-band)");
                }

                RecordFailure(agentName);
                return new ActionResult(false, -1,
                    $"Agent {agentName} unreachable: gRPC service down and out-of-band reboot failed. Manual intervention required.");
            }

            // Wait for agent recovery before giving up
            _appLogger.Warn("Dispatch",
                $"[{agentName}] UNAVAILABLE: {ex.Status.Detail} — waiting up to {_timeouts.UnavailableRecoverySeconds}s for recovery");
            StatusChanged?.Invoke(agentName, "Unavailable \u2014 waiting for recovery\u2026");

            var recovered = await WaitForAgentRecoveryAsync(
                agentName,
                TimeSpan.FromSeconds(_timeouts.UnavailableRecoverySeconds),
                ct);

            if (recovered)
            {
                _appLogger.Info("Dispatch",
                    $"[{agentName}] Agent recovered — retrying command: {cmdShort}");
                StatusChanged?.Invoke(agentName, "Recovered \u2014 retrying command");

                try
                {
                    var retryResult = await RunResilientStreamAsync(
                        endpoint, agentName, resolved, correlationId, ct);

                    var retryExitCode = retryResult.ExitCode;
                    var retryError = retryResult.ErrorMessage;
                    var retrySuccess = retryExitCode == 0 && string.IsNullOrEmpty(retryError);

                    RecordSuccess(agentName);

                    var retryElapsed = Stopwatch.GetElapsedTime(startTimestamp);
                    var retryStatusMsg = retrySuccess
                        ? "Ready"
                        : $"Failed (exit {retryExitCode}): {Truncate(retryError, 100)}";
                    StatusChanged?.Invoke(agentName, retryStatusMsg);

                    if (retrySuccess)
                        _appLogger.Info("Dispatch",
                            $"[{agentName}] \u2713 Recovery retry OK: {cmdShort} (total elapsed {retryElapsed:hh\\:mm\\:ss})");
                    else
                        _appLogger.Error("Dispatch",
                            $"[{agentName}] \u2717 Recovery retry FAILED (exit {retryExitCode}): {Truncate(retryError, 150)}");

                    return new ActionResult(retrySuccess, retryExitCode, retryError);
                }
                catch (BrokenCircuitException)
                {
                    _appLogger.Warn("Dispatch",
                        $"[{agentName}] Circuit breaker re-opened during recovery retry");
                    return new ActionResult(false, -1,
                        $"Agent {agentName} circuit breaker re-opened during recovery retry");
                }
                catch (Exception retryEx)
                {
                    RecordFailure(agentName);
                    _appLogger.Error("Dispatch",
                        $"[{agentName}] Recovery retry FAILED: {retryEx.Message}");
                    return new ActionResult(false, -1,
                        $"Agent {agentName} recovery retry failed: {retryEx.Message}");
                }
            }

            // Recovery wait expired — agent did not come back within normal timeout.
            // If AutoRebootOnUnavailable is enabled, attempt an out-of-band reboot
            // and retry the command. This handles cases where the agent service crashed
            // (e.g., after Install Build replaced dependencies) and needs a full machine
            // reboot to recover.
            if (_timeouts.AutoRebootOnUnavailable && !resolved.IsReboot)
            {
                _appLogger.Warn("Dispatch",
                    $"[{agentName}] Agent did not recover within {_timeouts.UnavailableRecoverySeconds}s. " +
                    "Attempting out-of-band reboot to recover agent...");
                StatusChanged?.Invoke(agentName, "Agent down \u2014 auto-rebooting machine\u2026");

                var oobSuccess = await TryOutOfBandRebootAsync(agentName, resolved, ct);
                if (oobSuccess)
                {
                    _appLogger.Info("Dispatch",
                        $"[{agentName}] Out-of-band reboot issued — waiting up to {_timeouts.AutoRebootRecoverySeconds}s for agent to come back");
                    StatusChanged?.Invoke(agentName, "Rebooting\u2026 waiting for agent service");

                    var client = endpoint.GetClient();
                    // Wait for machine to reboot and agent service to start
                    using var rebootCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    rebootCts.CancelAfter(TimeSpan.FromSeconds(_timeouts.AutoRebootRecoverySeconds));

                    bool agentRecovered = false;
                    while (!rebootCts.Token.IsCancellationRequested)
                    {
                        try { await Task.Delay(10_000, rebootCts.Token); } catch (OperationCanceledException) { break; }
                        try
                        {
                            // Re-read endpoint in case channel was reset during reboot
                            var currentEndpoint = _agents.TryGetValue(agentName, out var ep) ? ep : endpoint;
                            var pingClient = currentEndpoint.GetClient();
                            await pingClient.GetStateAsync(new Empty(),
                                deadline: DateTime.UtcNow.AddSeconds(_timeouts.PingTimeoutSeconds),
                                cancellationToken: rebootCts.Token);
                            agentRecovered = true;
                            break;
                        }
                        catch (RpcException) { /* still rebooting */ }
                        catch (OperationCanceledException) { break; }
                    }

                    if (agentRecovered)
                    {
                        _appLogger.Info("Dispatch",
                            $"[{agentName}] Agent back online after reboot — retrying command: {cmdShort}");
                        StatusChanged?.Invoke(agentName, "Agent recovered \u2014 retrying command");
                        RecordSuccess(agentName);

                        try
                        {
                            var retryEndpoint = _agents.TryGetValue(agentName, out var ep2) ? ep2 : endpoint;
                            var retryResult = await RunResilientStreamAsync(
                                retryEndpoint, agentName, resolved, correlationId, ct);

                            var retryExitCode = retryResult.ExitCode;
                            var retryError = retryResult.ErrorMessage;
                            var retrySuccess = retryExitCode == 0 && string.IsNullOrEmpty(retryError);

                            RecordSuccess(agentName);

                            var retryElapsed = Stopwatch.GetElapsedTime(startTimestamp);
                            var retryStatusMsg = retrySuccess
                                ? "Ready"
                                : $"Failed (exit {retryExitCode}): {Truncate(retryError, 100)}";
                            StatusChanged?.Invoke(agentName, retryStatusMsg);

                            if (retrySuccess)
                                _appLogger.Info("Dispatch",
                                    $"[{agentName}] \u2713 Post-reboot retry OK: {cmdShort} (total elapsed {retryElapsed:hh\\:mm\\:ss})");
                            else
                                _appLogger.Error("Dispatch",
                                    $"[{agentName}] \u2717 Post-reboot retry FAILED (exit {retryExitCode}): {Truncate(retryError, 150)}");

                            return new ActionResult(retrySuccess, retryExitCode, retryError);
                        }
                        catch (Exception retryEx)
                        {
                            RecordFailure(agentName);
                            _appLogger.Error("Dispatch",
                                $"[{agentName}] Post-reboot retry EXCEPTION: {retryEx.Message}");
                            return new ActionResult(false, -1,
                                $"Agent {agentName} post-reboot retry failed: {retryEx.Message}");
                        }
                    }

                    // Reboot was issued but agent didn't come back
                    _appLogger.Error("Dispatch",
                        $"[{agentName}] Agent did not recover after out-of-band reboot (waited {_timeouts.AutoRebootRecoverySeconds}s). " +
                        "Machine may have failed to reboot or agent service is permanently broken.");
                }
                else
                {
                    _appLogger.Error("Dispatch",
                        $"[{agentName}] Out-of-band reboot also FAILED — machine may be powered off or network-isolated.");
                }
            }

            _appLogger.Error("Dispatch",
                $"[{agentName}] UNAVAILABLE: Agent did not recover within {_timeouts.UnavailableRecoverySeconds}s. {ex.Status.Detail}");
            return new ActionResult(false, -1,
                $"Agent {agentName} unavailable: {ex.Status.Detail} (waited {_timeouts.UnavailableRecoverySeconds}s for recovery)");
        }
        catch (BrokenCircuitException)
        {
            // Don't call RecordFailure here — no real call was made, the circuit
            // is already open.  Inflating ConsecutiveFailures would prevent recovery
            // once the break duration expires.
            _logger.LogWarning(
                "Agent {Agent} circuit breaker is open — skipping call, will retry after break duration",
                agentName);
            _appLogger.Warn("Dispatch",
                $"[{agentName}] CIRCUIT BREAKER open — skipping '{cmdShort}'");
            return new ActionResult(false, -1,
                $"Agent {agentName} circuit breaker is open — too many recent failures");
        }
        catch (TimeoutRejectedException)
        {
            RecordFailure(agentName);
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            _logger.LogError(
                "Agent {Agent} hit Polly outer timeout for: {Command}",
                agentName, SecurityRedactor.Redact(resolved.Command));
            _appLogger.Error("Dispatch",
                $"[{agentName}] HARD TIMEOUT (24h safety net) for '{cmdShort}' after {elapsed:hh\\:mm\\:ss}");
            return new ActionResult(false, -1,
                $"Agent {agentName} hard timeout exceeded (24-hour resilience safety net)");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            _logger.LogWarning(
                "Action cancelled by user on {Agent} after {Elapsed}: {Command}",
                agentName, elapsed.ToString(@"hh\:mm\:ss"), SecurityRedactor.Redact(resolved.Command));
            _appLogger.Warn("Dispatch",
                $"[{agentName}] Cancelled by user after {elapsed:hh\\:mm\\:ss}: {cmdShort}");
            return new ActionResult(false, -1,
                $"Cancelled by user after {elapsed:hh\\:mm\\:ss}");
        }
        catch (OperationCanceledException ex)
        {
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            RecordFailure(agentName);
            _logger.LogError(
                "Action TIMED OUT on {Agent} after {Elapsed}: {Command}. " +
                "Configured timeout: {Timeout}s. Exception: {Error}",
                agentName, elapsed.ToString(@"hh\:mm\:ss"),
                SecurityRedactor.Redact(resolved.Command), resolved.Timeout, SecurityRedactor.Redact(ex.Message));

            var detail = resolved.Timeout > 0
                ? $"Timed out after {elapsed:hh\\:mm\\:ss} (limit: {resolved.Timeout}s). "
                : $"Timed out after {elapsed:hh\\:mm\\:ss} (no explicit timeout - check Kestrel/IIS limits). ";

            if (elapsed.TotalSeconds >= 3590 && elapsed.TotalSeconds <= 3610)
                detail += "HINT: Exactly 1 hour suggests IIS requestTimeout or Action Timeout=3600.";

            _appLogger.Error("Dispatch",
                $"[{agentName}] TIMED OUT after {elapsed:hh\\:mm\\:ss}: {cmdShort}");
            return new ActionResult(false, -1, detail);
        }
        catch (Exception ex)
        {
            RecordFailure(agentName);
            _appLogger.Error("Dispatch",
                $"[{agentName}] EXCEPTION: {ex.Message}", ex);
            return new ActionResult(false, -1, ex.Message);
        }
        finally
        {
            _activeExecutions.TryRemove(agentName, out _);
        }
    }

    /// <summary>
    /// Dispatches a reboot command directly, bypassing the Polly resilience pipeline
    /// (retry + circuit breaker). Recovery commands must not be blocked by the circuit
    /// breaker — the whole point of a reboot is to recover from the failure state that
    /// opened the breaker.
    /// </summary>
    private async Task<ActionResult> ExecuteRebootBypassingCircuitBreakerAsync(
        AgentEndpoint endpoint,
        string agentName,
        ActionConfig resolved,
        string cmdShort,
        string correlationId,
        long startTimestamp,
        CancellationToken ct)
    {
        _appLogger.Info("Dispatch",
            $"[{agentName}] REBOOT command — bypassing circuit breaker");

        try
        {
            var client = endpoint.GetClient();

            var streamResult = await StreamWithTimeoutAsync(
                client, agentName, resolved, ct,
                onProgressTick: null, correlationId: correlationId);

            // Reboot accepted — wait for agent to come back online
            if (streamResult.ExitCode != -1)
            {
                _appLogger.Info("Dispatch",
                    $"[{agentName}] REBOOT initiated — waiting up to 5 min for agent to come back online");
                StatusChanged?.Invoke(agentName, "Rebooting\u2026 waiting for agent");
                await WaitForAgentReady(client, agentName, TimeSpan.FromMinutes(5), ct);
                _appLogger.Info("Dispatch",
                    $"[{agentName}] REBOOT complete — agent back online");
            }

            var exitCode = streamResult.ExitCode;
            var errorMessage = streamResult.ErrorMessage;
            var success = exitCode == 0 && string.IsNullOrEmpty(errorMessage);

            var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            StatusChanged?.Invoke(agentName, success ? "Ready" : $"Failed (exit {exitCode}): {Truncate(errorMessage, 100)}");

            if (success)
                _appLogger.LogStructured(LogLevel.Information, "Dispatch",
                    $"[{agentName}] \u2713 REBOOT OK: {cmdShort}",
                    agent: agentName, runId: correlationId,
                    elapsedMs: (long)elapsed.TotalMilliseconds);
            else
                _appLogger.LogStructured(LogLevel.Error, "Dispatch",
                    $"[{agentName}] \u2717 REBOOT FAILED (exit {exitCode}): {Truncate(errorMessage, 150)}",
                    agent: agentName, runId: correlationId,
                    elapsedMs: (long)elapsed.TotalMilliseconds);

            RecordSuccess(agentName);
            return new ActionResult(success, exitCode, errorMessage);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            // Agent is ALREADY unreachable — the reboot command was never delivered via gRPC.
            // Use out-of-band Windows remote shutdown to actually reboot the machine.
            _appLogger.Warn("Dispatch",
                $"[{agentName}] Agent gRPC service unreachable — reboot command was NOT delivered. " +
                "Attempting out-of-band remote shutdown via Windows RPC...");
            StatusChanged?.Invoke(agentName, "gRPC down \u2014 attempting remote reboot\u2026");

            var oobSuccess = await TryOutOfBandRebootAsync(agentName, resolved, ct);
            if (oobSuccess)
            {
                _appLogger.Info("Dispatch",
                    $"[{agentName}] Out-of-band remote shutdown issued — waiting for agent to come back online");
                StatusChanged?.Invoke(agentName, "Rebooting (out-of-band)\u2026 waiting for agent");
                var client = endpoint.GetClient();
                await WaitForAgentReady(client, agentName, TimeSpan.FromMinutes(5), ct);
                _appLogger.Info("Dispatch",
                    $"[{agentName}] REBOOT complete — agent back online (out-of-band recovery)");
                RecordSuccess(agentName);
                return new ActionResult(true, 0, "Reboot completed (out-of-band)");
            }

            // Out-of-band reboot also failed — agent machine may be completely unreachable
            _appLogger.Error("Dispatch",
                $"[{agentName}] Out-of-band reboot FAILED — agent machine appears completely unreachable. " +
                "Manual intervention required.");
            RecordFailure(agentName);
            return new ActionResult(false, -1,
                $"Agent {agentName} unreachable: gRPC service down and out-of-band reboot failed. " +
                "The machine may be powered off or network-isolated. Manual intervention required.");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested)
        {
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            _appLogger.Warn("Dispatch",
                $"[{agentName}] Reboot cancelled by user after {elapsed:hh\\:mm\\:ss}");
            return new ActionResult(false, -1, $"Cancelled by user after {elapsed:hh\\:mm\\:ss}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            _appLogger.Warn("Dispatch",
                $"[{agentName}] Reboot cancelled by user after {elapsed:hh\\:mm\\:ss}");
            return new ActionResult(false, -1, $"Cancelled by user after {elapsed:hh\\:mm\\:ss}");
        }
        catch (Exception ex)
        {
            RecordFailure(agentName);
            _appLogger.Error("Dispatch",
                $"[{agentName}] REBOOT EXCEPTION: {ex.Message}", ex);
            return new ActionResult(false, -1, $"Reboot failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Executes a local RunCommand on the controller machine.
    /// Automatically wraps .bat/.cmd via cmd.exe and .ps1 via powershell.exe.
    /// Supports CompletionCheckCommand for child process monitoring after main process exits.
    /// </summary>
    public async Task<ActionResult> ExecuteLocalCommandAsync(
        ActionConfig action, PipelineExecutionContext ctx, CancellationToken ct)
    {
        var resolved = ParameterResolver.ResolveAction(action, ctx);

        try
        {
            var (fileName, arguments) = ResolveInterpreter(resolved.Command, resolved.Parameters);

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = !string.IsNullOrEmpty(ctx.WatchItemPath)
                    ? ctx.WatchItemPath : Environment.CurrentDirectory,
            };

            // Pass credentials if provided (for local runas scenarios, log only)
            if (!string.IsNullOrEmpty(resolved.UserName))
                _logger.LogInformation("Local exec with credential hint: {User}", resolved.UserName);

            // Unblock files from network paths (prevents "Open File Security Warning")
            if (fileName.StartsWith(@"\\") || arguments.Contains(@"\\"))
            {
                try
                {
                    var zoneFile = fileName + ":Zone.Identifier";
                    if (File.Exists(zoneFile))
                    {
                        File.Delete(zoneFile);
                        _logger.LogInformation("Unblocked security zone on {File}", fileName);
                    }
                }
                catch { /* best-effort — don't fail the action for this */ }
            }

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
                return new ActionResult(false, -1, "Failed to start process");

            OutputReceived?.Invoke("Controller", $"PID {process.Id}: {SecurityRedactor.RedactCommandLine(fileName, arguments)}", "info");

            var stdoutTask = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
                    OutputReceived?.Invoke("Controller", SecurityRedactor.Redact(line) ?? string.Empty, "stdout");
            }, ct);

            var stderrTask = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync(ct) is { } line)
                    OutputReceived?.Invoke("Controller", SecurityRedactor.Redact(line) ?? string.Empty, "stderr");
            }, ct);

            await process.WaitForExitAsync(ct);
            await Task.WhenAll(stdoutTask, stderrTask);

            // Completion polling for child process monitoring
            if (!string.IsNullOrWhiteSpace(resolved.CompletionCheckCommand) && process.ExitCode == 0)
            {
                OutputReceived?.Invoke("Controller",
                    "Main process exited. Polling for child process completion...", "info");

                var pollInterval = Math.Max(resolved.CompletionPollIntervalSeconds, 5);
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(pollInterval), ct);

                    var (checkFile, checkArgs) = ResolveInterpreter(resolved.CompletionCheckCommand, "");
                    using var checkProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = checkFile,
                        Arguments = checkArgs,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true,
                    });

                    if (checkProcess is null) break;

                    var output = await checkProcess.StandardOutput.ReadToEndAsync(ct);
                    await checkProcess.WaitForExitAsync(ct);

                    OutputReceived?.Invoke("Controller", $"Completion check: {SecurityRedactor.Redact(output.Trim())}", "info");

                    if (checkProcess.ExitCode != 0 || output.Contains("DONE", StringComparison.OrdinalIgnoreCase))
                    {
                        OutputReceived?.Invoke("Controller",
                            "Child processes completed. Install finished.", "info");
                        break;
                    }
                }
            }

            if (process.ExitCode != 0)
            {
                var errDetail = ClassifyExitCode(process.ExitCode, resolved.Command, "");
                var errMsg = $"Local command failed: {SecurityRedactor.RedactCommandLine(fileName, arguments)}. {errDetail}".TrimEnd();
                OutputReceived?.Invoke("Controller", $"[FAIL] Exit code {process.ExitCode}: {errDetail}", "stderr");
                return new ActionResult(false, process.ExitCode, errMsg);
            }
            return new ActionResult(true, 0, "");
        }
        catch (Exception ex)
        {
            return new ActionResult(false, -1, ex.Message);
        }
    }

    /// <summary>
    /// Resolves the correct interpreter for script files.
    /// .bat/.cmd  → cmd.exe /c "command" args
    /// .ps1       → powershell.exe -ExecutionPolicy Bypass -NoProfile -File "command" args
    /// Everything else → used directly as FileName.
    /// </summary>
    internal static (string FileName, string Arguments) ResolveInterpreter(string command, string arguments)
    {
        var cmd = command.Trim().Trim('"');
        var ext = "";
        try { ext = System.IO.Path.GetExtension(cmd).ToLowerInvariant(); }
        catch (ArgumentException) { /* cmd contains invalid path characters — treat as raw executable */ }

        return ext switch
        {
            ".bat" or ".cmd" =>
                ("cmd.exe", $"/c \"{cmd}\" {arguments}".TrimEnd()),
            ".ps1" =>
                ("powershell.exe", $"-ExecutionPolicy Bypass -NoProfile -File \"{cmd}\" {arguments}".TrimEnd()),
            _ =>
                (command, arguments),
        };
    }

    /// <summary>
    /// Maps known exit codes to human-readable explanations.
    /// </summary>
    internal static string ClassifyExitCode(int exitCode, string command, string errorMsg)
    {
        return exitCode switch
        {
            0 => "",
            1 => "[GENERAL ERROR]",
            2 => "[FILE NOT FOUND] The command or script path may be incorrect.",
            3 => "[PATH NOT FOUND] A directory in the path does not exist.",
            5 => "[ACCESS DENIED] Insufficient permissions or file locked.",
            -1 => "[ABNORMAL EXIT]",
            -1073741510 => "[CTRL+C / KILLED] Process was terminated externally.",
            -1073741819 => "[ACCESS VIOLATION] Process crashed with memory error.",
            -532462766 => "[.NET UNHANDLED EXCEPTION] Application crashed.",
            259 => "[STILL RUNNING] Process timeout — may be waiting for user input (security dialog, UAC).",
            1603 => "[MSI INSTALL FAILED] Windows Installer reported failure. Check install logs.",
            3010 => "[REBOOT REQUIRED] Installation succeeded but requires restart.",
            _ => errorMsg.Contains("security", StringComparison.OrdinalIgnoreCase)
                ? "[SECURITY BLOCKED] File may be blocked by Windows security policy."
                : errorMsg.Contains("not recognized", StringComparison.OrdinalIgnoreCase)
                ? "[COMMAND NOT FOUND] The command or executable was not found."
                : ""
        };
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// Waits up to 30 seconds for an agent to become free (not Running).
    /// This handles the race where a previous command's process tree is still being
    /// killed when the pipeline dispatches the next sequential command.
    /// </summary>
    private async Task WaitForAgentFree(
        TestAgentService.TestAgentServiceClient client,
        string agentName, CancellationToken ct)
    {
        var intervalMs = (_timeouts.WaitForAgentFreeMaxSeconds * 1000) / 6;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                var reply = await client.GetStateAsync(new Empty(),
                    deadline: DateTime.UtcNow.AddSeconds(_timeouts.TestConnectionTimeoutSeconds),
                    cancellationToken: ct);

                if (reply.State != AgentState.Running)
                    return;

                _logger.LogWarning(
                    "Agent {Agent} still busy (attempt {N}/6). Waiting for previous command to finish...",
                    agentName, attempt + 1);
                StatusChanged?.Invoke(agentName, $"Waiting for previous command to finish ({attempt + 1}/6)");
                await Task.Delay(intervalMs, ct);
            }
            catch (RpcException) { return; } // agent unreachable — proceed, will fail on the actual call
            catch (OperationCanceledException) { throw; }
        }

        _logger.LogWarning("Agent {Agent} still busy after {Secs}s — proceeding anyway",
            agentName, _timeouts.WaitForAgentFreeMaxSeconds);
        _appLogger.Warn("Dispatch",
            $"[{agentName}] Pre-check: still busy after {_timeouts.WaitForAgentFreeMaxSeconds}s polling — proceeding with dispatch");
    }

    /// <summary>
    /// Attempts an out-of-band remote shutdown using Windows native RPC
    /// (shutdown.exe /m \\hostname). This works even when the agent's gRPC
    /// service (port 5200) is crashed, as long as the machine itself is reachable.
    /// Used as a fallback when a gRPC-based reboot command cannot be delivered.
    /// </summary>
    private async Task<bool> TryOutOfBandRebootAsync(
        string agentName, ActionConfig resolved, CancellationToken ct)
    {
        // Extract hostname from the agent's registered address (e.g., "http://jvhist:5200" → "jvhist")
        var address = GetAgentAddress(agentName);
        if (string.IsNullOrEmpty(address))
        {
            _appLogger.Error("Dispatch",
                $"[{agentName}] Cannot perform out-of-band reboot — no address registered");
            return false;
        }

        string hostname;
        if (Uri.TryCreate(address, UriKind.Absolute, out var uri))
            hostname = uri.Host;
        else
            hostname = address.Split(':')[0].Replace("http://", "").Replace("https://", "");

        // Extract delay from the original command if possible (e.g., "shutdown /r /f /t 90" → 90)
        var delay = 0;
        var cmdLower = resolved.Command?.ToLowerInvariant() ?? "";
        var tIdx = cmdLower.IndexOf("/t ");
        if (tIdx >= 0)
        {
            var afterT = cmdLower[(tIdx + 3)..].TrimStart();
            var numEnd = 0;
            while (numEnd < afterT.Length && char.IsDigit(afterT[numEnd])) numEnd++;
            if (numEnd > 0) int.TryParse(afterT[..numEnd], out delay);
        }

        _appLogger.Info("Dispatch",
            $"[{agentName}] Executing out-of-band reboot: shutdown /r /f /t {delay} /m \\\\{hostname}");

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = $"/r /f /t {delay} /m \\\\{hostname}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            process.Start();

            // Wait up to 30s for the shutdown command to complete
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeoutCts.Token);

            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);

            if (process.ExitCode == 0)
            {
                _appLogger.Info("Dispatch",
                    $"[{agentName}] Out-of-band reboot command accepted (exit 0). Machine \\\\{hostname} will restart in {delay}s.");
                return true;
            }

            // Exit code non-zero — shutdown rejected (access denied, machine unreachable, etc.)
            var detail = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim();
            _appLogger.Error("Dispatch",
                $"[{agentName}] Out-of-band reboot FAILED (exit {process.ExitCode}): {detail}");
            return false;
        }
        catch (OperationCanceledException)
        {
            _appLogger.Warn("Dispatch",
                $"[{agentName}] Out-of-band reboot command timed out or was cancelled");
            return false;
        }
        catch (Exception ex)
        {
            _appLogger.Error("Dispatch",
                $"[{agentName}] Out-of-band reboot EXCEPTION: {ex.Message}", ex);
            return false;
        }
    }

    private async Task WaitForAgentReady(
        TestAgentService.TestAgentServiceClient client,
        string agentName, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        // Phase 1: Wait for the agent to become unreachable (reboot in progress)
        // or transition to Ready. If after 30s the agent is still Running,
        // it means the reboot hasn't taken effect yet — keep waiting.
        bool sawUnreachable = false;

        while (!cts.Token.IsCancellationRequested)
        {
            await Task.Delay(10_000, cts.Token);
            try
            {
                var reply = await client.GetStateAsync(new Empty(), cancellationToken: cts.Token);
                if (reply.State == AgentState.Ready)
                {
                    // Agent is Ready — either it rebooted quickly or came back
                    StatusChanged?.Invoke(agentName, "Back online");
                    return;
                }
                // Agent still Running — only treat as "back" if we saw it go down first
                if (reply.State == AgentState.Running && sawUnreachable)
                {
                    StatusChanged?.Invoke(agentName, "Back online");
                    return;
                }
                // Still Running without having gone down — reboot hasn't kicked in yet
            }
            catch (Exception ex)
            {
                // Agent unreachable — reboot is in progress
                sawUnreachable = true;
                _logger.LogDebug(ex, "Agent {Name} still rebooting", agentName);
            }
        }
    }

    /// <summary>
    /// Waits for an unavailable agent to come back online by polling GetState.
    /// Returns true if the agent recovered within the timeout.
    /// </summary>
    private async Task<bool> WaitForAgentRecoveryAsync(
        string agentName, TimeSpan timeout, CancellationToken ct)
    {
        if (timeout <= TimeSpan.Zero)
            return false;

        if (!_agents.TryGetValue(agentName, out var endpoint))
            return false;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var pollInterval = TimeSpan.FromSeconds(_timeouts.UnavailableRecoveryPollIntervalSeconds);

        _logger.LogInformation(
            "Waiting up to {Timeout}s for agent {Agent} to recover (polling every {Interval}s)",
            (int)timeout.TotalSeconds, agentName, _timeouts.UnavailableRecoveryPollIntervalSeconds);

        while (!cts.Token.IsCancellationRequested)
        {
            try { await Task.Delay(pollInterval, cts.Token); }
            catch (OperationCanceledException) { break; }

            try
            {
                var client = endpoint.GetClient();
                await client.GetStateAsync(new Empty(),
                    deadline: DateTime.UtcNow.AddSeconds(_timeouts.PingTimeoutSeconds),
                    cancellationToken: cts.Token);

                // Agent responded — it's back online
                _logger.LogInformation("Agent {Agent} recovered after unavailability", agentName);
                RecordSuccess(agentName);
                StatusChanged?.Invoke(agentName, "Recovered");
                return true;
            }
            catch (RpcException)
            {
                // Still unavailable — continue polling
                _logger.LogDebug("Agent {Agent} still unavailable, continuing recovery wait", agentName);
            }
            catch (OperationCanceledException) { break; }
        }

        return false;
    }

    public IEnumerable<string> RegisteredAgents => _agents.Keys;
    public int RegisteredAgentCount => _agents.Count;

    /// <inheritdoc/>
    public AgentHealthState? GetAgentHealth(string agentName)
        => _healthStates.TryGetValue(agentName, out var state) ? state : null;

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, AgentHealthState> GetAllAgentHealth()
        => _healthStates.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        foreach (var ep in _agents.Values)
            ep.Dispose();
        _agents.Clear();
        _healthStates.Clear();
    }

    /// <summary>
    /// Resets the gRPC channel for a given agent by disposing the old connection and
    /// creating a new one. Blocked during active execution.
    /// Returns true if the new channel is reachable (ping succeeds after reset).
    /// </summary>
    public async Task<bool> ResetChannelAsync(string agentName)
    {
        // Never reset during active execution — would kill the command stream
        if (IsAgentExecuting(agentName))
        {
            _logger.LogWarning("Channel reset blocked for {Agent} — execution in progress", agentName);
            return false;
        }

        // Atomically claim the old endpoint. If another thread already removed it
        // (concurrent reset) or if it doesn't exist, bail out.
        if (!_agents.TryRemove(agentName, out var oldEndpoint))
        {
            _logger.LogWarning("Channel reset failed — agent {Agent} not registered or already being reset", agentName);
            return false;
        }

        var address = oldEndpoint.Address;

        // Create fresh endpoint BEFORE disposing old — minimizes window where
        // no endpoint exists for this agent.
        var newEndpoint = new AgentEndpoint(agentName, address, _timeouts);
        _agents[agentName] = newEndpoint;

        // Now safe to dispose old endpoint — it's no longer in the dictionary
        // so no new operations will use it.
        oldEndpoint.Dispose();

        // Reset health state
        if (_healthStates.TryGetValue(agentName, out var health))
        {
            health.ConsecutiveFailures = 0;
            health.IsHealthy = true;
            health.CircuitOpenedUtc = null;
            health.LastSuccessUtc = null;
        }

        _logger.LogInformation("Channel reset for agent {Agent} at {Address}", agentName, address);
        _appLogger.Log(LogLevel.Information, "Channel", $"Channel reset for {agentName}");

        // Verify the new channel works
        return await PingAsync(agentName);
    }

    // ── Health tracking helpers ────────────────────────────────────────

    private void RecordSuccess(string agentName)
    {
        if (_healthStates.TryGetValue(agentName, out var state))
        {
            state.IsHealthy = true;
            state.ConsecutiveFailures = 0;
            state.LastSuccessUtc = DateTime.UtcNow;
            state.CircuitOpenedUtc = null;
        }
    }

    private void RecordFailure(string agentName)
    {
        if (_healthStates.TryGetValue(agentName, out var state))
        {
            state.ConsecutiveFailures++;
            if (state.ConsecutiveFailures >= 3)
            {
                state.IsHealthy = false;
                state.CircuitOpenedUtc ??= DateTime.UtcNow;
                _logger.LogWarning(
                    "Agent {Agent} marked unhealthy after {Failures} consecutive failures",
                    agentName, state.ConsecutiveFailures);
            }

            // Auto-reset channel after sustained failures (likely dead TCP connection)
            // Rate-limited to max 1 reset per 30s per agent to prevent reset storms during fleet outages.
            if (state.ConsecutiveFailures == _timeouts.AutoResetFailureThreshold && !IsAgentExecuting(agentName))
            {
                var now = DateTime.UtcNow;
                if (state.LastAutoResetUtc is null || (now - state.LastAutoResetUtc.Value).TotalSeconds >= 30)
                {
                    state.LastAutoResetUtc = now;
                    _logger.LogWarning("Auto-resetting channel for {Agent} after {Failures} failures",
                        agentName, state.ConsecutiveFailures);
                    _ = Task.Run(async () =>
                    {
                        try { await ResetChannelAsync(agentName); }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Auto-reset failed for {Agent}", agentName);
                        }
                    });
                }
                else
                {
                    _logger.LogDebug("Auto-reset skipped for {Agent} — cooldown active (last reset {LastReset})",
                        agentName, state.LastAutoResetUtc);
                }
            }
        }
    }

    // ── Inner types ────────────────────────────────────────────────────

    private sealed class AgentEndpoint : IDisposable
    {
        public string Name { get; }
        public string Address { get; }
        private readonly GrpcChannel _channel;
        private readonly TestAgentService.TestAgentServiceClient _client;

        public AgentEndpoint(string name, string address, ControllerTimeoutOptions? timeouts = null)
        {
            var t = timeouts ?? new ControllerTimeoutOptions();
            Name = name;
            Address = address;
            var handler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                ConnectTimeout               = TimeSpan.FromSeconds(t.ChannelConnectTimeoutSeconds),
                // Keep gRPC streams alive during long test runs (2-4+ hours).
                // Pings every 60s prevent proxies/firewalls from killing
                // idle-looking HTTP/2 streams when no stdout is flowing.
                KeepAlivePingDelay            = TimeSpan.FromSeconds(t.KeepAlivePingDelaySeconds),
                KeepAlivePingTimeout          = TimeSpan.FromSeconds(t.KeepAlivePingTimeoutSeconds),
                KeepAlivePingPolicy           = HttpKeepAlivePingPolicy.Always,
                PooledConnectionIdleTimeout   = TimeSpan.FromMinutes(t.PooledConnectionIdleMinutes),
                // Do NOT recycle connections with a short lifetime.
                PooledConnectionLifetime      = Timeout.InfiniteTimeSpan,
            };
            // Enable TLS when address uses HTTPS
            if (address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                handler.SslOptions = new SslClientAuthenticationOptions
                {
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                };
            }
            // Force HTTP/2 for plaintext (h2c) to fix HTTP_1_1_REQUIRED errors
            // on agents running Kestrel without TLS ALPN negotiation.
            var httpClient = new HttpClient(handler, disposeHandler: true)
            {
                DefaultRequestVersion = new Version(2, 0),
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
                // gRPC streaming calls must not be killed by HttpClient's default 100s timeout.
                // Cancellation is managed via gRPC deadlines / CancellationTokens instead.
                Timeout = Timeout.InfiniteTimeSpan,
            };
            _channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
            {
                HttpClient = httpClient,
                DisposeHttpClient = true,
            });
            _client = new TestAgentService.TestAgentServiceClient(_channel);
            Resilience = new ResiliencePipelineBuilder()
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = t.RetryMaxAttempts,
                    Delay = TimeSpan.FromSeconds(t.RetryDelaySeconds),
                    BackoffType = DelayBackoffType.Exponential,
                    ShouldHandle = new PredicateBuilder().Handle<RpcException>(ex =>
                        ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded),
                })
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio = 0.5,
                    SamplingDuration = TimeSpan.FromSeconds(30),
                    MinimumThroughput = 5,
                    BreakDuration = TimeSpan.FromSeconds(t.CircuitBreakerBreakSeconds),
                })
                // Outer safety net: configurable (default 24 hours).
                // Individual action Timeout is enforced separately via CancellationTokenSource.
                .AddTimeout(TimeSpan.FromHours(t.OuterSafetyNetTimeoutHours))
                .Build();
        }

        public TestAgentService.TestAgentServiceClient GetClient() => _client;
        public void Dispose() => _channel.Dispose();

        public ResiliencePipeline Resilience { get; }
    }

    /// <summary>
    /// Ensures the address has an http:// or https:// scheme.
    /// Defaults to http:// with port 5200 when not specified.
    /// </summary>
    private static string NormalizeAddress(string address)
    {
        address = address?.Trim() ?? "";
        if (string.IsNullOrEmpty(address)) return address;

        if (!address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            address = "http://" + address;
        }

        if (Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
            uri.Port is -1 or 80 && uri.Scheme == "http")
        {
            address = $"http://{uri.Host}:5200";
        }

        return address;
    }
}
