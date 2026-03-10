using System.IO;
using System.Xml.Linq;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Parses Visual Studio .trx (Test Results XML) files.
/// XML namespace: http://microsoft.com/schemas/VisualStudio/TeamTest/2010
/// </summary>
public class TrxResultsParser
{
    private static readonly XNamespace TrxNs =
        "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    /// <summary>
    /// Scans a build folder with structure: [BuildFolder] > [UseCaseFolder] > *.trx
    /// and returns a fully populated <see cref="BuildNode"/>.
    /// If no subdirectories exist, treats all .trx files as a single use case.
    /// </summary>
    public BuildNode ParseBuildFolder(string buildFolderPath)
    {
        var buildNumber = Path.GetFileName(buildFolderPath);
        var useCases = new List<UseCaseNode>();
        DateTime? earliest = null, latest = null;

        var subDirs = Directory.GetDirectories(buildFolderPath);
        if (subDirs.Length > 0)
        {
            // Hierarchical: [Build] > [UseCase] > *.trx
            foreach (var useCaseDir in subDirs.OrderBy(d => Path.GetFileName(d)))
            {
                var useCaseName = Path.GetFileName(useCaseDir);
                var trxFiles = Directory.GetFiles(useCaseDir, "*.trx");
                if (trxFiles.Length == 0) continue;

                var runs = trxFiles.Select(ParseFile).ToList();
                var uc = AggregateToUseCase(useCaseName, runs);
                useCases.Add(uc);

                TrackTimeRange(runs, ref earliest, ref latest);
            }
        }
        else
        {
            // Flat: .trx files directly in the build folder (group by feature name)
            var runs = ParseDirectory(buildFolderPath);
            foreach (var group in runs.GroupBy(r => r.FeatureName).OrderBy(g => g.Key))
            {
                var uc = AggregateToUseCase(group.Key, group.ToList());
                useCases.Add(uc);
            }
            TrackTimeRange(runs, ref earliest, ref latest);
        }

        return new BuildNode
        {
            BuildNumber = buildNumber,
            RootPath = buildFolderPath,
            EarliestRun = earliest,
            LatestRun = latest,
            UseCases = useCases,
        };
    }

    /// <summary>Parse a single .trx file.</summary>
    public TrxTestRun ParseFile(string trxFilePath)
    {
        var doc = XDocument.Load(trxFilePath);
        var root = doc.Root!;

        var fileName = Path.GetFileNameWithoutExtension(trxFilePath);
        var featureName = ExtractFeatureName(fileName);

        var counters = root.Descendants(TrxNs + "Counters").FirstOrDefault();

        var times = root.Descendants(TrxNs + "Times").FirstOrDefault();
        var startTime = DateTime.TryParse(times?.Attribute("start")?.Value, out var st) ? st : DateTime.MinValue;
        var endTime = DateTime.TryParse(times?.Attribute("finish")?.Value, out var et) ? et : DateTime.MinValue;

        var testCases = root.Descendants(TrxNs + "UnitTestResult")
            .Select(r =>
            {
                // Get all inner test results (test steps)
                var innerResults = r.Elements(TrxNs + "InnerResults")
                    .Descendants(TrxNs + "UnitTestResult")
                    .Select(ir => new TestStep
                    {
                        StepName = ir.Attribute("testName")?.Value ?? "",
                        Outcome = ir.Attribute("outcome")?.Value ?? "",
                        Duration = TimeSpan.TryParse(ir.Attribute("duration")?.Value, out var sd) ? sd : TimeSpan.Zero,
                        StdOut = ir.Descendants(TrxNs + "StdOut").FirstOrDefault()?.Value ?? "",
                        ErrorMessage = ir.Descendants(TrxNs + "Message").FirstOrDefault()?.Value,
                    })
                    .ToList();

                // Main Output > StdOut
                var mainStdOut = r.Element(TrxNs + "Output")
                    ?.Element(TrxNs + "StdOut")?.Value ?? "";

                // TextMessages (MSTest debug trace)
                var textMessages = r.Descendants(TrxNs + "TextMessages")
                    ?.Descendants(TrxNs + "Message")
                    .Select(m => m.Value)
                    .ToList() ?? new();

                return new TrxTestCase
                {
                    TestName = r.Attribute("testName")?.Value ?? "",
                    Outcome = r.Attribute("outcome")?.Value ?? "NotExecuted",
                    Duration = TimeSpan.TryParse(r.Attribute("duration")?.Value, out var d) ? d : TimeSpan.Zero,
                    ErrorMessage = r.Descendants(TrxNs + "Message").FirstOrDefault()?.Value,
                    StackTrace = r.Descendants(TrxNs + "StackTrace").FirstOrDefault()?.Value,
                    StdOut = mainStdOut,
                    TrxFileName = fileName,
                    ExecutionSteps = innerResults,
                    DebugTrace = string.Join("\n", textMessages),
                };
            })
            .ToList();

        return new TrxTestRun
        {
            FileName = fileName,
            FeatureName = featureName,
            StartTime = startTime,
            EndTime = endTime,
            Total = int.TryParse(counters?.Attribute("total")?.Value, out var t) ? t : testCases.Count,
            Passed = int.TryParse(counters?.Attribute("passed")?.Value, out var p) ? p : testCases.Count(c => c.Outcome == "Passed"),
            Failed = int.TryParse(counters?.Attribute("failed")?.Value, out var f) ? f : testCases.Count(c => c.Outcome == "Failed"),
            Timeout = int.TryParse(counters?.Attribute("timeout")?.Value, out var to) ? to : testCases.Count(c => c.Outcome == "Timeout"),
            NotExecuted = int.TryParse(counters?.Attribute("notExecuted")?.Value, out var ne) ? ne : testCases.Count(c => c.Outcome == "NotExecuted"),
            Duration = testCases.Aggregate(TimeSpan.Zero, (sum, tc) => sum + tc.Duration),
            TestCases = testCases,
        };
    }

