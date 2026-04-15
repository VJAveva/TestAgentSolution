using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

public class ConcurrentSessionTests
{
    private readonly Mock<IAgentGrpcDispatcher> _dispatcher;
    private readonly ExecutionSessionManager _sessionManager;
    private readonly ActionPipelineExecutor _executor;

    public ConcurrentSessionTests()
    {
        _dispatcher = new Mock<IAgentGrpcDispatcher>();
        _sessionManager = new ExecutionSessionManager();
        var config = new BuildResultsConfig();
        _executor = new ActionPipelineExecutor(
            _dispatcher.Object,
            _sessionManager,
            NullLogger<ActionPipelineExecutor>.Instance,
            new TrxResultsParser(),
            new BuildResultsAggregator(config),
            new BuildReportHtmlGenerator(config));
    }

    // ?????????????????????????????????????????????????????????????????
    // PipelineExecutionContext.SessionId defaults
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public void PipelineExecutionContext_Should_HaveEmptySessionId_When_Created()
    {
        var ctx = new PipelineExecutionContext();
        Assert.Equal("", ctx.SessionId);
    }

    [Fact]
    public void PipelineExecutionContext_Should_StoreSessionId_When_Set()
    {
        var ctx = new PipelineExecutionContext { SessionId = "abc123" };
        Assert.Equal("abc123", ctx.SessionId);
    }

    // ?????????????????????????????????????????????????????????????????
    // Session-aware log prefix
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public async Task Executor_Should_LogWithoutPrefix_When_SessionIdEmpty()
    {
        _dispatcher
            .Setup(d => d.ExecuteLocalCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActionResult(true, 0, ""));

        var logMessages = new List<string>();
        _executor.LogEntry += entry => logMessages.Add(entry.Message);

        var ctx = new PipelineExecutionContext { SessionId = "" };
        var action = new ActionConfig { Type = ActionType.RunCommand, Command = "test" };

        await _executor.ExecuteSingleActionAsync(action, ctx, CancellationToken.None);

        // Logs should NOT contain [sessionId] brackets for empty session
        Assert.DoesNotContain(logMessages, m => m.StartsWith("[") && m.Contains("] RunCommand"));
    }

    // ?????????????????????????????????????????????????????????????????
    // Multiple concurrent sessions don't interfere
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public async Task ConcurrentExecutions_Should_CompleteIndependently_When_RunInParallel()
    {
        var agentCommands = new System.Collections.Concurrent.ConcurrentBag<string>();

        _dispatcher
            .Setup(d => d.ExecuteLocalCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<ActionConfig, PipelineExecutionContext, CancellationToken>(async (a, _, _) =>
            {
                agentCommands.Add(a.Command);
                await Task.Delay(50);
                return new ActionResult(true, 0, "");
            });

        var evt1 = new EventConfig
        {
            Type = "Renamed", ExecutionType = ExecutionMode.Sequential,
            Children =
            [
                new ActionConfig { Type = ActionType.RunCommand, Command = "session1-action1" },
                new ActionConfig { Type = ActionType.RunCommand, Command = "session1-action2" },
            ]
        };

        var evt2 = new EventConfig
        {
            Type = "Renamed", ExecutionType = ExecutionMode.Sequential,
            Children =
            [
                new ActionConfig { Type = ActionType.RunCommand, Command = "session2-action1" },
                new ActionConfig { Type = ActionType.RunCommand, Command = "session2-action2" },
            ]
        };

        var ctx1 = new PipelineExecutionContext { SessionId = "ses001" };
        var ctx2 = new PipelineExecutionContext { SessionId = "ses002" };

        // Execute both concurrently
        var task1 = _executor.ExecuteEventAsync(evt1, ctx1, CancellationToken.None);
        var task2 = _executor.ExecuteEventAsync(evt2, ctx2, CancellationToken.None);

        await Task.WhenAll(task1, task2);

        Assert.Contains("session1-action1", agentCommands);
        Assert.Contains("session1-action2", agentCommands);
        Assert.Contains("session2-action1", agentCommands);
        Assert.Contains("session2-action2", agentCommands);
        Assert.Equal(4, agentCommands.Count);
    }

    // ?????????????????????????????????????????????????????????????????
    // Cancelling one session doesn't affect another
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public async Task CancelOneSession_Should_NotAffectOther_When_RunningConcurrently()
    {
        var session1Commands = new System.Collections.Concurrent.ConcurrentBag<string>();
        var session2Commands = new System.Collections.Concurrent.ConcurrentBag<string>();

        _dispatcher
            .Setup(d => d.ExecuteLocalCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<ActionConfig, PipelineExecutionContext, CancellationToken>(async (a, ctx, ct) =>
            {
                if (ctx.SessionId == "cancel")
                    session1Commands.Add(a.Command);
                else
                    session2Commands.Add(a.Command);
                await Task.Delay(200, ct);
                return new ActionResult(true, 0, "");
            });

        using var cts1 = new CancellationTokenSource();
        using var cts2 = new CancellationTokenSource();

        var evt1 = new EventConfig
        {
            Type = "Renamed", ExecutionType = ExecutionMode.Sequential,
            Children =
            [
                new ActionConfig { Type = ActionType.RunCommand, Command = "s1-cmd1" },
                new ActionConfig { Type = ActionType.RunCommand, Command = "s1-cmd2" },
            ]
        };

        var evt2 = new EventConfig
        {
            Type = "Renamed", ExecutionType = ExecutionMode.Sequential,
            Children =
            [
                new ActionConfig { Type = ActionType.RunCommand, Command = "s2-cmd1" },
                new ActionConfig { Type = ActionType.RunCommand, Command = "s2-cmd2" },
            ]
        };

        // Cancel session 1 quickly
        cts1.CancelAfter(50);

        // Wrap the cancellable task to catch the expected exception
        var task1 = Task.Run(async () =>
        {
            try
            {
                await _executor.ExecuteEventAsync(evt1, new PipelineExecutionContext { SessionId = "cancel" }, cts1.Token);
            }
            catch (OperationCanceledException) { /* expected */ }
        });
        var task2 = _executor.ExecuteEventAsync(evt2, new PipelineExecutionContext { SessionId = "keep" }, cts2.Token);

        await Task.WhenAll(task1, task2);

        // Session 1 was cancelled — should have fewer commands
        Assert.True(session1Commands.Count < 2);
        // Session 2 completed normally
        Assert.Equal(2, session2Commands.Count);
    }

    // ?????????????????????????????????????????????????????????????????
    // PipelineSession per-WatchItem isolation
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public void PipelineSession_Should_HaveIndependentCts_When_MultipleCreated()
    {
        var session1 = new PipelineSession { WatchItemTag = "Item1" };
        var session2 = new PipelineSession { WatchItemTag = "Item2" };

        session1.Cancel();

        Assert.True(session1.Cts.IsCancellationRequested);
        Assert.False(session2.Cts.IsCancellationRequested);
        Assert.Equal("Cancelled", session1.Status);
        Assert.Equal("Running", session2.Status);
    }
}
