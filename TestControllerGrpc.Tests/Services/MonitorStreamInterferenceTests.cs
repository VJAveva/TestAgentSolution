using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// TEST-001: Integration simulation verifying that monitor/polling operations
/// correctly detect and defer to an active execution stream.
///
/// Simulates a scenario where:
/// 1. An agent is executing (active stream in flight)
/// 2. Monitor polls (TestConnection/Ping/Diagnose) arrive concurrently
/// 3. All polling operations return synthetic/cached data without gRPC calls
/// 4. Once execution completes, polling resumes normal gRPC path
///
/// Category: Integration, Safety
/// </summary>
public class MonitorStreamInterferenceTests : IDisposable
{
    private readonly AgentGrpcDispatcher _dispatcher;
    private readonly ConcurrentDictionary<string, string> _activeExecutions;

    public MonitorStreamInterferenceTests()
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

    // ═══════════════════════════════════════════════════════════════════
    // Concurrent monitor + execution scenario
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ConcurrentMonitorPolling_DoesNotInterfere_WithActiveExecution()
    {
        // Arrange: Agent is running a long install command
        _dispatcher.RegisterAgent("Agent1", "http://192.168.255.255:5200");
        _activeExecutions["Agent1"] = "Install-Build.bat \\\\server\\share\\drop";

        // Act: Simulate monitor polling burst (5 concurrent calls)
        var tasks = Enumerable.Range(0, 5).Select(_ => Task.WhenAll(
            _dispatcher.TestConnectionAsync("Agent1"),
            Task.FromResult(_dispatcher.PingAsync("Agent1").Result),
            _dispatcher.DiagnoseAgentAsync("Agent1")
        )).ToArray();

        await Task.WhenAll(tasks);

        // Assert: All calls completed without timeout (would hang if gRPC was attempted)
        Assert.True(_dispatcher.IsAgentExecuting("Agent1"));
    }

    [Fact]
    public async Task MonitorPolling_ReturnsSyntheticData_DuringExecution_ThenRealGrpc_After()
    {
        _dispatcher.RegisterAgent("Agent1", "http://192.168.255.255:5200");

        // Phase 1: During execution — all operations return synthetic
        _activeExecutions["Agent1"] = "test-harness.bat";

        var (snapshot, error) = await _dispatcher.TestConnectionAsync("Agent1");
        Assert.NotNull(snapshot);
        Assert.Null(error);
        Assert.Equal(TestAgentGrpc.AgentState.Running, snapshot.State);
        Assert.True(await _dispatcher.PingAsync("Agent1"));

        var steps = await _dispatcher.DiagnoseAgentAsync("Agent1");
        Assert.Equal(2, steps.Count);
        Assert.Equal("Execution Guard", steps[1].Name);

        // Phase 2: Execution completes
        _activeExecutions.TryRemove("Agent1", out _);
        Assert.False(_dispatcher.IsAgentExecuting("Agent1"));

        // Phase 3: Monitor poll attempts real gRPC (fails due to unreachable)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var (snapshot2, error2) = await _dispatcher.TestConnectionAsync("Agent1", cts.Token);
        Assert.Null(snapshot2);
        Assert.NotNull(error2); // Real gRPC call failed
    }

    [Fact]
    public async Task MultipleAgents_Executing_AllProtected_Independently()
    {
        // Register 3 agents at unreachable IPs
        _dispatcher.RegisterAgent("AgentA", "http://192.168.255.1:5200");
        _dispatcher.RegisterAgent("AgentB", "http://192.168.255.2:5200");
        _dispatcher.RegisterAgent("AgentC", "http://192.168.255.3:5200");

        // Only A and B are executing
        _activeExecutions["AgentA"] = "install-a.bat";
        _activeExecutions["AgentB"] = "install-b.bat";

        // A and B should return synthetic data instantly
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (snapA, _) = await _dispatcher.TestConnectionAsync("AgentA");
        var (snapB, _) = await _dispatcher.TestConnectionAsync("AgentB");
        sw.Stop();

        Assert.NotNull(snapA);
        Assert.NotNull(snapB);
        Assert.True(sw.ElapsedMilliseconds < 200, $"Synthetic calls took {sw.ElapsedMilliseconds}ms");

        // C should attempt real gRPC (will fail/timeout)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var (snapC, errC) = await _dispatcher.TestConnectionAsync("AgentC", cts.Token);
        Assert.Null(snapC);
        Assert.NotNull(errC);
    }

    [Fact]
    public async Task MonitorCachedMetrics_PreservedDuringExecution()
    {
        _dispatcher.RegisterAgent("Agent1", "http://192.168.255.255:5200");
        _activeExecutions["Agent1"] = "run-tests.bat /parallel";

        // TestConnectionAsync returns synthetic snapshot with the command
        var (snapshot, _) = await _dispatcher.TestConnectionAsync("Agent1");

        Assert.NotNull(snapshot);
        Assert.Equal("Agent1", snapshot.AgentName);
        Assert.Contains("run-tests.bat", snapshot.CurrentCommand);
        Assert.Contains("Executing:", snapshot.CurrentActivity);
    }

    [Fact]
    public async Task DiagnoseAgent_SkipsNetworkSteps_WhenExecuting()
    {
        _dispatcher.RegisterAgent("Agent1", "http://192.168.255.255:5200");
        _activeExecutions["Agent1"] = "long-install.bat";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var steps = await _dispatcher.DiagnoseAgentAsync("Agent1");
        sw.Stop();

        // Execution guard should prevent all network steps
        Assert.True(sw.ElapsedMilliseconds < 100);
        Assert.Equal(2, steps.Count);
        Assert.Equal("Registration", steps[0].Name);
        Assert.True(steps[0].Passed);
        Assert.Equal("Execution Guard", steps[1].Name);
        Assert.True(steps[1].Passed);
        Assert.Contains("currently executing", steps[1].Detail);
    }

    [Fact]
    public async Task RapidPollingBurst_AllReturnConsistentSynthetic_WhenExecuting()
    {
        _dispatcher.RegisterAgent("Agent1", "http://192.168.255.255:5200");
        _activeExecutions["Agent1"] = "install.bat";

        // Simulate 20 rapid polls
        var results = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => _dispatcher.TestConnectionAsync("Agent1")));

        // All should be synthetic with consistent state
        foreach (var (snapshot, error) in results)
        {
            Assert.NotNull(snapshot);
            Assert.Null(error);
            Assert.Equal(TestAgentGrpc.AgentState.Running, snapshot.State);
            Assert.Equal("Agent1", snapshot.AgentName);
        }
    }
}