    /// <summary>Parse all .trx files in a build results directory.</summary>
    public List<TrxTestRun> ParseDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath)) return new();
        return Directory.GetFiles(directoryPath, "*.trx")
            .Select(ParseFile)
            .OrderBy(r => r.FeatureName)
            .ToList();
    }

    /// <summary>
    /// Discover all build directories under the results root.
    /// Returns build numbers sorted by most recent first.
    /// </summary>
    public List<(string BuildNumber, string Path, DateTime Modified)>
        DiscoverBuilds(string resultsRootPath)
    {
        if (!Directory.Exists(resultsRootPath)) return new();
        return Directory.GetDirectories(resultsRootPath)
            .Select(d => (
                BuildNumber: Path.GetFileName(d),
                Path: d,
                Modified: Directory.GetLastWriteTime(d)
            ))
            .OrderByDescending(b => b.Modified)
            .ToList();
    }

    /// <summary>Extract feature name from .trx filename.</summary>
    private static string ExtractFeatureName(string fileName)
    {
        var parts = fileName.Split('_');
        return parts.Length > 0 ? parts[0] : fileName;
    }

    /// <summary>Aggregate multiple TRX runs into a single UseCaseNode.</summary>
    private static UseCaseNode AggregateToUseCase(string useCaseName, List<TrxTestRun> runs)
    {
        var testResults = runs.SelectMany(r => r.TestCases)
            .Select(tc => new TestResult
            {
                TestName = tc.TestName,
                ClassName = tc.ClassName,
                Outcome = tc.Outcome,
                Duration = tc.Duration,
                ErrorMessage = tc.ErrorMessage,
                StackTrace = tc.StackTrace,
                StdOut = tc.StdOut,
                TrxFileName = tc.TrxFileName,
                UseCaseName = useCaseName,
                ExecutionSteps = tc.ExecutionSteps,
                DebugTrace = tc.DebugTrace,
            })
            .ToList();

        return new UseCaseNode
        {
            UseCaseName = useCaseName,
            Duration = TimeSpan.FromTicks(runs.Sum(r => r.Duration.Ticks)),
            Total = runs.Sum(r => r.Total),
            Passed = runs.Sum(r => r.Passed),
            Failed = runs.Sum(r => r.Failed),
            Timeout = runs.Sum(r => r.Timeout),
            NotExecuted = runs.Sum(r => r.NotExecuted),
            TestResults = testResults,
        };
    }

    private static void TrackTimeRange(List<TrxTestRun> runs, ref DateTime? earliest, ref DateTime? latest)
    {
        var starts = runs.Where(r => r.StartTime > DateTime.MinValue).Select(r => r.StartTime).ToList();
        var ends = runs.Where(r => r.EndTime > DateTime.MinValue).Select(r => r.EndTime).ToList();
        if (starts.Count > 0)
        {
            var min = starts.Min();
            if (earliest is null || min < earliest) earliest = min;
        }
        if (ends.Count > 0)
        {
            var max = ends.Max();
            if (latest is null || max > latest) latest = max;
        }
    }
}
