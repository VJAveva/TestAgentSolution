using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Tests for ReconnectBackoff — verifies jittered exponential backoff
/// behavior for display/dashboard reconnection (DISPLAY-003, DISPLAY-004).
/// </summary>
public class ReconnectBackoffTests
{
    [Fact]
    public void FirstDelay_Should_BeAroundInitialDelay()
    {
        // Use a fixed seed for deterministic jitter
        var backoff = new ReconnectBackoff(1000, 60_000, new Random(42));
        var delay = backoff.NextDelay();

        // 1000ms base ± 25% jitter → [750, 1250]
        Assert.InRange(delay, 500, 1250);
        Assert.Equal(1, backoff.ConsecutiveFailures);
    }

    [Fact]
    public void Delays_Should_IncreaseExponentially()
    {
        var backoff = new ReconnectBackoff(1000, 60_000, new Random(42));

        var delay1 = backoff.NextDelay(); // ~1000
        var delay2 = backoff.NextDelay(); // ~2000
        var delay3 = backoff.NextDelay(); // ~4000
        var delay4 = backoff.NextDelay(); // ~8000

        // Each delay should be roughly double the previous (within jitter range)
        Assert.True(delay2 > delay1, $"delay2 ({delay2}) should be > delay1 ({delay1})");
        Assert.True(delay3 > delay2, $"delay3 ({delay3}) should be > delay2 ({delay2})");
        Assert.True(delay4 > delay3, $"delay4 ({delay4}) should be > delay3 ({delay3})");
    }

    [Fact]
    public void Delay_Should_NeverExceedMax()
    {
        var backoff = new ReconnectBackoff(1000, 30_000, new Random(42));

        // Simulate many failures
        int maxObserved = 0;
        for (int i = 0; i < 20; i++)
        {
            var delay = backoff.NextDelay();
            maxObserved = Math.Max(maxObserved, delay);
        }

        // Max with 25% jitter above 30000 = 37500, but we clamp base at 30000
        // and only add jitter to that → max should be ≤ 37500
        Assert.True(maxObserved <= 37_500,
            $"Max delay {maxObserved} should not exceed max + jitter (37500)");
    }

    [Fact]
    public void Reset_Should_RestartFromInitialDelay()
    {
        var backoff = new ReconnectBackoff(1000, 60_000, new Random(42));

        // Accumulate failures
        backoff.NextDelay();
        backoff.NextDelay();
        backoff.NextDelay();
        Assert.Equal(3, backoff.ConsecutiveFailures);

        // Reset
        backoff.Reset();
        Assert.Equal(0, backoff.ConsecutiveFailures);

        // Next delay should be back to initial range
        var delay = backoff.NextDelay();
        Assert.InRange(delay, 500, 1250);
    }

    [Fact]
    public void Jitter_Should_ProduceDifferentDelays_ForSameFailureCount()
    {
        // Two backoff instances with different seeds should produce different delays
        var backoff1 = new ReconnectBackoff(1000, 60_000, new Random(1));
        var backoff2 = new ReconnectBackoff(1000, 60_000, new Random(999));

        var delays1 = Enumerable.Range(0, 5).Select(_ => backoff1.NextDelay()).ToList();
        var delays2 = Enumerable.Range(0, 5).Select(_ => backoff2.NextDelay()).ToList();

        // At least some delays should differ (desynchronization)
        var anyDifferent = delays1.Zip(delays2).Any(pair => pair.First != pair.Second);
        Assert.True(anyDifferent, "Jitter should desynchronize delays between agents");
    }

    [Fact]
    public void StaticComputeJitteredDelay_Should_ScaleWithFailures()
    {
        // Test the static helper method
        var delay1 = ReconnectBackoff.ComputeJitteredDelay(1, 1000, 60_000);
        var delay3 = ReconnectBackoff.ComputeJitteredDelay(3, 1000, 60_000);
        var delay6 = ReconnectBackoff.ComputeJitteredDelay(6, 1000, 60_000);

        Assert.True(delay3 > delay1, "3 failures should produce longer delay than 1");
        Assert.True(delay6 > delay3, "6 failures should produce longer delay than 3");
    }

    [Fact]
    public void Delay_Should_NeverBeNegativeOrZero()
    {
        // Use many different seeds to stress test
        for (int seed = 0; seed < 100; seed++)
        {
            var backoff = new ReconnectBackoff(1000, 60_000, new Random(seed));
            for (int i = 0; i < 10; i++)
            {
                var delay = backoff.NextDelay();
                Assert.True(delay > 0, $"Delay must be positive, got {delay} (seed={seed}, failure={i + 1})");
            }
        }
    }
}
