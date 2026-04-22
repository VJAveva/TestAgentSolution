using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
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
    private readonly ConcurrentDictionary<string, AgentEndpoint> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AgentHealthState> _healthStates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when execution output arrives.</summary>
    public event Action<string, string, string>? OutputReceived;  // agentName, line, kind

    /// <summary>Raised when execution state changes.</summary>
    public event Action<string, string>? StatusChanged;  // agentName, status

    public AgentGrpcDispatcher(ILogger<AgentGrpcDispatcher> logger)
    {
        _logger = logger;
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
        _agents[agentName] = new AgentEndpoint(agentName, grpcAddress);
        _healthStates[agentName] = new AgentHealthState { AgentName = agentName };
        _logger.LogInformation("Registered agent {Name} → {Address}", agentName, grpcAddress);
    }

    /// <summary>
    /// Removes a registered agent and disposes its gRPC channel.
    /// </summary>
    public bool UnregisterAgent(string agentName)
    {
        _healthStates.TryRemove(agentName, out _);
        if (_agents.TryRemove(agentName, out var ep))
        {
            ep.Dispose();
            _logger.LogInformation("Unregistered agent: {Name}", agentName);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Tests connectivity to an agent by calling GetAgentSnapshot.
    /// Returns the snapshot on success, or null + error message on failure.
    /// </summary>
    public async Task<(AgentSnapshot? Snapshot, string? Error)> TestConnectionAsync(
        string agentName, CancellationToken ct = default)
    {
        if (!_agents.TryGetValue(agentName, out var endpoint))
            return (null, $"Agent '{agentName}' not registered");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var client = endpoint.GetClient();
            var snapshot = await client.GetAgentSnapshotAsync(
                new Empty(), cancellationToken: cts.Token);

            StatusChanged?.Invoke(agentName, $"Online — {snapshot.State}");
            return (snapshot, null);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            StatusChanged?.Invoke(agentName, "Unreachable");
            return (null, $"Agent '{agentName}' unreachable at {endpoint.Address}");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
        {
            // Agent running older version without GetAgentSnapshot — fallback to GetState
            try
            {
                using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts2.CancelAfter(TimeSpan.FromSeconds(5));
                var client = endpoint.GetClient();
                var state = await client.GetStateAsync(new Empty(), cancellationToken: cts2.Token);
                StatusChanged?.Invoke(agentName, $"Online — {state.State} (legacy)");
                return (new AgentSnapshot { AgentName = agentName, State = state.State }, null);
            }
            catch (Exception ex2)
            {
                return (null, $"Fallback GetState also failed: {ex2.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            return (null, $"Connection to '{agentName}' timed out (5s)");
        }
        catch (Exception ex)
        {
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
            tcpCts.CancelAfter(TimeSpan.FromSeconds(3));
            await tcp.ConnectAsync(uri.Host, uri.Port, tcpCts.Token);
            steps.Add(new("TCP Connect", true, $"Port {uri.Port} is open"));
        }
        catch (OperationCanceledException)
        {
            steps.Add(new("TCP Connect", false, $"Port {uri.Port} connection timed out (3s) — firewall?"));
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
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
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
            cts.CancelAfter(TimeSpan.FromSeconds(5));
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
            cts.CancelAfter(TimeSpan.FromSeconds(5));
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
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
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

        if (!_agents.TryGetValue(agentName, out var endpoint))
        {
            var msg = $"Agent '{agentName}' not registered";
            _logger.LogWarning(msg);
            return new ActionResult(false, -1, msg);
        }

        StatusChanged?.Invoke(agentName, $"Executing: {resolved.Command}");
        var startTimestamp = Stopwatch.GetTimestamp();

        try
        {
            return await endpoint.Resilience.ExecuteAsync(async resilienceCt =>
            {
                var client = endpoint.GetClient();

                // Timeout is in SECONDS in the WatchList XML. 0 = no limit.
                CancellationTokenSource? timeoutCts = resolved.Timeout > 0
                    ? new CancellationTokenSource(TimeSpan.FromSeconds(resolved.Timeout))
                    : null;
                using var _timeoutCtsDisposable = timeoutCts;
                using var linked = timeoutCts is not null
                    ? CancellationTokenSource.CreateLinkedTokenSource(resilienceCt, timeoutCts.Token)
                    : CancellationTokenSource.CreateLinkedTokenSource(resilienceCt);

                // Send timeout to agent so it can enforce server-side (already in seconds)
                var timeoutSeconds = resolved.Timeout;

                using var call = client.RunCommandStreamed(new RunCommandRequest
                {
                    Command = resolved.Command,
                    Arguments = resolved.Parameters,
                    IsReboot = resolved.IsReboot,
                    UserName = resolved.UserName ?? "",
                    Password = resolved.Password ?? "",
                    TimeoutSeconds = timeoutSeconds,
                    CompletionCheckCommand = resolved.CompletionCheckCommand ?? "",
                    CompletionPollIntervalSeconds = resolved.CompletionPollIntervalSeconds,
                    EnableInstallLog = resolved.EnableInstallLog,
                    InstallLogPollSeconds = resolved.InstallLogPollSeconds > 0 ? resolved.InstallLogPollSeconds : 5,
                    InstallLogRoot = resolved.InstallLogRoot ?? "",
                }, cancellationToken: linked.Token);

                int exitCode = 0;
                string errorMessage = "";
                var stderrLines = new List<string>();
                bool receivedCompleted = false;
                var startTimestamp = Stopwatch.GetTimestamp();
                var lastProgressLog = startTimestamp;
                var progressInterval = TimeSpan.FromMinutes(5);

                await foreach (var evt in call.ResponseStream.ReadAllAsync(linked.Token))
                {
                    switch (evt.EventType)
                    {
                        case ExecutionEventType.EventStdoutLine:
                            OutputReceived?.Invoke(agentName, evt.OutputLine, "stdout");
                            break;
                        case ExecutionEventType.EventStderrLine:
                            OutputReceived?.Invoke(agentName, evt.OutputLine, "stderr");
                            stderrLines.Add(evt.OutputLine);
                            if (stderrLines.Count > 20) stderrLines.RemoveAt(0);
                            break;
                        case ExecutionEventType.EventProgress:
                            OutputReceived?.Invoke(agentName,
                                $"Progress: {evt.ProgressPct:F0}% \u2014 {evt.Detail}", "info");
                            break;
                        case ExecutionEventType.EventCompleted:
                            exitCode = evt.ExitCode;
                            receivedCompleted = true;
                            break;
                        case ExecutionEventType.EventFailed:
                            errorMessage = evt.ErrorMessage;
                            exitCode = -1;
                            break;
                    }

                    // Periodic progress logging for long-running actions
                    var now = Stopwatch.GetTimestamp();
                    if (Stopwatch.GetElapsedTime(lastProgressLog, now) >= progressInterval)
                    {
                        var elapsed = Stopwatch.GetElapsedTime(startTimestamp, now);
                        _logger.LogInformation(
                            "Long-running action on {Agent}: {Command} running for {Elapsed}",
                            agentName, resolved.Command, elapsed.ToString(@"hh\:mm\:ss"));
                        StatusChanged?.Invoke(agentName,
                            $"Running: {resolved.Command} ({elapsed:hh\\:mm\\:ss})");
                        lastProgressLog = now;
                    }
                }

                // Reboot handling: wait for agent to come back
                if (resolved.IsReboot)
                {
                    StatusChanged?.Invoke(agentName, "Rebooting\u2026 waiting for agent");
                    await WaitForAgentReady(client, agentName, TimeSpan.FromMinutes(5), resilienceCt);
                }

                // Build comprehensive error report
                if (!receivedCompleted && string.IsNullOrEmpty(errorMessage))
                {
                    exitCode = -1;
                    errorMessage = $"Agent '{agentName}' did not report completion. " +
                        "Process may have crashed, been killed, or gRPC connection was lost.";
                }

                if (exitCode != 0 && string.IsNullOrEmpty(errorMessage))
                {
                    errorMessage = stderrLines.Count > 0
                        ? $"Process exited with code {exitCode}. Last stderr: " +
                          string.Join(" | ", stderrLines.TakeLast(5))
                        : $"Process exited with code {exitCode}. No error output captured. " +
                          "Check if the process requires user interaction (security dialogs, UAC prompts).";
                }

                var exitDetail = ClassifyExitCode(exitCode, resolved.Command, errorMessage);
                if (!string.IsNullOrEmpty(exitDetail))
                    errorMessage = $"{exitDetail} {errorMessage}";

                var success = exitCode == 0 && string.IsNullOrEmpty(errorMessage);
                var statusMsg = success ? "Ready" : $"Failed (exit {exitCode}): {Truncate(errorMessage, 100)}";
                StatusChanged?.Invoke(agentName, statusMsg);

                RecordSuccess(agentName);
                return new ActionResult(success, exitCode, errorMessage);
            }, ct);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            RecordFailure(agentName);
            if (resolved.IsReboot)
            {
                StatusChanged?.Invoke(agentName, "Rebooting\u2026 waiting for agent");
                var client = endpoint.GetClient();
                await WaitForAgentReady(client, agentName, TimeSpan.FromMinutes(5), ct);
                RecordSuccess(agentName);
                return new ActionResult(true, 0, "Reboot completed");
            }
            return new ActionResult(false, -1, $"Agent {agentName} unavailable: {ex.Status.Detail}");
        }
        catch (BrokenCircuitException)
        {
            // Don't call RecordFailure here — no real call was made, the circuit
            // is already open.  Inflating ConsecutiveFailures would prevent recovery
            // once the break duration expires.
            _logger.LogWarning(
                "Agent {Agent} circuit breaker is open — skipping call, will retry after break duration",
                agentName);
            return new ActionResult(false, -1,
                $"Agent {agentName} circuit breaker is open — too many recent failures");
        }
        catch (TimeoutRejectedException)
        {
            RecordFailure(agentName);
            _logger.LogError(
                "Agent {Agent} hit Polly outer timeout for: {Command}",
                agentName, resolved.Command);
            return new ActionResult(false, -1,
                $"Agent {agentName} hard timeout exceeded (24-hour resilience safety net)");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            _logger.LogWarning(
                "Action cancelled by user on {Agent} after {Elapsed}: {Command}",
                agentName, elapsed.ToString(@"hh\:mm\:ss"), resolved.Command);
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
                resolved.Command, resolved.Timeout, ex.Message);

            var detail = resolved.Timeout > 0
                ? $"Timed out after {elapsed:hh\\:mm\\:ss} (limit: {resolved.Timeout}s). "
                : $"Timed out after {elapsed:hh\\:mm\\:ss} (no explicit timeout - check Kestrel/IIS limits). ";

            if (elapsed.TotalSeconds >= 3590 && elapsed.TotalSeconds <= 3610)
                detail += "HINT: Exactly 1 hour suggests IIS requestTimeout or Action Timeout=3600.";

            return new ActionResult(false, -1, detail);
        }
        catch (Exception ex)
        {
            RecordFailure(agentName);
            return new ActionResult(false, -1, ex.Message);
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

            OutputReceived?.Invoke("Controller", $"PID {process.Id}: {fileName} {arguments}", "info");

            var stdoutTask = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
                    OutputReceived?.Invoke("Controller", line, "stdout");
            }, ct);

            var stderrTask = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync(ct) is { } line)
                    OutputReceived?.Invoke("Controller", line, "stderr");
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

                    OutputReceived?.Invoke("Controller", $"Completion check: {output.Trim()}", "info");

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
                var errMsg = $"Local command failed: {fileName} {arguments}. {errDetail}".TrimEnd();
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

    private async Task WaitForAgentReady(
        TestAgentService.TestAgentServiceClient client,
        string agentName, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        while (!cts.Token.IsCancellationRequested)
        {
            await Task.Delay(10_000, cts.Token);
            try
            {
                var reply = await client.GetStateAsync(new Empty(), cancellationToken: cts.Token);
                if (reply.State == AgentState.Ready || reply.State == AgentState.Running)
                {
                    StatusChanged?.Invoke(agentName, "Back online");
                    return;
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Agent {Name} still rebooting", agentName); }
        }
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
        }
    }

    // ── Inner types ────────────────────────────────────────────────────

    private sealed class AgentEndpoint : IDisposable
    {
        public string Name { get; }
        public string Address { get; }
        private readonly GrpcChannel _channel;
        private readonly TestAgentService.TestAgentServiceClient _client;

        public AgentEndpoint(string name, string address)
        {
            Name = name;
            Address = address;
            _channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler
                {
                    EnableMultipleHttp2Connections = true,
                    ConnectTimeout               = TimeSpan.FromSeconds(30),
                    // Keep gRPC streams alive during long test runs (2-4+ hours).
                    // Pings every 60s prevent proxies/firewalls from killing
                    // idle-looking HTTP/2 streams when no stdout is flowing.
                    KeepAlivePingDelay            = TimeSpan.FromSeconds(60),
                    KeepAlivePingTimeout          = TimeSpan.FromSeconds(30),
                    KeepAlivePingPolicy           = HttpKeepAlivePingPolicy.Always,
                    PooledConnectionIdleTimeout   = TimeSpan.FromMinutes(5),
                    // Do NOT recycle connections with a short lifetime.
                    PooledConnectionLifetime      = Timeout.InfiniteTimeSpan,
                },
                DisposeHttpClient = true,
            });
            _client = new TestAgentService.TestAgentServiceClient(_channel);
            Resilience = new ResiliencePipelineBuilder()
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 3,
                    Delay = TimeSpan.FromSeconds(1),
                    BackoffType = DelayBackoffType.Exponential,
                    ShouldHandle = new PredicateBuilder().Handle<RpcException>(ex =>
                        ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded),
                })
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio = 0.5,
                    SamplingDuration = TimeSpan.FromSeconds(30),
                    MinimumThroughput = 5,
                    BreakDuration = TimeSpan.FromSeconds(15),
                })
                // Outer safety net: 24 hours. Individual action Timeout (in seconds)
                // is enforced separately via CancellationTokenSource in ExecuteRemoteCommandAsync.
                // The previous 60-minute limit killed long installs (e.g., SP upgrades that take 2–3 hours).
                .AddTimeout(TimeSpan.FromHours(24))
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
