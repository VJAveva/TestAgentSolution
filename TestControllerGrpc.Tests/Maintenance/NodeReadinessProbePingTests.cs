using System.Diagnostics;
using Moq;
using TestControllerGrpc.Core.Maintenance;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Maintenance;

/// <summary>
/// Wave 0: <c>WaitForPingAsync</c> read 0% because every operation test mocks <see cref="INodeReadinessProbe"/>,
/// so the real implementation had never executed. It decides whether a reverted machine is back, and a wrong
/// answer either strands a healthy node in quarantine or returns a dead one to rotation.
/// </summary>
public sealed class NodeReadinessProbePingTests
{
    // Reserved by RFC 6761 to never resolve, so this is a deterministic "never answers" host on any network.
    private const string UnreachableHost = "tc-probe-does-not-exist.invalid";

    private static NodeReadinessProbe Probe() => new(new Mock<IAgentGrpcDispatcher>().Object);

    [Fact]
    public async Task WaitForPingAsync_Should_ReportCancelled_When_CancelledDuringTheBootDelay()
    {
        var options = new PingOptions
        {
            BootDelay = TimeSpan.FromMinutes(5),
            Timeout = TimeSpan.FromSeconds(30),
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var watch = Stopwatch.StartNew();
        ReadinessResult result = await Probe().WaitForPingAsync("localhost", options, cts.Token);
        watch.Stop();

        Assert.False(result.Succeeded);
        Assert.Contains("boot delay", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        // Waiting out the full 5 minutes would mean the token was never observed.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task WaitForPingAsync_Should_Succeed_When_TheHostAnswers()
    {
        var options = new PingOptions
        {
            BootDelay = TimeSpan.Zero,
            Interval = TimeSpan.FromMilliseconds(50),
            Timeout = TimeSpan.FromSeconds(20),
            RequiredConsecutiveReplies = 2,
        };

        ReadinessResult result = await Probe().WaitForPingAsync("127.0.0.1", options, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task WaitForPingAsync_Should_GiveUp_When_TheHostNeverAnswers()
    {
        var options = new PingOptions
        {
            BootDelay = TimeSpan.Zero,
            Interval = TimeSpan.FromMilliseconds(50),
            Timeout = TimeSpan.FromSeconds(2),
            RequiredConsecutiveReplies = 1,
        };

        ReadinessResult result = await Probe().WaitForPingAsync(UnreachableHost, options, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
    }

    [Fact]
    public async Task WaitForPingAsync_Should_StillPing_When_TheBootDelayExceedsTheTimeout()
    {
        // Regression guard for the bug the code comments record: one shared stopwatch meant a BootDelay
        // >= Timeout burned the whole budget before the first probe, reporting "no replies" without pinging.
        var options = new PingOptions
        {
            BootDelay = TimeSpan.FromMilliseconds(400),
            Interval = TimeSpan.FromMilliseconds(50),
            Timeout = TimeSpan.FromMilliseconds(200),   // smaller than the boot delay
            RequiredConsecutiveReplies = 1,
        };

        ReadinessResult result = await Probe().WaitForPingAsync("127.0.0.1", options, CancellationToken.None);

        Assert.True(result.Succeeded, "the ping budget must start after the boot delay, not alongside it");
    }

    [Fact]
    public async Task WaitForPingAsync_Should_ReportCancelled_When_CancelledWhileAwaitingReplies()
    {
        var options = new PingOptions
        {
            BootDelay = TimeSpan.Zero,
            Interval = TimeSpan.FromMilliseconds(50),
            Timeout = TimeSpan.FromMinutes(5),
            RequiredConsecutiveReplies = 50,   // never satisfied before cancellation
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var watch = Stopwatch.StartNew();
        ReadinessResult result = await Probe().WaitForPingAsync(UnreachableHost, options, cts.Token);
        watch.Stop();

        Assert.False(result.Succeeded);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"took {watch.Elapsed}");
    }
}
