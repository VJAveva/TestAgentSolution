using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using TestController.Api.Hubs;

namespace TestController.WebApi.Services;

/// <summary>
/// Bridges the WPF controller's SignalR hub to this WebApi's <see cref="ControllerHub"/>.
///
/// The browser only ever talks to the WebApi (same-origin <c>/hubs/controller</c>); the
/// controller's port (5200) stays private. This background service opens a single
/// <see cref="HubConnection"/> to the controller's hub and re-broadcasts the live run +
/// lock + owner events, unchanged, to the WebApi's own hub clients. This is what ends
/// "request-and-forget": a pipeline triggered on the web (and executed on the controller)
/// now pushes its live state back to every web client.
///
/// Modelled on <see cref="AgentEventRelayService"/> (same BackgroundService pattern); the
/// transport here is SignalR rather than gRPC. Active only when a controller proxy URL is
/// configured (mirrors the forwarding middleware) — standalone WebApi-only deployments
/// have no controller to relay from.
///
/// No new events are introduced and no parallel notification path is built: every event is
/// forwarded by its existing name through the existing <see cref="ControllerHub"/>.
/// </summary>
public sealed class ControllerEventRelayService : BackgroundService
{
    /// <summary>
    /// The hub event names forwarded, unchanged, from the controller to web clients.
    /// Lock lifecycle, run lifecycle, per-action progress, and log entries. Agent
    /// status/output/heartbeats are intentionally excluded — this host already streams
    /// those directly from agents via <see cref="AgentEventRelayService"/>, so forwarding
    /// them again would double-deliver.
    /// </summary>
    private static readonly string[] ForwardedEvents =
    {
        "PipelineLockAcquired",
        "PipelineLockReleased",
        "PipelineLockExpired",
        "PipelineLockForceReleased",
        "PipelineLockRewritten",
        "ExecutionStarted",
        "ExecutionCompleted",
        "ExecutionCancelled",
        "ActionProgress",
        "LogEntry",
    };

    private readonly ControllerProxyService _proxy;
    private readonly IHubContext<ControllerHub> _hub;
    private readonly ILogger<ControllerEventRelayService> _logger;

    private HubConnection? _connection;

    public ControllerEventRelayService(
        ControllerProxyService proxy,
        IHubContext<ControllerHub> hub,
        ILogger<ControllerEventRelayService> logger)
    {
        _proxy = proxy;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_proxy.IsConfigured || string.IsNullOrEmpty(_proxy.BaseUrl))
        {
            _logger.LogInformation(
                "ControllerEventRelayService disabled: no ControllerProxyUrl configured.");
            return;
        }

        var hubUrl = $"{_proxy.BaseUrl.TrimEnd('/')}/hubs/controller";

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                // Same-machine co-located deployment: the WebApi authenticates to the
                // controller hub with its Windows identity (Negotiate). In None/Default
                // mode the hub treats the connection as authenticated anyway.
                options.UseDefaultCredentials = true;
            })
            .WithAutomaticReconnect()
            .Build();

        // Forward every event by name, payload untouched. Capturing the payload as a
        // JsonElement keeps it opaque so the relay never has to know the DTO shapes.
        foreach (var eventName in ForwardedEvents)
        {
            var name = eventName;
            _connection.On<JsonElement>(name, payload => ForwardAsync(name, payload, ct));
        }

        _connection.Reconnected += connectionId =>
        {
            _logger.LogInformation("Controller relay reconnected ({ConnectionId}).", connectionId);
            return Task.CompletedTask;
        };
        _connection.Closed += error =>
        {
            if (error is not null)
                _logger.LogWarning("Controller relay connection closed: {Message}", error.Message);
            return Task.CompletedTask;
        };

        await ConnectWithRetryAsync(hubUrl, ct);

        try
        {
            // Stay alive until shutdown; WithAutomaticReconnect handles transient drops.
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            if (_connection is not null)
                await _connection.DisposeAsync();
        }
    }

    private async Task ConnectWithRetryAsync(string hubUrl, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _connection!.StartAsync(ct);
                _logger.LogInformation("Controller relay connected to {HubUrl}.", hubUrl);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    "Controller relay connect to {HubUrl} failed: {Message}. Retrying in 5s.",
                    hubUrl, ex.Message);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async Task ForwardAsync(string eventName, JsonElement payload, CancellationToken ct)
    {
        try
        {
            await _hub.Clients.All.SendAsync(eventName, payload, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Failed to forward {Event} to web clients: {Message}", eventName, ex.Message);
        }
    }
}
