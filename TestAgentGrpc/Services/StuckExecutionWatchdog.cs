namespace TestAgentGrpc.Services;

/// <summary>
/// Background watchdog that detects if the agent is stuck in "Running" state
/// beyond the maximum allowed execution time. If the normal cancellation flow
/// (CTS timeout → kill process → finally block) has somehow failed, this
/// watchdog forcibly resets the agent state as a last resort.
///
/// This prevents the agent from being permanently stuck in "busy" state
/// and requiring a manual service restart.
/// </summary>
public sealed class StuckExecutionWatchdog : BackgroundService
{
    private readonly CommandExecutor _executor;
    private readonly AgentSettings _settings;
    private readonly ILogger<StuckExecutionWatchdog> _logger;

    public StuckExecutionWatchdog(
        CommandExecutor executor,
        Microsoft.Extensions.Options.IOptions<AgentSettings> settings,
        ILogger<StuckExecutionWatchdog> logger)
    {
        _executor = executor;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Check every 60 seconds
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (_executor.CurrentState != AgentState.Running)
                continue;

            var startedUtc = _executor.ExecutionStartedUtc;
            if (startedUtc is null)
                continue;

            var elapsed = DateTime.UtcNow - startedUtc.Value;
            // Use the executor's actual timeout for this execution (which may be much
            // longer than MaxExecutionTimeoutMinutes if the controller specified a large
            // Timeout in the action XML — e.g. 8-hour installs). The watchdog adds a
            // grace period beyond the effective timeout as a nuclear fallback.
            var effectiveTimeoutMinutes = _executor.CurrentTimeoutMinutes;
            var maxAllowed = TimeSpan.FromMinutes(effectiveTimeoutMinutes + _settings.WatchdogGraceMinutes);

            if (elapsed > maxAllowed)
            {
                _logger.LogError(
                    "WATCHDOG: Agent stuck in Running state for {Elapsed} (limit: {Max}). " +
                    "Forcing state reset. Execution: {Cmd}",
                    elapsed.ToString(@"hh\:mm\:ss"),
                    maxAllowed.ToString(@"hh\:mm\:ss"),
                    _executor.CurrentCommand);

                _executor.ForceReady();
            }
        }
    }
}
