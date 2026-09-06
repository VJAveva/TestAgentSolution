using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

public class ActionPipelineExecutorTests
{
    private readonly Mock<IAgentGrpcDispatcher> _dispatcher;
    private readonly ExecutionSessionManager _sessionManager;
    private readonly ActionPipelineExecutor _executor;

    public ActionPipelineExecutorTests()
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

    // ???????????????????????????????????????????????????????????????????
    // Sequential execution runs actions in order
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public async Task ExecuteGroupAsync_Should_RunActionsInOrder_When_Sequential()
    {
        var executionOrder = new List<string>();

        _dispatcher
            .Setup(d => d.ExecuteLocalCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<ActionConfig, PipelineExecutionContext, CancellationToken>((a, _, _) =>
            {
                executionOrder.Add(a.Command);
                return Task.FromResult(new ActionResult(true, 0, ""));
            });

        var group = new ActionGroupConfig
        {
            Tag = "SeqGroup",
            ExecutionType = ExecutionMode.Sequential,
            FailAndContinue = true,
            Children =
            [
                new ActionConfig { Type = ActionType.RunCommand, Command = "first" },
                new ActionConfig { Type = ActionType.RunCommand, Command = "second" },
                new ActionConfig { Type = ActionType.RunCommand, Command = "third" },
            ]
        };

        var result = await _executor.ExecuteGroupAsync(group, CreateContext(), CancellationToken.None);

        Assert.True(result);
        Assert.Equal(["first", "second", "third"], executionOrder);
    }

    // ???????????????????????????????????????????????????????????????????
    // Parallel execution runs all simultaneously
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public async Task ExecuteGroupAsync_Should_RunAllSimultaneously_When_Parallel()
    {
        var concurrencyCounter = 0;
        var maxConcurrency = 0;
        var lockObj = new object();

        _dispatcher
            .Setup(d => d.ExecuteLocalCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<ActionConfig, PipelineExecutionContext, CancellationToken>(async (_, _, _) =>
            {
                lock (lockObj)
                {
                    concurrencyCounter++;
                    maxConcurrency = Math.Max(maxConcurrency, concurrencyCounter);
                }
                await Task.Delay(100);
                lock (lockObj) { concurrencyCounter--; }
                return new ActionResult(true, 0, "");
            });

        var group = new ActionGroupConfig
        {
            Tag = "ParGroup",
            ExecutionType = ExecutionMode.Parallel,
            FailAndContinue = true,
            Children =
            [
                new ActionConfig { Type = ActionType.RunCommand, Command = "a" },
                new ActionConfig { Type = ActionType.RunCommand, Command = "b" },
                new ActionConfig { Type = ActionType.RunCommand, Command = "c" },
            ]
        };

        var result = await _executor.ExecuteGroupAsync(group, CreateContext(), CancellationToken.None);

        Assert.True(result);
        Assert.True(maxConcurrency > 1, $"Expected concurrent execution but maxConcurrency was {maxConcurrency}");
    }

    // ???????????????????????????????????????????????????????????????????
    // FailAndContinue=true continues after failure
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public async Task ExecuteGroupAsync_Should_ContinueAfterFailure_When_FailAndContinueTrue()
    {
        var executed = new List<string>();

        _dispatcher
            .Setup(d => d.ExecuteLocalCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<ActionConfig, PipelineExecutionContext, CancellationToken>((a, _, _) =>
            {
                executed.Add(a.Command);
                var success = a.Command != "fail";
                return Task.FromResult(new ActionResult(success, success ? 0 : 1, success ? "" : "error"));
            });

        var group = new ActionGroupConfig
        {
            Tag = "ContinueGroup",
            ExecutionType = ExecutionMode.Sequential,
            FailAndContinue = true,
            Children =
            [
                new ActionConfig { Type = ActionType.RunCommand, Command = "first" },
                new ActionConfig { Type = ActionType.RunCommand, Command = "fail", FailAndContinue = true },
                new ActionConfig { Type = ActionType.RunCommand, Command = "third" },
            ]
        };

        var result = await _executor.ExecuteGroupAsync(group, CreateContext(), CancellationToken.None);

        Assert.True(result);
        Assert.Equal(["first", "fail", "third"], executed);
    }

    // ???????????????????????????????????????????????????????????????????
    // FailAndContinue=false stops on first failure
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public async Task ExecuteGroupAsync_Should_StopOnFirstFailure_When_FailAndContinueFalse()
    {
        var executed = new List<string>();

        _dispatcher
            .Setup(d => d.ExecuteLocalCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<ActionConfig, PipelineExecutionContext, CancellationToken>((a, _, _) =>
            {
                executed.Add(a.Command);
                var success = a.Command != "fail";
                return Task.FromResult(new ActionResult(success, success ? 0 : 1, success ? "" : "error"));
            });

        var group = new ActionGroupConfig
        {
            Tag = "StopGroup",
            ExecutionType = ExecutionMode.Sequential,
            FailAndContinue = false,
            Children =
            [
                new ActionConfig { Type = ActionType.RunCommand, Command = "first" },
                new ActionConfig { Type = ActionType.RunCommand, Command = "fail" },
                new ActionConfig { Type = ActionType.RunCommand, Command = "third" },
            ]
        };

        var result = await _executor.ExecuteGroupAsync(group, CreateContext(), CancellationToken.None);

        Assert.False(result);
        Assert.Equal(["first", "fail"], executed);
        Assert.DoesNotContain("third", executed);
    }

    // ???????????????????????????????????????????????????????????????????
    // Initialize loads parameters before siblings execute
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public async Task ExecuteEventAsync_Should_LoadParameters_When_InitializeNodePresent()
    {
        // Create a temporary parameter file
        var tempDir = Path.Combine(Path.GetTempPath(), $"PipelineTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var paramFile = Path.Combine(tempDir, "params.txt");
            File.WriteAllText(paramFile, "_BuildNumber,42.0\n_DropLocation,\\\\share\\drop");

            string? capturedCommand = null;
            _dispatcher
                .Setup(d => d.ExecuteLocalCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
                .Returns<ActionConfig, PipelineExecutionContext, CancellationToken>((a, ctx, _) =>
                {
                    // Capture the resolved command � by this point Initialize should have run
                    capturedCommand = ParameterResolver.Resolve(a.Command, ctx);
                    return Task.FromResult(new ActionResult(true, 0, ""));
                });

            var evt = new EventConfig
            {
                Type = "Renamed",
                ExecutionType = ExecutionMode.Sequential,
                Children =
                [
                    new InitializeConfig { Tag = "Init", ParameterFile = paramFile },
                    new ActionConfig { Type = ActionType.RunCommand, Command = "[BuildNumber]" },
                ]
            };

            await _executor.ExecuteEventAsync(evt, CreateContext(), CancellationToken.None);

            Assert.Equal("42.0", capturedCommand);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    // ???????????????????????????????????????????????????????????????????
    // Ref expands template inline
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public async Task ExecuteEventAsync_Should_ExpandTemplateInline_When_RefNodePresent()
    {
        var executed = new List<string>();

        _dispatcher
            .Setup(d => d.ExecuteLocalCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<ActionConfig, PipelineExecutionContext, CancellationToken>((a, _, _) =>
            {
                executed.Add(a.Command);
                return Task.FromResult(new ActionResult(true, 0, ""));
            });

        // Load templates into the executor
        _executor.LoadTemplates(
        [
            new TemplateConfig
            {
                ID = "MyTemplate",
                Children =
                [
                    new ActionConfig { Type = ActionType.RunCommand, Command = "template-step1" },
                    new ActionConfig { Type = ActionType.RunCommand, Command = "template-step2" },
                ]
            }
        ]);

        var evt = new EventConfig
        {
            Type = "Renamed",
            ExecutionType = ExecutionMode.Sequential,
            Children =
            [
                new ActionConfig { Type = ActionType.RunCommand, Command = "before" },
                new RefConfig { TemplateID = "MyTemplate" },
                new ActionConfig { Type = ActionType.RunCommand, Command = "after" },
            ]
        };

        await _executor.ExecuteEventAsync(evt, CreateContext(), CancellationToken.None);

        Assert.Equal(["before", "template-step1", "template-step2", "after"], executed);
    }

    [Fact]
    public async Task ExecuteTemplateTrackedAsync_Should_RunTemplateActionsAndGroups_When_Invoked()
    {
        var executed = new List<string>();

        _dispatcher
            .Setup(d => d.ExecuteLocalCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<ActionConfig, PipelineExecutionContext, CancellationToken>((a, _, _) =>
            {
                executed.Add(a.Command);
                return Task.FromResult(new ActionResult(true, 0, ""));
            });

        var template = new TemplateConfig
        {
            ID = "SmokeSuite",
            Children =
            [
                new ActionConfig { Type = ActionType.RunCommand, Command = "top-action" },
                new ActionGroupConfig
                {
                    Tag = "Group1",
                    ExecutionType = ExecutionMode.Sequential,
                    Children =
                    [
                        new ActionConfig { Type = ActionType.RunCommand, Command = "group-step1" },
                        new ActionConfig { Type = ActionType.RunCommand, Command = "group-step2" },
                    ]
                },
            ]
        };

        await _executor.ExecuteTemplateTrackedAsync("Template:SmokeSuite", template, CreateContext(), CancellationToken.None);

        Assert.Equal(["top-action", "group-step1", "group-step2"], executed);
    }

    [Fact]
    public async Task ExecuteEventAsync_Should_ReturnFalse_When_RefTemplateNotFound()
    {
        // No templates loaded � Ref should fail
        var evt = new EventConfig
        {
            Type = "Renamed",
            ExecutionType = ExecutionMode.Sequential,
            Children =
            [
                new RefConfig { TemplateID = "NonExistent" },
            ]
        };

        // ExecuteEventAsync doesn't return bool, but we can verify via NodeProgress
        var statuses = new List<string>();
        _executor.NodeProgress += (node, status) =>
        {
            if (node is RefConfig) statuses.Add(status);
        };

        await _executor.ExecuteEventAsync(evt, CreateContext(), CancellationToken.None);

        Assert.Contains("Failed", statuses);
    }

    // ???????????????????????????????????????????????????????????????????
    // Cancellation token stops execution
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public async Task ExecuteGroupAsync_Should_StopExecution_When_TokenCanceled()
    {
        var executed = new List<string>();

        _dispatcher
            .Setup(d => d.ExecuteLocalCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<ActionConfig, PipelineExecutionContext, CancellationToken>(async (a, _, ct) =>
            {
                executed.Add(a.Command);
                await Task.Delay(5000, ct);
                return new ActionResult(true, 0, "");
            });

        var cts = new CancellationTokenSource();
        var group = new ActionGroupConfig
        {
            Tag = "CancelGroup",
            ExecutionType = ExecutionMode.Sequential,
            FailAndContinue = false,
            Children =
            [
                new ActionConfig { Type = ActionType.RunCommand, Command = "slow" },
                new ActionConfig { Type = ActionType.RunCommand, Command = "never-reached" },
            ]
        };

        // Cancel after a short delay
        cts.CancelAfter(50);

        // The cancellation causes the first action to fail (caught internally),
        // and FailAndContinue=false stops before reaching the second action.
        var result = await _executor.ExecuteGroupAsync(group, CreateContext(), cts.Token);

        Assert.False(result);
        Assert.Single(executed);
        Assert.DoesNotContain("never-reached", executed);
    }

    [Fact]
    public async Task ExecuteGroupAsync_Should_ThrowOperationCanceled_When_TokenPreCanceled()
    {
        var executed = new List<string>();

        _dispatcher
            .Setup(d => d.ExecuteLocalCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<ActionConfig, PipelineExecutionContext, CancellationToken>((a, _, _) =>
            {
                executed.Add(a.Command);
                return Task.FromResult(new ActionResult(true, 0, ""));
            });

        var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel

        var group = new ActionGroupConfig
        {
            Tag = "PreCanceled",
            ExecutionType = ExecutionMode.Sequential,
            FailAndContinue = false,
            Children =
            [
                new ActionConfig { Type = ActionType.RunCommand, Command = "should-not-run" },
            ]
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _executor.ExecuteGroupAsync(group, CreateContext(), cts.Token));

        Assert.Empty(executed);
    }

    // ???????????????????????????????????????????????????????????????????
    // Snapshot isolation: config changes don't affect running pipeline
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public async Task ExecuteEventTrackedAsync_Should_UseSnapshot_When_ConfigMutatedDuringExecution()
    {
        var executedCommands = new List<string>();

        // First action: succeeds, mutate the original event's children
        // Second action (from snapshot): should still run with original command
        _dispatcher
            .Setup(d => d.ExecuteLocalCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<ActionConfig, PipelineExecutionContext, CancellationToken>((a, _, _) =>
            {
                executedCommands.Add(a.Command);
                return Task.FromResult(new ActionResult(true, 0, ""));
            });

        var action1 = new ActionConfig { Type = ActionType.RunCommand, Command = "original-1" };
        var action2 = new ActionConfig { Type = ActionType.RunCommand, Command = "original-2" };
        var evt = new EventConfig
        {
            Type = "Renamed",
            ExecutionType = ExecutionMode.Sequential,
            Children = [action1, action2]
        };

        // Start execution in background
        var task = _executor.ExecuteEventTrackedAsync("TestItem", evt, CreateContext(), CancellationToken.None);

        // Mutate original config while execution is in progress
        action2.Command = "mutated-2";

        await task;

        // The snapshot should have preserved "original-2"
        Assert.Contains("original-1", executedCommands);
        Assert.Contains("original-2", executedCommands);
        Assert.DoesNotContain("mutated-2", executedCommands);
    }

    // ???????????????????????????????????????????????????????????????????
    // NodeProgress events are raised correctly
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public async Task ExecuteSingleActionAsync_Should_RaiseNodeProgress_When_ActionSucceeds()
    {
        _dispatcher
            .Setup(d => d.ExecuteLocalCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActionResult(true, 0, ""));

        var progressEvents = new List<(IActionNode Node, string Status)>();
        _executor.NodeProgress += (node, status) => progressEvents.Add((node, status));

        var action = new ActionConfig { Type = ActionType.RunCommand, Command = "test" };
        await _executor.ExecuteSingleActionAsync(action, CreateContext(), CancellationToken.None);

        Assert.Equal(2, progressEvents.Count);
        Assert.Equal("Running", progressEvents[0].Status);
        Assert.Equal("Success", progressEvents[1].Status);
    }

    [Fact]
    public async Task ExecuteSingleActionAsync_Should_RaiseNodeProgressFailed_When_ActionFails()
    {
        _dispatcher
            .Setup(d => d.ExecuteLocalCommandAsync(It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActionResult(false, 1, "error"));

        var progressEvents = new List<(IActionNode Node, string Status)>();
        _executor.NodeProgress += (node, status) => progressEvents.Add((node, status));

        var action = new ActionConfig { Type = ActionType.RunCommand, Command = "test", FailAndContinue = false };
        await _executor.ExecuteSingleActionAsync(action, CreateContext(), CancellationToken.None);

        Assert.Equal(2, progressEvents.Count);
        Assert.Equal("Running", progressEvents[0].Status);
        Assert.Equal("Failed", progressEvents[1].Status);
    }
}
