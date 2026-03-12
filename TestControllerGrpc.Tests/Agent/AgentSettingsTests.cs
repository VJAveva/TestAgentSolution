extern alias AgentAlias;
using AgentAlias::TestAgentGrpc;

namespace TestControllerGrpc.Tests.Agent;

public class AgentSettingsTests
{
    [Fact]
    public void AgentSettings_Should_HaveDefaultAgentName_When_Created()
    {
        var settings = new AgentSettings();
        Assert.Equal(Environment.MachineName, settings.AgentName);
    }

    [Fact]
    public void AgentSettings_Should_HaveDefaultGrpcPort_When_Created()
    {
        var settings = new AgentSettings();
        Assert.Equal(5200, settings.GrpcPort);
    }

    [Fact]
    public void AgentSettings_Should_HaveDefaultControllerAddress_When_Created()
    {
        var settings = new AgentSettings();
        Assert.Equal("http://localhost:5100", settings.ControllerAddress);
    }

    [Fact]
    public void AgentSettings_Should_HaveNullAgentEndpoint_When_Created()
    {
        var settings = new AgentSettings();
        Assert.Null(settings.AgentEndpoint);
    }

    [Fact]
    public void AgentSettings_Should_HaveReasonableRetryDefaults_When_Created()
    {
        var settings = new AgentSettings();
        Assert.Equal(3, settings.RegistrationRetryCount);
        Assert.Equal(30, settings.RegistrationRetryIntervalSeconds);
    }

    [Fact]
    public void AgentSettings_Should_HaveHeartbeatInterval_When_Created()
    {
        var settings = new AgentSettings();
        Assert.Equal(15, settings.HeartbeatIntervalSeconds);
    }

    [Fact]
    public void AgentSettings_Should_HaveHistoryLimits_When_Created()
    {
        var settings = new AgentSettings();
        Assert.Equal(200, settings.MaxExecutionHistoryCount);
        Assert.Equal(5000, settings.MaxOutputLinesPerExecution);
    }

    [Fact]
    public void AgentSettings_Should_CollectMetricsByDefault_When_Created()
    {
        var settings = new AgentSettings();
        Assert.True(settings.CollectSystemMetrics);
    }

    [Fact]
    public void AgentSettings_Should_AllowOverride_When_PropertiesSet()
    {
        var settings = new AgentSettings
        {
            AgentName = "CustomAgent",
            GrpcPort = 9999,
            ControllerAddress = "http://controller:5100",
            AgentEndpoint = "http://custom:5200",
            HeartbeatIntervalSeconds = 60,
        };

        Assert.Equal("CustomAgent", settings.AgentName);
        Assert.Equal(9999, settings.GrpcPort);
        Assert.Equal("http://controller:5100", settings.ControllerAddress);
        Assert.Equal("http://custom:5200", settings.AgentEndpoint);
        Assert.Equal(60, settings.HeartbeatIntervalSeconds);
    }
}
