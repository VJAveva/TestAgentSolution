using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using TestAgentGrpc;

namespace TestControllerGrpc.Services;

/// <summary>
/// gRPC server-side implementation of <see cref="TestControllerService"/>.
/// Hosted by the Controller so that remote agents can:
///   • Register / UnRegister themselves
///   • Push state changes and heartbeats
///   • Stream real-time execution events
///
/// Raises events that the MainViewModel subscribes to for auto-adding
/// agents to the UI and displaying live status.
/// </summary>
public sealed class TestControllerGrpcService : TestControllerService.TestControllerServiceBase
{
    private readonly AgentGrpcDispatcher _dispatcher;
    private readonly ILogger<TestControllerGrpcService> _logger;

    /// <summary>Fired when an agent registers itself. (agentName, agentAddress)</summary>
    public static event Action<string, string>? AgentRegistered;

    /// <summary>Fired when an agent unregisters. (agentName)</summary>
    public static event Action<string>? AgentUnregistered;

    /// <summary>Fired when an agent pushes a state change. (agentName, state)</summary>
    public static event Action<string, AgentState>? AgentStateChanged;

    /// <summary>Fired when heartbeat arrives. (agentName, state, metrics)</summary>
    public static event Action<string, AgentState, ResourceMetrics?>? HeartbeatReceived;

    /// <summary>Fired when an execution event is pushed. (event)</summary>
    public static event Action<ExecutionEvent>? ExecutionEventReceived;

    public TestControllerGrpcService(
        AgentGrpcDispatcher dispatcher,
        ILogger<TestControllerGrpcService> logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public override Task<Empty> Register(TestAgentRef request, ServerCallContext context)
    {
        _logger.LogInformation("Agent registered via gRPC: {Name} (state={State})",
            request.Name, request.State);

        // Extract the agent's address from the peer info
        var peerAddress = context.Peer; // e.g. "ipv4:192.168.1.100:54321"
        var agentGrpcAddress = ExtractAgentAddress(peerAddress, request.Name);

        // Register in dispatcher so controller can call back
        _dispatcher.RegisterAgent(request.Name, agentGrpcAddress);

        AgentRegistered?.Invoke(request.Name, agentGrpcAddress);
        return Task.FromResult(new Empty());
    }

    public override Task<Empty> UnRegister(TestAgentRef request, ServerCallContext context)
    {
        _logger.LogInformation("Agent unregistered via gRPC: {Name}", request.Name);
        _dispatcher.UnregisterAgent(request.Name);
        AgentUnregistered?.Invoke(request.Name);
        return Task.FromResult(new Empty());
    }

    public override Task<Empty> UpdateClientState(TestAgentRef request, ServerCallContext context)
    {
        _logger.LogInformation("Agent {Name} state → {State}", request.Name, request.State);
        AgentStateChanged?.Invoke(request.Name, request.State);
        return Task.FromResult(new Empty());
    }

    public override Task<Empty> Heartbeat(HeartbeatRequest request, ServerCallContext context)
    {
        _logger.LogDebug("Heartbeat from {Name}: {State}", request.AgentName, request.State);
        HeartbeatReceived?.Invoke(request.AgentName, request.State, request.Metrics);
        return Task.FromResult(new Empty());
    }

    public override async Task<Empty> PushExecutionEvents(
        IAsyncStreamReader<ExecutionEvent> requestStream, ServerCallContext context)
    {
        _logger.LogInformation("Agent opened event push stream from {Peer}", context.Peer);

        await foreach (var evt in requestStream.ReadAllAsync(context.CancellationToken))
        {
            ExecutionEventReceived?.Invoke(evt);
        }

        _logger.LogInformation("Agent event push stream closed from {Peer}", context.Peer);
        return new Empty();
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the agent's gRPC address from the peer string.
    /// The agent's Name typically IS its endpoint (e.g. "http://hostname:5200"),
    /// or we derive it from the peer IP + default port 5200.
    /// </summary>
    private static string ExtractAgentAddress(string peer, string agentName)
    {
        // If agent name looks like an address, use it directly
        if (agentName.StartsWith("http://") || agentName.StartsWith("https://"))
            return agentName;

        // Try to extract IP from peer string like "ipv4:192.168.1.100:54321"
        try
        {
            var parts = peer.Split(':');
            if (parts.Length >= 2)
            {
                var ip = parts[1];
                return $"http://{ip}:5200";
            }
        }
        catch { }

        // Fallback: assume hostname:5200
        return $"http://{agentName}:5200";
    }
}
