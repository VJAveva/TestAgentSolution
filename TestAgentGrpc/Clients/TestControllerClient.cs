using System.Net.Security;
using System.Security.Authentication;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Services;

namespace TestAgentGrpc.Clients;

/// <summary>
/// gRPC client for communicating with the TestController service.
///
/// Beyond the original register/unregister/updateState, this version adds:
///   • <see cref="PushEventsAsync"/> — opens a client-streaming RPC to push
///     real-time execution events to the controller
///   • <see cref="SendHeartbeatAsync"/> — periodic liveness signal with metrics
///
/// Replaces the WCF <c>TestControllerProxy</c> + <c>WSHttpBinding</c>.
/// </summary>
public sealed class TestControllerClient : IDisposable
{
    private readonly AgentSettings _settings;
    private readonly ILogger<TestControllerClient> _logger;
    private GrpcChannel? _channel;
    private readonly object _channelLock = new();

    public TestControllerClient(IOptions<AgentSettings> settings, ILogger<TestControllerClient> logger)
    {
        _settings = settings.Value;
        _logger   = logger;
    }

    private GrpcChannel GetOrCreateChannel()
    {
        lock (_channelLock)
        {
            if (_channel == null)
            {
                var handler = new SocketsHttpHandler
                {
                    EnableMultipleHttp2Connections = true,
                    ConnectTimeout               = TimeSpan.FromSeconds(10),
                    KeepAlivePingDelay            = TimeSpan.FromSeconds(30),
                    KeepAlivePingTimeout          = TimeSpan.FromSeconds(10),
                    PooledConnectionIdleTimeout   = TimeSpan.FromSeconds(90),
                    PooledConnectionLifetime      = TimeSpan.FromMinutes(30),
                };

                // Enable TLS when the controller address uses HTTPS
                if (_settings.ControllerAddress.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    handler.SslOptions = new SslClientAuthenticationOptions
                    {
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    };
                }

                _channel = GrpcChannel.ForAddress(_settings.ControllerAddress, new GrpcChannelOptions
                {
                    HttpHandler = handler,
                });
            }

            return _channel;
        }
    }

    private TestControllerService.TestControllerServiceClient Client => new(GetOrCreateChannel());

    /// <summary>
    /// Forces recreation of the gRPC channel (e.g., after detecting persistent failures).
    /// </summary>
    public void ResetChannel()
    {
        lock (_channelLock)
        {
            _logger.LogInformation("Forcing gRPC channel reset");
            CrashDumpHelper.AppendCrashLog(
                $"[gRPC] Channel RESET forced — target: {_settings.ControllerAddress}");
            try { _channel?.Dispose(); } catch { }
            _channel = null;
        }
    }

    // ── Registration (with retry) ──────────────────────────────────────

    /// <summary>
    /// Registers with the controller, retrying on failure.
    /// Returns (success, lastErrorMessage).
    /// Includes checkpoint logging for diagnosing partial registration failures.
    /// </summary>
    public async Task<(bool Success, string? Error)> RegisterAsync(CancellationToken ct = default)
    {
        var agentName = _settings.AgentName;
        var endpoint  = _settings.GetResolvedEndpoint();
        var retries   = _settings.RegistrationRetryCount;
        var delay     = TimeSpan.FromSeconds(_settings.RegistrationRetryIntervalSeconds);
        string? lastError = null;

        // ── Checkpoint 1: Validate all registration parameters ─────────
        var validationErrors = ValidateRegistrationParams(agentName, endpoint);
        if (validationErrors is not null)
        {
            var msg = $"Registration parameter validation FAILED: {validationErrors}";
            _logger.LogError(msg);
            CrashDumpHelper.AppendCrashLog($"[Registration] CHECKPOINT_FAIL: {msg}");
            return (false, msg);
        }
        _logger.LogInformation(
            "[Checkpoint 1/4 PASS] Parameters validated: Name={Name}, Endpoint={Ep}, Controller={Ctrl}",
            agentName, endpoint, _settings.ControllerAddress);

        // ── Checkpoint 2: Verify DNS/connectivity to controller ────────
        if (!await VerifyControllerReachable(ct))
        {
            var msg = $"Controller at '{_settings.ControllerAddress}' is not reachable (DNS/TCP failed)";
            _logger.LogError("[Checkpoint 2/4 FAIL] {Msg}", msg);
            CrashDumpHelper.AppendCrashLog($"[Registration] CHECKPOINT_FAIL: {msg}");
            // Don't return — still attempt gRPC (it may work via cached DNS)
        }
        else
        {
            _logger.LogInformation("[Checkpoint 2/4 PASS] Controller reachable at {Addr}", _settings.ControllerAddress);
        }

        // ── Checkpoint 3: Execute gRPC registration with retries ───────
        for (int attempt = 0; attempt <= retries; attempt++)
        {
            try
            {
                _logger.LogInformation(
                    "[Checkpoint 3/4] Registration attempt {N}/{Max}: Name={Name}, Endpoint={Ep}, State={State}",
                    attempt + 1, retries + 1, agentName, endpoint, AgentState.Ready);

                await Client.RegisterAsync(new TestAgentRef
                {
                    Name     = agentName,
                    State    = AgentState.Ready,
                    Endpoint = endpoint,
                }, cancellationToken: ct);

                // ── Checkpoint 4: Registration confirmed ───────────────
                _logger.LogInformation(
                    "[Checkpoint 4/4 PASS] Registration CONFIRMED by controller. " +
                    "Agent='{Name}', Endpoint='{Ep}', Machine='{Machine}'",
                    agentName, endpoint, Environment.MachineName);
                CrashDumpHelper.AppendCrashLog(
                    $"[Registration] SUCCESS: Name={agentName}, Endpoint={endpoint}, " +
                    $"Machine={Environment.MachineName}, Controller={_settings.ControllerAddress}");
                return (true, null);
            }
            catch (RpcException ex)
            {
                lastError = $"gRPC {ex.StatusCode}: {ex.Status.Detail} ({ex.Message})";
                _logger.LogWarning(
                    "[Checkpoint 3/4 FAIL] Attempt {N}: {Error}", attempt + 1, lastError);
                CrashDumpHelper.AppendCrashLog(
                    $"[Registration] ATTEMPT_{attempt + 1}_FAILED: {lastError}");
                if (attempt < retries) await Task.Delay(delay, ct);
            }
            catch (HttpRequestException ex)
            {
                lastError = $"HTTP error: {ex.Message} (InnerException: {ex.InnerException?.Message})";
                _logger.LogWarning(
                    "[Checkpoint 3/4 FAIL] Attempt {N}: {Error}", attempt + 1, lastError);
                CrashDumpHelper.AppendCrashLog(
                    $"[Registration] ATTEMPT_{attempt + 1}_FAILED (HTTP): {lastError}");
                if (attempt < retries) await Task.Delay(delay, ct);
            }
            catch (Exception ex)
            {
                lastError = $"{ex.GetType().Name}: {ex.Message}";
                _logger.LogWarning(ex,
                    "[Checkpoint 3/4 FAIL] Attempt {N}: {Error}", attempt + 1, lastError);
                CrashDumpHelper.AppendCrashLog(
                    $"[Registration] ATTEMPT_{attempt + 1}_FAILED (Unexpected): {lastError}");
                if (attempt < retries) await Task.Delay(delay, ct);
            }
        }

        _logger.LogError(
            "[Checkpoint 3/4 EXHAUSTED] Registration failed after {N} attempts. Last error: {Error}",
            retries + 1, lastError);
        CrashDumpHelper.AppendCrashLog(
            $"[Registration] ALL_ATTEMPTS_EXHAUSTED: {retries + 1} attempts failed. " +
            $"Name={agentName}, Endpoint={endpoint}, Controller={_settings.ControllerAddress}, " +
            $"LastError={lastError}");
        return (false, lastError);
    }

