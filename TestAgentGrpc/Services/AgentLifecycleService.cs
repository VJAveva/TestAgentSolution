using TestAgentGrpc.Clients;
using TestAgentGrpc.Services;
using Microsoft.Extensions.Options;

namespace TestAgentGrpc;

/// <summary>
/// Background service managing the full agent lifecycle:
///   1. Registers with controller on start (with retry)
///   2. Pushes state changes whenever <see cref="CommandExecutor.StateChanged"/> fires
///   3. Runs a periodic heartbeat with system metrics
///   4. Opens a persistent event push stream to the controller
///   5. Unregisters on shutdown
///
/// Replaces all the scattered registration/state code from the legacy
/// <c>ApplicationContextEx.StartService()</c> and <c>TestAgentSvc</c>.
/// </summary>
public sealed class AgentLifecycleService : IHostedService, IDisposable
{
    private readonly TestControllerClient _controller;
    private readonly CommandExecutor _executor;
    private readonly EventBroadcaster _broadcaster;
    private readonly SystemMetricsCollector _metrics;
    private readonly AgentSettings _settings;
    private readonly ILogger<AgentLifecycleService> _logger;

    private CancellationTokenSource? _cts;
    private Task? _heartbeatTask;
    private Task? _eventPushTask;

    public AgentLifecycleService(
        TestControllerClient controller,
        CommandExecutor executor,
        EventBroadcaster broadcaster,
        SystemMetricsCollector metrics,
        IOptions<AgentSettings> settings,
        ILogger<AgentLifecycleService> logger)
    {
        _controller  = controller;
        _executor    = executor;
        _broadcaster = broadcaster;
        _metrics     = metrics;
        _settings    = settings.Value;
        _logger      = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _logger.LogInformation("Agent lifecycle starting…");

        // Wire state changes → controller push
        _executor.StateChanged += OnStateChanged;

        // Register with retries (non-blocking — agent stays Ready regardless)
        bool registered = await _controller.RegisterAsync(ct);
        if (!registered)
        {
            _logger.LogWarning("Controller registration failed — running in standalone mode (still Ready)");
            // CRITICAL: Do NOT set Inactive. The agent can still serve commands
            // from any controller that connects to it via TestAgentService RPCs.
            // Inactive should only be set on graceful shutdown.
        }

        // Start heartbeat loop (will silently fail if controller unreachable)
        _heartbeatTask = RunHeartbeatLoopAsync(_cts.Token);

        // Start event push stream to controller (auto-reconnects on failure)
        var (reader, _) = _broadcaster.Subscribe(capacity: 5000);
        _eventPushTask = _controller.PushEventsAsync(reader, _cts.Token);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _logger.LogInformation("Agent lifecycle stopping…");
        _executor.StateChanged -= OnStateChanged;
        _executor.SetState(AgentState.Inactive);

        _cts?.Cancel();

        // Wait for background tasks to finish gracefully
        try { if (_heartbeatTask is not null) await _heartbeatTask.WaitAsync(ct); } catch { }
        try { if (_eventPushTask is not null) await _eventPushTask.WaitAsync(ct); } catch { }

        await _controller.UnRegisterAsync(ct);
    }

    // ── Heartbeat ──────────────────────────────────────────────────────

    private async Task RunHeartbeatLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(_settings.HeartbeatIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct);

                var sysMetrics = _metrics.Collect();
                await _controller.SendHeartbeatAsync(_executor.CurrentState, sysMetrics, ct);

                // Also broadcast as a local event so subscribers see it
                _broadcaster.Publish(new ExecutionEvent
                {
                    ExecutionId = "",
                    AgentName   = _settings.GetResolvedEndpoint(),
                    Timestamp   = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
                    EventType   = ExecutionEventType.EventHeartbeat,
                    AgentState  = _executor.CurrentState,
                    Metrics     = sysMetrics,
                    Detail      = $"CPU {sysMetrics.CpuUsagePct}% | Mem {sysMetrics.MemoryUsedMb}MB | Disk {sysMetrics.DiskFreeGb}GB free",
                });
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Heartbeat iteration failed");
            }
        }
    }

    // ── State-change forwarding ────────────────────────────────────────

    private async void OnStateChanged(object? sender, AgentState newState)
    {
        try { await _controller.UpdateStateAsync(newState); }
        catch (Exception ex) { _logger.LogWarning(ex, "State push failed"); }
    }

    public void Dispose()
    {
        _executor.StateChanged -= OnStateChanged;
        _cts?.Dispose();
    }
}
