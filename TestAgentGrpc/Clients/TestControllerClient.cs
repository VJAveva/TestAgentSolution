using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Options;

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
    private readonly Lazy<GrpcChannel> _channel;

    public TestControllerClient(IOptions<AgentSettings> settings, ILogger<TestControllerClient> logger)
    {
        _settings = settings.Value;
        _logger   = logger;
        _channel  = new Lazy<GrpcChannel>(() =>
            GrpcChannel.ForAddress(_settings.ControllerAddress, new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler
                {
                    EnableMultipleHttp2Connections = true,
                    KeepAlivePingDelay   = TimeSpan.FromSeconds(60),
                    KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
                    ConnectTimeout       = TimeSpan.FromSeconds(30),
                }
            }));
    }

    private TestControllerService.TestControllerServiceClient Client => new(_channel.Value);

    // ── Registration (with retry) ──────────────────────────────────────

    public async Task<bool> RegisterAsync(CancellationToken ct = default)
    {
        var agentName = _settings.AgentName;
        var endpoint  = _settings.GetResolvedEndpoint();
        var retries   = _settings.RegistrationRetryCount;
        var delay     = TimeSpan.FromSeconds(_settings.RegistrationRetryIntervalSeconds);

        for (int attempt = 0; attempt <= retries; attempt++)
        {
            try
            {
                _logger.LogInformation("Registering (attempt {N}): Name={Name}, Endpoint={Ep}",
                    attempt + 1, agentName, endpoint);
                await Client.RegisterAsync(new TestAgentRef
                {
                    Name     = agentName,
                    State    = AgentState.Ready,
                    Endpoint = endpoint,
                }, cancellationToken: ct);
                _logger.LogInformation("Registration successful");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Registration attempt {N} failed", attempt + 1);
                if (attempt < retries) await Task.Delay(delay, ct);
            }
        }

        _logger.LogError("Registration failed after {N} attempts", retries + 1);
        return false;
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
        try
        {
            await Client.HeartbeatAsync(new HeartbeatRequest
            {
                AgentName = _settings.GetResolvedEndpoint(),
                State     = state,
                Metrics   = metrics,
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            }, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Heartbeat failed");
        }
    }

    public void Dispose()
    {
        if (_channel.IsValueCreated) _channel.Value.Dispose();
    }
}
