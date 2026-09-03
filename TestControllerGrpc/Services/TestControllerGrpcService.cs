using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using TestAgentGrpc;
using System.IO;

namespace TestControllerGrpc.Services;

/// <summary>
/// gRPC server-side implementation of <see cref="TestControllerService"/>.
/// Hosted by the Controller so that remote agents can:
///   • Register / UnRegister themselves
///   • Push state changes and heartbeats
///   • Stream real-time execution events
///
/// Publishes events via <see cref="IEventAggregator"/> so that the UI
/// and other services can react without tight static-event coupling.
/// </summary>
public sealed class TestControllerGrpcService : TestControllerService.TestControllerServiceBase
{
    private const int MaxAgentNameLength = 128;
    private const int DefaultAgentGrpcPort = 5200;

    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly IEventAggregator _events;
    private readonly ILogger<TestControllerGrpcService> _logger;
    private readonly IAppLogger _appLogger;
    private readonly ControllerTimeoutOptions _timeouts;

    public TestControllerGrpcService(
        IAgentGrpcDispatcher dispatcher,
        IEventAggregator events,
        ILogger<TestControllerGrpcService> logger,
        IAppLogger appLogger,
        ControllerTimeoutOptions timeouts)
    {
        _dispatcher = dispatcher;
        _events = events;
        _logger = logger;
        _appLogger = appLogger;
        _timeouts = timeouts;
    }

