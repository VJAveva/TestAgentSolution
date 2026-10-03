using System.Text.Json;
using TestControllerGrpc.Core.Preflight;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Tests.Preflight;

/// <summary>
/// The faults that actually stopped production runs, asserted as blocking pre-flight failures:
/// a watch folder that never existed, an installer consolidated out of the copy source, a token
/// nothing defines, an offline agent, and a pipeline somebody else is already running.
/// </summary>
public class PreflightRunnerTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "preflight-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeFileSystem _fs = new();
    private readonly Dictionary<string, PreflightAgentFacts> _agents = new(StringComparer.OrdinalIgnoreCase);

    public PreflightRunnerTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { /* temp dir */ }
        GC.SuppressFinalize(this);
    }

    // ── Doubles ─────────────────────────────────────────────────────

    private sealed class FakeFileSystem : IPreflightFileSystem
    {
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Denied { get; } = new(StringComparer.OrdinalIgnoreCase);
        public double? ControllerFreeGb { get; set; } = 500;
        public string Identity => @"TESTDOMAIN\preflight-test$";

        public PathAccess CheckFile(string path) =>
            Denied.Contains(path) ? PathAccess.Denied
            : Files.Contains(path) ? PathAccess.Exists
            : PathAccess.Missing;

        public PathAccess CheckDirectory(string path) =>
            Denied.Contains(path) ? PathAccess.Denied
            : Directories.Contains(path) ? PathAccess.Exists
            : PathAccess.Missing;

        public double? FreeSpaceGb(string path) => ControllerFreeGb;
    }

    private PreflightRunner Runner() => new(_fs,
        name => _agents.TryGetValue(name, out var facts) ? facts : PreflightAgentFacts.Unknown(name));

    private PreflightAgentFacts Online(string name, double disk = 200) => new()
    {
        AgentName = name,
        IsRegistered = true,
        IsOnline = true,
        MaintenanceState = "None",
        DiskFreeGb = disk,
    };

    // ── Fixture ─────────────────────────────────────────────────────

    private string WriteParameterFile(Action<PipelineParameterConfig>? tweak = null)
    {
        var config = new PipelineParameterConfig
        {
            Global = new(StringComparer.OrdinalIgnoreCase)
            {
                ["_ControllerName"] = "JVGR22",
                ["_ReleaseName"] = "SP2023R2SP2",
                ["_InstallFolder"] = @"TestSetup\Install\SP2023R2SP2",
                ["_DropLocation"] = @"\\drop\builds\2026.1",
                ["_Agent1"] = "jvgr1",
                ["_BaseInstaller"] = @"C:\TestSetup\SP2023R2SP2\Base\Install-Build.bat",
            },
            Profiles = new(StringComparer.OrdinalIgnoreCase) { ["Sanity"] = new() { ["_Agent1"] = "jvgr1" } },
            Pipelines = new(StringComparer.OrdinalIgnoreCase) { ["Sanity"] = new() { ["_BuildNumber"] = "2026.1" } },
        };
        tweak?.Invoke(config);

        var path = Path.Combine(_temp, "params.json");
        File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private WatchListConfig Config(string parameterFile, string watchPath = @"C:\Triggers\SP2023R2SP2") => new()
    {
        WatchItems =
        [
            new WatchItemConfig
            {
                Tag = "Sanity",
                Path = watchPath,
                Events =
                [
                    new EventConfig
                    {
                        Type = "Renamed",
                        ExecutionType = ExecutionMode.Sequential,
                        Children =
                        [
                            new InitializeConfig { Tag = "Init", ParameterFile = parameterFile, Profile = "Sanity" },
                            new ActionGroupConfig
                            {
                                Tag = "Install",
                                Children =
                                [
                                    new ActionConfig
                                    {
                                        Tag = "Install base",
                                        Type = ActionType.RunRemoteCommand,
                                        AgentName = "[_Agent1]",
                                        Command = "[_BaseInstaller]",
                                    },
                                ],
                            },
                        ],
                    },
                ],
            },
        ],
    };

    /// <summary>Every path the happy-path fixture needs, so a test can remove exactly one.</summary>
    private void SeedHealthyDisk(string watchPath = @"C:\Triggers\SP2023R2SP2")
    {
        _fs.Directories.Add(watchPath);
        _fs.Directories.Add(@"\\drop\builds\2026.1");
        _fs.Directories.Add(@"\\JVGR22\c$\TestSetup\Install\SP2023R2SP2");
        _fs.Files.Add(@"\\JVGR22\c$\TestSetup\Install\SP2023R2SP2\Base\Install-Build.bat");
        _agents["jvgr1"] = Online("jvgr1");
    }

    private PreflightRequest Request(WatchListConfig config) => new()
    {
        Config = config,
        Pipeline = config.WatchItems[0],
        Scope = PreflightScope.Pipeline,
    };

    private static PreflightCheck? Find(PreflightReport report, string group, string nameFragment) =>
        report.Checks.FirstOrDefault(c => c.Group == group &&
            c.Name.Contains(nameFragment, StringComparison.OrdinalIgnoreCase));

    // ── All green ───────────────────────────────────────────────────

    [Fact]
    public void Run_Should_AllowTheRun_When_EverythingIsInPlace()
    {
        SeedHealthyDisk();
        var report = Runner().Run(Request(Config(WriteParameterFile())));

        Assert.True(report.CanRun, "Expected no blocking failures but got: " +
            string.Join(" | ", report.Failures.Select(f => $"{f.Name}: {f.Detail}")));
        Assert.False(report.HasErrors);
    }

    // ── 1. The production incident: trigger folder never existed ────

    [Fact]
    public void Run_Should_Fail_When_TheWatchFolderDoesNotExist()
    {
        SeedHealthyDisk();
        _fs.Directories.Remove(@"C:\Triggers\SP2023R2SP2");

        var report = Runner().Run(Request(Config(WriteParameterFile())));

        var check = Find(report, PreflightGroups.ControllerFiles, "Watch folder");
        Assert.NotNull(check);
        Assert.Equal(PreflightStatus.Fail, check!.Status);
        Assert.Contains(@"C:\Triggers\SP2023R2SP2", check.Detail);
        Assert.False(report.CanRun);
    }

    // ── 2. The production incident: installer consolidated away ─────

    [Fact]
    public void Run_Should_Fail_When_AnAgentPathIsMissingFromTheControllerSource()
    {
        SeedHealthyDisk();
        _fs.Files.Remove(@"\\JVGR22\c$\TestSetup\Install\SP2023R2SP2\Base\Install-Build.bat");

        var report = Runner().Run(Request(Config(WriteParameterFile())));

        var check = Find(report, PreflightGroups.InstallSources, "_BaseInstaller");
        Assert.NotNull(check);
        Assert.Equal(PreflightStatus.Fail, check!.Status);
        Assert.Contains(@"C:\TestSetup\SP2023R2SP2\Base\Install-Build.bat", check.Detail);
        Assert.Contains("copy", check.FixHint!, StringComparison.OrdinalIgnoreCase);
        Assert.False(report.CanRun);
    }

    // ── 3. Unresolved token ─────────────────────────────────────────

    [Fact]
    public void Run_Should_Fail_When_ATokenResolvesToNothing()
    {
        SeedHealthyDisk();
        var file = WriteParameterFile(c => c.Global.Remove("_BaseInstaller"));

        var report = Runner().Run(Request(Config(file)));

        var check = Find(report, PreflightGroups.Settings, "Token resolution");
        Assert.NotNull(check);
        Assert.Equal(PreflightStatus.Fail, check!.Status);
        Assert.Contains("_BaseInstaller", check.Detail);
        Assert.False(report.CanRun);
    }

    [Fact]
    public void Run_Should_Fail_When_TheParameterFileIsMissing()
    {
        SeedHealthyDisk();
        var report = Runner().Run(Request(Config(Path.Combine(_temp, "gone.json"))));

        Assert.False(report.CanRun);
        Assert.Contains(report.Failures, f => f.Group == PreflightGroups.Settings);
    }

    [Fact]
    public void Run_Should_Fail_When_TheProfileIsNotDefined()
    {
        SeedHealthyDisk();
        var file = WriteParameterFile(c => c.Profiles.Remove("Sanity"));

        var report = Runner().Run(Request(Config(file)));

        Assert.False(report.CanRun);
        Assert.Contains(report.Failures, f => f.Detail.Contains("profile", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Run_Should_Warn_When_ThePipelinesEntryIsMissing()
    {
        SeedHealthyDisk();
        var file = WriteParameterFile(c => c.Pipelines.Remove("Sanity"));

        var report = Runner().Run(Request(Config(file)));

        var check = Find(report, PreflightGroups.Settings, "Pipelines entry");
        Assert.NotNull(check);
        Assert.Equal(PreflightStatus.Warn, check!.Status);
        Assert.True(report.CanRun, "A missing pipelines entry warns; it must not block.");
    }

    // ── 4. Agents ───────────────────────────────────────────────────

    [Fact]
    public void Run_Should_Fail_When_AnAgentIsOffline()
    {
        SeedHealthyDisk();
        _agents["jvgr1"] = Online("jvgr1") with { IsOnline = false };

        var report = Runner().Run(Request(Config(WriteParameterFile())));

        var check = Find(report, PreflightGroups.Agents, "jvgr1");
        Assert.NotNull(check);
        Assert.Equal(PreflightStatus.Fail, check!.Status);
        Assert.Contains("offline", check.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.False(report.CanRun);
    }

    [Fact]
    public void Run_Should_Fail_When_AnAgentIsNotRegistered()
    {
        SeedHealthyDisk();
        _agents.Remove("jvgr1");

        var report = Runner().Run(Request(Config(WriteParameterFile())));

        Assert.False(report.CanRun);
        Assert.Contains(report.Failures, f => f.Group == PreflightGroups.Agents && f.Name == "jvgr1");
    }

    [Fact]
    public void Run_Should_Fail_When_AnAgentIsQuarantined()
    {
        SeedHealthyDisk();
        _agents["jvgr1"] = Online("jvgr1") with { MaintenanceState = "Quarantined" };

        var report = Runner().Run(Request(Config(WriteParameterFile())));

        Assert.False(report.CanRun);
        Assert.Contains(report.Failures, f => f.Detail.Contains("Quarantined", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_Should_Fail_When_AnAgentIsLocked()
    {
        SeedHealthyDisk();
        _agents["jvgr1"] = Online("jvgr1") with { LockedBy = "Warm run" };

        var report = Runner().Run(Request(Config(WriteParameterFile())));

        Assert.False(report.CanRun);
        Assert.Contains(report.Failures, f => f.Detail.Contains("Warm run", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_Should_Warn_When_AnAgentNeedsAReboot()
    {
        SeedHealthyDisk();
        _agents["jvgr1"] = Online("jvgr1") with { RebootRequired = true };

        var report = Runner().Run(Request(Config(WriteParameterFile())));

        var check = Find(report, PreflightGroups.Agents, "jvgr1");
        Assert.Equal(PreflightStatus.Warn, check!.Status);
        Assert.True(report.CanRun, "A pending reboot warns; it must not block.");
    }

    // ── 5. Pipeline lock ────────────────────────────────────────────

    [Fact]
    public void Run_Should_Fail_When_ThePipelineIsAlreadyLocked()
    {
        SeedHealthyDisk();
        var config = Config(WriteParameterFile());
        var request = new PreflightRequest
        {
            Config = config,
            Pipeline = config.WatchItems[0],
            PipelineLockedBy = "anna (Web)",
        };

        var report = Runner().Run(request);

        Assert.False(report.CanRun);
        var check = report.Failures.First(f => f.Group == PreflightGroups.Agents && f.Name.Contains("Sanity"));
        Assert.Contains("anna (Web)", check.Detail);
        Assert.Contains("wait", check.FixHint!, StringComparison.OrdinalIgnoreCase);
    }

    // ── 6. Disk ─────────────────────────────────────────────────────

    [Fact]
    public void Run_Should_Warn_When_DiskIsBelowTheMinimum()
    {
        SeedHealthyDisk();
        _fs.ControllerFreeGb = 4;
        _agents["jvgr1"] = Online("jvgr1", disk: 2);

        var report = Runner().Run(Request(Config(WriteParameterFile())));

        Assert.Equal(PreflightStatus.Warn, Find(report, PreflightGroups.Disk, "Controller")!.Status);
        Assert.Equal(PreflightStatus.Warn, Find(report, PreflightGroups.Disk, "jvgr1")!.Status);
        Assert.True(report.CanRun, "Low disk warns; it must not block.");
    }

    // ── Report shape ────────────────────────────────────────────────

    [Fact]
    public void Report_Should_MaskSecretTokenValues()
    {
        SeedHealthyDisk();
        var file = WriteParameterFile(c => c.Global["_AdminPassword"] = "hunter2");

        var report = Runner().Run(Request(Config(file)));

        var token = report.Tokens.First(t => t.Token == "_AdminPassword");
        Assert.NotEqual("hunter2", token.Value);
        Assert.DoesNotContain("hunter2", report.ToPlainText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Report_Should_NameTheSourceLayerOfEveryToken()
    {
        SeedHealthyDisk();
        var report = Runner().Run(Request(Config(WriteParameterFile())));

        Assert.All(report.Tokens, t => Assert.False(string.IsNullOrWhiteSpace(t.SourceLayer)));
        Assert.Contains(report.Tokens, t => t.Token == "_BuildNumber");
    }

    /// <summary>
    /// Operators read these, so they must be the layer names from the config file - not the
    /// internal ParameterRank names (ParameterFile / PipelinePin).
    /// </summary>
    [Fact]
    public void Report_Should_UseOperatorLayerNames_NotInternalRankNames()
    {
        SeedHealthyDisk();
        var report = Runner().Run(Request(Config(WriteParameterFile())));

        Assert.Equal("Global", report.Tokens.First(t => t.Token == "_ControllerName").SourceLayer);
        Assert.Equal("Profile", report.Tokens.First(t => t.Token == "_Agent1").SourceLayer);
        Assert.Equal("Pipeline", report.Tokens.First(t => t.Token == "_BuildNumber").SourceLayer);

        Assert.All(report.Tokens, t =>
            Assert.DoesNotContain(t.SourceLayer, new[] { "ParameterFile", "PipelinePin", "TriggerFile" }));
    }

    // ── Access denied is NOT absence ────────────────────────────────

    /// <summary>
    /// The A/B case from JVGR22: the build drop exists and the controller can read it, but the IIS
    /// pool identity cannot. Reporting that as missing blocked a perfectly good run.
    /// </summary>
    [Fact]
    public void Run_Should_Warn_NotFail_When_APathCannotBeReadByThisIdentity()
    {
        SeedHealthyDisk();
        _fs.Directories.Remove(@"\\drop\builds\2026.1");
        _fs.Denied.Add(@"\\drop\builds\2026.1");

        var report = Runner().Run(Request(Config(WriteParameterFile())));

        var check = Find(report, PreflightGroups.ControllerFiles, "_DropLocation");
        Assert.NotNull(check);
        Assert.Equal(PreflightStatus.Warn, check!.Status);
        Assert.Contains("denied access", check.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"TESTDOMAIN\preflight-test$", check.Detail);
        Assert.True(report.CanRun, "An unreadable path must never block the run.");
    }

    [Fact]
    public void Run_Should_StillFail_When_APathIsGenuinelyMissing()
    {
        SeedHealthyDisk();
        _fs.Directories.Remove(@"\\drop\builds\2026.1");

        var report = Runner().Run(Request(Config(WriteParameterFile())));

        var check = Find(report, PreflightGroups.ControllerFiles, "_DropLocation");
        Assert.Equal(PreflightStatus.Fail, check!.Status);
        Assert.False(report.CanRun);
    }

    [Fact]
    public void Run_Should_NameThePathAndIdentity_When_AccessIsDenied()
    {
        SeedHealthyDisk();
        _fs.Directories.Remove(@"C:\Triggers\SP2023R2SP2");
        _fs.Denied.Add(@"C:\Triggers\SP2023R2SP2");

        var report = Runner().Run(Request(Config(WriteParameterFile())));

        var check = Find(report, PreflightGroups.ControllerFiles, "Watch folder")!;
        Assert.Equal(PreflightStatus.Warn, check.Status);
        Assert.Contains(@"C:\Triggers\SP2023R2SP2", check.Detail);
        Assert.Contains("reachable by the account that runs the pipeline", check.FixHint!);
    }

    [Fact]
    public void Report_Should_GroupEverythingUnderTheSixGroups()
    {
        SeedHealthyDisk();
        var report = Runner().Run(Request(Config(WriteParameterFile())));

        Assert.All(report.Checks, c => Assert.Contains(c.Group, PreflightGroups.InOrder));
    }

    [Fact]
    public void Report_Should_OfferAFixHint_ForEveryFailure()
    {
        SeedHealthyDisk();
        _fs.Directories.Remove(@"C:\Triggers\SP2023R2SP2");
        _agents["jvgr1"] = Online("jvgr1") with { IsOnline = false };

        var report = Runner().Run(Request(Config(WriteParameterFile())));

        Assert.NotEmpty(report.Failures);
        Assert.All(report.Failures, f =>
            Assert.False(string.IsNullOrWhiteSpace(f.FixHint), $"'{f.Name}' failed without telling anyone how to fix it."));
    }

    [Fact]
    public void Run_Should_FinishWellInsideTheBudget()
    {
        SeedHealthyDisk();
        var report = Runner().Run(Request(Config(WriteParameterFile())));

        Assert.True(report.Elapsed < TimeSpan.FromSeconds(60), $"Took {report.Elapsed}.");
    }

    // ── Scope ───────────────────────────────────────────────────────

    [Fact]
    public void Run_Should_OnlyCheckTheSelectedSubtree_When_ScopeIsNode()
    {
        SeedHealthyDisk();
        var config = Config(WriteParameterFile());
        var mail = new ActionConfig
        {
            Tag = "Notify",
            Type = ActionType.SendMail,
            To = "[_NobodyDefinesThis]",
        };
        config.WatchItems[0].Events[0].Children.Add(mail);

        var wholePipeline = Runner().Run(Request(config));
        Assert.False(wholePipeline.CanRun, "The whole pipeline includes the broken mail action.");

        var justTheGroup = Runner().Run(new PreflightRequest
        {
            Config = config,
            Pipeline = config.WatchItems[0],
            Scope = PreflightScope.Node,
            Nodes = [config.WatchItems[0].Events[0].Children.OfType<ActionGroupConfig>().First()],
        });

        Assert.True(justTheGroup.CanRun,
            "A node-level run must not be blocked by a sibling it will never execute.");
    }

    [Fact]
    public void Run_Should_ExpandRefTemplates_When_CountingTokensAndAgents()
    {
        SeedHealthyDisk();
        var config = Config(WriteParameterFile());
        config.Templates.Add(new TemplateConfig
        {
            ID = "Extra",
            Children = [new ActionConfig { Tag = "Deep", Type = ActionType.RunCommand, Command = "[_MissingInTemplate]" }],
        });
        config.WatchItems[0].Events[0].Children.Add(new RefConfig { TemplateID = "Extra" });

        var report = Runner().Run(Request(config));

        Assert.False(report.CanRun);
        Assert.Contains(report.Failures, f => f.Detail.Contains("_MissingInTemplate", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_Should_Fail_When_NoPipelineSuppliesTheSettings()
    {
        var config = Config(WriteParameterFile());
        var report = Runner().Run(new PreflightRequest
        {
            Config = config,
            Pipeline = null,
            Scope = PreflightScope.Template,
            TemplateId = "Prep",
        });

        Assert.False(report.CanRun);
        Assert.Contains(report.Failures, f => f.Group == PreflightGroups.Settings);
    }

    [Fact]
    public void Run_Should_UseTriggerValues_When_TheyFillAGap()
    {
        SeedHealthyDisk();
        var file = WriteParameterFile(c => c.Global.Remove("_BaseInstaller"));
        var config = Config(file);

        var without = Runner().Run(Request(config));
        Assert.False(without.CanRun);

        var with = Runner().Run(new PreflightRequest
        {
            Config = config,
            Pipeline = config.WatchItems[0],
            Scope = PreflightScope.TriggerFile,
            TriggerValues = new Dictionary<string, string> { ["_BaseInstaller"] = @"C:\x\go.bat" },
        });

        Assert.DoesNotContain(with.Failures, f => f.Name == "Token resolution");
    }
}
