using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// The shared <c>GlobalVariables.json</c> layer (docs/GlobalVariables/GlobalVariables_Requirement.md).
///
/// Two things make this layer dangerous enough to pin hard. First, it was wired into the execution
/// paths for the FIRST time - before this it reached a context only in the validator and the WPF
/// token display - so "nothing changes when the file is absent" is the headline regression test.
/// Second, it carries a BUILD, and applying one release's build to another release's pipeline would
/// install the wrong product; that is what the release-match guard exists to stop.
///
/// Every test sets <see cref="PipelineExecutionContext.GlobalVariablesFile"/> rather than the static
/// <see cref="ParameterResolver.GlobalVariablesPath"/>, so a real file on the build machine can never
/// leak in and these stay safe under xUnit's parallel run.
/// </summary>
public class GlobalVariablesLayerTests : IDisposable
{
    private const string Tag = "SP2023R2SP2 - Sanity";

    private readonly string _dir;

    public GlobalVariablesLayerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"GlobalVarsTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        GC.SuppressFinalize(this);
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>A per-release config shaped like the live ones: _ReleaseName lives in the global layer.</summary>
    private string ReleaseConfig(string release, string build = "config-build") => Write(
        $"{release}-pipeline-config.json",
        $$"""
        {
          "version": 1,
          "global": {
            "_ReleaseName": "{{release}}",
            "_BuildNumber": "{{build}}",
            "_DropLocation": "\\\\drop\\{{build}}",
            "_ControllerName": "jvgr22",
            "_EmailCheck": "release-team@corp.com"
          },
          "profiles": { "Sanity": { "_Agent1": "jvgr1" } },
          "pipelines": { "{{Tag}}": {} }
        }
        """);

    private string GlobalVariables(string release, string build = "GLOBAL-BUILD") => Write(
        "GlobalVariables.json",
        $$"""
        {
          "Version": 1,
          "_ControllerName": "JVGR22",
          "_EmailCheck": "global@corp.com",
          "_ReleaseName": "{{release}}",
          "_BuildNumber": "{{build}}",
          "_DropLocation": "\\\\DevTFSBldoaksp\\REPL\\{{build}}"
        }
        """);

    private static PipelineExecutionContext Context(string? globalVarsPath) => new()
    {
        WatchItemTag = Tag,
        GlobalVariablesFile = globalVarsPath ?? "",
    };

    private static WatchItemConfig WatchItem(string parameterFile, string profile = "Sanity") => new()
    {
        Tag = Tag,
        Events =
        [
            new EventConfig
            {
                Type = "FileCreated",
                Children = [new InitializeConfig { Tag = "Init", ParameterFile = parameterFile, Profile = profile }],
            },
        ],
    };

    // ── the feature ─────────────────────────────────────────────────────

    [Fact]
    public void TryLoadInitializeSource_Should_ApplyGlobalBuild_When_ReleaseMatches()
    {
        var config = ReleaseConfig("SP2023R2SP2");
        var ctx = Context(GlobalVariables("SP2023R2SP2"));

        var reason = ParameterResolver.TryLoadInitializeSource(ctx, config, "Sanity", Tag);

        Assert.Null(reason);
        Assert.Equal("GLOBAL-BUILD", ctx.Parameters["_BuildNumber"]);
        Assert.Equal(@"\\DevTFSBldoaksp\REPL\GLOBAL-BUILD", ctx.Parameters["_DropLocation"]);
    }

    [Fact]
    public void TryLoadInitializeSource_Should_KeepOwnBuild_When_ReleaseDiffers()
    {
        var config = ReleaseConfig("SP2026", build: "sp2026-own-build");
        var ctx = Context(GlobalVariables("SP2023R2SP2"));

        ParameterResolver.TryLoadInitializeSource(ctx, config, "Sanity", Tag);

        Assert.Equal("sp2026-own-build", ctx.Parameters["_BuildNumber"]);
        Assert.Equal("SP2026", ctx.Parameters["_ReleaseName"]);
    }

    [Fact]
    public void TryLoadInitializeSource_Should_ApplyConstants_When_ReleaseDiffers()
    {
        var config = ReleaseConfig("SP2026");
        var ctx = Context(GlobalVariables("SP2023R2SP2"));

        ParameterResolver.TryLoadInitializeSource(ctx, config, "Sanity", Tag);

        // Constants are not release-scoped, so a mismatch must not suppress them.
        Assert.Equal("global@corp.com", ctx.Parameters["_EmailCheck"]);
        Assert.Equal("JVGR22", ctx.Parameters["_ControllerName"]);
    }

