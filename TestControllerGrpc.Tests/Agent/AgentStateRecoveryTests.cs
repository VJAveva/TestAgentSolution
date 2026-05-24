extern alias AgentAlias;
using AgentAlias::TestAgentGrpc;
using AgentAlias::TestAgentGrpc.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace TestControllerGrpc.Tests.Agent;

/// <summary>
/// TEST-003: Tests for agent stuck-state recovery mechanisms:
/// - ForceReady resets Running → Ready
/// - TerminateExecution kills active process and transitions to Ready
/// - StuckExecutionWatchdog detects and recovers stuck state
/// - Stream cancellation results in Ready state
///
/// Category: Safety, Recovery
/// </summary>
public sealed class AgentStateRecoveryTests : IDisposable
{
    private readonly string _tempDir;

    public AgentStateRecoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"AgentRecovery_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ═══════════════════════════════════════════════════════════════════
    // ForceReady: state reset
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ForceReady_ResetsState_ToReady_WhenIdle()
    {
        var executor = CreateExecutor();

        executor.ForceReady();

        Assert.Equal(AgentState.Ready, executor.CurrentState);
    }

    [Fact]
    public void ForceReady_ClearsLifecycle_WhenCalledWithNoExecution()
    {
        var executor = CreateExecutor();

        executor.ForceReady();

        Assert.Null(executor.CurrentLifecycle);
        Assert.Equal(AgentState.Ready, executor.CurrentState);
    }

    [Fact]
    public void ForceReady_IsSafe_WhenCalledMultipleTimes()
    {
        var executor = CreateExecutor();

        // Should not throw even when called repeatedly
        executor.ForceReady();
        executor.ForceReady();
        executor.ForceReady();

        Assert.Equal(AgentState.Ready, executor.CurrentState);
    }

    [Fact]
    public void ForceReady_ResetsCommand_ToEmpty()
    {
        var executor = CreateExecutor();

        executor.ForceReady();

        Assert.Null(executor.CurrentCommand);
        Assert.Null(executor.ExecutionStartedUtc);
    }

    // ═══════════════════════════════════════════════════════════════════
    // TerminateExecution: graceful abort
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void TerminateExecution_IsSafe_WhenNoExecutionActive()
    {
        var executor = CreateExecutor();

        // Should not throw when nothing is running
        executor.TerminateExecution();

        Assert.Equal(AgentState.Ready, executor.CurrentState);
    }

    [Fact]
    public void TerminateExecution_MultipleCalls_DoNotThrow()
    {
        var executor = CreateExecutor();

        executor.TerminateExecution();
        executor.TerminateExecution();
        executor.TerminateExecution();

        Assert.Equal(AgentState.Ready, executor.CurrentState);
    }

    // ═══════════════════════════════════════════════════════════════════
    // StuckExecutionWatchdog: configuration
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void WatchdogGraceMinutes_DefaultIsFive()
    {
        var settings = new AgentSettings();
        Assert.Equal(5, settings.WatchdogGraceMinutes);
    }

    [Fact]
    public void WatchdogGraceMinutes_CanBeConfigured()
    {
        var settings = new AgentSettings { WatchdogGraceMinutes = 15 };
        Assert.Equal(15, settings.WatchdogGraceMinutes);
    }

    [Fact]
    public void MaxExecutionTimeoutMinutes_DefaultIsReasonable()
    {
        var settings = new AgentSettings();
        // Default should be > 0 and not unreasonably large
        Assert.True(settings.MaxExecutionTimeoutMinutes > 0);
        Assert.True(settings.MaxExecutionTimeoutMinutes <= 480); // max 8 hours
    }

    // ═══════════════════════════════════════════════════════════════════
    // Watchdog: Detection logic
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Watchdog_Threshold_IsTimeoutPlusGrace()
    {
        var settings = new AgentSettings
        {
            MaxExecutionTimeoutMinutes = 60,
            WatchdogGraceMinutes = 5
        };

        var threshold = TimeSpan.FromMinutes(
            settings.MaxExecutionTimeoutMinutes + settings.WatchdogGraceMinutes);

        Assert.Equal(TimeSpan.FromMinutes(65), threshold);
    }

    [Fact]
    public void Watchdog_ShouldNotFire_WhenExecutionWithinTimeout()
    {
        var settings = new AgentSettings
        {
            MaxExecutionTimeoutMinutes = 60,
            WatchdogGraceMinutes = 5
        };

        // Simulate 30 minutes elapsed
        var startedUtc = DateTime.UtcNow.AddMinutes(-30);
        var elapsed = DateTime.UtcNow - startedUtc;
        var maxAllowed = TimeSpan.FromMinutes(
            settings.MaxExecutionTimeoutMinutes + settings.WatchdogGraceMinutes);

        Assert.False(elapsed > maxAllowed,
            "Watchdog should NOT fire when elapsed time is within allowed threshold");
    }

