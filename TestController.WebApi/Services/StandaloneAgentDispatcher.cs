using System.Diagnostics;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using TestAgentGrpc;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Services;

/// <summary>
/// Full gRPC dispatcher for standalone WebApi deployment.
/// Uses <see cref="AgentGrpcClientManager"/> for channel management and
/// executes commands on remote agents via gRPC streaming.
/// Also handles local command execution (RunCommand) on the WebApi server machine.
/// </summary>
public sealed class StandaloneAgentDispatcher : IAgentGrpcDispatcher
{
    private readonly AgentGrpcClientManager _clientManager;
    private readonly AgentRegistry _registry;
    private readonly ILogger<StandaloneAgentDispatcher> _logger;

    public event Action<string, string, string>? OutputReceived;
    public event Action<string, string>? StatusChanged;

    public StandaloneAgentDispatcher(
        AgentGrpcClientManager clientManager,
        AgentRegistry registry,
        ILogger<StandaloneAgentDispatcher> logger)
    {
        _clientManager = clientManager;
        _registry = registry;
        _logger = logger;
    }

    // ?? Registry operations ????????????????????????????????????????????

    public void RegisterAgent(string agentName, string grpcAddress)
        => _registry.Register(agentName, grpcAddress);

    public bool UnregisterAgent(string agentName)
    {
        if (_registry.TryGet(agentName, out var entry))
            _clientManager.RemoveChannel(entry.Address);
        return _registry.Unregister(agentName);
    }

    public IEnumerable<string> RegisteredAgents
        => _registry.GetAll().Select(a => a.Name);

    public int RegisteredAgentCount
        => _registry.GetAll().Count;

    public string? GetAgentAddress(string agentName)
        => _registry.TryGet(agentName, out var e) ? e.Address : null;

    public AgentHealthState? GetAgentHealth(string agentName)
        => _registry.TryGet(agentName, out _)
            ? new AgentHealthState { AgentName = agentName, IsHealthy = true }
            : null;

    public IReadOnlyDictionary<string, AgentHealthState> GetAllAgentHealth()
        => _registry.GetAll().ToDictionary(
            a => a.Name,
            a => new AgentHealthState { AgentName = a.Name, IsHealthy = true },
            StringComparer.OrdinalIgnoreCase);

    // ?? Connectivity ???????????????????????????????????????????????????

