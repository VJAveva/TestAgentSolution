using System.IO;
using System.Text.RegularExpressions;

namespace TestControllerGrpc.Tests.Ui;

/// <summary>
/// Rule 10: Trigger All always asks first. Buttons must bind TriggerAllWithConfirmCommand; binding the raw
/// TriggerAllWatchItemsCommand starts every pipeline on one click (BACKLOG P5 - the ribbon and a per-session
/// "Retry" button did exactly that).
/// </summary>
public sealed class TriggerAllConfirmTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TestAgentSolution.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void Views_Should_NotBindRawTriggerAll_When_ScannedForCommands()
    {
        var views = Path.Combine(RepoRoot(), "TestControllerGrpc", "Views");
        var offenders = Directory.EnumerateFiles(views, "*.xaml", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"Command=""\{Binding[^}]*TriggerAllWatchItemsCommand")
                .Select(_ => Path.GetFileName(f)))
            .ToList();

        Assert.True(offenders.Count == 0, "Bind TriggerAllWithConfirmCommand instead in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void MainWindow_Should_BindConfirmedTriggerAll_When_RibbonIsRendered()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "TestControllerGrpc", "Views", "MainWindow.xaml"));

        Assert.True(Regex.Matches(xaml, @"TriggerAllWithConfirmCommand").Count >= 4,
            "Expected the key binding plus the ribbon, simplified bar and dashboard buttons to use the confirm command.");
    }
}
