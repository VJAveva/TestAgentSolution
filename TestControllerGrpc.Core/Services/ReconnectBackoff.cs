namespace TestControllerGrpc.Services;

/// <summary>
/// Computes jittered exponential backoff delays for gRPC/SignalR reconnections.
/// Shared between TestAgentDisplay and TestController.Dashboard to prevent
/// synchronized reconnection storms across multiple agents (DISPLAY-003).
/// </summary>
public sealed class ReconnectBackoff
{
    private readonly int _initialDelayMs;
    private readonly int _maxDelayMs;
    private readonly Random _rng;
    private int _consecutiveFailures;

    public ReconnectBackoff(int initialDelayMs = 1000, int maxDelayMs = 60_000, Random? rng = null)
    {
        _initialDelayMs = initialDelayMs;
        _maxDelayMs = maxDelayMs;
        _rng = rng ?? Random.Shared;
    }

    /// <summary>Current number of consecutive failures.</summary>
    public int ConsecutiveFailures => _consecutiveFailures;

    /// <summary>Records a failure and returns the next jittered backoff delay in milliseconds.</summary>
    public int NextDelay()
    {
        _consecutiveFailures++;
        return ComputeDelay(_consecutiveFailures);
    }

    /// <summary>Resets the failure counter on successful connection.</summary>
    public void Reset() => _consecutiveFailures = 0;

    /// <summary>
    /// Computes a jittered backoff delay: base * 2^(failures-1) ± 25% jitter.
    /// Capped at <see cref="_maxDelayMs"/>.
    /// </summary>
    private int ComputeDelay(int failures)
    {
        // Exponential: 1s, 2s, 4s, 8s, 16s, 32s, 60s cap
        var exponent = Math.Min(failures - 1, 6);
        var baseDelay = _initialDelayMs * (1 << exponent);
        baseDelay = Math.Min(baseDelay, _maxDelayMs);

        // ±25% jitter to desynchronize multiple agents
        var jitterRange = baseDelay / 4;
        var jitter = jitterRange > 0 ? _rng.Next(-jitterRange, jitterRange) : 0;

        return Math.Max(_initialDelayMs / 2, baseDelay + jitter);
    }

    /// <summary>
    /// Computes a one-off jittered delay without mutating state.
    /// Useful for testing or scenarios where you manage failure counts externally.
    /// </summary>
    public static int ComputeJitteredDelay(int failures, int initialDelayMs = 1000, int maxDelayMs = 60_000)
    {
        var exponent = Math.Min(failures - 1, 6);
        var baseDelay = initialDelayMs * (1 << exponent);
        baseDelay = Math.Min(baseDelay, maxDelayMs);
        var jitterRange = baseDelay / 4;
        var jitter = jitterRange > 0 ? Random.Shared.Next(-jitterRange, jitterRange) : 0;
        return Math.Max(initialDelayMs / 2, baseDelay + jitter);
    }
}