    public async Task<(AgentSnapshot? Snapshot, string? Error)> TestConnectionAsync(
        string agentName, CancellationToken ct = default)
    {
        if (!_registry.TryGet(agentName, out var entry))
            return (null, $"Agent '{agentName}' not registered");

        try
        {
            var client = _clientManager.GetClient(entry.Address);
            var snapshot = await client.GetAgentSnapshotAsync(new Empty(), cancellationToken: ct);
            _registry.UpdateStatus(agentName, $"Online — {snapshot.State}");
            StatusChanged?.Invoke(agentName, $"Online — {snapshot.State}");
            return (snapshot, null);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            _registry.UpdateStatus(agentName, "Unreachable", ex.Status.Detail);
            StatusChanged?.Invoke(agentName, "Unreachable");
            return (null, $"Agent '{agentName}' unreachable: {ex.Status.Detail}");
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    public async Task<List<DiagnosticStep>> DiagnoseAgentAsync(string agentName, CancellationToken ct = default)
    {
        var steps = new List<DiagnosticStep>();

        if (!_registry.TryGet(agentName, out var entry))
        {
            steps.Add(new("Agent Lookup", false, $"Agent '{agentName}' not registered"));
            return steps;
        }

        // DNS
        try
        {
            var uri = new Uri(entry.Address);
            var addresses = await System.Net.Dns.GetHostAddressesAsync(uri.Host, ct);
            steps.Add(new("DNS Resolution", true, $"Resolved to {string.Join(", ", addresses.Select(a => a.ToString()))}"));
        }
        catch (Exception ex)
        {
            steps.Add(new("DNS Resolution", false, ex.Message));
            return steps;
        }

        // TCP
        try
        {
            var uri = new Uri(entry.Address);
            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync(uri.Host, uri.Port, ct);
            steps.Add(new("TCP Port", true, $"Port {uri.Port} open"));
        }
        catch (Exception ex)
        {
            steps.Add(new("TCP Port", false, ex.Message));
            return steps;
        }

        // gRPC GetState
        try
        {
            var client = _clientManager.GetClient(entry.Address);
            var state = await client.GetStateAsync(new Empty(), cancellationToken: ct);
            steps.Add(new("gRPC GetState", true, $"Agent state: {state.State}"));
        }
        catch (Exception ex)
        {
            steps.Add(new("gRPC GetState", false, ex.Message));
        }

        return steps;
    }

    public async Task<bool> PingAsync(string agentName, CancellationToken ct = default)
    {
        if (!_registry.TryGet(agentName, out var entry)) return false;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var client = _clientManager.GetClient(entry.Address);
            await client.GetStateAsync(new Empty(), cancellationToken: cts.Token);
            return true;
        }
        catch { return false; }
    }

    // ?? Remote command execution via gRPC streaming ????????????????????

    public async Task<ActionResult> ExecuteRemoteCommandAsync(
        ActionConfig action, PipelineExecutionContext ctx, CancellationToken ct)
    {
        var resolved = ParameterResolver.ResolveAction(action, ctx);
        var agentName = resolved.AgentName.Trim();

        if (!_registry.TryGet(agentName, out var entry))
        {
            var msg = $"Agent '{agentName}' not registered";
            _logger.LogWarning("{Message}", msg);
            return new ActionResult(false, -1, msg);
        }

        StatusChanged?.Invoke(agentName, $"Executing: {resolved.Command}");
        var startTimestamp = Stopwatch.GetTimestamp();

        try
        {
            var client = _clientManager.GetClient(entry.Address);

            CancellationTokenSource? timeoutCts = resolved.Timeout > 0
                ? new CancellationTokenSource(TimeSpan.FromSeconds(resolved.Timeout))
                : null;
            using var _timeoutDisposable = timeoutCts;
            using var linked = timeoutCts is not null
                ? CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token)
                : CancellationTokenSource.CreateLinkedTokenSource(ct);

            using var call = client.RunCommandStreamed(new RunCommandRequest
            {
                Command = resolved.Command,
                Arguments = resolved.Parameters,
                IsReboot = resolved.IsReboot,
                UserName = resolved.UserName ?? "",
                Password = resolved.Password ?? "",
                TimeoutSeconds = resolved.Timeout,
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
                            $"Progress: {evt.ProgressPct:F0}% — {evt.Detail}", "info");
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
            }

            // Reboot handling
            if (resolved.IsReboot)
            {
                StatusChanged?.Invoke(agentName, "Rebooting… waiting for agent");
                await WaitForAgentReady(client, agentName, TimeSpan.FromMinutes(5), ct);
            }

            if (!receivedCompleted && string.IsNullOrEmpty(errorMessage))
            {
                exitCode = -1;
                errorMessage = $"Agent '{agentName}' did not report completion. Process may have crashed.";
            }

            if (exitCode != 0 && string.IsNullOrEmpty(errorMessage))
            {
                errorMessage = stderrLines.Count > 0
                    ? $"Process exited with code {exitCode}. Last stderr: " +
                      string.Join(" | ", stderrLines.TakeLast(5))
                    : $"Process exited with code {exitCode}. No error output captured.";
            }

            var exitDetail = ExitCodeReference.Describe(exitCode);
            if (exitCode != 0 && exitDetail != $"Unknown exit code {exitCode}")
                errorMessage = $"[{exitDetail}] {errorMessage}";

            var success = exitCode == 0 && string.IsNullOrEmpty(errorMessage);
            StatusChanged?.Invoke(agentName, success ? "Ready" : $"Failed (exit {exitCode})");
            return new ActionResult(success, exitCode, errorMessage);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            if (resolved.IsReboot)
            {
                StatusChanged?.Invoke(agentName, "Rebooting… waiting for agent");
                var client = _clientManager.GetClient(entry.Address);
                await WaitForAgentReady(client, agentName, TimeSpan.FromMinutes(5), ct);
                return new ActionResult(true, 0, "Reboot completed");
            }
            return new ActionResult(false, -1, $"Agent {agentName} unavailable: {ex.Status.Detail}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            return new ActionResult(false, -1, $"Cancelled by user after {elapsed:hh\\:mm\\:ss}");
        }
        catch (OperationCanceledException ex)
        {
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
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
            return new ActionResult(false, -1, ex.Message);
        }
    }

    // ?? Local command execution via Process ?????????????????????????????

    public async Task<ActionResult> ExecuteLocalCommandAsync(
        ActionConfig action, PipelineExecutionContext ctx, CancellationToken ct)
    {
        var resolved = ParameterResolver.ResolveAction(action, ctx);

        try
        {
            var (fileName, arguments) = ResolveInterpreter(resolved.Command, resolved.Parameters);

            var psi = new ProcessStartInfo
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

            using var process = Process.Start(psi);
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
                    using var checkProcess = Process.Start(new ProcessStartInfo
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
                var detail = ExitCodeReference.Describe(process.ExitCode);
                var errMsg = $"Local command failed: {fileName} {arguments}. [{detail}]".TrimEnd();
                OutputReceived?.Invoke("Controller", $"[FAIL] Exit code {process.ExitCode}: {detail}", "stderr");
                return new ActionResult(false, process.ExitCode, errMsg);
            }

            return new ActionResult(true, 0, "");
        }
        catch (Exception ex)
        {
            return new ActionResult(false, -1, ex.Message);
        }
    }

    // ?? Helpers ????????????????????????????????????????????????????????

    private static (string FileName, string Arguments) ResolveInterpreter(string command, string arguments)
    {
        var cmd = command.Trim().Trim('"');
        var ext = "";
        try { ext = Path.GetExtension(cmd).ToLowerInvariant(); }
        catch (ArgumentException) { }

        return ext switch
        {
            ".bat" or ".cmd" => ("cmd.exe", $"/c \"{cmd}\" {arguments}".TrimEnd()),
            ".ps1" => ("powershell.exe", $"-ExecutionPolicy Bypass -NoProfile -File \"{cmd}\" {arguments}".TrimEnd()),
            _ => (command, arguments),
        };
    }

    private async Task WaitForAgentReady(
        TestAgentService.TestAgentServiceClient client, string agentName,
        TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
            try
            {
                await client.GetStateAsync(new Empty(),
                    deadline: DateTime.UtcNow.AddSeconds(10),
                    cancellationToken: ct);
                StatusChanged?.Invoke(agentName, "Online (post-reboot)");
                _registry.UpdateStatus(agentName, "Online");
                return;
            }
            catch { /* agent not ready yet */ }
        }
    }

    public void Dispose()
    {
        // Channels are managed by AgentGrpcClientManager
    }
}