    [Fact]
    public void TryApplyGlobalVariables_Should_SkipBuildKeys_When_PipelineHasNoReleaseName()
    {
        var ctx = Context(GlobalVariables("SP2023R2SP2"));

        // No release resolved yet: the match cannot be proven, so the build must not be touched.
        var reason = ParameterResolver.TryApplyGlobalVariables(ctx);

        Assert.Null(reason);
        Assert.False(ctx.Parameters.ContainsKey("_BuildNumber"));
        Assert.Equal("global@corp.com", ctx.Parameters["_EmailCheck"]);
    }

    [Fact]
    public void TryApplyGlobalVariables_Should_SkipBuildKeys_When_FileNamesNoRelease()
    {
        var file = Write("GlobalVariables.json", """
        { "Version": 1, "_BuildNumber": "NO-RELEASE", "_EmailCheck": "global@corp.com" }
        """);
        var ctx = Context(file);
        ParameterResolver.SetParameter(ctx, "_ReleaseName", "SP2023R2SP2", ParameterRank.Global);

        ParameterResolver.TryApplyGlobalVariables(ctx);

        Assert.False(ctx.Parameters.ContainsKey("_BuildNumber"));
    }

    // ── precedence ──────────────────────────────────────────────────────

    [Fact]
    public void GlobalBuild_Should_OutrankPipelinePin_When_ReleaseMatches()
    {
        var config = Write("pinned-config.json", $$"""
        {
          "version": 1,
          "global":    { "_ReleaseName": "SP2023R2SP2", "_BuildNumber": "from-global" },
          "pipelines": { "{{Tag}}": { "_BuildNumber": "PINNED-TO-PIPELINE" } }
        }
        """);
        var ctx = Context(GlobalVariables("SP2023R2SP2"));

        ParameterResolver.TryLoadInitializeSource(ctx, config, profile: null, Tag);

        Assert.Equal("GLOBAL-BUILD", ctx.Parameters["_BuildNumber"]);
    }

    [Fact]
    public void TriggerBuild_Should_OutrankGlobalBuild_When_BothSupplyIt()
    {
        var config = ReleaseConfig("SP2023R2SP2");
        var ctx = Context(GlobalVariables("SP2023R2SP2"));

        ParameterResolver.TryLoadInitializeSource(ctx, config, "Sanity", Tag);
        ParameterResolver.SetParameter(ctx, "_BuildNumber", "FROM-TRIGGER", ParameterRank.TriggerFile);

        Assert.Equal("FROM-TRIGGER", ctx.Parameters["_BuildNumber"]);
    }

    [Fact]
    public void GlobalBuild_Should_NotOverwriteTrigger_When_AppliedAfterTheTriggerFile()
    {
        // Order must not decide precedence - rank must. The trigger path loads before Initialize on
        // some entry points, so applying the global layer afterwards may not undo it.
        var config = ReleaseConfig("SP2023R2SP2");
        var ctx = Context(GlobalVariables("SP2023R2SP2"));

        ParameterResolver.SetParameter(ctx, "_BuildNumber", "FROM-TRIGGER", ParameterRank.TriggerFile);
        ParameterResolver.TryLoadInitializeSource(ctx, config, "Sanity", Tag);

        Assert.Equal("FROM-TRIGGER", ctx.Parameters["_BuildNumber"]);
    }

    [Fact]
    public void GlobalBuild_Should_ResolveBothTokenSpellings_When_Applied()
    {
        var config = ReleaseConfig("SP2023R2SP2");
        var ctx = Context(GlobalVariables("SP2023R2SP2"));

        ParameterResolver.TryLoadInitializeSource(ctx, config, "Sanity", Tag);

        Assert.Equal("GLOBAL-BUILD", ParameterResolver.Resolve("[_BuildNumber]", ctx));
        Assert.Equal("GLOBAL-BUILD", ParameterResolver.Resolve("[BuildNumber]", ctx));
    }

    // ── no regression when the file is absent or bad ────────────────────

    [Fact]
    public void TryLoadInitializeSource_Should_ResolveExactlyAsBefore_When_FileIsMissing()
    {
        var config = ReleaseConfig("SP2023R2SP2", build: "config-build");
        var ctx = Context(Path.Combine(_dir, "does-not-exist.json"));

        var reason = ParameterResolver.TryLoadInitializeSource(ctx, config, "Sanity", Tag);

        Assert.Null(reason);
        Assert.Equal("config-build", ctx.Parameters["_BuildNumber"]);
        Assert.Equal("release-team@corp.com", ctx.Parameters["_EmailCheck"]);
    }

    [Fact]
    public void TryLoadInitializeSource_Should_ReportAndChangeNothing_When_FileIsNotValidJson()
    {
        var config = ReleaseConfig("SP2023R2SP2", build: "config-build");
        var ctx = Context(Write("GlobalVariables.json", "{ not json at all"));

        var reason = ParameterResolver.TryLoadInitializeSource(ctx, config, "Sanity", Tag);

        Assert.NotNull(reason);
        Assert.Contains("global variables file", reason);
        // Reported, never silently used: the pipeline's own build survives untouched.
        Assert.Equal("config-build", ctx.Parameters["_BuildNumber"]);
    }

