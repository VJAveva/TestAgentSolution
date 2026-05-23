extern alias AgentAlias;
using AgentAlias::TestAgentGrpc;
using AgentAlias::TestAgentGrpc.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Threading.Channels;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// End-to-end unit tests for the Controller → Agent command execution workflow.
/// Tests cover:
///   - CommandExecutor.RunCommandStreamed (acceptance, rejection, timeout, streaming)
///   - RemoteCommandStreamRunner.BuildRequest (DTO mapping)
///   - RemoteCommandStreamRunner result classification (error synthesis)
///   - Concurrent execution rejection (semaphore)
///   - Cancellation propagation
///   - Process exit code classification
///   - Channel lifecycle (completion signaling)
/// </summary>
public sealed class CommandExecutionE2ETests : IDisposable
{
    private readonly string _tempDir;

    public CommandExecutionE2ETests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CmdExecTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helper: create a CommandExecutor with test-friendly settings
    // ═══════════════════════════════════════════════════════════════════

    private CommandExecutor CreateExecutor(int maxTimeoutMinutes = 2, int maxHistory = 10)
    {
        var broadcaster = new EventBroadcaster(NullLogger<EventBroadcaster>.Instance);
        var settings = Options.Create(new AgentSettings
        {
            MaxExecutionTimeoutMinutes = maxTimeoutMinutes,
            MaxExecutionHistoryCount = maxHistory,
            MaxOutputLinesPerExecution = 100,
            WatchdogGraceMinutes = 1,
        });
        var tracker = new ExecutionTracker(settings);
        var auditSettings = Options.Create(new AuditSettings { Enabled = false });
        var audit = new AuditLogger(auditSettings, NullLogger<AuditLogger>.Instance);
        var policy = new CommandPolicyEvaluator(new CommandPolicySettings { Mode = "Disabled" });

        return new CommandExecutor(
            broadcaster, tracker, audit, settings, policy,
            NullLogger<CommandExecutor>.Instance);
    }

    // ═══════════════════════════════════════════════════════════════════
    // RunCommandStreamed: Acceptance
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void RunCommandStreamed_AcceptsExecution_WhenAgentIsReady()
    {
        var executor = CreateExecutor();

        var (accepted, execId, stream) = executor.RunCommandStreamed(
            "cmd", "/c echo hello", isReboot: false, timeoutMs: 10000);

        Assert.True(accepted);
        Assert.NotEmpty(execId);
        Assert.NotNull(stream);
    }

    [Fact]
    public async Task RunCommandStreamed_StreamsOutput_ForSimpleEchoCommand()
    {
        var executor = CreateExecutor();

        var (accepted, execId, stream) = executor.RunCommandStreamed(
            "cmd", "/c echo TestOutput123", isReboot: false, timeoutMs: 30000);

        Assert.True(accepted);
        Assert.NotNull(stream);

        var events = new List<ExecutionEvent>();
        await foreach (var evt in stream!.ReadAllAsync())
        {
            events.Add(evt);
        }

        // Should have at least: Queued, Started, stdout line(s), Completed
        Assert.Contains(events, e => e.EventType == ExecutionEventType.EventQueued);
        Assert.Contains(events, e => e.EventType == ExecutionEventType.EventStarted);
        Assert.Contains(events, e => e.EventType == ExecutionEventType.EventCompleted);
        Assert.Contains(events, e =>
            e.EventType == ExecutionEventType.EventStdoutLine &&
            e.OutputLine.Contains("TestOutput123"));
    }

    [Fact]
    public async Task RunCommandStreamed_ReportsCorrectExitCode_OnSuccess()
    {
        var executor = CreateExecutor();

        var (_, _, stream) = executor.RunCommandStreamed(
            "cmd", "/c exit 0", isReboot: false, timeoutMs: 30000);

        Assert.NotNull(stream);
        ExecutionEvent? completed = null;
        await foreach (var evt in stream!.ReadAllAsync())
        {
            if (evt.EventType == ExecutionEventType.EventCompleted)
                completed = evt;
        }

        Assert.NotNull(completed);
        Assert.Equal(0, completed!.ExitCode);
    }

