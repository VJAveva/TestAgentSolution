using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using TestAgentGrpc;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Dispatches commands to agent nodes via gRPC.
/// Maintains a pool of channels keyed by agent name → address.
/// Supports command execution with timeout, polling, and reboot handling.
/// </summary>
public sealed class AgentGrpcDispatcher : IDisposable
{
    private readonly ILogger<AgentGrpcDispatcher> _logger;
    private readonly ConcurrentDictionary<string, AgentEndpoint> _agents = new(StringComparer.OrdinalIgnoreCase);

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
    /// </summary>
    public void RegisterAgent(string agentName, string grpcAddress)
    {
        // Dispose old endpoint if re-registering same name
        if (_agents.TryRemove(agentName, out var old))
            old.Dispose();
        _agents[agentName] = new AgentEndpoint(agentName, grpcAddress);
        _logger.LogInformation("Registered agent {Name} → {Address}", agentName, grpcAddress);
    }

    /// <summary>
    /// Removes a registered agent and disposes its gRPC channel.
    /// </summary>
    public bool UnregisterAgent(string agentName)
    {
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
                $"GET / failed (may be normal for pure gRPC): {ex.Message}"));
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

    public sealed record DiagnosticStep(string Name, bool Passed, string Detail);

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
        catch { return false; }
    }

    /// <summary>Gets the address of a registered agent.</summary>
    public string? GetAgentAddress(string agentName)
        => _agents.TryGetValue(agentName, out var ep) ? ep.Address : null;

    /// <summary>
    /// Executes a RunRemoteCommand on the specified agent.
    /// Streams stdout/stderr events back in real-time.
    /// Handles reboot actions (waits for agent to come back).
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

        try
        {
            var client = endpoint.GetClient();

            // Use streamed RPC for real-time output
            using var timeoutCts = resolved.Timeout > 0
                ? new CancellationTokenSource(TimeSpan.FromSeconds(resolved.Timeout))
                : new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            using var call = client.RunCommandStreamed(new RunCommandRequest
            {
                Command = resolved.Command,
                Arguments = resolved.Parameters,
                IsReboot = resolved.IsReboot,
                UserName = resolved.UserName ?? "",
                Password = resolved.Password ?? "",
            }, cancellationToken: linked.Token);

            int exitCode = 0;
            string errorMessage = "";

            await foreach (var evt in call.ResponseStream.ReadAllAsync(linked.Token))
            {
                switch (evt.EventType)
                {
                    case ExecutionEventType.EventStdoutLine:
                        OutputReceived?.Invoke(agentName, evt.OutputLine, "stdout");
                        break;
                    case ExecutionEventType.EventStderrLine:
                        OutputReceived?.Invoke(agentName, evt.OutputLine, "stderr");
                        break;
                    case ExecutionEventType.EventCompleted:
                        exitCode = evt.ExitCode;
                        break;
                    case ExecutionEventType.EventFailed:
                        errorMessage = evt.ErrorMessage;
                        exitCode = -1;
                        break;
                }
            }

            // Reboot handling: wait for agent to come back
            if (resolved.IsReboot)
            {
                StatusChanged?.Invoke(agentName, "Rebooting… waiting for agent");
                await WaitForAgentReady(client, agentName, TimeSpan.FromMinutes(5), ct);
            }

            var success = exitCode == 0 && string.IsNullOrEmpty(errorMessage);
            StatusChanged?.Invoke(agentName, success ? "Ready" : $"Failed (exit {exitCode})");
            return new ActionResult(success, exitCode, errorMessage);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            if (resolved.IsReboot)
            {
                StatusChanged?.Invoke(agentName, "Rebooting… waiting for agent");
                var client = endpoint.GetClient();
                await WaitForAgentReady(client, agentName, TimeSpan.FromMinutes(5), ct);
                return new ActionResult(true, 0, "Reboot completed");
            }
            return new ActionResult(false, -1, $"Agent {agentName} unavailable: {ex.Status.Detail}");
        }
        catch (OperationCanceledException)
        {
            return new ActionResult(false, -1, $"Timed out ({resolved.Timeout}s)");
        }
        catch (Exception ex)
        {
            return new ActionResult(false, -1, ex.Message);
        }
    }

    /// <summary>
    /// Executes a local RunCommand on the controller machine.
    /// Automatically wraps .bat/.cmd via cmd.exe and .ps1 via powershell.exe.
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

            return new ActionResult(process.ExitCode == 0, process.ExitCode, "");
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
        try { ext = System.IO.Path.GetExtension(cmd).ToLowerInvariant(); } catch { }

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
            catch { /* agent still down */ }
        }
    }

    public IEnumerable<string> RegisteredAgents => _agents.Keys;

    public void Dispose()
    {
        foreach (var ep in _agents.Values)
            ep.Dispose();
        _agents.Clear();
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
                    KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                    KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
                },
                DisposeHttpClient = true,
            });
            _client = new TestAgentService.TestAgentServiceClient(_channel);
        }

        public TestAgentService.TestAgentServiceClient GetClient() => _client;
        public void Dispose() => _channel.Dispose();
    }
}

public sealed record ActionResult(bool Success, int ExitCode, string ErrorMessage);
