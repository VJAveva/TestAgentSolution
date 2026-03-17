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
    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly IEventAggregator _events;
    private readonly ILogger<TestControllerGrpcService> _logger;

    public TestControllerGrpcService(
        IAgentGrpcDispatcher dispatcher,
        IEventAggregator events,
        ILogger<TestControllerGrpcService> logger)
    {
        _dispatcher = dispatcher;
        _events = events;
        _logger = logger;
    }

    public override Task<Empty> Register(TestAgentRef request, ServerCallContext context)
    {
        // Use the explicit endpoint when provided; fall back to peer-derived address
        var agentGrpcAddress = !string.IsNullOrEmpty(request.Endpoint)
            ? request.Endpoint
            : ExtractAgentAddress(context.Peer, request.Name);

        _logger.LogInformation(
            "Agent registered via gRPC: Name={Name}, Endpoint={Endpoint}, Peer={Peer}",
            request.Name, agentGrpcAddress, context.Peer);

        // Register under the friendly name so WatchList XML AgentName references resolve
        _dispatcher.RegisterAgent(request.Name, agentGrpcAddress);

        _events.Publish(new AgentRegisteredEvent(request.Name, agentGrpcAddress));
        return Task.FromResult(new Empty());
    }

    public override Task<Empty> UnRegister(TestAgentRef request, ServerCallContext context)
    {
        _logger.LogInformation("Agent unregistered via gRPC: {Name}", request.Name);
        _dispatcher.UnregisterAgent(request.Name);
        _events.Publish(new AgentUnregisteredEvent(request.Name));
        return Task.FromResult(new Empty());
    }

    public override Task<Empty> UpdateClientState(TestAgentRef request, ServerCallContext context)
    {
        _logger.LogInformation("Agent {Name} state → {State}", request.Name, request.State);
        _events.Publish(new AgentStateChangedEvent(request.Name, request.State));
        return Task.FromResult(new Empty());
    }

    public override Task<Empty> Heartbeat(HeartbeatRequest request, ServerCallContext context)
    {
        _logger.LogDebug("Heartbeat from {Name}: {State}", request.AgentName, request.State);
        _events.Publish(new AgentHeartbeatEvent(request.AgentName, request.State, request.Metrics));
        return Task.FromResult(new Empty());
    }

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
                    return $"http://{host}:5200";
                }
            }
        }
        catch (Exception ex)
        {
            // Peer string format unexpected — fall through to hostname default
            _logger.LogDebug(ex, "Failed to parse agent address from peer string: {Peer}", peer);
        }

        // Fallback: assume hostname:5200
        return $"http://{agentName}:5200";
    }
}
