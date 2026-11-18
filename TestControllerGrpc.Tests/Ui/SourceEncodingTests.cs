using System.IO;
using System.Text;

namespace TestControllerGrpc.Tests.Ui;

/// <summary>
/// Source files must be UTF-8 with no U+FFFD. A Windows-1252 file compiles fine until a UTF-8 tool re-saves it,
/// which turns every dash into a permanent replacement character that users then see in logs and dialogs (BACKLOG P3).
/// </summary>
public sealed class SourceEncodingTests
{
    private static readonly string[] Roots =
    [
        "TestControllerGrpc", "TestControllerGrpc.Core", "TestController.Api", "TestController.WebApi",
        "TestControllerGrpc.Tests", "TestController.WebApi.Tests", Path.Combine("TestController.WebClient", "src"),
        "TestAgentGrpc", "TestAgentDisplay", "TestController.Persistence", "TestController.Reporting",
        "TestController.Impact", "TestController.Dashboard",
    ];

    private static readonly string[] Extensions = [".cs", ".xaml", ".ts", ".tsx"];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TestAgentSolution.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static IEnumerable<string> SourceFiles()
    {
        var root = RepoRoot();
        foreach (var sub in Roots)
        {
            var dir = Path.Combine(root, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var parts = file.Split(Path.DirectorySeparatorChar);
                if (parts.Contains("bin") || parts.Contains("obj") || parts.Contains("node_modules")) continue;
                if (Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) yield return file;
            }
        }
    }

    [Fact]
    public void SourceFiles_Should_BeValidUtf8WithoutReplacementChars_When_Scanned()
    {
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var bad = new List<string>();

        foreach (var file in SourceFiles())
        {
            string text;
            try { text = strict.GetString(File.ReadAllBytes(file)); }
            catch (DecoderFallbackException) { bad.Add($"{file}: not valid UTF-8 (save it as UTF-8)"); continue; }

            if (text.Contains('\uFFFD')) bad.Add($"{file}: contains U+FFFD (a character lost in an encoding round-trip)");
        }

        Assert.True(bad.Count == 0, string.Join(Environment.NewLine, bad));
    }
}
