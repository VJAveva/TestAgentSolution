using System.Net.Sockets;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Services;

namespace TestAgentGrpc.Services;

/// <summary>
/// Background watchdog that periodically verifies the agent's own gRPC listener
/// (port 5200) is still accepting TCP connections. If the listener becomes
/// unreachable from localhost, the watchdog logs a critical diagnostic and
/// triggers a host restart via <see cref="IHostApplicationLifetime"/>.
///
/// This addresses the defect where agents silently stop listening on port 5200
/// mid-pipeline, leaving the controller unable to dispatch commands (including
/// recovery reboots) with no diagnostic from the agent side.
///
/// Scenarios detected:
///   - Kestrel listener died silently (port unbound)
///   - Another process stole port 5200
///   - Network stack/Winsock issue preventing local connections
/// </summary>
public sealed class GrpcListenerWatchdog : BackgroundService
{
    private readonly AgentSettings _settings;
    private readonly AuditLogger _audit;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<GrpcListenerWatchdog> _logger;

    /// <summary>How often to check the listener (seconds).</summary>
    private const int CheckIntervalSeconds = 30;

    /// <summary>Consecutive failures before triggering restart.</summary>
    private const int FailureThresholdBeforeRestart = 3;

    /// <summary>TCP connect timeout for the self-check.</summary>
    private static readonly TimeSpan TcpTimeout = TimeSpan.FromSeconds(5);

    public GrpcListenerWatchdog(
        IOptions<AgentSettings> settings,
        AuditLogger audit,
        IHostApplicationLifetime lifetime,
        ILogger<GrpcListenerWatchdog> logger)
    {
        _settings = settings.Value;
        _audit = audit;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for startup to complete before starting checks
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        int consecutiveFailures = 0;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(CheckIntervalSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var reachable = await CheckListenerAsync(stoppingToken);

            if (reachable)
            {
                if (consecutiveFailures > 0)
                {
                    _logger.LogInformation(
                        "gRPC listener self-check recovered after {Failures} consecutive failures",
                        consecutiveFailures);
                    _audit.Log("ListenerRecovered",
                        detail: $"Listener self-check passed after {consecutiveFailures} failures");
                }
                consecutiveFailures = 0;
            }
            else
            {
                consecutiveFailures++;
                _logger.LogError(
                    "gRPC listener self-check FAILED (attempt {Count}/{Threshold}) — " +
                    "port {Port} not accepting connections from localhost",
                    consecutiveFailures, FailureThresholdBeforeRestart, _settings.GrpcPort);

                _audit.Log("ListenerUnreachable", severity: "Error",
                    detail: $"Self-check failed #{consecutiveFailures}: port {_settings.GrpcPort} " +
                            "not accepting TCP connections from localhost");

                CrashDumpHelper.AppendCrashLog(
                    $"[ListenerWatchdog] SELF-CHECK FAIL #{consecutiveFailures}: " +
                    $"port {_settings.GrpcPort} unreachable from localhost. " +
                    $"Threshold={FailureThresholdBeforeRestart}");

                if (consecutiveFailures >= FailureThresholdBeforeRestart)
                {
                    _logger.LogCritical(
                        "gRPC listener confirmed DEAD after {Count} consecutive failures. " +
                        "Requesting host shutdown so Windows Service recovery can restart the agent.",
                        consecutiveFailures);

                    _audit.Log("ListenerDead", severity: "Critical",
                        detail: $"Listener dead after {consecutiveFailures} checks. " +
                                "Requesting graceful shutdown for service recovery.");

                    CrashDumpHelper.AppendCrashLog(
                        $"[ListenerWatchdog] CRITICAL: Listener dead. " +
                        $"Triggering host shutdown for service recovery. " +
                        $"PID={Environment.ProcessId}");

                    CrashDumpHelper.WriteMiniDump("ListenerWatchdog_Dead");

                    // Request graceful shutdown — Windows Service Manager (or sc.exe
                    // recovery config) will restart the process automatically.
                    _lifetime.StopApplication();
                    return;
                }
            }
        }
    }

    private async Task<bool> CheckListenerAsync(CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TcpTimeout);

            await client.ConnectAsync("127.0.0.1", _settings.GrpcPort, connectCts.Token);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Respect host shutdown
        }
        catch
        {
            return false;
        }
    }
}
