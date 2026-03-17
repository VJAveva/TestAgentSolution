using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.SignalR;
using TestAgentGrpc;
using TestController.WebApi.Hubs;

namespace TestController.WebApi.Services;

/// <summary>
/// Background service that subscribes to gRPC agent event streams
/// and broadcasts them to all connected SignalR clients.
/// </summary>
public sealed class SignalRBroadcastService : BackgroundService
{
    private readonly IHubContext<LiveHub> _hub;
    private readonly AgentGrpcClientManager _grpcManager;
    private readonly AgentRegistry _registry;
    private readonly ILogger<SignalRBroadcastService> _logger;

    public SignalRBroadcastService(
        IHubContext<LiveHub> hub,
        AgentGrpcClientManager grpcManager,
        AgentRegistry registry,
        ILogger<SignalRBroadcastService> logger)
    {
        _hub = hub;
        _grpcManager = grpcManager;
        _registry = registry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Wait briefly for app startup to complete
        await Task.Delay(2000, ct);

        while (!ct.IsCancellationRequested)
        {
            var agents = _registry.GetAll();
            var tasks = agents.Select(a => SubscribeToAgentAsync(a, ct));
            await Task.WhenAll(tasks);

            // Re-check every 30 seconds for new agents
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }

    private async Task SubscribeToAgentAsync(AgentEntry agent, CancellationToken ct)
    {
        try
        {
            var client = _grpcManager.GetClient(agent.Address);
            using var stream = client.SubscribeAgentEvents(new Empty(), cancellationToken: ct);

            _registry.UpdateStatus(agent.Name, "Connected");
            await _hub.Clients.All.SendAsync("AgentStatus", agent.Name, "Connected", ct);

            await foreach (var evt in stream.ResponseStream.ReadAllAsync(ct))
            {
                await BroadcastEventAsync(agent.Name, evt, ct);
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            _registry.UpdateStatus(agent.Name, "Unreachable", ex.Message);
            await _hub.Clients.All.SendAsync("AgentStatus", agent.Name, "Unreachable", ct);
            _logger.LogWarning("Agent {Name} unreachable: {Message}", agent.Name, ex.Status.Detail);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown — expected
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error subscribing to agent {Name} events", agent.Name);
            _registry.UpdateStatus(agent.Name, "Error", ex.Message);
        }
    }

    private async Task BroadcastEventAsync(string agentName, ExecutionEvent evt, CancellationToken ct)
    {
        switch (evt.EventType)
        {
            case ExecutionEventType.EventStdoutLine:
            case ExecutionEventType.EventStderrLine:
                var kind = evt.EventType == ExecutionEventType.EventStdoutLine ? "stdout" : "stderr";
                await _hub.Clients.All.SendAsync("ExecutionEvent", agentName, evt.OutputLine, kind, ct);
                break;

            case ExecutionEventType.EventStarted:
                await _hub.Clients.All.SendAsync("ExecutionLog", new
                {
                    Agent = agentName,
                    Message = $"Started: {evt.Command} {evt.Arguments}",
                    evt.ExecutionId,
                    Timestamp = DateTime.UtcNow
                }, ct);
                break;

            case ExecutionEventType.EventCompleted:
                await _hub.Clients.All.SendAsync("ExecutionLog", new
                {
                    Agent = agentName,
                    Message = $"Completed (exit code {evt.ExitCode})",
                    evt.ExecutionId,
                    Timestamp = DateTime.UtcNow
                }, ct);
                break;

            case ExecutionEventType.EventFailed:
                await _hub.Clients.All.SendAsync("ExecutionLog", new
                {
                    Agent = agentName,
                    Message = $"Failed: {evt.ErrorMessage}",
                    evt.ExecutionId,
                    Timestamp = DateTime.UtcNow
                }, ct);
                break;

            case ExecutionEventType.EventStateChanged:
                _registry.UpdateStatus(agentName, evt.AgentState.ToString());
                await _hub.Clients.All.SendAsync("AgentStatus", agentName, evt.AgentState.ToString(), ct);
                break;

            case ExecutionEventType.EventHeartbeat:
                _registry.UpdateStatus(agentName, "Online");
                break;
        }
    }
}
