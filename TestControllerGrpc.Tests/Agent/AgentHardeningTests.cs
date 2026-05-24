extern alias AgentAlias;
using AgentAlias::TestAgentGrpc;
using AgentAlias::TestAgentGrpc.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace TestControllerGrpc.Tests.Agent;

/// <summary>
/// Tests for the agent hardening features:
/// - AgentKestrelOptions defaults
/// - ExecutionLifecycleState tracking
/// - CommandPolicyEvaluator safe path allowlist
/// - ExecutionTracker redaction
/// - SystemMetricsCollector caching
/// - Watchdog configurable grace
/// - AgentSnapshot capabilities
/// </summary>
public sealed class AgentHardeningTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"AgentHarden_{Guid.NewGuid():N}");

    public AgentHardeningTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── AgentKestrelOptions ────────────────────────────────────────────

    [Fact]
    public void AgentKestrelOptions_Defaults_AreProduction_Safe()
    {
        var opts = new AgentKestrelOptions();

        Assert.Equal(240, opts.KeepAliveTimeoutMinutes);
        Assert.True(opts.DisableMinRequestBodyDataRate);
        Assert.True(opts.DisableMinResponseDataRate);
        Assert.True(opts.WarnOnPlaintextHttp2);
    }

    [Fact]
    public void AgentKestrelOptions_CanOverride_KeepAlive()
    {
        var opts = new AgentKestrelOptions { KeepAliveTimeoutMinutes = 60 };

        Assert.Equal(60, opts.KeepAliveTimeoutMinutes);
    }

    // ── ExecutionLifecycleState ────────────────────────────────────────

    [Fact]
    public void ExecutionLifecycleState_Initializes_WithExpectedProperties()
    {
        var lifecycle = new ExecutionLifecycleState("exec-001", "install.bat", "-password Secret123");

        Assert.Equal("exec-001", lifecycle.ExecutionId);
        Assert.Equal("install.bat", lifecycle.Command);
        Assert.Contains(SecurityRedactor.Redacted, lifecycle.RedactedCommand);
        Assert.DoesNotContain("Secret123", lifecycle.RedactedCommand);
        Assert.False(lifecycle.LockAcquired);
        Assert.False(lifecycle.TerminationRequested);
        Assert.Null(lifecycle.ResetReason);
        Assert.True(lifecycle.StartedUtc <= DateTime.UtcNow);
    }

    [Fact]
    public void ExecutionLifecycleState_MarkLockAcquired_SetsFlag()
    {
        var lifecycle = new ExecutionLifecycleState("exec-002", "cmd.exe", "/c dir");

        lifecycle.MarkLockAcquired();

        Assert.True(lifecycle.LockAcquired);
    }

    [Fact]
    public void ExecutionLifecycleState_RequestTermination_SetsFieldsCorrectly()
    {
        var lifecycle = new ExecutionLifecycleState("exec-003", "test.exe", "args");

        lifecycle.RequestTermination("Controller request");

        Assert.True(lifecycle.TerminationRequested);
        Assert.Equal("Controller request", lifecycle.ResetReason);
    }

    [Fact]
    public void ExecutionLifecycleState_MarkReset_RecordsReason()
    {
        var lifecycle = new ExecutionLifecycleState("exec-004", "cmd.exe", "args");

        lifecycle.MarkReset("ForceReady invoked");

        Assert.Equal("ForceReady invoked", lifecycle.ResetReason);
    }

    // ── CommandPolicyEvaluator: Safe path allowlist ────────────────────

    [Fact]
    public void CommandPolicy_AllowedExecutablePaths_Empty_AllowsAll()
    {
        var settings = new CommandPolicySettings
        {
            Mode = "Enforce",
            AllowedExecutablePaths = [],
            DetectShellChaining = false,
        };
        var evaluator = new CommandPolicyEvaluator(settings);

        var result = evaluator.Evaluate("anycommand.exe", "");

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void CommandPolicy_AllowedExecutablePaths_DeniesNotFound()
    {
        var settings = new CommandPolicySettings
        {
            Mode = "Enforce",
            AllowedExecutablePaths = [@"C:\OnlyHere"],
            AllowedCommandPrefixes = [],
            DetectShellChaining = false,
        };
        var evaluator = new CommandPolicyEvaluator(settings);

        var result = evaluator.Evaluate("nonexistent_xyz_abc.exe", "");

        Assert.False(result.IsAllowed);
        Assert.Contains("not found on safe path", result.Reason);
    }

    [Fact]
    public void CommandPolicy_AllowedExecutablePaths_AllowsFromConfiguredDir()
    {
        // Create a temp script in the allowed path
        var script = Path.Combine(_tempDir, "allowed-test.cmd");
        File.WriteAllText(script, "@echo test");

        var settings = new CommandPolicySettings
        {
            Mode = "Enforce",
            AllowedExecutablePaths = [_tempDir],
            AllowedCommandPrefixes = [],
            DetectShellChaining = false,
        };
        var evaluator = new CommandPolicyEvaluator(settings);

        var result = evaluator.Evaluate(script, "");

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void CommandPolicy_AllowedExecutablePaths_DeniesOutsideDir()
    {
        // Create script in a subdirectory NOT in the allowlist
        var otherDir = Path.Combine(_tempDir, "other");
        Directory.CreateDirectory(otherDir);
        var script = Path.Combine(otherDir, "blocked-test.cmd");
        File.WriteAllText(script, "@echo test");

        var settings = new CommandPolicySettings
        {
            Mode = "Enforce",
            AllowedExecutablePaths = [Path.Combine(_tempDir, "allowed-only")],
            AllowedCommandPrefixes = [],
            DetectShellChaining = false,
        };
        var evaluator = new CommandPolicyEvaluator(settings);

        var result = evaluator.Evaluate(script, "");

        Assert.False(result.IsAllowed);
        Assert.Contains("not under any allowed directory", result.Reason);
    }

    // ── ExecutionTracker: Redaction ───────────────────────────────────

    [Fact]
    public void ExecutionTracker_Redacts_ArgumentsInHistory()
    {
        var settings = Options.Create(new AgentSettings
        {
            MaxExecutionHistoryCount = 100,
            MaxOutputLinesPerExecution = 100,
        });
        var tracker = new ExecutionTracker(settings);

        var builder = tracker.BeginTracked("exec-redact", "install.bat", "password=Secret123 token=abc");
        builder.Complete(0);

        var history = tracker.GetHistory(max: 1);
        var record = Assert.Single(history);

        Assert.Contains(SecurityRedactor.Redacted, record.Arguments);
        Assert.DoesNotContain("Secret123", record.Arguments);
        Assert.DoesNotContain("abc", record.Arguments);
    }

    [Fact]
    public void ExecutionTracker_PreservesNonSensitive_Arguments()
    {
        var settings = Options.Create(new AgentSettings
        {
            MaxExecutionHistoryCount = 100,
            MaxOutputLinesPerExecution = 100,
        });
        var tracker = new ExecutionTracker(settings);

        var builder = tracker.BeginTracked("exec-safe", "dir.bat", "/s /q C:\\temp");
        builder.Complete(0);

        var history = tracker.GetHistory(max: 1);
        var record = Assert.Single(history);

        Assert.Equal("/s /q C:\\temp", record.Arguments);
    }

    // ── SystemMetricsCollector: Caching ───────────────────────────────

    [Fact]
    public void SystemMetricsCollector_ReturnsSameInstance_WithinTtl()
    {
        var collector = new SystemMetricsCollector(NullLogger<SystemMetricsCollector>.Instance);

        var first = collector.Collect();
        var second = collector.Collect();

        // Same reference within cache TTL
        Assert.Same(first, second);
    }

    [Fact]
    public void SystemMetricsCollector_Collect_ReturnsValidMetrics()
    {
        var collector = new SystemMetricsCollector(NullLogger<SystemMetricsCollector>.Instance);

        var metrics = collector.Collect();

        Assert.True(metrics.ActiveProcessCount > 0);
        Assert.True(metrics.MemoryTotalMb > 0);
        Assert.False(string.IsNullOrEmpty(metrics.OsDescription));
    }

    // ── AgentSettings: Configurable watchdog grace ────────────────────

    [Fact]
    public void AgentSettings_WatchdogGraceMinutes_DefaultIsFive()
    {
        var settings = new AgentSettings();

        Assert.Equal(5, settings.WatchdogGraceMinutes);
    }

    [Fact]
    public void AgentSettings_WatchdogGraceMinutes_CanBeOverridden()
    {
        var settings = new AgentSettings { WatchdogGraceMinutes = 10 };

        Assert.Equal(10, settings.WatchdogGraceMinutes);
    }

    // ── CommandExecutor: ForceReady lifecycle tracking ─────────────────

    [Fact]
    public void CommandExecutor_ForceReady_ClearsLifecycle()
    {
        var broadcaster = new EventBroadcaster(NullLogger<EventBroadcaster>.Instance);
        var settings = Options.Create(new AgentSettings());
        var tracker = new ExecutionTracker(settings);
        var auditSettings = Options.Create(new AuditSettings { Enabled = false });
        var audit = new AuditLogger(auditSettings, NullLogger<AuditLogger>.Instance);
        var policy = new CommandPolicyEvaluator(new CommandPolicySettings { Mode = "Disabled" });
        var enhancedPolicy = new EnhancedCommandPolicyEvaluator(new CommandPolicySettings { Mode = "Disabled" }, NullLogger<EnhancedCommandPolicyEvaluator>.Instance);
        var executor = new CommandExecutor(
            broadcaster, tracker, audit, settings, policy, enhancedPolicy,
            NullLogger<CommandExecutor>.Instance);

        // Initially no lifecycle state
        Assert.Null(executor.CurrentLifecycle);

        // ForceReady should be safe even when idle
        executor.ForceReady();
        Assert.Null(executor.CurrentLifecycle);
        Assert.Equal(AgentAlias::TestAgentGrpc.AgentState.Ready, executor.CurrentState);
    }

    // ── CommandPolicy: Disabled mode bypasses all checks ──────────────

    [Fact]
    public void CommandPolicy_Disabled_BypassesAllChecks()
    {
        var settings = new CommandPolicySettings { Mode = "Disabled" };
        var evaluator = new CommandPolicyEvaluator(settings);

        var result = evaluator.Evaluate("danger.exe", "password=secret && rm -rf /");

        Assert.True(result.IsAllowed);
    }
}