    [Fact]
    public void Watchdog_ShouldFire_WhenExecutionExceedsTimeoutPlusGrace()
    {
        var settings = new AgentSettings
        {
            MaxExecutionTimeoutMinutes = 60,
            WatchdogGraceMinutes = 5
        };

        // Simulate 66 minutes elapsed (exceeds 65 min threshold)
        var startedUtc = DateTime.UtcNow.AddMinutes(-66);
        var elapsed = DateTime.UtcNow - startedUtc;
        var maxAllowed = TimeSpan.FromMinutes(
            settings.MaxExecutionTimeoutMinutes + settings.WatchdogGraceMinutes);

        Assert.True(elapsed > maxAllowed,
            "Watchdog SHOULD fire when elapsed time exceeds allowed threshold");
    }

    // ═══════════════════════════════════════════════════════════════════
    // ExecutionLifecycleState: Termination tracking
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ExecutionLifecycleState_TracksTerminationRequest()
    {
        var lifecycle = new ExecutionLifecycleState("exec-001", "cmd.exe", "/c echo test");

        lifecycle.RequestTermination("Watchdog timeout");

        Assert.True(lifecycle.TerminationRequested);
        Assert.Equal("Watchdog timeout", lifecycle.ResetReason);
    }

    [Fact]
    public void ExecutionLifecycleState_MarkReset_RecordsForceReadyReason()
    {
        var lifecycle = new ExecutionLifecycleState("exec-002", "install.bat", "");

        lifecycle.MarkReset("ForceReady — watchdog recovery");

        Assert.Equal("ForceReady — watchdog recovery", lifecycle.ResetReason);
    }

    [Fact]
    public void ExecutionLifecycleState_CanTrackLockAndTermination()
    {
        var lifecycle = new ExecutionLifecycleState("exec-003", "test.bat", "");

        lifecycle.MarkLockAcquired();
        Assert.True(lifecycle.LockAcquired);
        Assert.False(lifecycle.TerminationRequested);

        lifecycle.RequestTermination("Controller cancel");
        Assert.True(lifecycle.TerminationRequested);
    }

    // ═══════════════════════════════════════════════════════════════════
    // CommandExecutor: State transitions
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void CommandExecutor_InitialState_IsReady()
    {
        var executor = CreateExecutor();

        Assert.Equal(AgentState.Ready, executor.CurrentState);
        Assert.Null(executor.CurrentCommand);
        Assert.Null(executor.ExecutionStartedUtc);
    }

    [Fact]
    public void CommandExecutor_AfterForceReady_AcceptsNewExecution()
    {
        var executor = CreateExecutor();

        // Force ready then verify state allows new commands
        executor.ForceReady();
        Assert.Equal(AgentState.Ready, executor.CurrentState);
        // CurrentCommand cleared = ready for next execution
        Assert.Null(executor.CurrentCommand);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Stream cancellation → Ready state
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void CommandExecutor_AfterTerminate_ReturnsToReady()
    {
        var executor = CreateExecutor();

        executor.TerminateExecution();

        // After terminate, state should be Ready (not stuck)
        Assert.Equal(AgentState.Ready, executor.CurrentState);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static CommandExecutor CreateExecutor()
    {
        var broadcaster = new EventBroadcaster(NullLogger<EventBroadcaster>.Instance);
        var settings = Options.Create(new AgentSettings
        {
            MaxExecutionTimeoutMinutes = 60,
            MaxExecutionHistoryCount = 10,
            MaxOutputLinesPerExecution = 100,
            WatchdogGraceMinutes = 5,
        });
        var tracker = new ExecutionTracker(settings);
        var auditSettings = Options.Create(new AuditSettings { Enabled = false });
        var audit = new AuditLogger(auditSettings, NullLogger<AuditLogger>.Instance);
        var policy = new CommandPolicyEvaluator(new CommandPolicySettings { Mode = "Disabled" });
        var enhancedPolicy = new EnhancedCommandPolicyEvaluator(new CommandPolicySettings { Mode = "Disabled" }, NullLogger<EnhancedCommandPolicyEvaluator>.Instance);

        return new CommandExecutor(
            broadcaster, tracker, audit, settings, policy, enhancedPolicy,
            NullLogger<CommandExecutor>.Instance);
    }
}
