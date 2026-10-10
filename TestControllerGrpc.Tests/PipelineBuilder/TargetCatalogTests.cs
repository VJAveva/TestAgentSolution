using TestControllerGrpc.Core.PipelineBuilder;

namespace TestControllerGrpc.Tests.PipelineBuilder;

/// <summary>
/// The catalog is DERIVED from the per-release configs rather than authored as its own file, so a
/// second source of truth for installer/binaries paths cannot drift from what the executor reads.
/// These pin the two things that make that safe: a team adding a release config surfaces it with no
/// code change, and nothing environment-scoped, build-scoped or secret is ever carried forward.
/// </summary>
public class TargetCatalogTests : IDisposable
{
    private readonly string _root;

    public TargetCatalogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"TargetCatalog_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        GC.SuppressFinalize(this);
    }

    private string WriteConfig(string folder, string name, string json)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>Mirrors the live shape: paths + constants + secrets all in the global layer.</summary>
    private static string ReleaseConfig(string release, string product = "SystemPlatform") => $$"""
    {
      "version": 1,
      "global": {
        "_ReleaseName": "{{release}}",
        "_Product": "{{product}}",
        "_OrgName": "AVEVA",
        "_Installer": "C:\\TestSetup\\{{release}}\\setup.exe",
        "_Response": "C:\\TestSetup\\{{release}}\\response.xml",
        "_BinariesFolder": "C:\\TestSetup\\FrameworkBinaries\\{{release}}",
        "_SetupFolder": "C:\\TestSetup\\{{release}}",
        "_InstallFolder": "C:\\TestSetup",
        "_BuildNumber": "OAK_{{release}}_1.2",
        "_DropLocation": "\\\\drop\\{{release}}",
        "_ControllerName": "jvgr22",
        "_EmailCheck": "qa@corp.com",
        "_VCloudPassword": "super-secret",
        "_RcloudUser": "svc-user",
        "_RcloudPassword": "also-secret"
      },
      "profiles": { "Sanity": { "_Agent1": "jvgr1" }, "Warm": { "_Agent1": "warmgr" } },
      "pipelines": { }
    }
    """;

    private TargetCatalogResult Catalog() => new DerivedTargetCatalog(_root).GetTargets();

    [Fact]
    public void GetTargets_Should_DeriveATarget_When_AReleaseConfigExists()
    {
        WriteConfig("SP2026", "SP2026-pipeline-config.json", ReleaseConfig("SP2026"));

        var target = Assert.Single(Catalog().Targets);

        Assert.Equal("SP2026-pipeline-config", target.Id);
        Assert.Equal("SP2026", target.ReleaseName);
        Assert.Equal(@"C:\TestSetup\SP2026\setup.exe", target.Values["_Installer"]);
        Assert.Equal(["Sanity", "Warm"], target.Profiles);
    }

    [Fact]
    public void GetTargets_Should_ReturnTwoTargets_When_TwoConfigsShareOneReleaseName()
    {
        // The live fleet really does this: SP2026R2 has a Sanity config and a WARM config, so the
        // catalog key has to be the config file, not the release.
        WriteConfig("SP2026R2", "SP2026R2-pipeline-config.json", ReleaseConfig("SP2026R2"));
        WriteConfig("SP2026R2", "SP2026R2-WARM-pipeline-config.json", ReleaseConfig("SP2026R2"));

        var targets = Catalog().Targets;

        Assert.Equal(2, targets.Count);
        Assert.All(targets, t => Assert.Equal("SP2026R2", t.ReleaseName));
        Assert.Equal(2, targets.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public void GetTargets_Should_NeverExposeASecret()
    {
        WriteConfig("SP2026", "SP2026-pipeline-config.json", ReleaseConfig("SP2026"));

        var values = Assert.Single(Catalog().Targets).Values;

        Assert.DoesNotContain("_VCloudPassword", values.Keys);
        Assert.DoesNotContain("_RcloudPassword", values.Keys);
        Assert.DoesNotContain(values.Values, v => v.Contains("secret"));
    }

    [Fact]
    public void GetTargets_Should_DropBuildAndEnvironmentKeys_So_TheyComeFromGlobalVariables()
    {
        WriteConfig("SP2026", "SP2026-pipeline-config.json", ReleaseConfig("SP2026"));

        var values = Assert.Single(Catalog().Targets).Values;

        Assert.DoesNotContain("_BuildNumber", values.Keys);
        Assert.DoesNotContain("_DropLocation", values.Keys);
        Assert.DoesNotContain("_ControllerName", values.Keys);
        Assert.DoesNotContain("_EmailCheck", values.Keys);
    }

    [Fact]
    public void GetTargets_Should_KeepAnUnknownKey_So_ATeamCanAddOneWithoutACodeChange()
    {
        // Deny-list, not allow-list: an allow-list silently drops a team's new path key.
        WriteConfig("SP2026", "SP2026-pipeline-config.json", """
        {
          "version": 1,
          "global": { "_ReleaseName": "SP2026", "_TeamSpecificPath": "C:\\team\\thing" }
        }
        """);

        var values = Assert.Single(Catalog().Targets).Values;

        Assert.Equal(@"C:\team\thing", values["_TeamSpecificPath"]);
    }

    [Fact]
    public void GetTargets_Should_SurfaceANewRelease_When_AConfigIsDroppedIn()
    {
        WriteConfig("SP2026", "SP2026-pipeline-config.json", ReleaseConfig("SP2026"));
        Assert.Single(Catalog().Targets);

        WriteConfig("SP2027", "SP2027-pipeline-config.json", ReleaseConfig("SP2027"));

        Assert.Equal(2, Catalog().Targets.Count);
    }

    [Fact]
    public void GetTargets_Should_IgnoreAFileWithNoReleaseName()
    {
        // GlobalVariables.json lives in this same folder and is not a target.
        WriteConfig("", "GlobalVariables.json", """
        { "Version": 1, "_ControllerName": "jvgr22", "_BuildNumber": "x" }
        """);

        Assert.Empty(Catalog().Targets);
    }

    [Fact]
    public void GetTargets_Should_ReportABadFile_ButStillReturnTheGoodOnes()
    {
        WriteConfig("SP2026", "SP2026-pipeline-config.json", ReleaseConfig("SP2026"));
        WriteConfig("broken", "broken-pipeline-config.json", "{ not json");

        var result = Catalog();

        Assert.Single(result.Targets);
        Assert.Contains(result.Problems, p => p.Contains("broken-pipeline-config.json"));
    }

    [Fact]
    public void GetTargets_Should_ReturnEmpty_When_TheRootDoesNotExist()
    {
        var catalog = new DerivedTargetCatalog(Path.Combine(_root, "nope"));

        var result = catalog.GetTargets();

        Assert.Empty(result.Targets);
        Assert.Empty(result.Problems);
    }
}
