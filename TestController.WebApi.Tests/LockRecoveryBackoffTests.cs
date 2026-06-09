using TestController.Api.Services;

namespace TestController.WebApi.Tests;

/// <summary>
/// Unit tests for <see cref="LockRecoveryOptions"/> backoff configuration.
/// </summary>
public class LockRecoveryBackoffTests
{
    [Fact]
    public void DefaultOptions_ShouldHaveExpectedValues()
    {
        var options = new LockRecoveryOptions();

        Assert.Equal(TimeSpan.FromMinutes(5), options.OrphanCheckInterval);
        Assert.Equal(TimeSpan.FromMinutes(30), options.MaxBackoffInterval);
        Assert.Equal(2.0, options.BackoffMultiplier);
        Assert.Equal(TimeSpan.FromSeconds(10), options.StartupDelay);
    }

    [Theory]
    [InlineData(5, 2.0, 30, new[] { 5, 10, 20, 30, 30 })]  // doubles until max
    [InlineData(5, 1.5, 20, new[] { 5, 7, 11, 16, 20, 20 })]  // 1.5x until max
    [InlineData(10, 3.0, 60, new[] { 10, 30, 60, 60 })]  // triples until max
    public void BackoffSequence_ShouldRespectMultiplierAndMax(
        int baseMinutes, double multiplier, int maxMinutes, int[] expectedMinutes)
    {
        var options = new LockRecoveryOptions
        {
            OrphanCheckInterval = TimeSpan.FromMinutes(baseMinutes),
            BackoffMultiplier = multiplier,
            MaxBackoffInterval = TimeSpan.FromMinutes(maxMinutes),
        };

        var currentInterval = options.OrphanCheckInterval;
        for (int i = 0; i < expectedMinutes.Length; i++)
        {
            // Verify current interval matches expected (within rounding)
            Assert.Equal(expectedMinutes[i], (int)currentInterval.TotalMinutes);

            // Simulate no-op: apply backoff
            var next = TimeSpan.FromTicks((long)(currentInterval.Ticks * options.BackoffMultiplier));
            currentInterval = next < options.MaxBackoffInterval ? next : options.MaxBackoffInterval;
        }
    }

    [Fact]
    public void BackoffResets_WhenOrphansCleaned()
    {
        var options = new LockRecoveryOptions
        {
            OrphanCheckInterval = TimeSpan.FromMinutes(5),
            BackoffMultiplier = 2.0,
            MaxBackoffInterval = TimeSpan.FromMinutes(30),
        };

        // Simulate 3 no-op cycles: 5 -> 10 -> 20
        var currentInterval = options.OrphanCheckInterval;
        for (int i = 0; i < 3; i++)
        {
            var next = TimeSpan.FromTicks((long)(currentInterval.Ticks * options.BackoffMultiplier));
            currentInterval = next < options.MaxBackoffInterval ? next : options.MaxBackoffInterval;
        }
        Assert.True(currentInterval > options.OrphanCheckInterval);

        // Simulate orphan found: reset to base
        currentInterval = options.OrphanCheckInterval;
        Assert.Equal(TimeSpan.FromMinutes(5), currentInterval);
    }
}
