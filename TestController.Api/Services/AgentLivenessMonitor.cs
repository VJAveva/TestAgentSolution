using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Services;

namespace TestController.Api.Services;

/// <summary>
/// Background service that actively probes idle agents on a short interval so the
/// fleet screen turns an agent red within seconds of it dying, rather than waiting
/// for a run to hang on an unreachable agent.
///
/// It does this by invoking <see cref="IAgentGrpcDispatcher.TestConnectionAsync"/>,
/// which already (a) safely skips agents that are mid-execution, (b) updates the
/// agent's health state via the dispatcher's RecordSuccess/RecordFailure logic, and
/// (c) fires the dispatcher's <see cref="IAgentGrpcDispatcher.StatusChanged"/> event.
/// The existing SignalRNotifier forwards that event as "AgentStatusChanged", which the
/// WebClient fleet view is already subscribed to, so no frontend change is required.
/// </summary>
public sealed class AgentLivenessMonitor : BackgroundService
{
    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly ILogger<AgentLivenessMonitor> _logger;
    private readonly AgentLivenessOptions _options;

    public AgentLivenessMonitor(
        IAgentGrpcDispatcher dispatcher,
        ILogger<AgentLivenessMonitor> logger,
        IOptions<AgentLivenessOptions> options)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("[AgentLiveness] Disabled via configuration; idle-agent probing will not run");
            return;
        }

        try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, _options.StartupDelaySeconds)), ct); }
        catch (OperationCanceledException) { return; }

        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.IntervalSeconds));
        using var timer = new PeriodicTimer(interval);

        _logger.LogInformation(
            "[AgentLiveness] Probing idle agents every {Interval}s", interval.TotalSeconds);

        do
        {
            try
            {
                await ProbeAllAgentsAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let a sweep failure kill the loop.
                _logger.LogWarning(ex, "[AgentLiveness] Sweep failed; will retry next interval");
            }
        }
        while (await SafeWaitForNextTickAsync(timer, ct));
    }

    private async Task ProbeAllAgentsAsync(CancellationToken ct)
    {
        var agents = _dispatcher.RegisteredAgents.ToArray();
        if (agents.Length == 0)
            return;

        using var gate = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentProbes));

        var probes = agents.Select(async agentName =>
        {
            await gate.WaitAsync(ct);
            try
            {
                // TestConnectionAsync safely no-ops for agents that are mid-execution and
                // handles its own exceptions, returning (null, error) on failure. It updates
                // health state and fires StatusChanged, which drives the fleet broadcast.
                await _dispatcher.TestConnectionAsync(agentName, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[AgentLiveness] Probe error for agent {Agent}", agentName);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(probes);
    }

    private static async Task<bool> SafeWaitForNextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
