using System.IO;
using System.Text.RegularExpressions;

namespace TestControllerGrpc.Tests;

/// <summary>
/// Ratchet on U+FFFD, the Unicode replacement character. It is never typed deliberately - it is what a
/// non-ASCII character decays into when a file is read as one encoding and written back as another, so every
/// occurrence is corruption. A sweep on 2026-10-02 found 398 across 46 files: em dashes in comment dividers,
/// but also live user-visible text (report titles, log messages, e-mail subjects).
///
/// Fixing all of them at once would touch six unrelated projects, so this pins the current count and lets it
/// only go DOWN, the same shape as <see cref="Ui.TokenAuditTests"/>. Files the e-mail styling work touched are
/// held at a hard zero by <see cref="EmailAndReportSources_Should_HaveNoReplacementCharacter"/>.
///
/// When you repair a batch, lower the baseline in the same commit - the failure message gives you the number.
/// </summary>
public sealed class SourceEncodingTests
{
    private const char Replacement = '\uFFFD';

    // Measured 2026-10-02 BY THIS TEST after the e-mail palette work, which cleared 22 lines across 6 files,
    // and deploy/README.md, which cleared 8 more. Take this number from the failing assertion, never from a
    // shell sweep: PowerShell's per-line scan reported 376 where this counts 385, because a line carrying two
    // replacement characters counts once there and twice here. Of what remains, roughly 149 are comment
    // dividers; the user-visible remainder is tracked as a backlog item in deploy/README.md.
    private const int ReplacementCharBaseline = 385;

    private static readonly string[] ScannedExtensions =
        [".cs", ".xaml", ".ps1", ".json", ".md", ".ts", ".tsx", ".css", ".html", ".xml", ".proto", ".bat", ".csx"];

    private static readonly Regex ExcludedPath =
        new(@"[\\/](bin|obj|node_modules|dist|publish|TestResults|\.git)[\\/]", RegexOptions.IgnoreCase);

    /// <summary>Files repaired by the e-mail styling work; these must never regress.</summary>
    private static readonly string[] MustBeClean =
    [
        @"TestControllerGrpc.Core\Services\BuildReportHtmlGenerator.cs",
        @"TestControllerGrpc.Core\Services\ConsolidatedRunEmailBuilder.cs",
        @"TestControllerGrpc.Core\Services\EmailPalette.cs",
        @"TestControllerGrpc\ViewModels\BuildResultsViewModel.Helpers.cs",
        @"TestControllerGrpc\ViewModels\BuildResultsViewModel.Commands.cs",
        @"TestController.WebApi\Endpoints\ResultsEndpoints.cs",
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TestAgentSolution.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => ScannedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !ExcludedPath.IsMatch(f[root.Length..]));

    private static int CountIn(string path) => File.ReadAllText(path).Count(c => c == Replacement);

    [Fact]
    public void Sources_Should_NotRegressOnReplacementCharacter_When_ScannedRepoWide()
    {
        var root = RepoRoot();

        var offenders = SourceFiles(root)
            .Select(f => (Path: f[(root.Length + 1)..], Count: CountIn(f)))
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count)
            .ToList();

        var total = offenders.Sum(x => x.Count);

        Assert.True(total <= ReplacementCharBaseline,
            $"U+FFFD count rose to {total}, baseline is {ReplacementCharBaseline}. A character was mangled by " +
            $"an encoding round-trip. Worst files:{Environment.NewLine}" +
            string.Join(Environment.NewLine, offenders.Take(10).Select(x => $"  {x.Count,4}  {x.Path}")));
    }

    [Fact]
    public void EmailAndReportSources_Should_HaveNoReplacementCharacter()
    {
        var root = RepoRoot();

        var dirty = MustBeClean
            .Select(rel => (Rel: rel, Full: Path.Combine(root, rel)))
            .Select(x => (x.Rel, Count: File.Exists(x.Full) ? CountIn(x.Full) : -1))
            .Where(x => x.Count != 0)
            .ToList();

        Assert.True(dirty.Count == 0,
            "These files are held at zero U+FFFD (-1 means the file was moved or renamed):" + Environment.NewLine +
            string.Join(Environment.NewLine, dirty.Select(x => $"  {x.Count,4}  {x.Rel}")));
    }
}
