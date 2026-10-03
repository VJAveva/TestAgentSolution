using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// End-to-end cover for the <c>profiles</c> layer of a layered JSON config.
///
/// The layer was dead in production: the snapshot clone taken at the start of every tracked run
/// rebuilt <see cref="InitializeConfig"/> from a hand-written field list that omitted
/// <see cref="InitializeConfig.Profile"/>, so <c>ExecuteInitialize</c> always saw an empty profile
/// and skipped the overlay. Unit-testing the clone alone would not have caught the consequence,
/// so these tests assert the thing the user actually observes: <c>[_Agent1]</c> becomes <c>jvgr1</c>.
/// </summary>
public class InitializeProfileResolutionTests : IDisposable
{
    private const string PipelineTag = "Sanity 5 Nodes";

    private readonly string _configPath =
        Path.Combine(Path.GetTempPath(), $"pipeline-config_{Guid.NewGuid():N}.json");

    private readonly Mock<IAgentGrpcDispatcher> _dispatcher = new();
    private readonly ExecutionSessionManager _sessions = new();
    private readonly ActionPipelineExecutor _executor;

    public InitializeProfileResolutionTests()
    {
        // _Agent1 differs per layer, so the assertion can only pass if the PROFILE layer applied:
        // global alone yields "unset-agent", the pipelines layer never defines _Agent1 at all.
        File.WriteAllText(_configPath, """
        {
          "version": 1,
          "global":  { "_Agent1": "unset-agent", "_EmailCheck": "global@corp.com" },
          "profiles": {
            "Sanity": { "_Agent1": "jvgr1" },
            "Warm":   { "_Agent1": "warmgr" }
          },
          "pipelines": {
            "Sanity 5 Nodes": { "_EmailCheck": "sanity-team@corp.com" }
          }
        }
        """);

        var config = new BuildResultsConfig();
        _executor = new ActionPipelineExecutor(
            _dispatcher.Object,
            _sessions,
            NullLogger<ActionPipelineExecutor>.Instance,
            new TrxResultsParser(),
            new BuildResultsAggregator(config),
            new BuildReportHtmlGenerator(config),
            config);
    }

    public void Dispose()
    {
        if (File.Exists(_configPath)) File.Delete(_configPath);
        GC.SuppressFinalize(this);
    }

