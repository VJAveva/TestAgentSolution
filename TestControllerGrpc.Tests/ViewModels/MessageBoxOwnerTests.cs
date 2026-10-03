using System.Text.RegularExpressions;

namespace TestControllerGrpc.Tests.ViewModels;

/// <summary>
/// Guards against passing a null owner to MessageBox.
/// </summary>
/// <remarks>
/// <c>MessageBox.Show(owner, ...)</c> THROWS <c>ArgumentNullException(window)</c> when owner is null -
/// it does not quietly fall back to an ownerless dialog. <c>Application.Current.MainWindow</c> is
/// null before the shell is shown, so <c>Application.Current?.MainWindow!</c> compiles, silences the
/// nullable warning, and then crashes the UI at the moment the dialog is needed.
/// That is exactly what happened when "Check only" was first clicked on JVGR22.
///
/// File analysis because showing a real MessageBox needs an STA message pump, which cannot run here.
/// </remarks>
public class MessageBoxOwnerTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TestAgentSolution.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>Any `MainWindow` forced non-null and handed straight to a dialog is the bug.</summary>
    [Fact]
    public void NoCallSite_Should_PassAForcedNonNullMainWindow_AsADialogOwner()
    {
        var offenders = new List<string>();
        var pattern = new Regex(@"MainWindow!\s*,", RegexOptions.Compiled);

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(RepoRoot(), "TestControllerGrpc"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;

            var text = File.ReadAllText(file);
            foreach (Match m in pattern.Matches(text))
            {
                var line = text.Take(m.Index).Count(c => c == '\n') + 1;
                offenders.Add($"{Path.GetFileName(file)}:{line}");
            }
        }

        Assert.True(offenders.Count == 0,
            "MessageBox.Show(null, ...) throws. Choose the ownerless overload instead:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void PreflightDialog_Should_GuardTheOwner_RatherThanAssignItBlindly()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "TestControllerGrpc", "Views", "Dialogs", "PreflightDialog.xaml.cs"));

        // Window.Owner rejects null, so every assignment must sit behind a null check.
        foreach (Match m in new Regex(@"^\s*(?:dialog\.)?Owner\s*=", RegexOptions.Multiline).Matches(source))
        {
            var before = source[..m.Index];
            Assert.True(before.Contains("owner is not null"),
                $"'{m.Value.Trim()}' is not guarded by a null check - Window.Owner throws on null.");
        }

        Assert.Contains("if (owner is not null", source);
    }

    [Fact]
    public void Preflight_Should_RouteEveryDialog_ThroughTheThemedWindow()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "TestControllerGrpc", "ViewModels", "MainViewModel.Preflight.cs"));

        // The crash was MessageBox.Show(Application.Current?.MainWindow!, ...). No raw MessageBox
        // may come back into this file; the themed dialog owns the null-owner decision now.
        Assert.DoesNotMatch(new Regex(@"MessageBox\.Show\("), source);
        Assert.Contains("PreflightDialog.Show(", source);
    }
}
