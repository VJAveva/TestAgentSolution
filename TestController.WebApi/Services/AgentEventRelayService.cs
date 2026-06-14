using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using TestAgentGrpc;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Services;

/// <summary>
/// Background service that subscribes to gRPC agent event streams
/// and publishes events through <see cref="IRealtimeNotifier"/>.
///
/// Replaces the former <c>SignalRBroadcastService</c> which broadcast
/// directly to a separate <c>LiveHub</c>. Events now flow through the
/// single <c>ControllerHub</c> via <c>SignalRNotifier</c>.
/// </summary>
public sealed class AgentEventRelayService : BackgroundService
{
    private readonly IRealtimeNotifier _notifier;
    private readonly AgentGrpcClientManager _grpcManager;
    private readonly AgentRegistry _registry;
    private readonly ILogger<AgentEventRelayService> _logger;

    public AgentEventRelayService(
        IRealtimeNotifier notifier,
        AgentGrpcClientManager grpcManager,
        AgentRegistry registry,
        ILogger<AgentEventRelayService> logger)
    {
        _notifier = notifier;
        _grpcManager = grpcManager;
        _registry = registry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown — don't let this propagate as BackgroundService failure
        }
    }

    private async Task SubscribeToAgentAsync(AgentEntry agent, CancellationToken ct)
    {
        try
        {
            var client = _grpcManager.GetClient(agent.Address);
            using var stream = client.SubscribeAgentEvents(new Empty(), cancellationToken: ct);

            _registry.UpdateStatus(agent.Name, "Connected");
            await _notifier.NotifyAgentStatusChanged(new
            {
                agentName = agent.Name,
                status = "Connected",
                timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
            });

            await foreach (var evt in stream.ResponseStream.ReadAllAsync(ct))
            {
                await BroadcastEventAsync(agent.Name, evt, ct);
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            _registry.UpdateStatus(agent.Name, "Unreachable", ex.Message);
            await _notifier.NotifyAgentStatusChanged(new
            {
                agentName = agent.Name,
                status = "Unreachable",
                timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
            });
            _logger.LogWarning("Agent {Name} unreachable: {Message}", agent.Name, ex.Status.Detail);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown � expected
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
                await _notifier.NotifyAgentOutput(new
                {
                    agentName,
                    line = SecurityRedactor.Redact(evt.OutputLine),
                    kind,
                    timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
                    severity = kind == "stderr" ? "Error" : "Info",
                    category = "Output",
                    message = $"[{agentName}:{kind}] {SecurityRedactor.Redact(evt.OutputLine)}",
                });
                break;

            case ExecutionEventType.EventStarted:
                await _notifier.NotifyLogEntry(new PipelineLogEntry(
                    DateTime.Now, "AgentStream",
                    $"[{agentName}] Started: {SecurityRedactor.RedactCommandLine(evt.Command, evt.Arguments)}"));
                break;

            case ExecutionEventType.EventCompleted:
                await _notifier.NotifyLogEntry(new PipelineLogEntry(
                    DateTime.Now, "AgentStream",
                    $"[{agentName}] Completed (exit code {evt.ExitCode})"));
                break;

            case ExecutionEventType.EventFailed:
                await _notifier.NotifyLogEntry(new PipelineLogEntry(
                    DateTime.Now, "AgentStream",
                    $"[{agentName}] Failed: {SecurityRedactor.Redact(evt.ErrorMessage)}"));
                break;

            case ExecutionEventType.EventStateChanged:
                _registry.UpdateStatus(agentName, evt.AgentState.ToString());
                await _notifier.NotifyAgentStatusChanged(new
                {
                    agentName,
                    status = evt.AgentState.ToString(),
                    timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
                });
                break;

            case ExecutionEventType.EventHeartbeat:
                _registry.UpdateStatus(agentName, "Online");
                break;
        }
    }
}