    private List<string> CaptureAgentNames()
    {
        var seen = new List<string>();
        _dispatcher
            .Setup(d => d.ExecuteRemoteCommandAsync(
                It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<ActionConfig, PipelineExecutionContext, CancellationToken>((a, ctx, _) =>
            {
                seen.Add(ParameterResolver.Resolve(a.AgentName, ctx));
                return Task.FromResult(new ActionResult(true, 0, ""));
            });
        return seen;
    }

    private static InitializeConfig Init(string path, string profile) =>
        new() { Tag = "Init", ParameterFile = path, Profile = profile };

    private static ActionConfig AgentAction() => new()
    {
        Type = ActionType.RunRemoteCommand,
        Tag = "Run on agent 1",
        AgentName = "[_Agent1]",
        Command = @"C:\tools\run.bat",
    };

    // ── Full run (tracked path — this is where the clone dropped Profile) ──

    [Fact]
    public async Task ExecuteEventTrackedAsync_Should_ResolveAgentFromProfile_When_InitializeDeclaresProfile()
    {
        var seen = CaptureAgentNames();

        var evt = new EventConfig
        {
            Type = "Renamed",
            ExecutionType = ExecutionMode.Sequential,
            Children = [Init(_configPath, "Sanity"), AgentAction()],
        };

        await _executor.ExecuteEventTrackedAsync(
            PipelineTag, evt, new PipelineExecutionContext { WatchItemTag = PipelineTag }, CancellationToken.None);

        Assert.Equal(["jvgr1"], seen);
    }

    [Fact]
    public async Task ExecuteEventTrackedAsync_Should_ResolveAgentFromProfile_When_InitializeIsInsideNestedGroup()
    {
        var seen = CaptureAgentNames();

        var evt = new EventConfig
        {
            Type = "Renamed",
            ExecutionType = ExecutionMode.Sequential,
            Children =
            [
                new ActionGroupConfig
                {
                    Tag = "Outer",
                    Children =
                    [
                        new ActionGroupConfig
                        {
                            Tag = "Inner",
                            Children = [Init(_configPath, "Sanity"), AgentAction()],
                        },
                    ],
                },
            ],
        };

        await _executor.ExecuteEventTrackedAsync(
            PipelineTag, evt, new PipelineExecutionContext { WatchItemTag = PipelineTag }, CancellationToken.None);

        Assert.Equal(["jvgr1"], seen);
    }

    [Fact]
    public async Task ExecuteEventTrackedAsync_Should_ApplyProfileBeneathPipelinePin_When_BothDefineAKey()
    {
        var seen = new List<(string Agent, string Email)>();
        _dispatcher
            .Setup(d => d.ExecuteRemoteCommandAsync(
                It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<ActionConfig, PipelineExecutionContext, CancellationToken>((a, ctx, _) =>
            {
                seen.Add((ParameterResolver.Resolve(a.AgentName, ctx), ParameterResolver.Resolve("[_EmailCheck]", ctx)));
                return Task.FromResult(new ActionResult(true, 0, ""));
            });

        var evt = new EventConfig
        {
            Type = "Renamed",
            ExecutionType = ExecutionMode.Sequential,
            Children = [Init(_configPath, "Sanity"), AgentAction()],
        };

        await _executor.ExecuteEventTrackedAsync(
            PipelineTag, evt, new PipelineExecutionContext { WatchItemTag = PipelineTag }, CancellationToken.None);

        // Profile supplies the agent; the pipelines layer outranks global for the per-pipeline override.
        Assert.Equal([("jvgr1", "sanity-team@corp.com")], seen);
    }

    // ── Node-level run, WPF path (group clone — ExecuteGroupTrackedAsync) ──

    [Fact]
    public async Task ExecuteGroupTrackedAsync_Should_ResolveAgentFromProfile_When_RunningASingleGroup()
    {
        var seen = CaptureAgentNames();

        var group = new ActionGroupConfig
        {
            Tag = "Install",
            ExecutionType = ExecutionMode.Sequential,
            Children = [Init(_configPath, "Sanity"), AgentAction()],
        };

        await _executor.ExecuteGroupTrackedAsync(
            PipelineTag, group, new PipelineExecutionContext { WatchItemTag = PipelineTag }, CancellationToken.None);

        Assert.Equal(["jvgr1"], seen);
    }

    /// <summary>
    /// The WPF node-run pre-loads tokens before dispatch (MainViewModel.CollectInitializeParameters)
    /// and the web node-run does the same via <see cref="ParameterResolver.LoadForWatchItem"/>; both
    /// must honour the Profile attribute or a single-node run resolves agents differently from a
    /// full run of the very same tree.
    /// </summary>
    [Fact]
    public void LoadForWatchItem_Should_ResolveAgentFromProfile_When_PreloadingForANodeRun()
    {
        var watchItem = new WatchItemConfig
        {
            Tag = PipelineTag,
            Events = [new EventConfig { Children = [Init(_configPath, "Sanity"), AgentAction()] }],
        };

        var ctx = new PipelineExecutionContext();
        ParameterResolver.LoadForWatchItem(ctx, watchItem);

        Assert.Equal("jvgr1", ParameterResolver.Resolve("[_Agent1]", ctx));
        Assert.Equal("sanity-team@corp.com", ParameterResolver.Resolve("[_EmailCheck]", ctx));
    }

    [Fact]
    public void LoadForWatchItem_Should_ResolveADifferentAgent_When_ProfileIsWarm()
    {
        var watchItem = new WatchItemConfig
        {
            Tag = PipelineTag,
            Events = [new EventConfig { Children = [Init(_configPath, "Warm")] }],
        };

        var ctx = new PipelineExecutionContext();
        ParameterResolver.LoadForWatchItem(ctx, watchItem);

        Assert.Equal("warmgr", ParameterResolver.Resolve("[_Agent1]", ctx));
    }
}