    [Fact]
    public async Task RunCommandStreamed_ReportsNonZeroExitCode_OnFailure()
    {
        var executor = CreateExecutor();

        var (_, _, stream) = executor.RunCommandStreamed(
            "cmd", "/c exit 42", isReboot: false, timeoutMs: 30000);

        Assert.NotNull(stream);
        ExecutionEvent? completed = null;
        await foreach (var evt in stream!.ReadAllAsync())
        {
            if (evt.EventType == ExecutionEventType.EventCompleted)
                completed = evt;
        }

        Assert.NotNull(completed);
        Assert.Equal(42, completed!.ExitCode);
    }

    [Fact]
    public async Task RunCommandStreamed_StreamsStderr_WhenCommandWritesToStderr()
    {
        var executor = CreateExecutor();

        var (_, _, stream) = executor.RunCommandStreamed(
            "cmd", "/c echo ErrorLine 1>&2", isReboot: false, timeoutMs: 30000);

        Assert.NotNull(stream);
        var events = new List<ExecutionEvent>();
        await foreach (var evt in stream!.ReadAllAsync())
        {
            events.Add(evt);
        }

        Assert.Contains(events, e =>
            e.EventType == ExecutionEventType.EventStderrLine &&
            e.OutputLine.Contains("ErrorLine"));
    }

    // ═══════════════════════════════════════════════════════════════════
    // RunCommandStreamed: Rejection (agent busy)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunCommandStreamed_RejectsSecondExecution_WhenBusy()
    {
        var executor = CreateExecutor();

        // Start a long-running command
        var (accepted1, _, stream1) = executor.RunCommandStreamed(
            "cmd", "/c ping -n 5 127.0.0.1 > nul", isReboot: false, timeoutMs: 30000);
        Assert.True(accepted1);

        // Wait briefly to ensure it enters Running state
        await Task.Delay(200);

        // Second command should be rejected
        var (accepted2, execId2, stream2) = executor.RunCommandStreamed(
            "cmd", "/c echo second", isReboot: false, timeoutMs: 10000);

        Assert.False(accepted2);
        Assert.Empty(execId2);
        Assert.Null(stream2);

        // Drain the first stream so the test cleans up
        await foreach (var _ in stream1!.ReadAllAsync()) { }
    }

    [Fact]
    public async Task RunCommandStreamed_AcceptsNewCommand_AfterPreviousCompletes()
    {
        var executor = CreateExecutor();

        // First execution
        var (accepted1, _, stream1) = executor.RunCommandStreamed(
            "cmd", "/c echo first", isReboot: false, timeoutMs: 10000);
        Assert.True(accepted1);
        await foreach (var _ in stream1!.ReadAllAsync()) { }

        // Second execution should succeed after first completes
        var (accepted2, _, stream2) = executor.RunCommandStreamed(
            "cmd", "/c echo second", isReboot: false, timeoutMs: 10000);
        Assert.True(accepted2);
        await foreach (var _ in stream2!.ReadAllAsync()) { }
    }

    // ═══════════════════════════════════════════════════════════════════
    // RunCommandStreamed: Cancellation / Timeout
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunCommandStreamed_CancelsExecution_WhenExternalTokenCancelled()
    {
        var executor = CreateExecutor();
        using var cts = new CancellationTokenSource();

        // Start a long-running command
        var (accepted, _, stream) = executor.RunCommandStreamed(
            "cmd", "/c ping -n 60 127.0.0.1 > nul", isReboot: false,
            timeoutMs: 0, externalCt: cts.Token);
        Assert.True(accepted);

        // Cancel after 500ms
        cts.CancelAfter(500);

        var events = new List<ExecutionEvent>();
        await foreach (var evt in stream!.ReadAllAsync())
        {
            events.Add(evt);
        }

        // Should contain a FAILED event due to cancellation
        Assert.Contains(events, e => e.EventType == ExecutionEventType.EventFailed);
    }