    /// <summary>
    /// Validates that all registration parameters are non-empty and well-formed.
    /// Returns null on success, or an error description on failure.
    /// </summary>
    private static string? ValidateRegistrationParams(string agentName, string endpoint)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(agentName))
            errors.Add("AgentName is empty (Environment.MachineName returned blank?)");
        if (string.IsNullOrWhiteSpace(endpoint))
            errors.Add("Endpoint is empty");
        else if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            errors.Add($"Endpoint '{endpoint}' is not a valid URI");
        else if (string.IsNullOrWhiteSpace(uri.Host) || uri.Host == "localhost")
            errors.Add($"Endpoint host '{uri.Host}' is not a routable hostname — controller cannot call back");

        if (agentName?.Length > 0 && agentName.Contains(' '))
            errors.Add($"AgentName '{agentName}' contains spaces — may cause routing issues");

        return errors.Count > 0 ? string.Join("; ", errors) : null;
    }

    /// <summary>
    /// Quick TCP probe to verify the controller address is reachable before attempting gRPC.
    /// </summary>
    private async Task<bool> VerifyControllerReachable(CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(_settings.ControllerAddress, UriKind.Absolute, out var uri))
                return false;

            using var tcp = new System.Net.Sockets.TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await tcp.ConnectAsync(uri.Host, uri.Port, linked.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task UnRegisterAsync(CancellationToken ct = default)
    {
        try
        {
            await Client.UnRegisterAsync(new TestAgentRef
            {
                Name     = _settings.AgentName,
                State    = AgentState.Inactive,
                Endpoint = _settings.GetResolvedEndpoint(),
            }, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unregistration failed");
        }
    }

    public async Task UpdateStateAsync(AgentState state, CancellationToken ct = default)
    {
        try
        {
            await Client.UpdateClientStateAsync(new TestAgentRef
            {
                Name     = _settings.AgentName,
                State    = state,
                Endpoint = _settings.GetResolvedEndpoint(),
            }, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "State update failed");
        }
    }

    // ── NEW: Push real-time events to controller ───────────────────────

    /// <summary>
    /// Opens a long-lived client-streaming RPC and continuously pushes events
    /// read from the <see cref="Services.EventBroadcaster"/> to the controller.
    ///
    /// Runs for the lifetime of the agent; reconnects automatically on failure.
    /// </summary>
    public async Task PushEventsAsync(
        System.Threading.Channels.ChannelReader<ExecutionEvent> reader,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Opening event push stream to controller…");
                using var call = Client.PushExecutionEvents(cancellationToken: ct);

                await foreach (var evt in reader.ReadAllAsync(ct))
                {
                    await call.RequestStream.WriteAsync(evt);
                }

                await call.RequestStream.CompleteAsync();
                await call;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
            {
                _logger.LogWarning("Controller unavailable — retrying event push in 10s…");
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Event push failed — retrying in 5s…");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    // ── NEW: Heartbeat ─────────────────────────────────────────────────

    public async Task SendHeartbeatAsync(AgentState state, ResourceMetrics metrics, CancellationToken ct = default)
    {
        await Client.HeartbeatAsync(new HeartbeatRequest
        {
            AgentName = _settings.AgentName,
            State     = state,
            Metrics   = metrics,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        }, cancellationToken: ct);
    }

    public void Dispose()
    {
        lock (_channelLock)
        {
            _channel?.Dispose();
            _channel = null;
        }
    }
}
