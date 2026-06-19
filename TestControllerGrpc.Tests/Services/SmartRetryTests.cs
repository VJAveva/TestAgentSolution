using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

public class SmartRetryTests
{
    private readonly Mock<IAgentGrpcDispatcher> _dispatcher;
    private readonly ExecutionSessionManager _sessionManager;
    private readonly ActionPipelineExecutor _executor;

    public SmartRetryTests()
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
            new BuildReportHtmlGenerator(config),
            config);
    }

    private static PipelineExecutionContext CreateContext() => new();

    // ?????????????????????????????????????????????????????????????????
    // ActionConfig retry property defaults
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public void ActionConfig_Should_HaveRetryDefaults_When_Created()
    {
        var action = new ActionConfig();

        Assert.Equal(0, action.MaxRetries);
        Assert.Equal(10, action.RetryDelaySeconds);
        Assert.Equal("Exponential", action.RetryBackoff);
        Assert.Equal("", action.RetryOnExitCodes);
    }

    // ?????????????????????????????????????????????????????????????????
    // No retry when MaxRetries=0 (backward compatible)
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public async Task ExecuteAction_Should_NotRetry_When_MaxRetriesIsZero()
    {
        var callCount = 0;
        _dispatcher
            .Setup(d => d.ExecuteLocalCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<ActionConfig, PipelineExecutionContext, CancellationToken>((_, _, _) =>
            {
                callCount++;
                return Task.FromResult(new ActionResult(false, 1, "error"));
            });

        var action = new ActionConfig
        {
            Type = ActionType.RunCommand, Command = "fail",
            MaxRetries = 0, FailAndContinue = true,
        };
        var group = new ActionGroupConfig
        {
            Tag = "NoRetry", ExecutionType = ExecutionMode.Sequential,
            FailAndContinue = true,
            Children = [action]
        };

        await _executor.ExecuteGroupAsync(group, CreateContext(), CancellationToken.None);

        Assert.Equal(1, callCount);
    }

    // ?????????????????????????????????????????????????????????????????
    // Retry succeeds on second attempt
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public async Task ExecuteAction_Should_SucceedOnRetry_When_SecondAttemptPasses()
    {
        var callCount = 0;
        _dispatcher
            .Setup(d => d.ExecuteLocalCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<ActionConfig, PipelineExecutionContext, CancellationToken>((_, _, _) =>
            {
                callCount++;
                var success = callCount >= 2;
                return Task.FromResult(new ActionResult(success, success ? 0 : 1, success ? "" : "transient"));
            });

        var action = new ActionConfig
        {
            Type = ActionType.RunCommand, Command = "flaky",
            MaxRetries = 2, RetryDelaySeconds = 1, RetryBackoff = "Fixed",
            FailAndContinue = false,
        };

        var result = await _executor.ExecuteSingleActionAsync(action, CreateContext(), CancellationToken.None);

        Assert.True(result);
        Assert.Equal(2, callCount);
    }

    // ?????????????????????????????????????????????????????????????????
    // All retries exhausted
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public async Task ExecuteAction_Should_FailAfterAllRetries_When_NeverSucceeds()
    {
        var callCount = 0;
        _dispatcher
            .Setup(d => d.ExecuteLocalCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<ActionConfig, PipelineExecutionContext, CancellationToken>((_, _, _) =>
            {
                callCount++;
                return Task.FromResult(new ActionResult(false, 1, "persistent error"));
            });

        var action = new ActionConfig
        {
            Type = ActionType.RunCommand, Command = "alwaysfail",
            MaxRetries = 2, RetryDelaySeconds = 1, RetryBackoff = "Fixed",
            FailAndContinue = true,
        };
        var group = new ActionGroupConfig
        {
            Tag = "RetryExhaust", ExecutionType = ExecutionMode.Sequential,
            FailAndContinue = true,
            Children = [action]
        };

        await _executor.ExecuteGroupAsync(group, CreateContext(), CancellationToken.None);

        // 1 initial + 2 retries = 3 total
        Assert.Equal(3, callCount);
    }

    // ?????????????????????????????????????????????????????????????????
    // RetryOnExitCodes filter
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public async Task ExecuteAction_Should_NotRetry_When_ExitCodeNotInFilter()
    {
        var callCount = 0;
        _dispatcher
            .Setup(d => d.ExecuteLocalCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<ActionConfig, PipelineExecutionContext, CancellationToken>((_, _, _) =>
            {
                callCount++;
                return Task.FromResult(new ActionResult(false, 99, "unretryable"));
            });

        var action = new ActionConfig
        {
            Type = ActionType.RunCommand, Command = "fail99",
            MaxRetries = 3, RetryDelaySeconds = 1, RetryBackoff = "Fixed",
            RetryOnExitCodes = "1,2,3",  // 99 is NOT in this list
            FailAndContinue = true,
        };
        var group = new ActionGroupConfig
        {
            Tag = "FilteredRetry", ExecutionType = ExecutionMode.Sequential,
            FailAndContinue = true,
            Children = [action]
        };

        await _executor.ExecuteGroupAsync(group, CreateContext(), CancellationToken.None);

        // Should only execute once — exit code 99 doesn't match filter
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task ExecuteAction_Should_Retry_When_ExitCodeInFilter()
    {
        var callCount = 0;
        _dispatcher
            .Setup(d => d.ExecuteLocalCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<ActionConfig, PipelineExecutionContext, CancellationToken>((_, _, _) =>
            {
                callCount++;
                return Task.FromResult(new ActionResult(false, 1, "retryable"));
            });

        var action = new ActionConfig
        {
            Type = ActionType.RunCommand, Command = "fail1",
            MaxRetries = 1, RetryDelaySeconds = 1, RetryBackoff = "Fixed",
            RetryOnExitCodes = "-1,1,2",
            FailAndContinue = true,
        };
        var group = new ActionGroupConfig
        {
            Tag = "MatchedRetry", ExecutionType = ExecutionMode.Sequential,
            FailAndContinue = true,
            Children = [action]
        };

        await _executor.ExecuteGroupAsync(group, CreateContext(), CancellationToken.None);

        // 1 initial + 1 retry = 2
        Assert.Equal(2, callCount);
    }

    // ?????????????????????????????????????????????????????????????????
    // FailAndContinue respected after retries exhaust
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public async Task ExecuteAction_Should_ContinuePipeline_When_FailAndContinueTrueAfterRetries()
    {
        var executed = new List<string>();
        _dispatcher
            .Setup(d => d.ExecuteLocalCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<ActionConfig, PipelineExecutionContext, CancellationToken>((a, _, _) =>
            {
                executed.Add(a.Command);
                var success = a.Command != "flaky";
                return Task.FromResult(new ActionResult(success, success ? 0 : 1, success ? "" : "err"));
            });

        var group = new ActionGroupConfig
        {
            Tag = "RetryThenContinue", ExecutionType = ExecutionMode.Sequential,
            FailAndContinue = true,
            Children =
            [
                new ActionConfig
                {
                    Type = ActionType.RunCommand, Command = "flaky",
                    MaxRetries = 1, RetryDelaySeconds = 1, RetryBackoff = "Fixed",
                    FailAndContinue = true,
                },
                new ActionConfig { Type = ActionType.RunCommand, Command = "after" },
            ]
        };

        var result = await _executor.ExecuteGroupAsync(group, CreateContext(), CancellationToken.None);

        Assert.True(result);
        // flaky tried twice, then "after" still executes
        Assert.Equal(3, executed.Count);
        Assert.Equal("flaky", executed[0]);
        Assert.Equal("flaky", executed[1]);
        Assert.Equal("after", executed[2]);
    }

    // ?????????????????????????????????????????????????????????????????
    // Retry log events
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public async Task ExecuteAction_Should_LogRetryAttempts_When_Retrying()
    {
        var callCount = 0;
        _dispatcher
            .Setup(d => d.ExecuteLocalCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<ActionConfig, PipelineExecutionContext, CancellationToken>((_, _, _) =>
            {
                callCount++;
                var success = callCount >= 2;
                return Task.FromResult(new ActionResult(success, success ? 0 : 1, success ? "" : "err"));
            });

        var logMessages = new List<string>();
        _executor.LogEntry += entry => logMessages.Add(entry.Message);

        var action = new ActionConfig
        {
            Type = ActionType.RunCommand, Command = "retryable",
            MaxRetries = 2, RetryDelaySeconds = 1, RetryBackoff = "Fixed",
        };

        await _executor.ExecuteSingleActionAsync(action, CreateContext(), CancellationToken.None);

        Assert.Contains(logMessages, m => m.Contains("will retry"));
        Assert.Contains(logMessages, m => m.Contains("Succeeded on attempt"));
    }

    // ?????????????????????????????????????????????????????????????????
    // Cancellation during retry delay
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public async Task ExecuteAction_Should_StopRetrying_When_CancellationRequested()
    {
        var callCount = 0;
        _dispatcher
            .Setup(d => d.ExecuteLocalCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<ActionConfig, PipelineExecutionContext, CancellationToken>((_, _, _) =>
            {
                callCount++;
                return Task.FromResult(new ActionResult(false, 1, "error"));
            });

        using var cts = new CancellationTokenSource();

        var action = new ActionConfig
        {
            Type = ActionType.RunCommand, Command = "cancelme",
            MaxRetries = 5, RetryDelaySeconds = 60, RetryBackoff = "Fixed",
            FailAndContinue = true,
        };
        var group = new ActionGroupConfig
        {
            Tag = "CancelRetry", ExecutionType = ExecutionMode.Sequential,
            FailAndContinue = true,
            Children = [action]
        };

        // Cancel shortly after first attempt fails and delay begins
        cts.CancelAfter(200);

        await _executor.ExecuteGroupAsync(group, CreateContext(), cts.Token);

        // Should have executed only once (or maybe twice if timing allows),
        // but definitely not all 6 attempts
        Assert.True(callCount < 3, $"Expected early stop but got {callCount} attempts");
    }
}
