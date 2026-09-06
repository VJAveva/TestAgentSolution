using Microsoft.Extensions.Hosting;
using TestAgentGrpc;
using TestControllerGrpc.Core.Maintenance;

namespace TestControllerGrpc.Services;

/// <summary>
/// Controller-host relay: agents push their event firehose to this host's gRPC server, which republishes each event
/// as an <see cref="ExecutionEventReceivedEvent"/>. This watches that stream for EVENT_WINDOWS_UPDATE and applies the
/// reported posture to the <see cref="INodeUpdateStatusStore"/>. Mirror of the standalone WebApi's
/// <c>AgentEventRelayService</c>, which does the same off the SubscribeAgentEvents pull firehose.
/// </summary>
public sealed class NodeUpdateStatusRelay : BackgroundService
{
    private readonly IEventAggregator _events;
    private readonly INodeUpdateStatusStore _updateStatus;
    private readonly IAppLogger _logger;
    private IDisposable? _subscription;

    public NodeUpdateStatusRelay(
        IEventAggregator events,
        INodeUpdateStatusStore updateStatus,
        IAppLogger logger)
    {
        _events = events;
        _updateStatus = updateStatus;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscription = _events.Subscribe<ExecutionEventReceivedEvent>(OnAgentEvent);
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        base.Dispose();
    }

    private void OnAgentEvent(ExecutionEventReceivedEvent wrapper)
    {
        var evt = wrapper.Event;
        if (evt.EventType != ExecutionEventType.EventWindowsUpdate) return;

        var posture = WindowsUpdateEventMapper.TryMap(evt.AgentName, evt.Detail, DateTimeOffset.UtcNow);
        if (posture is null) return;

        _updateStatus.Apply(posture);
        _logger.Info("WindowsUpdate",
            $"Posture for {evt.AgentName}: pending={posture.Status.PendingCount} rebootRequired={posture.Status.RebootRequired}");
    }
}