    [Fact]
    public void GlobalVariablesPath_Should_DefaultToDisabled_So_TheLayerIsOptIn()
    {
        // A non-empty default would make every host - and every unit test - pick up whatever file
        // happens to sit at the standard location.
        Assert.Equal("", ParameterResolver.GlobalVariablesPath);
    }

    // ── the shared funnel: one source, not N ────────────────────────────

    [Fact]
    public void LoadForWatchItem_Should_ApplyGlobalBuild_When_ReleaseMatches()
    {
        var ctx = Context(GlobalVariables("SP2023R2SP2"));

        ParameterResolver.LoadForWatchItem(ctx, WatchItem(ReleaseConfig("SP2023R2SP2")));

        Assert.Equal("GLOBAL-BUILD", ctx.Parameters["_BuildNumber"]);
    }

    [Fact]
    public void LoadForWatchItem_Should_ApplyConstants_When_PipelineHasNoInitializeAtAll()
    {
        var ctx = Context(GlobalVariables("SP2023R2SP2"));

        ParameterResolver.LoadForWatchItem(ctx, new WatchItemConfig { Tag = Tag });

        Assert.Equal("JVGR22", ctx.Parameters["_ControllerName"]);
        Assert.False(ctx.Parameters.ContainsKey("_BuildNumber"));
    }

    [Fact]
    public void TryLoadJsonConfig_Should_ApplyGlobalBuild_When_ReleaseMatches()
    {
        var ctx = Context(GlobalVariables("SP2023R2SP2"));

        Assert.True(ParameterResolver.TryLoadJsonConfig(ctx, ReleaseConfig("SP2023R2SP2"), "Sanity", Tag));
        Assert.Equal("GLOBAL-BUILD", ctx.Parameters["_BuildNumber"]);
    }

    [Fact]
    public void TryLoadJsonConfig_Should_StayTrue_When_OnlyTheGlobalFileIsBad()
    {
        // The bool answers "was the LAYERED config readable". Flipping it would make the WPF token
        // display roll back to last-good values over an unrelated file.
        var ctx = Context(Write("GlobalVariables.json", "{ not json at all"));

        Assert.True(ParameterResolver.TryLoadJsonConfig(ctx, ReleaseConfig("SP2023R2SP2"), "Sanity", Tag));
    }

    [Fact]
    public void LoadForWatchItem_Should_ApplyGlobalBuild_When_InitializeIsAPlainTextFile()
    {
        // The .txt branch bypasses the JSON loader, so it needs its own apply or a legacy pipeline
        // would silently never see the global build.
        var txt = Write("WarmVariables.txt", "_ReleaseName,SP2023R2SP2\n_BuildNumber,from-txt");
        var ctx = Context(GlobalVariables("SP2023R2SP2"));

        ParameterResolver.LoadForWatchItem(ctx, WatchItem(txt, profile: ""));

        Assert.Equal("GLOBAL-BUILD", ctx.Parameters["_BuildNumber"]);
    }

    [Fact]
    public void LayerFromRank_Should_NameTheGlobalBuildLayer_So_TooltipsDoNotReadUnknown()
    {
        Assert.Equal(TokenLayer.GlobalBuild, TokenDisplay.LayerFromRank(ParameterRank.GlobalVars));
    }

    [Fact]
    public void Analyze_Should_ResolveTokensFromAJsonGlobalFile_When_WatchListPointsAtOne()
    {
        // The validator used to hand this file to the CSV parser, which turns a whole JSON line into
        // a key - so the token stayed undefined and the validator contradicted the executor.
        var globalVars = GlobalVariables("SP2023R2SP2");
        var pipelineConfig = Write("no-controller-config.json", $$"""
        {
          "version": 1,
          "global": { "_ReleaseName": "SP2023R2SP2" },
          "pipelines": { "{{Tag}}": {} }
        }
        """);

        var config = new WatchListConfig
        {
            GlobalVariablesFile = globalVars,
            WatchItems =
            [
                new WatchItemConfig
                {
                    Tag = Tag,
                    Path = @"C:\Triggers",
                    Events =
                    [
                        new EventConfig
                        {
                            Type = "Renamed",
                            Children =
                            [
                                new InitializeConfig { Tag = "Init", ParameterFile = pipelineConfig },
                                new ActionConfig
                                {
                                    Type = ActionType.RunRemoteCommand,
                                    Tag = "install",
                                    AgentName = "jvgr1",
                                    Command = @"C:\tools\run.bat [_ControllerName]",
                                },
                            ],
                        },
                    ],
                },
            ],
        };

        var messages = WatchListValidator.Analyze(config).Select(i => i.Message).ToList();

        Assert.DoesNotContain(messages, m => m.Contains("_ControllerName"));
    }
}
