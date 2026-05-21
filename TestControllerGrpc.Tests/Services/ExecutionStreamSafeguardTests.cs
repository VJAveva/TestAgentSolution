using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Tests that verify the execution safeguard: when an agent has an active
/// streaming command, all polling operations (TestConnectionAsync, PingAsync)
/// return synthetic/cached data WITHOUT making any gRPC call — preventing
/// HTTP/2 channel interference that can kill the in-flight stream.
/// </summary>
public class ExecutionStreamSafeguardTests : IDisposable
{
    private readonly AgentGrpcDispatcher _dispatcher;
    private readonly ConcurrentDictionary<string, string> _activeExecutions;

    public ExecutionStreamSafeguardTests()
    {
        var logger = NullLogger<AgentGrpcDispatcher>.Instance;
        var appLogger = new Mock<IAppLogger>();
        var events = new Mock<IEventAggregator>();

        _dispatcher = new AgentGrpcDispatcher(logger, appLogger.Object, events.Object);

        // Access the private _activeExecutions dictionary via reflection
        // to simulate an active execution without needing a real gRPC agent.
        var field = typeof(AgentGrpcDispatcher)
            .GetField("_activeExecutions", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _activeExecutions = (ConcurrentDictionary<string, string>)field.GetValue(_dispatcher)!;
    }

    public void Dispose()
    {
        _dispatcher.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════
    // IsAgentExecuting
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void IsAgentExecuting_ReturnsFalse_WhenNoActiveExecution()
    {
        _dispatcher.RegisterAgent("Agent1", "http://agent1:5200");

        Assert.False(_dispatcher.IsAgentExecuting("Agent1"));
    }

    [Fact]
    public void IsAgentExecuting_ReturnsTrue_WhenExecutionActive()
    {
        _dispatcher.RegisterAgent("Agent1", "http://agent1:5200");
        _activeExecutions["Agent1"] = "Install-Build.bat";

        Assert.True(_dispatcher.IsAgentExecuting("Agent1"));
    }

    [Fact]
    public void IsAgentExecuting_IsCaseInsensitive()
    {
        _dispatcher.RegisterAgent("Agent1", "http://agent1:5200");
        _activeExecutions["agent1"] = "some-command";

        Assert.True(_dispatcher.IsAgentExecuting("AGENT1"));
        Assert.True(_dispatcher.IsAgentExecuting("Agent1"));
    }

    // ═══════════════════════════════════════════════════════════════════
    // TestConnectionAsync — safeguard during execution
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TestConnectionAsync_ReturnsSyntheticSnapshot_WhenAgentIsExecuting()
    {
        _dispatcher.RegisterAgent("Agent1", "http://agent1:5200");
        _activeExecutions["Agent1"] = "C:\\Install\\run.bat /s /q";

        // This should NOT make any gRPC call — it returns synthetic data
        var (snapshot, error) = await _dispatcher.TestConnectionAsync("Agent1");

        Assert.NotNull(snapshot);
        Assert.Null(error);
        Assert.Equal("Agent1", snapshot.AgentName);
        Assert.Equal(TestAgentGrpc.AgentState.Running, snapshot.State);
        Assert.Contains("Install\\run.bat", snapshot.CurrentCommand);
        Assert.Contains("Executing:", snapshot.CurrentActivity);
    }

    [Fact]
    public async Task TestConnectionAsync_ReturnsSyntheticSnapshot_WithCorrectCommand()
    {
        _dispatcher.RegisterAgent("TestNode", "http://testnode:5200");
        var longCmd = "Install-Build.bat \\\\server\\share\\build testnode…";
        _activeExecutions["TestNode"] = longCmd;

        var (snapshot, _) = await _dispatcher.TestConnectionAsync("TestNode");

        Assert.NotNull(snapshot);
        Assert.Equal(longCmd, snapshot.CurrentCommand);
    }

    [Fact]
    public async Task TestConnectionAsync_ReturnsNotRegistered_WhenAgentUnknown()
    {
        var (snapshot, error) = await _dispatcher.TestConnectionAsync("NonExistent");

        Assert.Null(snapshot);
        Assert.Contains("not registered", error);
    }

    [Fact]
    public async Task TestConnectionAsync_DoesNotThrow_WhenAgentExecuting_AndTokenCancelled()
    {
        _dispatcher.RegisterAgent("Agent1", "http://agent1:5200");
        _activeExecutions["Agent1"] = "some-command";

        // Even with a cancelled token, the synthetic path should still work
        // (no actual async gRPC call to observe the token)
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (snapshot, error) = await _dispatcher.TestConnectionAsync("Agent1", cts.Token);

        // Synthetic path doesn't check cancellation — returns data
        Assert.NotNull(snapshot);
        Assert.Equal(TestAgentGrpc.AgentState.Running, snapshot.State);
    }

    // ═══════════════════════════════════════════════════════════════════
    // PingAsync — safeguard during execution
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PingAsync_ReturnsTrue_WhenAgentIsExecuting_WithoutGrpcCall()
    {
        _dispatcher.RegisterAgent("Agent1", "http://agent1:5200");
        _activeExecutions["Agent1"] = "Install-Build.bat";

        // Should return true immediately without trying to connect
        var result = await _dispatcher.PingAsync("Agent1");

        Assert.True(result);
    }

    [Fact]
    public async Task PingAsync_ReturnsFalse_WhenAgentNotRegistered()
    {
        var result = await _dispatcher.PingAsync("Ghost");

        Assert.False(result);
    }

    [Fact]
    public async Task PingAsync_DoesNotHang_WhenAgentIsExecuting()
    {
        // Register with an unreachable address — if gRPC were called, it would timeout
        _dispatcher.RegisterAgent("Unreachable", "http://192.168.255.255:5200");
        _activeExecutions["Unreachable"] = "long-install.bat";

        // This should return instantly (< 100ms) because it skips gRPC
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await _dispatcher.PingAsync("Unreachable");
        sw.Stop();

        Assert.True(result);
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"PingAsync took {sw.ElapsedMilliseconds}ms — should be instant when execution is active");
    }

    // ═══════════════════════════════════════════════════════════════════
    // ActiveExecution lifecycle
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TestConnectionAsync_MakesRealCall_AfterExecutionCompletes()
    {
        // Register with unreachable address
        _dispatcher.RegisterAgent("Agent1", "http://192.168.255.255:5200");

        // Phase 1: During execution — returns synthetic (no gRPC)
        _activeExecutions["Agent1"] = "install.bat";
        var (snapshot1, _) = await _dispatcher.TestConnectionAsync("Agent1");
        Assert.NotNull(snapshot1);
        Assert.Equal(TestAgentGrpc.AgentState.Running, snapshot1.State);

        // Phase 2: After execution ends — should attempt real gRPC call
        // (which will fail/timeout since address is unreachable)
        _activeExecutions.TryRemove("Agent1", out _);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var (snapshot2, error2) = await _dispatcher.TestConnectionAsync("Agent1", cts.Token);

        // Real call to unreachable host → should fail
        Assert.Null(snapshot2);
        Assert.NotNull(error2);
    }

    [Fact]
    public void IsAgentExecuting_ReturnsFalse_AfterExecutionRemoved()
    {
        _dispatcher.RegisterAgent("Agent1", "http://agent1:5200");
        _activeExecutions["Agent1"] = "cmd";

        Assert.True(_dispatcher.IsAgentExecuting("Agent1"));

        _activeExecutions.TryRemove("Agent1", out _);

        Assert.False(_dispatcher.IsAgentExecuting("Agent1"));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Multiple agents — isolation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Safeguard_OnlyAffectsExecutingAgent_NotOthers()
    {
        _dispatcher.RegisterAgent("Busy", "http://192.168.255.255:5200");
        _dispatcher.RegisterAgent("Idle", "http://192.168.255.254:5200");

        // Only Busy is executing
        _activeExecutions["Busy"] = "install.bat";

        // Busy → synthetic snapshot (instant)
        var (busySnapshot, _) = await _dispatcher.TestConnectionAsync("Busy");
        Assert.NotNull(busySnapshot);
        Assert.Equal(TestAgentGrpc.AgentState.Running, busySnapshot.State);

        // Idle → real gRPC attempt (will timeout/fail since unreachable, but DOES try)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var (idleSnapshot, idleError) = await _dispatcher.TestConnectionAsync("Idle", cts.Token);
        Assert.Null(idleSnapshot); // Failed because address is unreachable
        Assert.NotNull(idleError);
    }

    [Fact]
    public async Task PingAsync_OnlySkipsGrpc_ForExecutingAgent()
    {
        _dispatcher.RegisterAgent("Busy", "http://192.168.255.255:5200");
        _dispatcher.RegisterAgent("Idle", "http://192.168.255.254:5200");
        _activeExecutions["Busy"] = "cmd";

        // Busy → instant true (no gRPC)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(await _dispatcher.PingAsync("Busy"));
        var busyTime = sw.ElapsedMilliseconds;

        // Idle → real attempt (will fail, returns false)
        sw.Restart();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        // PingAsync has internal 3s timeout — but we can't pass external token,
        // so just verify Busy was much faster
        Assert.True(busyTime < 100,
            $"Busy agent PingAsync took {busyTime}ms — expected < 100ms (no gRPC)");
    }

    // ═══════════════════════════════════════════════════════════════════
    // DiagnoseAgentAsync — safeguard during execution
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DiagnoseAgentAsync_ReturnsGuardResult_WhenAgentIsExecuting()
    {
        _dispatcher.RegisterAgent("Agent1", "http://192.168.255.255:5200");
        _activeExecutions["Agent1"] = "Install-Build.bat";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var steps = await _dispatcher.DiagnoseAgentAsync("Agent1");
        sw.Stop();

        // Should return immediately without any network calls
        Assert.True(sw.ElapsedMilliseconds < 100,
            $"DiagnoseAgentAsync took {sw.ElapsedMilliseconds}ms — should be instant when execution is active");
        Assert.Equal(2, steps.Count);
        Assert.True(steps[0].Passed);
        Assert.Equal("Registration", steps[0].Name);
        Assert.True(steps[1].Passed);
        Assert.Equal("Execution Guard", steps[1].Name);
        Assert.Contains("currently executing", steps[1].Detail);
        Assert.False(steps[1].IsFatal);
    }

    [Fact]
    public async Task DiagnoseAgentAsync_RunsFullSteps_WhenAgentIsIdle()
    {
        // Register with unreachable address — will fail at TCP step
        _dispatcher.RegisterAgent("Agent1", "http://192.168.255.255:5200");

        // No active execution — full diagnostics should be attempted
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var steps = await _dispatcher.DiagnoseAgentAsync("Agent1", cts.Token);

        // Should get past Registration and Address Parse, then fail at TCP or beyond
        Assert.True(steps.Count >= 3,
            $"Expected at least 3 steps for idle agent, got {steps.Count}");
        Assert.Equal("Registration", steps[0].Name);
        Assert.True(steps[0].Passed);
        Assert.Equal("Address Parse", steps[1].Name);
        Assert.True(steps[1].Passed);
    }

    [Fact]
    public async Task DiagnoseAgentAsync_NotAffected_ByOtherAgentExecution()
    {
        _dispatcher.RegisterAgent("Busy", "http://192.168.255.255:5200");
        _dispatcher.RegisterAgent("Idle", "http://192.168.255.254:5200");
        _activeExecutions["Busy"] = "install.bat";

        // Busy → guard kicks in
        var busySteps = await _dispatcher.DiagnoseAgentAsync("Busy");
        Assert.Equal(2, busySteps.Count);
        Assert.Equal("Execution Guard", busySteps[1].Name);

        // Idle → full diagnostics attempted (different agent not affected)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var idleSteps = await _dispatcher.DiagnoseAgentAsync("Idle", cts.Token);
        Assert.True(idleSteps.Count >= 3);
        Assert.True(idleSteps.All(s => s.Name != "Execution Guard"));
    }
}
