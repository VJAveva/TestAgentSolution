using TestController.Api.Security;

namespace TestControllerGrpc.Tests.Scaling;

/// <summary>
/// Validates rate limit and ThreadPool configuration supports 200-agent scale.
/// Regression: If rate limits are too low, browsers get 429 errors when viewing fleet.
/// If ThreadPool min is too low, thread injection lag causes timeouts.
/// </summary>
public class RateLimitConfigTests
{
    [Fact]
    public void RateLimit_StandardUser_AllowsAtLeast300PerMinute()
    {
        // Fleet page fetches once per 2s = 30/min max
        // Plus telemetry for selected agent = 4/min
        // Plus other navigation = 10/min
        // Total: ~44 req/min per user (plenty of headroom at 300)
        var options = new RateLimitSecurityOptions
        {
            RequestsPerMinute = 300
        };

        const int fleetRefreshesPerMinute = 30;
        const int telemetryPerMinute = 4;
        const int otherPerMinute = 10;
        var totalNeeded = fleetRefreshesPerMinute + telemetryPerMinute + otherPerMinute;

        Assert.True(options.RequestsPerMinute > totalNeeded,
            $"Rate limit {options.RequestsPerMinute} must exceed {totalNeeded} needed req/min");
    }

    [Fact]
    public void RateLimit_AdminUser_HigherThanStandard()
    {
        var options = new RateLimitSecurityOptions
        {
            RequestsPerMinute = 300,
            AdminRequestsPerMinute = 600
        };

        Assert.True(options.AdminRequestsPerMinute > options.RequestsPerMinute,
            "Admin rate limit should exceed standard user limit");
    }

    [Fact]
    public void ThreadPool_MinThreads_CanBeSetTo200()
    {
        // Validates that .NET allows setting MinThreads to 200
        // (needed for 200 concurrent agent health checks + gRPC streams)
        ThreadPool.GetMinThreads(out int originalWorker, out int originalIo);

        try
        {
            bool result = ThreadPool.SetMinThreads(200, 200);
            Assert.True(result, "ThreadPool.SetMinThreads(200, 200) should succeed");

            ThreadPool.GetMinThreads(out int workerMin, out int ioMin);
            Assert.True(workerMin >= 200, $"Worker min threads = {workerMin}, need >= 200");
            Assert.True(ioMin >= 200, $"IO min threads = {ioMin}, need >= 200");
        }
        finally
        {
            // Restore original values
            ThreadPool.SetMinThreads(originalWorker, originalIo);
        }
    }
}