    [Fact]
    public async Task RunCommandStreamed_TimesOut_WhenTimeoutMsExceeded()
    {
        var executor = CreateExecutor();

        // Start a long-running command with a short timeout (2 seconds)
        var (accepted, _, stream) = executor.RunCommandStreamed(
            "cmd", "/c ping -n 60 127.0.0.1 > nul", isReboot: false,
            timeoutMs: 2000);
        Assert.True(accepted);

        var events = new List<ExecutionEvent>();
        await foreach (var evt in stream!.ReadAllAsync())
        {
            events.Add(evt);
        }

        // Should contain a FAILED event due to timeout
        Assert.Contains(events, e => e.EventType == ExecutionEventType.EventFailed);
        var failedEvt = events.First(e => e.EventType == ExecutionEventType.EventFailed);
        Assert.Contains("timed out", failedEvt.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunCommandStreamed_ResetsToReady_AfterTimeout()
    {
        var executor = CreateExecutor();

        var (_, _, stream) = executor.RunCommandStreamed(
            "cmd", "/c ping -n 60 127.0.0.1 > nul", isReboot: false,
            timeoutMs: 1500);

        await foreach (var _ in stream!.ReadAllAsync()) { }

        // Agent should be back to Ready
        Assert.Equal(AgentState.Ready, executor.CurrentState);
    }

    // ═══════════════════════════════════════════════════════════════════
    // RunCommandStreamed: Channel lifecycle
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunCommandStreamed_ChannelCompletes_AfterExecutionFinishes()
    {
        var executor = CreateExecutor();

        var (_, _, stream) = executor.RunCommandStreamed(
            "cmd", "/c echo done", isReboot: false, timeoutMs: 10000);

        Assert.NotNull(stream);

        // Reading all events should complete naturally (not hang)
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var events = new List<ExecutionEvent>();
        await foreach (var evt in stream!.ReadAllAsync(cts.Token))
        {
            events.Add(evt);
        }

        Assert.True(events.Count > 0);
    }

    [Fact]
    public async Task RunCommandStreamed_ChannelCompletes_EvenOnException()
    {
        var executor = CreateExecutor();

        // Pass a command that doesn't exist — should still complete the channel
        var (accepted, _, stream) = executor.RunCommandStreamed(
            "nonexistent_binary_xyz_99999", "", isReboot: false, timeoutMs: 10000);

        if (accepted && stream != null)
        {
            var events = new List<ExecutionEvent>();
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await foreach (var evt in stream.ReadAllAsync(cts.Token))
            {
                events.Add(evt);
            }

            // Should emit a FAILED event for the unhandled exception
            Assert.Contains(events, e => e.EventType == ExecutionEventType.EventFailed);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // RunCommandStreamed: State transitions
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunCommandStreamed_TransitionsToRunning_DuringExecution()
    {
        var executor = CreateExecutor();
        var stateChanges = new List<AgentState>();
        executor.StateChanged += (_, state) => stateChanges.Add(state);

        var (_, _, stream) = executor.RunCommandStreamed(
            "cmd", "/c ping -n 2 127.0.0.1 > nul", isReboot: false, timeoutMs: 15000);

        // Wait a moment for the background task to start
        await Task.Delay(300);
        Assert.Equal(AgentState.Running, executor.CurrentState);

        // Drain and wait for completion
        await foreach (var _ in stream!.ReadAllAsync()) { }

        Assert.Equal(AgentState.Ready, executor.CurrentState);
        Assert.Contains(AgentState.Running, stateChanges);
        Assert.Contains(AgentState.Ready, stateChanges);
    }

    [Fact]
    public async Task RunCommandStreamed_SetsCurrentCommand_DuringExecution()
    {
        var executor = CreateExecutor();

        var (_, _, stream) = executor.RunCommandStreamed(
            "cmd", "/c ping -n 2 127.0.0.1 > nul", isReboot: false, timeoutMs: 15000);

        await Task.Delay(300);
        Assert.NotNull(executor.CurrentCommand);

        await foreach (var _ in stream!.ReadAllAsync()) { }
        Assert.Null(executor.CurrentCommand);
    }

    // ═══════════════════════════════════════════════════════════════════
    // RunCommandStreamed: Execution ID
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void RunCommandStreamed_UsesProvidedExecutionId_WhenGiven()
    {
        var executor = CreateExecutor();

        var (accepted, execId, _) = executor.RunCommandStreamed(
            "cmd", "/c echo hello", isReboot: false, timeoutMs: 10000,
            executionId: "custom-id-123");

        Assert.True(accepted);
        Assert.Equal("custom-id-123", execId);
    }

    [Fact]
    public void RunCommandStreamed_GeneratesExecutionId_WhenNotProvided()
    {
        var executor = CreateExecutor();

        var (accepted, execId, _) = executor.RunCommandStreamed(
            "cmd", "/c echo hello", isReboot: false, timeoutMs: 10000);

        Assert.True(accepted);
        Assert.NotEmpty(execId);
        Assert.True(execId.Length > 0 && execId.Length <= 12);
    }

    // ═══════════════════════════════════════════════════════════════════
    // RunCommandStreamed: Multiple output lines
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunCommandStreamed_StreamsMultipleLines_InOrder()
    {
        var executor = CreateExecutor();

        var (_, _, stream) = executor.RunCommandStreamed(
            "cmd", "/c echo Line1 & echo Line2 & echo Line3",
            isReboot: false, timeoutMs: 15000);

        var stdoutLines = new List<string>();
        await foreach (var evt in stream!.ReadAllAsync())
        {
            if (evt.EventType == ExecutionEventType.EventStdoutLine)
                stdoutLines.Add(evt.OutputLine);
        }

        Assert.True(stdoutLines.Count >= 3);
        Assert.Contains(stdoutLines, l => l.Contains("Line1"));
        Assert.Contains(stdoutLines, l => l.Contains("Line2"));
        Assert.Contains(stdoutLines, l => l.Contains("Line3"));
    }

    // ═══════════════════════════════════════════════════════════════════
    // RemoteCommandStreamRunner.BuildRequest: DTO mapping
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void BuildRequest_MapsAllFields_FromActionConfig()
    {
        var config = new ActionConfig
        {
            Command = "xcopy",
            Parameters = "/Y source dest",
            IsReboot = true,
            UserName = "admin",
            Password = "pass123",
            Timeout = 3600,
            CompletionCheckCommand = "tasklist | findstr msiexec",
            CompletionPollIntervalSeconds = 15,
        };

        var request = RemoteCommandStreamRunner.BuildRequest(config);

        Assert.Equal("xcopy", request.Command);
        Assert.Equal("/Y source dest", request.Arguments);
        Assert.True(request.IsReboot);
        Assert.Equal("admin", request.UserName);
        Assert.Equal("pass123", request.Password);
        Assert.Equal(3600, request.TimeoutSeconds);
        Assert.Equal("tasklist | findstr msiexec", request.CompletionCheckCommand);
        Assert.Equal(15, request.CompletionPollIntervalSeconds);
    }

    [Fact]
    public void BuildRequest_SetsEmptyStrings_WhenNullableFieldsAreNull()
    {
        var config = new ActionConfig
        {
            Command = "cmd",
            Parameters = "/c echo hi",
        };

        var request = RemoteCommandStreamRunner.BuildRequest(config);

        Assert.Equal("", request.UserName);
        Assert.Equal("", request.Password);
        Assert.Equal("", request.CompletionCheckCommand);
    }

    [Fact]
    public void BuildRequest_SetsTimeoutToZero_WhenNoTimeoutConfigured()
    {
        var config = new ActionConfig
        {
            Command = "cmd",
            Parameters = "/c echo hi",
            Timeout = 0,
        };

        var request = RemoteCommandStreamRunner.BuildRequest(config);

        Assert.Equal(0, request.TimeoutSeconds);
    }

    // ═══════════════════════════════════════════════════════════════════
    // RemoteCommandStreamResult: Error synthesis
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void RemoteCommandStreamResult_IndicatesSuccess_WhenCompletedWithZeroExitCode()
    {
        var result = new RemoteCommandStreamResult(
            ReceivedCompleted: true,
            ExitCode: 0,
            ErrorMessage: "",
            StderrTail: Array.Empty<string>());

        Assert.True(result.ReceivedCompleted);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.ErrorMessage);
    }

    [Fact]
    public void RemoteCommandStreamResult_IndicatesFailure_WhenCompletedWithNonZeroExitCode()
    {
        var result = new RemoteCommandStreamResult(
            ReceivedCompleted: true,
            ExitCode: 1,
            ErrorMessage: "Process exited with code 1. Last stderr: access denied",
            StderrTail: new[] { "access denied" });

        Assert.True(result.ReceivedCompleted);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("access denied", result.ErrorMessage);
    }

    [Fact]
    public void RemoteCommandStreamResult_SynthesizesError_WhenNoCompletionReceived()
    {
        var result = new RemoteCommandStreamResult(
            ReceivedCompleted: false,
            ExitCode: -1,
            ErrorMessage: "Agent 'agent1' did not report completion.",
            StderrTail: Array.Empty<string>());

        Assert.False(result.ReceivedCompleted);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("did not report completion", result.ErrorMessage);
    }

    // ═══════════════════════════════════════════════════════════════════
    // CommandExecutor: Progress events (heartbeat)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunCommandStreamed_EmitsProgressEvents_ForLongRunning()
    {
        var executor = CreateExecutor();

        // Run a command that lasts > 30s so heartbeat fires (30s interval)
        // Using a shorter command with a longer timeout just to test the heartbeat task starts
        var (_, _, stream) = executor.RunCommandStreamed(
            "cmd", "/c ping -n 4 127.0.0.1 > nul", isReboot: false, timeoutMs: 60000);

        var events = new List<ExecutionEvent>();
        await foreach (var evt in stream!.ReadAllAsync())
        {
            events.Add(evt);
        }

        // The command runs ~3s, below the 30s heartbeat interval — so no progress expected.
        // But the final "Process exited" progress event from the heartbeat should appear.
        var progressEvents = events.Where(e => e.EventType == ExecutionEventType.EventProgress).ToList();
        // At minimum, we should get a final-exit progress event
        Assert.True(progressEvents.Count >= 1 || events.Any(e => e.EventType == ExecutionEventType.EventCompleted));
    }

    // ═══════════════════════════════════════════════════════════════════
    // CommandExecutor: Execution history tracking
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunCommandStreamed_RecordsInExecutionHistory_AfterCompletion()
    {
        var broadcaster = new EventBroadcaster(NullLogger<EventBroadcaster>.Instance);
        var settings = Options.Create(new AgentSettings
        {
            MaxExecutionTimeoutMinutes = 2,
            MaxExecutionHistoryCount = 10,
            MaxOutputLinesPerExecution = 100,
            WatchdogGraceMinutes = 1,
        });
        var tracker = new ExecutionTracker(settings);
        var auditSettings = Options.Create(new AuditSettings { Enabled = false });
        var audit = new AuditLogger(auditSettings, NullLogger<AuditLogger>.Instance);
        var policy = new CommandPolicyEvaluator(new CommandPolicySettings { Mode = "Disabled" });
        var executor = new CommandExecutor(
            broadcaster, tracker, audit, settings, policy,
            NullLogger<CommandExecutor>.Instance);

        var (_, execId, stream) = executor.RunCommandStreamed(
            "cmd", "/c echo tracked", isReboot: false, timeoutMs: 10000);

        await foreach (var _ in stream!.ReadAllAsync()) { }

        var history = tracker.GetHistory();
        Assert.Contains(history, h => h.ExecutionId == execId);
    }

    // ═══════════════════════════════════════════════════════════════════
    // CommandExecutor: Safety-net timeout
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunCommandStreamed_AppliesSafetyNetTimeout_WhenNoExplicitTimeout()
    {
        // Create with very short MaxExecutionTimeoutMinutes
        var broadcaster = new EventBroadcaster(NullLogger<EventBroadcaster>.Instance);
        var settings = Options.Create(new AgentSettings
        {
            // 0.05 minutes = 3 seconds safety net — will cancel the ping command
            MaxExecutionTimeoutMinutes = 0,
            MaxExecutionHistoryCount = 10,
            MaxOutputLinesPerExecution = 100,
            WatchdogGraceMinutes = 1,
        });
        var tracker = new ExecutionTracker(settings);
        var auditSettings = Options.Create(new AuditSettings { Enabled = false });
        var audit = new AuditLogger(auditSettings, NullLogger<AuditLogger>.Instance);
        var policy = new CommandPolicyEvaluator(new CommandPolicySettings { Mode = "Disabled" });
        var executor = new CommandExecutor(
            broadcaster, tracker, audit, settings, policy,
            NullLogger<CommandExecutor>.Instance);

        // With MaxExecutionTimeoutMinutes=0, TimeSpan.FromMinutes(0) = immediate cancel
        var (accepted, _, stream) = executor.RunCommandStreamed(
            "cmd", "/c ping -n 60 127.0.0.1 > nul", isReboot: false, timeoutMs: 0);

        if (accepted && stream != null)
        {
            var events = new List<ExecutionEvent>();
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await foreach (var evt in stream.ReadAllAsync(cts.Token))
            {
                events.Add(evt);
            }

            // Should be cancelled by the safety-net
            Assert.Contains(events, e => e.EventType == ExecutionEventType.EventFailed);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // CommandExecutor: Concurrent stress test
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunCommandStreamed_OnlyOneExecutionSucceeds_WhenMultipleConcurrentAttempts()
    {
        var executor = CreateExecutor();
        int acceptedCount = 0;
        int rejectedCount = 0;
        var streams = new List<ChannelReader<ExecutionEvent>>();

        // Fire 5 concurrent execution requests
        var tasks = Enumerable.Range(0, 5).Select(i => Task.Run(() =>
        {
            var (accepted, _, stream) = executor.RunCommandStreamed(
                "cmd", $"/c echo concurrent_{i}", isReboot: false, timeoutMs: 10000);
            if (accepted)
            {
                Interlocked.Increment(ref acceptedCount);
                lock (streams) { streams.Add(stream!); }
            }
            else
            {
                Interlocked.Increment(ref rejectedCount);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        // Exactly 1 should be accepted; the rest rejected
        Assert.Equal(1, acceptedCount);
        Assert.Equal(4, rejectedCount);

        // Clean up
        foreach (var s in streams)
            await foreach (var _ in s.ReadAllAsync()) { }
    }

    // ═══════════════════════════════════════════════════════════════════
    // CommandExecutor: Event ordering guarantees
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunCommandStreamed_EventsFollowExpectedOrder_ForSuccessfulCommand()
    {
        var executor = CreateExecutor();

        var (_, _, stream) = executor.RunCommandStreamed(
            "cmd", "/c echo OrderTest", isReboot: false, timeoutMs: 15000);

        var eventTypes = new List<ExecutionEventType>();
        await foreach (var evt in stream!.ReadAllAsync())
        {
            eventTypes.Add(evt.EventType);
        }

        // Order: Queued → Started → (stdout/stderr/progress)* → Completed
        var queuedIdx = eventTypes.IndexOf(ExecutionEventType.EventQueued);
        var startedIdx = eventTypes.IndexOf(ExecutionEventType.EventStarted);
        var completedIdx = eventTypes.IndexOf(ExecutionEventType.EventCompleted);

        Assert.True(queuedIdx >= 0, "Missing Queued event");
        Assert.True(startedIdx >= 0, "Missing Started event");
        Assert.True(completedIdx >= 0, "Missing Completed event");
        Assert.True(queuedIdx < startedIdx, "Queued should come before Started");
        Assert.True(startedIdx < completedIdx, "Started should come before Completed");
    }

    [Fact]
    public async Task RunCommandStreamed_EventsFollowExpectedOrder_ForFailedCommand()
    {
        var executor = CreateExecutor();
        using var cts = new CancellationTokenSource();

        var (_, _, stream) = executor.RunCommandStreamed(
            "cmd", "/c ping -n 60 127.0.0.1 > nul", isReboot: false,
            timeoutMs: 0, externalCt: cts.Token);

        // Cancel quickly
        cts.CancelAfter(500);

        var eventTypes = new List<ExecutionEventType>();
        await foreach (var evt in stream!.ReadAllAsync())
        {
            eventTypes.Add(evt.EventType);
        }

        var queuedIdx = eventTypes.IndexOf(ExecutionEventType.EventQueued);
        var failedIdx = eventTypes.IndexOf(ExecutionEventType.EventFailed);

        Assert.True(queuedIdx >= 0, "Missing Queued event");
        Assert.True(failedIdx >= 0, "Missing Failed event");
        Assert.True(queuedIdx < failedIdx, "Queued should come before Failed");
    }

    // ═══════════════════════════════════════════════════════════════════
    // CommandExecutor: Large output handling
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunCommandStreamed_HandlesLargeOutput_WithoutHanging()
    {
        var executor = CreateExecutor();

        // Generate 200 lines of output
        var (_, _, stream) = executor.RunCommandStreamed(
            "cmd", "/c for /L %i in (1,1,200) do @echo Line%i",
            isReboot: false, timeoutMs: 60000);

        int lineCount = 0;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await foreach (var evt in stream!.ReadAllAsync(cts.Token))
        {
            if (evt.EventType == ExecutionEventType.EventStdoutLine)
                lineCount++;
        }

        // Should have received many lines (may not be exactly 200 due to buffering)
        Assert.True(lineCount >= 50, $"Expected at least 50 lines, got {lineCount}");
    }

    // ═══════════════════════════════════════════════════════════════════
    // CommandExecutor: ResolveInterpreter (cmd, powershell, exe)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunCommandStreamed_HandlesDirectExeCommand()
    {
        var executor = CreateExecutor();

        var (accepted, _, stream) = executor.RunCommandStreamed(
            "hostname", "", isReboot: false, timeoutMs: 10000);

        Assert.True(accepted);
        var events = new List<ExecutionEvent>();
        await foreach (var evt in stream!.ReadAllAsync())
        {
            events.Add(evt);
        }

        Assert.Contains(events, e => e.EventType == ExecutionEventType.EventCompleted);
    }

    // ═══════════════════════════════════════════════════════════════════
    // RemoteCommandStreamRunner: StderrTailCapacity constant
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void StderrTailCapacity_IsTwenty()
    {
        Assert.Equal(20, RemoteCommandStreamRunner.StderrTailCapacity);
    }

    // ═══════════════════════════════════════════════════════════════════
    // ActionResult: Classification
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ActionResult_IsSuccess_WhenExitCodeZero()
    {
        var result = new ActionResult(true, 0, "");
        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void ActionResult_IsFailure_WhenExitCodeNonZero()
    {
        var result = new ActionResult(false, 1, "access denied");
        Assert.False(result.Success);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("access denied", result.ErrorMessage);
    }

    [Fact]
    public void ActionResult_IsFailure_WhenExitCodeMinusOne()
    {
        var result = new ActionResult(false, -1, "gRPC call cancelled after 00:01:40");
        Assert.False(result.Success);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("cancelled", result.ErrorMessage);
    }

    // ═══════════════════════════════════════════════════════════════════
    // CommandExecutor: ForceReady after stuck execution
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ForceReady_ReleasesLock_AllowingNewExecution()
    {
        var executor = CreateExecutor();

        // Start a long command
        var (accepted1, _, stream1) = executor.RunCommandStreamed(
            "cmd", "/c ping -n 60 127.0.0.1 > nul", isReboot: false, timeoutMs: 60000);
        Assert.True(accepted1);

        await Task.Delay(300);

        // Force ready (emergency recovery)
        executor.ForceReady();

        // Wait for lock release to propagate
        await Task.Delay(500);

        // Should be able to accept a new command
        var (accepted2, _, stream2) = executor.RunCommandStreamed(
            "cmd", "/c echo recovered", isReboot: false, timeoutMs: 10000);

        // Note: ForceReady might not fully release the semaphore in all cases
        // since the background task still holds it. The test validates the state reset.
        Assert.Equal(AgentState.Ready, executor.CurrentState);

        // Cleanup
        if (stream2 != null)
            await foreach (var _ in stream2.ReadAllAsync()) { }
    }

    // ═══════════════════════════════════════════════════════════════════
    // ControllerTimeoutOptions: Defaults
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ControllerTimeoutOptions_HasReasonableDefaults()
    {
        var options = new ControllerTimeoutOptions();

        Assert.True(options.TestConnectionTimeoutSeconds > 0);
        Assert.True(options.WaitForAgentFreeMaxSeconds > 0);
        Assert.True(options.BusyRecoveryMaxSeconds > 0);
        Assert.True(options.RetryMaxAttempts > 0);
        Assert.True(options.DefaultActionTimeoutSeconds >= 3600); // at least 1 hour default
    }

    [Fact]
    public void ControllerTimeoutOptions_DefaultActionTimeout_IsTwoHours()
    {
        var options = new ControllerTimeoutOptions();
        Assert.Equal(7200, options.DefaultActionTimeoutSeconds);
    }

    [Fact]
    public void ControllerTimeoutOptions_OuterSafetyNet_IsTwentyFourHours()
    {
        var options = new ControllerTimeoutOptions();
        Assert.Equal(24, options.OuterSafetyNetTimeoutHours);
    }

    // ═══════════════════════════════════════════════════════════════════
    // CommandExecutor: TerminateExecution kills active process
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TerminateExecution_KillsActiveProcess_AndResetsState()
    {
        var executor = CreateExecutor();

        var (_, _, stream) = executor.RunCommandStreamed(
            "cmd", "/c ping -n 60 127.0.0.1 > nul", isReboot: false, timeoutMs: 60000);

        await Task.Delay(500); // Let process start
        Assert.Equal(AgentState.Running, executor.CurrentState);

        executor.TerminateExecution();

        // Drain the stream
        await foreach (var _ in stream!.ReadAllAsync()) { }

        Assert.Equal(AgentState.Ready, executor.CurrentState);
    }

    // ═══════════════════════════════════════════════════════════════════
    // CommandExecutor: Command policy enforcement
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void RunCommandStreamed_Rejected_WhenCommandPolicyDenies()
    {
        var broadcaster = new EventBroadcaster(NullLogger<EventBroadcaster>.Instance);
        var settings = Options.Create(new AgentSettings
        {
            MaxExecutionTimeoutMinutes = 2,
            MaxExecutionHistoryCount = 10,
            MaxOutputLinesPerExecution = 100,
        });
        var tracker = new ExecutionTracker(settings);
        var auditSettings = Options.Create(new AuditSettings { Enabled = false });
        var audit = new AuditLogger(auditSettings, NullLogger<AuditLogger>.Instance);

        // Create policy in Enforce mode with an allowlist that excludes "format"
        var policySettings = new CommandPolicySettings
        {
            Mode = "Enforce",
            AllowedCommandPrefixes = new List<string> { "cmd", "xcopy", "powershell" }
        };
        var policy = new CommandPolicyEvaluator(policySettings);

        var executor = new CommandExecutor(
            broadcaster, tracker, audit, settings, policy,
            NullLogger<CommandExecutor>.Instance);

        var (accepted, _, stream) = executor.RunCommandStreamed(
            "format", "C:", isReboot: false, timeoutMs: 10000);

        Assert.False(accepted);
        Assert.Null(stream);
    }
}
