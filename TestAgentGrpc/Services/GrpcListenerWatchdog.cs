using System.Net.Security;
using System.Net.Sockets;
using Grpc.Core;
using Grpc.Health.V1;
using Grpc.Net.Client;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Services;

namespace TestAgentGrpc.Services;

/// <summary>
/// Self-healing watchdog that periodically verifies the agent's own gRPC
/// listener is still serving. It runs a two-stage self-check on loopback:
///   Stage 1 (cheap): is anything accepting TCP connections on the gRPC port?
///   Stage 2 (real):  does the standard grpc.health.v1 service respond Serving?
/// A TCP-only check can pass while the gRPC pipeline is wedged ("zombie
/// listener"), so stage 2 is essential.
///
/// On <see cref="FailureThresholdBeforeRestart"/> consecutive failures the
/// watchdog exits the process with <see cref="ExitCodeRestartNeeded"/> so the
/// Windows Service Manager (configured via Configure-AgentRecovery.ps1 with
/// failureflag=1) restarts a FRESH instance that binds a clean port. In-process
/// listener rebinding is intentionally NOT attempted because Kestrel/gRPC port
/// rebinding in the same process is unreliable.
///
/// This addresses the defect where agents silently stop serving on the gRPC
/// port mid-pipeline, leaving the controller unable to dispatch commands
/// (including recovery reboots) with no diagnostic from the agent side.
/// </summary>
public sealed class GrpcListenerWatchdog : BackgroundService
{
    private readonly AgentSettings _settings;
    private readonly AgentKestrelOptions _kestrel;
    private readonly AuditLogger _audit;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<GrpcListenerWatchdog> _logger;

    /// <summary>How often to check the listener (seconds).</summary>
    private const int CheckIntervalSeconds = 30;

    /// <summary>Consecutive failures before triggering restart.</summary>
    private const int FailureThresholdBeforeRestart = 3;

    /// <summary>TCP connect timeout for the stage-1 self-check.</summary>
    private static readonly TimeSpan TcpTimeout = TimeSpan.FromSeconds(5);

    /// <summary>gRPC health-call timeout for the stage-2 self-check.</summary>
    private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Exit code signalling "restart needed". Distinct from a normal shutdown
    /// (0). The sc.exe recovery policy uses failureflag=1 so this non-zero exit
    /// is treated as a failure and the service is restarted.
    /// </summary>
    private const int ExitCodeRestartNeeded = 3;

