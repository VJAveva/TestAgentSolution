using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// A parameter source that fails to load must stop the run BEFORE any action dispatches.
/// Previously <c>ExecuteInitialize</c> returned true regardless, and a missing CSV file was not
/// even logged, so the first visible symptom was an agent being handed a literal "[_Agent1]".
/// </summary>
public class InitializeFailureStopsRunTests : IDisposable
{
    private const string PipelineTag = "Sanity 5 Nodes";

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"initfail_{Guid.NewGuid():N}");

    private readonly Mock<IAgentGrpcDispatcher> _dispatcher = new();
    private readonly ExecutionSessionManager _sessions = new();
    private readonly ActionPipelineExecutor _executor;
    private readonly List<string> _dispatched = [];
    private readonly List<PipelineLogEntry> _log = [];

    public InitializeFailureStopsRunTests()
    {
        Directory.CreateDirectory(_dir);

        var config = new BuildResultsConfig();
        _executor = new ActionPipelineExecutor(
            _dispatcher.Object,
            _sessions,
            NullLogger<ActionPipelineExecutor>.Instance,
            new TrxResultsParser(),
            new BuildResultsAggregator(config),
            new BuildReportHtmlGenerator(config),
            config);

        _executor.LogEntry += e => _log.Add(e);

        _dispatcher
            .Setup(d => d.ExecuteRemoteCommandAsync(
                It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<ActionConfig, PipelineExecutionContext, CancellationToken>((a, _, _) =>
            {
                _dispatched.Add(a.Tag);
                return Task.FromResult(new ActionResult(true, 0, ""));
            });
        _dispatcher
            .Setup(d => d.ExecuteLocalCommandAsync(
                It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<ActionConfig, PipelineExecutionContext, CancellationToken>((a, _, _) =>
            {
                _dispatched.Add(a.Tag);
                return Task.FromResult(new ActionResult(true, 0, ""));
            });
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string WriteConfig(string name, string json)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, json);
        return path;
    }

    private const string ValidConfig = """
    { "version": 1, "global": { "_Agent1": "jvgr1" }, "profiles": { "Sanity": { "_Agent1": "jvgr1" } } }
    """;

    /// <summary>
    /// Deliberately token-FREE. The unresolved-token gate would refuse to dispatch an action
    /// containing "[_Agent1]", so a test built on one would pass even with the abort removed and
    /// prove nothing. Only an actual abort can stop this action.
    /// </summary>
    private static ActionConfig Action(string tag) => new()
    {
        Type = ActionType.RunRemoteCommand,
        Tag = tag,
        AgentName = "jvgr1",
        Command = @"C:\tools\run.bat",
        FailAndContinue = true,
    };

    private Task RunEvent(params IActionNode[] children) =>
        _executor.ExecuteEventTrackedAsync(
            PipelineTag,
            // FailAndContinue is irrelevant for a parameter failure: the event level always passes
            // true, so only an explicit abort can stop the run here.
            new EventConfig { Type = "Renamed", ExecutionType = ExecutionMode.Sequential, Children = [.. children] },
            new PipelineExecutionContext { WatchItemTag = PipelineTag },
            CancellationToken.None);

    private string LogText => string.Join("\n", _log.Select(e => e.Message));

    /// <summary>
    /// An aborted run must not even ATTEMPT the action. Checking only that the agent was never
    /// called is too weak - the token gate also produces that, but by recording a failed action.
    /// </summary>
    private void AssertNothingAttempted()
    {
        Assert.Empty(_dispatched);

        var attempted = _sessions.GetHistory(50).SelectMany(s => s.ActionResults).ToList();
        Assert.True(attempted.Count == 0,
            "The run aborted, so no action should have been attempted, but these were recorded: "
            + string.Join(", ", attempted.Select(r => $"{r.ActionTag}={r.Outcome}")));
    }

    [Fact]
    public async Task ExecuteEventTrackedAsync_Should_DispatchNothing_When_ParameterFileIsMissing()
    {
        await RunEvent(
            new InitializeConfig { Tag = "Init", ParameterFile = Path.Combine(_dir, "nope.json") },
            Action("after-init"));

        AssertNothingAttempted();
        Assert.Contains("does not exist", LogText);
        Assert.Contains("Init", LogText);
    }

    [Fact]
    public async Task ExecuteEventTrackedAsync_Should_DispatchNothing_When_ProfileIsNotInTheFile()
    {
        var path = WriteConfig("pipeline-config.json", ValidConfig);

        await RunEvent(
            new InitializeConfig { Tag = "Init", ParameterFile = path, Profile = "Warm" },
            Action("after-init"));

        AssertNothingAttempted();
        Assert.Contains("profile 'Warm' is not defined", LogText);
        Assert.Contains("defined profiles are: Sanity", LogText);
    }

    [Fact]
    public async Task ExecuteEventTrackedAsync_Should_DispatchNothing_When_MissingCsvParameterFile()
    {
        await RunEvent(
            new InitializeConfig { Tag = "Init", ParameterFile = Path.Combine(_dir, "vars.txt") },
            Action("after-init"));

        AssertNothingAttempted();
        Assert.Contains("does not exist", LogText);
    }

    [Fact]
    public async Task ExecuteEventTrackedAsync_Should_DispatchNothing_When_ConfigIsNotValidJson()
    {
        var path = WriteConfig("broken.json", "{ this is not json");

        await RunEvent(
            new InitializeConfig { Tag = "Init", ParameterFile = path },
            Action("after-init"));

        AssertNothingAttempted();
        Assert.Contains("could not be read as JSON", LogText);
    }

    /// <summary>
    /// The abort has to beat FailAndContinue at every level, or an Initialize buried in a group
    /// would let the group's siblings run on with unresolved tokens.
    /// </summary>
    [Fact]
    public async Task ExecuteEventTrackedAsync_Should_AbortOuterSiblings_When_InitializeFailsInsideAGroup()
    {
        await RunEvent(
            new ActionGroupConfig
            {
                Tag = "Inner",
                FailAndContinue = true,
                Children =
                [
                    new InitializeConfig { Tag = "Init", ParameterFile = Path.Combine(_dir, "nope.json") },
                    Action("inside-group"),
                ],
            },
            Action("outer-sibling"));

        AssertNothingAttempted();
    }

    [Fact]
    public async Task ExecuteEventTrackedAsync_Should_RunEveryAction_When_ParameterSourceLoadsCleanly()
    {
        var path = WriteConfig("pipeline-config.json", ValidConfig);

        await RunEvent(
            new InitializeConfig { Tag = "Init", ParameterFile = path, Profile = "Sanity" },
            Action("first"),
            Action("second"));

        Assert.Equal(["first", "second"], _dispatched);
        Assert.DoesNotContain("ABORTED", LogText);
    }

    [Fact]
    public async Task ExecuteEventTrackedAsync_Should_ReportTheFailingNode_When_InitializeFails()
    {
        var failures = new List<string>();
        _executor.NodeFailed += (node, _, msg) =>
        {
            if (node is InitializeConfig) failures.Add(msg);
        };

        await RunEvent(
            new InitializeConfig { Tag = "LoadSanityVars", ParameterFile = Path.Combine(_dir, "nope.json") },
            Action("after-init"));

        var message = Assert.Single(failures);
        Assert.Contains("LoadSanityVars", message);
        Assert.Contains("Stopping the run before any action", message);
    }
}
