using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Tests for <see cref="AgentGrpcDispatcher.ResetChannelAsync"/> — verifies
/// channel reset is safe (blocked during execution) and resets health state.
/// </summary>
public class ChannelResetTests : IDisposable
{
    private readonly AgentGrpcDispatcher _dispatcher;
    private readonly ConcurrentDictionary<string, string> _activeExecutions;

    public ChannelResetTests()
    {
        var logger = NullLogger<AgentGrpcDispatcher>.Instance;
        var appLogger = new Mock<IAppLogger>();
        var events = new Mock<IEventAggregator>();

        _dispatcher = new AgentGrpcDispatcher(logger, appLogger.Object, events.Object);

        var field = typeof(AgentGrpcDispatcher)
            .GetField("_activeExecutions", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _activeExecutions = (ConcurrentDictionary<string, string>)field.GetValue(_dispatcher)!;
    }

    public void Dispose() => _dispatcher.Dispose();

    [Fact]
    public async Task ResetChannel_ReturnsFalse_WhenAgentNotRegistered()
    {
        var result = await _dispatcher.ResetChannelAsync("Ghost");
        Assert.False(result);
    }

    [Fact]
    public async Task ResetChannel_ReturnsFalse_WhenAgentIsExecuting()
    {
        _dispatcher.RegisterAgent("Agent1", "http://agent1:5200");
        _activeExecutions["Agent1"] = "install.bat";

        var result = await _dispatcher.ResetChannelAsync("Agent1");

        Assert.False(result);
        // Agent should still be registered (not removed)
        Assert.Contains("Agent1", _dispatcher.RegisteredAgents);
    }

    [Fact]
    public async Task ResetChannel_ResetsHealthState()
    {
        _dispatcher.RegisterAgent("Agent1", "http://192.168.255.255:5200");

        // Simulate failures to mark unhealthy
        var healthField = typeof(AgentGrpcDispatcher)
            .GetField("_healthStates", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var healthStates = (ConcurrentDictionary<string, AgentHealthState>)healthField.GetValue(_dispatcher)!;

        healthStates["Agent1"].ConsecutiveFailures = 5;
        healthStates["Agent1"].IsHealthy = false;
        healthStates["Agent1"].CircuitOpenedUtc = DateTime.UtcNow;

        // Reset the channel (will fail ping since unreachable, but health should reset)
        await _dispatcher.ResetChannelAsync("Agent1");

        var health = _dispatcher.GetAgentHealth("Agent1");
        Assert.NotNull(health);
        Assert.Equal(0, health.ConsecutiveFailures);
        // IsHealthy may become false again after the failed ping, but that's expected
    }

    [Fact]
    public async Task ResetChannel_AgentStillRegistered_AfterReset()
    {
        _dispatcher.RegisterAgent("Agent1", "http://192.168.255.255:5200");

        await _dispatcher.ResetChannelAsync("Agent1");

        // Agent is still in registry
        Assert.Contains("Agent1", _dispatcher.RegisteredAgents);
        Assert.NotNull(_dispatcher.GetAgentAddress("Agent1"));
    }

    [Fact]
    public async Task ResetChannel_PreservesAddress()
    {
        _dispatcher.RegisterAgent("Agent1", "http://myhost:5200");

        await _dispatcher.ResetChannelAsync("Agent1");

        Assert.Equal("http://myhost:5200", _dispatcher.GetAgentAddress("Agent1"));
    }
}