    public GrpcListenerWatchdog(
        IOptions<AgentSettings> settings,
        IOptions<AgentKestrelOptions> kestrel,
        AuditLogger audit,
        IHostApplicationLifetime lifetime,
        ILogger<GrpcListenerWatchdog> logger)
    {
        _settings = settings.Value;
        _kestrel = kestrel.Value;
        _audit = audit;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for startup to complete before starting checks
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        _logger.LogInformation(
            "GrpcListenerWatchdog started. Monitoring gRPC port {Port} every {Sec}s " +
            "(TCP + grpc.health.v1 self-check).",
            _settings.GrpcPort, CheckIntervalSeconds);

        int consecutiveFailures = 0;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(CheckIntervalSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var healthy = await IsListenerHealthyAsync(stoppingToken);

            if (healthy)
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
                    "port {Port} not serving (TCP or grpc.health.v1)",
                    consecutiveFailures, FailureThresholdBeforeRestart, _settings.GrpcPort);

                _audit.Log("ListenerUnreachable", severity: "Error",
                    detail: $"Self-check failed #{consecutiveFailures}: port {_settings.GrpcPort} " +
                            "not serving (TCP or grpc.health.v1)");

                CrashDumpHelper.AppendCrashLog(
                    $"[ListenerWatchdog] SELF-CHECK FAIL #{consecutiveFailures}: " +
                    $"port {_settings.GrpcPort} not serving. " +
                    $"Threshold={FailureThresholdBeforeRestart}");

                if (consecutiveFailures >= FailureThresholdBeforeRestart)
                {
                    Heal(consecutiveFailures);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Two-stage health check: cheap TCP probe first, then a real gRPC health
    /// call. Both must pass for the listener to be considered healthy.
    /// </summary>
    private async Task<bool> IsListenerHealthyAsync(CancellationToken ct)
    {
        // Stage 1 — TCP listener present?
        if (!await IsTcpListeningAsync(ct))
        {
            _logger.LogWarning("Port {Port} has no TCP listener", _settings.GrpcPort);
            return false;
        }

        // Stage 2 — gRPC health service actually Serving?
        return await IsGrpcHealthyAsync(ct);
    }

    private async Task<bool> IsTcpListeningAsync(CancellationToken ct)
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

    /// <summary>
    /// Calls grpc.health.v1.Health/Check on loopback. Targets the plaintext
    /// h2c port by default; falls back to the TLS port (with cert validation
    /// bypassed for the loopback self-call) when the agent is TLS-only.
    /// </summary>
    private async Task<bool> IsGrpcHealthyAsync(CancellationToken ct)
    {
        var (address, insecureTls) = ResolveLoopbackTarget();

        try
        {
            var handler = new SocketsHttpHandler();
            if (insecureTls)
            {
                handler.SslOptions = new SslClientAuthenticationOptions
                {
                    // Loopback self-check only — the agent's own cert may be
                    // self-signed; we are not authenticating a remote peer here.
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                };
            }

            using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
            {
                HttpHandler = handler,
            });
            var client = new Health.HealthClient(channel);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(HealthCheckTimeout);

            var resp = await client.CheckAsync(
                new HealthCheckRequest(),
                cancellationToken: cts.Token);

            bool serving = resp.Status == HealthCheckResponse.Types.ServingStatus.Serving;
            if (!serving)
                _logger.LogWarning("grpc.health.v1 reports status: {Status}", resp.Status);
            return serving;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Respect host shutdown
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(
                "gRPC self-check failed: {Code} — {Detail}",
                ex.StatusCode, ex.Status.Detail);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "gRPC self-check threw unexpectedly");
            return false;
        }
    }

    /// <summary>
    /// Resolves the loopback address for the gRPC health call. Uses plaintext
    /// h2c on the gRPC port unless the agent is listening TLS-only on that port,
    /// in which case it uses HTTPS on the TLS port with cert validation bypassed.
    /// </summary>
    private (string address, bool insecureTls) ResolveLoopbackTarget()
    {
        // Plaintext listener is active unless TLS is enabled AND it reuses the
        // same port number as the plaintext gRPC port (TLS-only on that port).
        bool tlsOnlyOnGrpcPort = _kestrel.EnableTls && _kestrel.TlsPort == _settings.GrpcPort;
        return tlsOnlyOnGrpcPort
            ? ($"https://127.0.0.1:{_kestrel.TlsPort}", true)
            : ($"http://127.0.0.1:{_settings.GrpcPort}", false);
    }

    /// <summary>
    /// Self-heal by exiting cleanly with a non-zero code. The Windows Service
    /// Manager (sc.exe failureflag=1) treats this as a failure and restarts a
    /// fresh process that binds a clean port.
    /// </summary>
    private void Heal(int consecutiveFailures)
    {
        _logger.LogCritical(
            "gRPC listener confirmed DEAD after {Count} consecutive failures. " +
            "Self-healing: exiting with code {Code} so the service manager restarts a clean instance.",
            consecutiveFailures, ExitCodeRestartNeeded);

        _audit.Log("ListenerDead", severity: "Critical",
            detail: $"Listener dead after {consecutiveFailures} checks. " +
                    $"Exiting with code {ExitCodeRestartNeeded} for service recovery.");

        CrashDumpHelper.AppendCrashLog(
            $"[ListenerWatchdog] CRITICAL: Listener dead. " +
            $"Exiting with code {ExitCodeRestartNeeded} for service recovery. " +
            $"PID={Environment.ProcessId}");

        CrashDumpHelper.WriteMiniDump("ListenerWatchdog_Dead");

        // Non-zero exit code so the recovery policy (failureflag=1) restarts us.
        Environment.ExitCode = ExitCodeRestartNeeded;
        _lifetime.StopApplication();
    }
}
