using System.Text.RegularExpressions;
using System.Xml.Linq;
using TestControllerGrpc.Core.Fir;
using TestControllerGrpc.Reporting.Fir;

namespace TestControllerGrpc.Tests.Fir;

/// <summary>
/// Loads the sanitized fixtures and the shipped rule packs off disk.
///
/// The fixtures are vstest console logs, not TRX files. That is deliberate: the log carries the same
/// error message, stack trace and debug trace a TRX does, so the exit criteria can be proven today and
/// re-run unchanged against real TRX files when they land.
/// </summary>
internal static partial class FirFixtures
{
    public static string FixtureDirectory => Path.Combine(RepoRoot(), "docs", "FIRFailedTest", "Fir");

    public static string RulesDirectory => Path.Combine(RepoRoot(), "rules");

    public static IReadOnlyList<FirRulePack> Packs()
    {
        var result = FirRulePackLoader.LoadDirectory(RulesDirectory);
        Assert.Empty(result.Errors);
        return result.Packs;
    }

    /// <summary>The production reader, unmodified.</summary>
    public static ITestDataSheetReader SheetReader() => new ClosedXmlTestDataSheetReader();

    /// <summary>
    /// A results folder shaped the way the run leaves one: the workbook sits in TestData\ under the
    /// folder holding the TRX, named as the log named it. Lets the real resolver do the finding.
    /// </summary>
    public static TempDirectory NewResultsFolder(string workbookName = "WASSet1.xlsx")
    {
        var dir = new TempDirectory("FirResults");
        if (workbookName.Length > 0)
        {
            var testData = Path.Combine(dir.Path, "TestData");
            Directory.CreateDirectory(testData);
            File.Copy(Path.Combine(FixtureDirectory, "WASSet1.fixture.xlsx"), Path.Combine(testData, workbookName));
        }

        return dir;
    }

    /// <summary>
    /// Writes a TRX carrying the fixture's error, stack and debug trace - the same content a real run
    /// puts there. Harness noise is deliberately absent: vstest writes that to the console, not the TRX.
    /// </summary>
    public static string WriteTrx(string folder, string fixtureName, string? outcome = null)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var input = Load(fixtureName);
        var text = File.ReadAllText(Path.Combine(FixtureDirectory, fixtureName));

        var run = new XElement(ns + "TestRun",
            new XElement(ns + "Results",
                new XElement(ns + "UnitTestResult",
                    new XAttribute("testName", input.TestName),
                    new XAttribute("outcome", outcome ?? input.Outcome),
                    new XAttribute("duration", input.Duration.ToString()),
                    new XAttribute("computerName", input.Computer),
                    new XElement(ns + "Output",
                        new XElement(ns + "StdOut", Clean(Section(text, "Standard Output Messages:", "Debug Trace:"))),
                        new XElement(ns + "DebugTrace", Clean(Section(text, "Debug Trace:", "Results File:"))),
                        new XElement(ns + "ErrorInfo",
                            new XElement(ns + "Message", Clean(input.ErrorMessage)),
                            new XElement(ns + "StackTrace", Clean(input.StackTrace)))))));

        var path = Path.Combine(folder, input.TestName + ".trx");
        new XDocument(run).Save(path);
        return path;
    }

    private static string Clean(string? value)
        => value is null ? "" : new string([.. value.Where(c => c >= ' ' || c is '\t' or '\r' or '\n')]);

    public static FirTrxInput Load(string fileName, string? resultsFolder = null)
    {
        var path = Path.Combine(FixtureDirectory, fileName);
        Assert.True(File.Exists(path), $"Fixture not found: {path}");

        var text = File.ReadAllText(path);
        Assert.NotEmpty(text);

        var header = HeaderRegex().Match(text);
        Assert.True(header.Success, $"No vstest result header in {fileName}");

        return new FirTrxInput
        {
            TestName = header.Groups["name"].Value,
            ClassName = ClassNameFrom(Section(text, "Stack Trace:", "Standard Output Messages:")),
            Outcome = header.Groups["outcome"].Value,
            Duration = ParseDuration(header.Groups["dur"].Value),
            ErrorMessage = Section(text, "Error Message:", "Stack Trace:"),
            StackTrace = Section(text, "Stack Trace:", "Standard Output Messages:"),
            Computer = "jvgr1",
            TestAssembly = "wassmoketest.dll",
            ResultsFolder = resultsFolder,
            LogText = text,
        };
    }

    /// <summary>The log never states the test class; its last stack frame does.</summary>
    private static string ClassNameFrom(string? stackTrace)
    {
        if (string.IsNullOrWhiteSpace(stackTrace)) return "";

        var frames = FirStackTrace.Parse(stackTrace);
        return frames.Count == 0 ? "" : frames[^1].DeclaringType;
    }

    private static string? Section(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        if (from < 0) return null;
        from += start.Length;

        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        var body = (to < 0 ? text[from..] : text[from..to]).Trim('\r', '\n');
        return string.IsNullOrWhiteSpace(body) ? null : body;
    }

    internal static TimeSpan ParseDuration(string value)
    {
        var total = TimeSpan.Zero;
        foreach (Match part in DurationRegex().Matches(value))
        {
            var n = double.Parse(part.Groups["n"].Value);
            total += part.Groups["unit"].Value switch
            {
                "h" => TimeSpan.FromHours(n),
                "m" => TimeSpan.FromMinutes(n),
                "s" => TimeSpan.FromSeconds(n),
                _ => TimeSpan.Zero,
            };
        }

        return total;
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "TestAgentSolution.sln")))
            dir = Path.GetDirectoryName(dir);

        Assert.NotNull(dir);
        return dir;
    }

    [GeneratedRegex(@"^\s*(?<outcome>Failed|Passed)\s+(?<name>\S+)\s+\[(?<dur>[^\]]+)\]", RegexOptions.Multiline)]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"(?<n>\d+(?:\.\d+)?)\s*(?<unit>h|m|s)\b")]
    private static partial Regex DurationRegex();
}
