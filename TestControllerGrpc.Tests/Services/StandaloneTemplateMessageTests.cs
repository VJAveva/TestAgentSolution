using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Wording of the unresolved-token failure. Running a Template from the library names tokens the
/// user cannot fix: there is no Initialize to go and repair, so "the Initialize parameter source
/// for this pipeline did not load" sends them hunting a file that does not exist.
/// </summary>
public class StandaloneTemplateMessageTests
{
    private readonly Mock<IAgentGrpcDispatcher> _dispatcher = new();
    private readonly ExecutionSessionManager _sessions = new();
    private readonly ActionPipelineExecutor _executor;

    public StandaloneTemplateMessageTests()
    {
        var cfg = new BuildResultsConfig();
        _executor = new ActionPipelineExecutor(
            _dispatcher.Object, _sessions,
            NullLogger<ActionPipelineExecutor>.Instance,
            new TrxResultsParser(), new BuildResultsAggregator(cfg),
            new BuildReportHtmlGenerator(cfg), cfg);
    }

    private async Task<string> RunAndGetError(bool standaloneTemplate)
    {
        var template = new TemplateConfig
        {
            ID = "PrepSanity",
            Children =
            [
                new ActionConfig
                {
                    Type = ActionType.RunRemoteCommand,
                    Tag = "Copy prep files",
                    AgentName = "[_Agent1]",
                    Command = @"C:\[_InstallFolder]\prep.bat",
                },
            ],
        };

        var ctx = new PipelineExecutionContext { IsStandaloneTemplateRun = standaloneTemplate };
        await _executor.ExecuteTemplateTrackedAsync("Template:PrepSanity", template, ctx, CancellationToken.None);

        var result = _sessions.GetHistory(10).SelectMany(s => s.ActionResults).Single();
        return result.ErrorMessage ?? "";
    }

    [Fact]
    public async Task ExecuteTemplate_Should_SayTheTemplateHasNoSettings_When_RunStandalone()
    {
        var message = await RunAndGetError(standaloneTemplate: true);

        Assert.Contains("this template has no settings of its own", message);
        Assert.Contains("run it from a pipeline that uses it", message);
        Assert.DoesNotContain("Initialize parameter source", message);

        // The tokens themselves must still be named, or the message is unactionable.
        Assert.Contains("[_Agent1]", message);
        Assert.Contains("[_InstallFolder]", message);
    }

    [Fact]
    public async Task ExecuteTemplate_Should_KeepThePipelineWording_When_RunInsideAPipeline()
    {
        var message = await RunAndGetError(standaloneTemplate: false);

        Assert.Contains("the Initialize parameter source for this pipeline did not load", message);
        Assert.DoesNotContain("no settings of its own", message);
    }
}
