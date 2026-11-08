using Moq;
using TestControllerGrpc.Core.Preflight;
using TestControllerGrpc.Features;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Tests.Features;

public sealed class FeatureUsagePreflightTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"FU_{Guid.NewGuid():N}");
    private readonly string _file;

    public FeatureUsagePreflightTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "Features.json");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("curl http://jvgr22:81/api/impact/consolidated", FeatureKeys.CodeChurn)]
    [InlineData(@"powershell -File C:\deploy\Rebuild-ImpactIndex.ps1", FeatureKeys.ImpactMapping)]
    [InlineData("curl -X POST http://jvgr22:81/api/impact-mapping/run", FeatureKeys.ImpactMapping)]
    [InlineData("curl http://jvgr22:81/api/results/builds", FeatureKeys.Results)]
    [InlineData("curl http://jvgr22:81/api/results/builds/1/test/T1/fir", FeatureKeys.FirReport)]
    [InlineData("curl http://jvgr22:81/api/reportcard/builds", FeatureKeys.Reports)]
    public void Detect_Should_NameTheFeature_When_TheStepCallsIt(string command, string feature)
    {
        var found = FeatureUsage.Detect(new ActionConfig { Command = command });

        Assert.Equal([feature], found.Select(f => f.Feature));
    }

    [Fact]
    public void Detect_Should_FindNothing_When_TheStepCallsNoGatedFeature()
    {
        Assert.Empty(FeatureUsage.Detect(new ActionConfig { Command = @"C:\TestSetup\Install-Build.bat", Parameters = "/impactful" }));
    }

    [Fact]
    public void Preflight_Should_WarnNamingStepAndFeature_When_StepCallsADisabledFeature()
    {
        File.WriteAllText(_file, """{ "Features": { "CodeChurn": false } }""");
        using var gate = new FeatureGate(_file, watch: false);

        var report = Run(gate, "curl http://jvgr22:81/api/impact/consolidated");

        var warning = Assert.Single(report.Checks, c => c.Name == "Feature 'CodeChurn'");
        Assert.Equal(PreflightStatus.Warn, warning.Status);
        Assert.Contains("Step 'Churn report'", warning.Detail);
        Assert.Contains("turned off on this controller", warning.Detail);
    }

    [Fact]
    public void Preflight_Should_GiveTheReason_When_TheFeatureIsOffThroughItsParent()
    {
        File.WriteAllText(_file, """{ "Features": { "Results": false } }""");
        using var gate = new FeatureGate(_file, watch: false);

        var report = Run(gate, "curl http://jvgr22:81/api/results/builds/1/test/T1/fir");

        var warning = Assert.Single(report.Checks, c => c.Name.StartsWith("Feature '", StringComparison.Ordinal));
        Assert.Equal("Feature 'FirReport'", warning.Name);
        Assert.Contains("off because Results is off", warning.Detail);
    }

    [Fact]
    public void Preflight_Should_NotWarn_When_TheFeatureIsOn()
    {
        File.WriteAllText(_file, """{ "Features": { } }""");
        using var gate = new FeatureGate(_file, watch: false);

        var report = Run(gate, "curl http://jvgr22:81/api/impact/consolidated");

        Assert.DoesNotContain(report.Checks, c => c.Name.StartsWith("Feature '", StringComparison.Ordinal));
    }

    private static PreflightReport Run(IFeatureGate gate, string command)
    {
        var fs = new Mock<IPreflightFileSystem>();
        fs.Setup(f => f.CheckDirectory(It.IsAny<string>())).Returns(PathAccess.Exists);
        fs.Setup(f => f.CheckFile(It.IsAny<string>())).Returns(PathAccess.Exists);
        var runner = new PreflightRunner(fs.Object, PreflightAgentFacts.Unknown);
        var pipeline = new WatchItemConfig
        {
            Tag = "Churn",
            Events =
            [
                new EventConfig
                {
                    Children = [new ActionConfig { Tag = "Churn report", Type = ActionType.RunCommand, Command = command }],
                },
            ],
        };

        return runner.Run(new PreflightRequest
        {
            Config = new WatchListConfig { WatchItems = [pipeline] },
            Pipeline = pipeline,
            Features = gate,
        });
    }
}
