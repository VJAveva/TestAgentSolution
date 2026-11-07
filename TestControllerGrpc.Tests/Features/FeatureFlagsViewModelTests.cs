using System.Text.RegularExpressions;
using TestControllerGrpc.Features;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Tests.Features;

public sealed class FeatureFlagsViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"FF_{Guid.NewGuid():N}");
    private readonly string _file;

    public FeatureFlagsViewModelTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "Features.json");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Flags_Should_AllBeOn_When_NoGate()
    {
        var vm = new FeatureFlagsViewModel(null);
        Assert.True(vm.Results && vm.FirReport && vm.CodeChurn && vm.Reports && vm.GlobalVariables && vm.PipelineBuilder);
    }

    [Fact]
    public void Flags_Should_FollowTheFile_When_GateReloads()
    {
        File.WriteAllText(_file, """{ "Features": { } }""");
        using var gate = new FeatureGate(_file, watch: false);
        var vm = new FeatureFlagsViewModel(gate);
        Assert.True(vm.CodeChurn);

        File.WriteAllText(_file, """{ "Features": { "CodeChurn": false, "Results": false } }""");
        gate.Reload();

        Assert.False(vm.CodeChurn);
        Assert.False(vm.ImpactMapping);
        Assert.False(vm.FirReport);
        Assert.True(vm.AnyResultsGroup);
    }

    [Fact]
    public void AnyResultsGroup_Should_BeFalse_When_ResultsAndReportsOff()
    {
        File.WriteAllText(_file, """{ "Features": { "Results": false, "Reports": false } }""");
        using var gate = new FeatureGate(_file, watch: false);
        Assert.False(new FeatureFlagsViewModel(gate).AnyResultsGroup);
    }

    [Theory]
    [InlineData("OpenResultsCommand", "Results")]
    [InlineData("OpenReportCardCommand", "Reports")]
    [InlineData("OpenRegressionCommand", "CodeChurn")]
    public void MainWindow_Should_BindEveryGatedButtonToItsFeature(string command, string feature)
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "TestControllerGrpc", "Views", "MainWindow.xaml"));
        var buttons = Regex.Matches(xaml, $@"<Button\b[^>]*Command=""\{{Binding {command}\}}""[^>]*>").ToList();

        Assert.NotEmpty(buttons);
        Assert.All(buttons, b => Assert.Contains($"Visibility=\"{{Binding Features.{feature},", b.Value));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TestAgentSolution.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}