    public override Task<Empty> Register(TestAgentRef request, ServerCallContext context)
        => GrpcGuard.RunAsync(_appLogger, "ControllerGrpc.Register", context, () =>
        {
        // ── Server-side validation: never trust the client. ──
        var agentName = request.Name?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(agentName))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Agent name is required"));
        if (agentName.Length > MaxAgentNameLength)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Agent name exceeds {MaxAgentNameLength} characters"));

        // Prefer the agent-advertised endpoint, but reject non-routable/loopback
        // values (a remote agent advertising localhost would make the controller
        // call itself); fall back to the peer-derived address in that case.
        var agentGrpcAddress = ResolveAgentAddress(request.Endpoint, context.Peer, agentName);

        // Capacity guard: cap NEW registrations so a registration flood cannot
        // exhaust controller resources.
        var isNew = _dispatcher.GetAgentAddress(agentName) is null;
        if (isNew && _dispatcher.RegisteredAgentCount >= _timeouts.MaxRegisteredAgents)
        {
            _appLogger.Warn("ControllerGrpc.Register",
                $"Rejected '{agentName}': agent capacity {_timeouts.MaxRegisteredAgents} reached");
            throw new RpcException(new Status(StatusCode.ResourceExhausted,
                $"Controller agent capacity ({_timeouts.MaxRegisteredAgents}) reached"));
        }

        _logger.LogInformation(
            "Agent registered via gRPC: Name={Name}, Endpoint={Endpoint}, Peer={Peer}",
            agentName, agentGrpcAddress, context.Peer);

        // Register under the friendly name so WatchList XML AgentName references resolve
        // (dispatcher publishes AgentRegisteredEvent internally)
        _dispatcher.RegisterAgent(agentName, agentGrpcAddress);

        return Task.FromResult(new Empty());
        });

    public override Task<Empty> UnRegister(TestAgentRef request, ServerCallContext context)
        => GrpcGuard.RunAsync(_appLogger, "ControllerGrpc.UnRegister", context, () =>
        {
        _logger.LogInformation("Agent unregistered via gRPC: {Name}", request.Name);
        // Dispatcher publishes AgentUnregisteredEvent internally
        _dispatcher.UnregisterAgent(request.Name);
        return Task.FromResult(new Empty());
        });

    public override Task<Empty> UpdateClientState(TestAgentRef request, ServerCallContext context)
        => GrpcGuard.RunAsync(_appLogger, "ControllerGrpc.UpdateClientState", context, () =>
        {
        _logger.LogInformation("Agent {Name} state → {State}", request.Name, request.State);
        _events.Publish(new AgentStateChangedEvent(request.Name, request.State));
        return Task.FromResult(new Empty());
        });

    public override Task<Empty> Heartbeat(HeartbeatRequest request, ServerCallContext context)
        => GrpcGuard.RunAsync(_appLogger, "ControllerGrpc.Heartbeat", context, () =>
        {
        _logger.LogDebug("Heartbeat from {Name}: {State}", request.AgentName, request.State);
        _events.Publish(new AgentHeartbeatEvent(request.AgentName, request.State, request.Metrics));
        return Task.FromResult(new Empty());
        });

    public override async Task<Empty> PushExecutionEvents(
        IAsyncStreamReader<ExecutionEvent> requestStream, ServerCallContext context)
    {
        _logger.LogInformation("Agent opened event push stream from {Peer}", context.Peer);

        try
        {
            await foreach (var evt in requestStream.ReadAllAsync(context.CancellationToken))
            {
                _events.Publish(new ExecutionEventReceivedEvent(evt));
            }

            _logger.LogInformation("Agent event push stream closed from {Peer}", context.Peer);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Agent at {Peer} disconnected unexpectedly.", context.Peer);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Agent event push stream from {Peer} was cancelled.", context.Peer);
        }
        catch (Exception ex)
        {
            // Any other failure (e.g. gRPC framing errors surfacing as
            // InvalidDataException during TryReadMessage) must be logged with
            // the real exception so it can never be masked as a generic
            // "Exception was thrown by handler." on the agent side.
            _logger.LogError(ex, "PushExecutionEvents failed for {Peer}", context.Peer);
            throw;
        }

        return new Empty();
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Fallback: extracts the agent's gRPC address from the peer string
    /// when the agent does not send an explicit endpoint (legacy agents).
    /// Peer format: "ipv4:192.168.1.100:54321" or "ipv6:[::1]:54321".
    /// </summary>
    private string ExtractAgentAddress(string peer, string agentName)
    {
        // If agent name already looks like an address, use it directly
        if (agentName.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            agentName.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return agentName;

        // Try to extract IP from peer string like "ipv4:192.168.1.100:54321"
        try
        {
            // Format: "protocol:host:ephemeralPort"
            var firstColon = peer.IndexOf(':');
            if (firstColon >= 0)
            {
                var afterProtocol = peer[(firstColon + 1)..];
                var lastColon = afterProtocol.LastIndexOf(':');
                if (lastColon > 0)
                {
                    var host = afterProtocol[..lastColon];
                    // For IPv6 addresses like "[::1]", keep brackets
                    return $"http://{host}:{DefaultAgentGrpcPort}";
                }
            }
        }
        catch (Exception ex)
        {
            // Peer string format unexpected — fall through to hostname default
            _logger.LogDebug(ex, "Failed to parse agent address from peer string: {Peer}", peer);
        }

        // Fallback: assume hostname:default gRPC port
        return $"http://{agentName}:{DefaultAgentGrpcPort}";
    }

    /// <summary>
    /// Chooses the callback address for an agent: the advertised endpoint when it is
    /// absolute and routable, otherwise the address derived from the gRPC peer.
    /// </summary>
    private string ResolveAgentAddress(string? advertisedEndpoint, string peer, string agentName)
    {
        if (!string.IsNullOrWhiteSpace(advertisedEndpoint) &&
            Uri.TryCreate(advertisedEndpoint, UriKind.Absolute, out var uri) &&
            !IsLoopbackHost(uri.Host))
        {
            return advertisedEndpoint;
        }

        if (!string.IsNullOrWhiteSpace(advertisedEndpoint))
            _logger.LogWarning(
                "Agent {Name} advertised non-routable endpoint '{Ep}' — deriving from peer {Peer}",
                agentName, advertisedEndpoint, peer);

        return ExtractAgentAddress(peer, agentName);
    }

    private static bool IsLoopbackHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        var bare = host.Trim('[', ']');
        return System.Net.IPAddress.TryParse(bare, out var ip) && System.Net.IPAddress.IsLoopback(ip);
    }
}
