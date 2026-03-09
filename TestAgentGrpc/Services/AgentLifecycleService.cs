using Grpc.Core;
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
    private readonly ConnectionHealthMonitor _healthMonitor;
    private readonly AuditLogger _audit;
    private readonly AgentSettings _settings;
    private readonly AuditSettings _auditSettings;
    private readonly ILogger<AgentLifecycleService> _logger;

    private CancellationTokenSource? _cts;
    private Task? _heartbeatTask;
    private Task? _eventPushTask;
    private IDisposable? _eventPushSubscription;
    private volatile bool _registeredWithController;

    /// <summary>True when the agent has an active registration with the controller.</summary>
    public bool IsRegistered => _registeredWithController;

    public AgentLifecycleService(
        TestControllerClient controller,
        CommandExecutor executor,
        EventBroadcaster broadcaster,
        SystemMetricsCollector metrics,
        ConnectionHealthMonitor healthMonitor,
        AuditLogger audit,
        IOptions<AgentSettings> settings,
        IOptions<AuditSettings> auditSettings,
        ILogger<AgentLifecycleService> logger)
    {
        _controller    = controller;
        _executor      = executor;
        _broadcaster   = broadcaster;
        _metrics       = metrics;
        _healthMonitor = healthMonitor;
        _audit         = audit;
        _settings      = settings.Value;
        _auditSettings = auditSettings.Value;
        _logger        = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _logger.LogInformation("Agent lifecycle starting…");

        _audit.Log("AgentStarted", detail: $"Agent {_settings.AgentName} starting on port {_settings.GrpcPort}");

        // Log resolved configuration for diagnostics
        _logger.LogInformation(
            "Configuration: AgentName={Name}, ControllerAddress={Ctrl}, Endpoint={Ep}",
            _settings.AgentName, _settings.ControllerAddress, _settings.GetResolvedEndpoint());

        // Validate controller address early
        if (!ValidateControllerAddress(_settings.ControllerAddress))
        {
            _logger.LogError(
                "ControllerAddress '{Address}' is invalid. " +
                "Check AgentSettings:ControllerAddress in appsettings.json. " +
                "Expected format: http://hostname:port",
                _settings.ControllerAddress);
            _audit.Log("ConfigurationError", severity: "Error",
                detail: $"Invalid ControllerAddress: {_settings.ControllerAddress}");
        }

        // Wire state changes → controller push
        _executor.StateChanged += OnStateChanged;

        // Register with retries (non-blocking — agent stays Ready regardless)
        var (registered, regError) = await _controller.RegisterAsync(ct);
        _registeredWithController = registered;
        if (_registeredWithController)
        {
            _healthMonitor.RecordRegistration(
                _settings.AgentName, _settings.ControllerAddress);
            _audit.Log("RegistrationAcked", source: _settings.ControllerAddress,
                controller: _settings.ControllerAddress);
        }
        else
        {
            _logger.LogWarning(
                "Controller registration failed — will retry during heartbeat loop. Error: {Error}",
                regError);
            _audit.Log("RegistrationFailed", severity: "Warning",
                detail: $"Initial registration failed: {regError}",
                controller: _settings.ControllerAddress);
        }

        // Start heartbeat loop (will silently fail if controller unreachable)
        _heartbeatTask = RunHeartbeatLoopAsync(_cts.Token);

        // Start event push stream to controller (auto-reconnects on failure)
        var (reader, subscription) = _broadcaster.Subscribe(capacity: 5000);
        _eventPushSubscription = subscription;
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
        _healthMonitor.RecordUnregistration();
        _audit.Log("AgentStopped", detail: "Agent shutting down gracefully");
    }

    // ── Heartbeat ──────────────────────────────────────────────────────

    private async Task RunHeartbeatLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(_settings.HeartbeatIntervalSeconds);
        int consecutiveFailures = 0;
        long heartbeatCount = 0;
        bool controllerLost = false;
        var logEveryN = _auditSettings.LogHeartbeatEveryN;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct);

                // Re-register if previous registration failed or controller may have restarted
                if (!_registeredWithController || consecutiveFailures >= 3)
                {
                    _logger.LogInformation("Attempting re-registration with controller…");
                    var (regOk, regError) = await _controller.RegisterAsync(ct);
                    _registeredWithController = regOk;
                    if (regOk)
                    {
                        consecutiveFailures = 0;
                        _healthMonitor.RecordRegistration(
                            _settings.AgentName, _settings.ControllerAddress);
                        _audit.Log("RegistrationAcked", source: _settings.ControllerAddress,
                            controller: _settings.ControllerAddress);

                        if (controllerLost)
                        {
                            controllerLost = false;
                            _audit.Log("ControllerRecovered",
                                controller: _settings.ControllerAddress,
                                detail: "Connection recovered after failures");
                        }

                        _logger.LogInformation("Re-registration successful");
                    }
                    else
                    {
                        // Registration failed — count as a failure and skip heartbeat
                        consecutiveFailures++;
                        _healthMonitor.RecordHeartbeatFailure($"Registration failed: {regError}");
                        _audit.Log("RegistrationFailed", severity: "Warning",
                            detail: $"Re-registration failed: {regError}",
                            controller: _settings.ControllerAddress);
                        _logger.LogWarning(
                            "Re-registration failed (consecutive: {Count}): {Error}",
                            consecutiveFailures, regError);

                        if (consecutiveFailures >= 3 && !controllerLost)
                        {
                            controllerLost = true;
                            _audit.Log("ControllerLost", severity: "Error",
                                controller: _settings.ControllerAddress,
                                detail: $"Lost connection after {consecutiveFailures} consecutive failures. Last error: {regError}");
                        }

                        continue; // Skip heartbeat — can't send to a controller we're not registered with
                    }
                }

                var sysMetrics = _metrics.Collect();
                await _controller.SendHeartbeatAsync(_executor.CurrentState, sysMetrics, ct);
                consecutiveFailures = 0;
                heartbeatCount++;
                _healthMonitor.RecordHeartbeatSuccess();

                // Log every Nth heartbeat to avoid noise
                if (logEveryN > 0 && heartbeatCount % logEveryN == 0)
                {
                    _audit.Log("HeartbeatAcked",
                        detail: $"Heartbeat #{heartbeatCount}, CPU {sysMetrics.CpuUsagePct}%, Mem {sysMetrics.MemoryUsedMb}MB");
                }

                _broadcaster.Publish(new ExecutionEvent
                {
                    ExecutionId = "",
                    AgentName   = _settings.AgentName,
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
                consecutiveFailures++;
                var errorDetail = ex is RpcException rpc
                    ? $"gRPC {rpc.StatusCode}: {rpc.Status.Detail}"
                    : ex.Message;
                _healthMonitor.RecordHeartbeatFailure(errorDetail);
                _audit.Log("HeartbeatFailed", severity: "Warning",
                    detail: errorDetail, controller: _settings.ControllerAddress);

                if (consecutiveFailures >= 3 && !controllerLost)
                {
                    controllerLost = true;
                    _audit.Log("ControllerLost", severity: "Error",
                        controller: _settings.ControllerAddress,
                        detail: $"Lost connection after {consecutiveFailures} consecutive heartbeat failures. Last error: {errorDetail}");
                }

                _logger.LogWarning(ex, "Heartbeat iteration failed (consecutive: {Count})", consecutiveFailures);
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
        _eventPushSubscription?.Dispose();
        _cts?.Dispose();
    }

    /// <summary>
    /// Performs an on-demand re-registration cycle: unregister → register.
    /// Returns (success, errorMessage).
    /// </summary>
    public async Task<(bool Success, string? Error)> ReRegisterAsync()
    {
        _logger.LogInformation("Manual re-registration requested");
        _audit.Log("ReRegistrationRequested", detail: "Manual re-registration triggered from tray menu");

        try
        {
            // Unregister first to clear stale state on the controller
            await _controller.UnRegisterAsync();
            _healthMonitor.RecordUnregistration();
            _registeredWithController = false;

            // Brief pause to let the controller process the unregistration
            await Task.Delay(500);

            // Re-register
            var (success, error) = await _controller.RegisterAsync();
            _registeredWithController = success;

            if (success)
            {
                _healthMonitor.RecordRegistration(
                    _settings.AgentName, _settings.ControllerAddress);
                _audit.Log("ReRegistrationSuccess",
                    source: _settings.ControllerAddress,
                    controller: _settings.ControllerAddress,
                    detail: "Manual re-registration successful");
                _logger.LogInformation("Manual re-registration successful");
            }
            else
            {
                _audit.Log("ReRegistrationFailed", severity: "Warning",
                    detail: $"Manual re-registration failed: {error}",
                    controller: _settings.ControllerAddress);
                _logger.LogWarning("Manual re-registration failed: {Error}", error);
            }

            return (success, error);
        }
        catch (Exception ex)
        {
            var msg = $"Re-registration error: {ex.Message}";
            _logger.LogError(ex, "Manual re-registration failed");
            _audit.Log("ReRegistrationFailed", severity: "Error", detail: msg);
            return (false, msg);
        }
    }

    // ── Configuration validation ───────────────────────────────────────

    private bool ValidateControllerAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return false;

        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme is not ("http" or "https"))
            return false;

        // Catch common placeholder/template values that won't resolve
        var host = uri.Host;
        if (host.Contains("controller-machine", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("your-", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("example", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "ControllerAddress '{Address}' looks like a placeholder. " +
                "Update AgentSettings:ControllerAddress in appsettings.json with the actual controller hostname.",
                address);
            return false;
        }

        return true;
    }
}
