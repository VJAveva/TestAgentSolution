using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Tests that <see cref="ControllerTimeoutOptions"/> defaults match the
/// original hardcoded values — ensures the refactoring introduced no
/// behavioral change without explicit configuration.
/// </summary>
public class TimeoutOptionsTests
{
    [Fact]
    public void Defaults_MatchOriginalHardcodedValues()
    {
        var opts = new ControllerTimeoutOptions();

        Assert.Equal(5, opts.TestConnectionTimeoutSeconds);
        Assert.Equal(3, opts.PingTimeoutSeconds);
        Assert.Equal(3, opts.DiagnosticsTcpTimeoutSeconds);
        Assert.Equal(3, opts.DiagnosticsHttpTimeoutSeconds);
        Assert.Equal(5, opts.DiagnosticsGrpcTimeoutSeconds);
        Assert.Equal(30, opts.WaitForAgentFreeMaxSeconds);
        Assert.Equal(120, opts.BusyRecoveryMaxSeconds);
        Assert.Equal(10, opts.BusyRecoveryIntervalSeconds);
        Assert.Equal(30, opts.ChannelConnectTimeoutSeconds);
        Assert.Equal(60, opts.KeepAlivePingDelaySeconds);
        Assert.Equal(30, opts.KeepAlivePingTimeoutSeconds);
        Assert.Equal(5, opts.PooledConnectionIdleMinutes);
        Assert.Equal(15, opts.CircuitBreakerBreakSeconds);
        Assert.Equal(10, opts.AutoResetFailureThreshold);
        Assert.Equal(2000, opts.TelemetryPollIntervalMs);
        Assert.Equal(1000, opts.SessionElapsedTimerMs);
        Assert.Equal(24, opts.OuterSafetyNetTimeoutHours);
        Assert.Equal(3, opts.RetryMaxAttempts);
        Assert.Equal(1, opts.RetryDelaySeconds);
    }

    [Fact]
    public void SectionName_IsCorrect()
    {
        Assert.Equal("Controller:Timeouts", ControllerTimeoutOptions.SectionName);
    }

    [Fact]
    public void AllProperties_AreConfigurable()
    {
        var opts = new ControllerTimeoutOptions
        {
            TestConnectionTimeoutSeconds = 10,
            PingTimeoutSeconds = 5,
            BusyRecoveryMaxSeconds = 300,
            AutoResetFailureThreshold = 20,
        };

        Assert.Equal(10, opts.TestConnectionTimeoutSeconds);
        Assert.Equal(5, opts.PingTimeoutSeconds);
        Assert.Equal(300, opts.BusyRecoveryMaxSeconds);
        Assert.Equal(20, opts.AutoResetFailureThreshold);
    }
}
