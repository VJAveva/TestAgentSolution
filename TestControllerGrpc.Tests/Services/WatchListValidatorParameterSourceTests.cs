using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// The four parameter-source mistakes that used to be completely silent: a missing file, a Profile
/// that is not in the file, a WatchItem Tag with no <c>pipelines</c> entry (a rename drops every
/// override with no signal), and a token no layer defines.
/// </summary>
public class WatchListValidatorParameterSourceTests : IDisposable
{
    private const string Tag = "Sanity 5 Nodes";

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"validator_{Guid.NewGuid():N}");

    public WatchListValidatorParameterSourceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string WriteConfig(string json)
    {
        var path = Path.Combine(_dir, "pipeline-config.json");
        File.WriteAllText(path, json);
        return path;
    }

    private const string FullConfig = """
    {
      "version": 1,
      "global":   { "_Agent1": "jvgr1" },
      "profiles": { "Sanity": { "_Agent1": "jvgr1" } },
      "pipelines": { "Sanity 5 Nodes": { "_EmailCheck": "qa@corp.com" } }
    }
    """;

    private static WatchListConfig Build(InitializeConfig init, params ActionConfig[] actions)
    {
        List<IActionNode> children = [init, .. actions];
        return new WatchListConfig
        {
            WatchItems =
            [
                new WatchItemConfig
                {
                    Tag = Tag,
                    Path = @"C:\Triggers",
                    Events = [new EventConfig { Type = "Renamed", Children = children }],
                },
            ],
        };
    }

    private static ActionConfig Remote(string tag, string agent = "jvgr1") => new()
    {
        Type = ActionType.RunRemoteCommand,
        Tag = tag,
        AgentName = agent,
        Command = @"C:\tools\run.bat",
    };

    private static List<string> WarningsOf(WatchListConfig config) =>
        WatchListValidator.Analyze(config)
            .Where(i => i.Severity == WatchIssueSeverity.Warning)
            .Select(i => i.Message)
            .ToList();

    [Fact]
    public void Analyze_Should_WarnFileNotFound_When_ParameterFileIsMissing()
    {
        var config = Build(
            new InitializeConfig { Tag = "Init", ParameterFile = Path.Combine(_dir, "absent.json") },
            Remote("install"));

        Assert.Contains(WarningsOf(config), m => m.Contains("does not exist"));
    }

    [Fact]
    public void Analyze_Should_WarnProfileMissing_When_ProfileIsNotInTheFile()
    {
        var path = WriteConfig(FullConfig);
        var config = Build(
            new InitializeConfig { Tag = "Init", ParameterFile = path, Profile = "Warm" },
            Remote("install"));

        Assert.Contains(WarningsOf(config), m =>
            m.Contains("Profile 'Warm' is not defined") && m.Contains("available: Sanity"));
    }

    [Fact]
    public void Analyze_Should_WarnTagHasNoPipelinesEntry_When_TagWasRenamed()
    {
        var path = WriteConfig("""
        {
          "version": 1,
          "global":   { "_Agent1": "jvgr1" },
          "profiles": { "Sanity": { "_Agent1": "jvgr1" } },
          "pipelines": { "Old Pipeline Name": { "_EmailCheck": "qa@corp.com" } }
        }
        """);

        var config = Build(
            new InitializeConfig { Tag = "Init", ParameterFile = path, Profile = "Sanity" },
            Remote("install"));

        Assert.Contains(WarningsOf(config), m =>
            m.Contains("No 'pipelines' entry is keyed 'Sanity 5 Nodes'"));
    }

    [Fact]
    public void Analyze_Should_NotWarnAboutPipelinesEntry_When_TagMatches()
    {
        var path = WriteConfig(FullConfig);
        var config = Build(
            new InitializeConfig { Tag = "Init", ParameterFile = path, Profile = "Sanity" },
            Remote("install"));

        Assert.DoesNotContain(WarningsOf(config), m => m.Contains("No 'pipelines' entry"));
    }

    [Fact]
    public void Analyze_Should_WarnUndefinedToken_When_NoLayerDefinesIt()
    {
        var path = WriteConfig(FullConfig);
        var action = Remote("notify");
        action.Type = ActionType.SendMail;
        action.To = "[_NotDefinedAnywhere]";

        var config = Build(
            new InitializeConfig { Tag = "Init", ParameterFile = path, Profile = "Sanity" },
            action);

        Assert.Contains(WarningsOf(config), m =>
            m.Contains("[_NotDefinedAnywhere] is not defined by any parameter layer"));
    }

    [Fact]
    public void Analyze_Should_NotWarnAboutToken_When_ThePipelinesLayerDefinesIt()
    {
        var path = WriteConfig(FullConfig);
        var action = Remote("notify");
        action.Type = ActionType.SendMail;
        action.To = "[_EmailCheck]";

        var config = Build(
            new InitializeConfig { Tag = "Init", ParameterFile = path, Profile = "Sanity" },
            action);

        Assert.DoesNotContain(WarningsOf(config), m => m.Contains("_EmailCheck"));
    }

    /// <summary>
    /// _BuildNumber and _DropLocation arrive with the trigger file, so they are legitimately absent
    /// from the config. Flagging them would light up every pipeline and train users to ignore the panel.
    /// </summary>
    [Fact]
    public void Analyze_Should_NotWarnAboutToken_When_TheTriggerFileSuppliesIt()
    {
        var path = WriteConfig(FullConfig);
        var action = Remote("notify");
        action.Type = ActionType.SendMail;
        action.To = "[_EmailCheck]";
        action.Title = "Build [_BuildNumber]";
        action.Body = "Drop = [_DropLocation]";

        var config = Build(
            new InitializeConfig { Tag = "Init", ParameterFile = path, Profile = "Sanity" },
            action);

        var warnings = WarningsOf(config);
        Assert.DoesNotContain(warnings, m => m.Contains("_BuildNumber"));
        Assert.DoesNotContain(warnings, m => m.Contains("_DropLocation"));
    }

    [Fact]
    public void Analyze_Should_WarnUndefinedToken_When_UsedInsideARefExpandedTemplate()
    {
        var path = WriteConfig(FullConfig);
        var config = Build(
            new InitializeConfig { Tag = "Init", ParameterFile = path, Profile = "Sanity" },
            Remote("install"));

        config.WatchItems[0].Events[0].Children.Add(new RefConfig { TemplateID = "Smoke" });
        config.Templates.Add(new TemplateConfig
        {
            ID = "Smoke",
            Children = [Remote("smoke", agent: "[_MissingAgent]")],
        });

        Assert.Contains(WarningsOf(config), m => m.Contains("[_MissingAgent] is not defined"));
    }

    /// <summary>A tokenised ParameterFile path cannot be resolved without a run, so it must not be judged.</summary>
    [Fact]
    public void Analyze_Should_NotWarnFileNotFound_When_ParameterFilePathIsTokenised()
    {
        var config = Build(
            new InitializeConfig { Tag = "Init", ParameterFile = @"C:\Params\[_Release]\pipeline-config.json" },
            Remote("install"));

        Assert.DoesNotContain(WarningsOf(config), m => m.Contains("does not exist"));
    }
}
