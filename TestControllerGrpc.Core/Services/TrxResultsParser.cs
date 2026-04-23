using System.IO;
using System.Collections.Concurrent;
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

    // Cached XName instances to avoid repeated allocations from (TrxNs + string)
    private static readonly XName CountersName = TrxNs + "Counters";
    private static readonly XName TimesName = TrxNs + "Times";
    private static readonly XName UnitTestResultName = TrxNs + "UnitTestResult";
    private static readonly XName InnerResultsName = TrxNs + "InnerResults";
    private static readonly XName OutputName = TrxNs + "Output";
    private static readonly XName StdOutName = TrxNs + "StdOut";
    private static readonly XName TextMessagesName = TrxNs + "TextMessages";
    private static readonly XName MessageName = TrxNs + "Message";
    private static readonly XName StackTraceName = TrxNs + "StackTrace";
    private static readonly XName ErrorInfoName = TrxNs + "ErrorInfo";
    private static readonly XName ResultsName = TrxNs + "Results";
    private static readonly XName StdErrName = TrxNs + "StdErr";
    private static readonly XName DebugTraceName = TrxNs + "DebugTrace";

    private readonly ConcurrentDictionary<string, TrxTestRun> _fileCache = new();
    private const int MaxFileCacheSize = 500;

    /// <summary>
    /// Scans a build folder with structure: [BuildFolder] > [UseCaseFolder] > *.trx
    /// and returns a fully populated <see cref="BuildNode"/>.
    /// If no subdirectories exist, treats all .trx files as a single use case.
    /// </summary>
    public BuildNode ParseBuildFolder(string buildFolderPath)
    {
        var buildNumber = Path.GetFileName(buildFolderPath);

        if (!Directory.Exists(buildFolderPath))
        {
            return new BuildNode
            {
                BuildNumber = buildNumber,
                RootPath = buildFolderPath,
                UseCases = [],
                AllFailedTests = [],
            };
        }

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

        var totalTests = 0;
        var passedTests = 0;
        var failedTests = 0;
        var timeoutTests = 0;
        var notExecutedTests = 0;
        var totalDurationTicks = 0L;
        List<TestResult>? allFailed = null;

        foreach (var uc in useCases)
        {
            totalTests += uc.Total;
            passedTests += uc.Passed;
            failedTests += uc.Failed;
            timeoutTests += uc.Timeout;
            notExecutedTests += uc.NotExecuted;
            totalDurationTicks += uc.Duration.Ticks;

            if (uc.FailedTests.Count > 0)
            {
                allFailed ??= new List<TestResult>();
                allFailed.AddRange(uc.FailedTests);
            }
        }

        return new BuildNode
        {
            BuildNumber = buildNumber,
            RootPath = buildFolderPath,
            EarliestRun = earliest,
            LatestRun = latest,
            UseCases = useCases,
            TotalDuration = TimeSpan.FromTicks(totalDurationTicks),
            TotalTests = totalTests,
            PassedTests = passedTests,
            FailedTests = failedTests,
            TimeoutTests = timeoutTests,
            NotExecutedTests = notExecutedTests,
            PassRate = totalTests > 0 ? (double)passedTests / totalTests * 100 : 0,
            AllFailedTests = allFailed ?? [],
        };
    }

    /// <summary>Parse a single .trx file (cached by path + last-write timestamp).</summary>
    public TrxTestRun ParseFile(string trxFilePath)
    {
        var lastWrite = File.GetLastWriteTimeUtc(trxFilePath);
        var cacheKey = $"{trxFilePath}|{lastWrite:O}";
        if (_fileCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var doc = XDocument.Load(trxFilePath);
        var root = doc.Root!;

        var fileName = Path.GetFileNameWithoutExtension(trxFilePath);
        var featureName = ExtractFeatureName(fileName);

        var counters = root.Descendants(CountersName).FirstOrDefault();

        var times = root.Descendants(TimesName).FirstOrDefault();
        var startTime = DateTime.TryParse(times?.Attribute("start")?.Value, out var st) ? st : DateTime.MinValue;
        var endTime = DateTime.TryParse(times?.Attribute("finish")?.Value, out var et) ? et : DateTime.MinValue;

        // Use targeted navigation: Results > UnitTestResult instead of full Descendants scan
        var resultsElement = root.Element(ResultsName);
        var unitTestResults = resultsElement?.Elements(UnitTestResultName);

        var testCases = new List<TrxTestCase>();
        long totalDurationTicks = 0;

        if (unitTestResults is not null)
        {
            foreach (var r in unitTestResults)
            {
                // Get all inner test results (test steps) via direct child navigation
                var innerResultsEl = r.Element(InnerResultsName);
                List<TestStep> innerResults;
                if (innerResultsEl is not null)
                {
                    innerResults = new List<TestStep>();
                    foreach (var ir in innerResultsEl.Elements(UnitTestResultName))
                    {
                        // Navigate Output > StdOut and Output > ErrorInfo > Message for inner results
                        var irOutput = ir.Element(OutputName);
                        var irStdOut = irOutput?.Element(StdOutName)?.Value ?? "";
                        var irErrorInfo = irOutput?.Element(ErrorInfoName);
                        var irMessage = irErrorInfo?.Element(MessageName)?.Value;

                        innerResults.Add(new TestStep
                        {
                            StepName = ir.Attribute("testName")?.Value ?? "",
                            Outcome = ir.Attribute("outcome")?.Value ?? "",
                            Duration = TimeSpan.TryParse(ir.Attribute("duration")?.Value, out var sd) ? sd : TimeSpan.Zero,
                            StdOut = irStdOut,
                            ErrorMessage = irMessage,
                        });
                    }
                }
                else
                {
                    innerResults = [];
                }

                // Main Output > StdOut via direct child navigation
                var outputEl = r.Element(OutputName);
                var mainStdOut = outputEl?.Element(StdOutName)?.Value ?? "";

                // Output > ErrorInfo > Message and StackTrace via targeted navigation
                var errorInfoEl = outputEl?.Element(ErrorInfoName);
                var errorMessage = errorInfoEl?.Element(MessageName)?.Value;
                var stackTrace = errorInfoEl?.Element(StackTraceName)?.Value;

                // DebugTrace element (System.Diagnostics.Trace output)
                var debugTraceEl = outputEl?.Element(DebugTraceName)?.Value ?? "";

                // StdErr
                var stdErr = outputEl?.Element(StdErrName)?.Value ?? "";

                // TextMessages > Message (additional diagnostic output)
                var textMessagesEl = outputEl?.Element(TextMessagesName);
                var textMessages = textMessagesEl is not null
                    ? string.Join("\n", textMessagesEl.Elements(MessageName).Select(m => m.Value))
                    : "";

                // Merge all trace sources
                var debugTrace = string.Join(Environment.NewLine,
                    new[] { debugTraceEl, textMessages }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));

                // Merge StdErr into StdOut if present
                var combinedStdOut = string.Join(Environment.NewLine,
                    new[] { mainStdOut, stdErr }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));

                var duration = TimeSpan.TryParse(r.Attribute("duration")?.Value, out var d) ? d : TimeSpan.Zero;
                totalDurationTicks += duration.Ticks;

                testCases.Add(new TrxTestCase
                {
                    TestName = r.Attribute("testName")?.Value ?? "",
                    Outcome = r.Attribute("outcome")?.Value ?? "NotExecuted",
                    Duration = duration,
                    ErrorMessage = errorMessage,
                    StackTrace = stackTrace,
                    StdOut = combinedStdOut,
                    TrxFileName = fileName,
                    ExecutionSteps = innerResults,
                    DebugTrace = debugTrace,
                });
            }
        }

        var result = new TrxTestRun
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
            Duration = TimeSpan.FromTicks(totalDurationTicks),
            TestCases = testCases,
        };

        // Evict stale entries for the same file path (old timestamps) to prevent
        // unbounded key growth when .trx files are rewritten.
        var pathPrefix = trxFilePath + "|";
        foreach (var existingKey in _fileCache.Keys)
        {
            if (existingKey.StartsWith(pathPrefix, StringComparison.Ordinal) && existingKey != cacheKey)
                _fileCache.TryRemove(existingKey, out _);
        }

        _fileCache[cacheKey] = result;

        // Size cap: if still over limit, clear the oldest half
        if (_fileCache.Count > MaxFileCacheSize)
        {
            var keysToRemove = _fileCache.Keys.Take(_fileCache.Count / 2).ToList();
            foreach (var key in keysToRemove)
                _fileCache.TryRemove(key, out _);
        }

        return result;
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
    /// The modified date is resolved from the folder name when possible
    /// (e.g. "OAK_main_20260401.7" ? 2026-04-01) because
    /// <see cref="Directory.GetLastWriteTime"/> can be unreliable on
    /// network shares. Falls back to the directory timestamp otherwise.
    /// </summary>
    public List<(string BuildNumber, string Path, DateTime Modified)>
        DiscoverBuilds(string resultsRootPath)
    {
        if (!Directory.Exists(resultsRootPath)) return new();
        return Directory.GetDirectories(resultsRootPath)
            .Select(d => (
                BuildNumber: Path.GetFileName(d),
                Path: d,
                Modified: ResolveBuildDate(d)
            ))
            .OrderByDescending(b => b.Modified)
            .ToList();
    }

    /// <summary>
    /// Attempts to extract a date from the folder name (pattern: *_yyyyMMdd*).
    /// Falls back to <see cref="Directory.GetLastWriteTime"/>.
    /// </summary>
    private static DateTime ResolveBuildDate(string directoryPath)
    {
        var name = Path.GetFileName(directoryPath);
        // Look for an 8-digit segment that parses as yyyyMMdd
        foreach (var segment in name.Split('_', '.'))
        {
            if (segment.Length == 8 &&
                DateTime.TryParseExact(segment, "yyyyMMdd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var parsed))
            {
                return parsed;
            }
        }

        return Directory.GetLastWriteTime(directoryPath);
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

        var failedTests = testResults.Where(t => t.Outcome == "Failed").ToList();
        var total = runs.Sum(r => r.Total);
        var passed = runs.Sum(r => r.Passed);

        return new UseCaseNode
        {
            UseCaseName = useCaseName,
            Duration = TimeSpan.FromTicks(runs.Sum(r => r.Duration.Ticks)),
            Total = total,
            Passed = passed,
            Failed = runs.Sum(r => r.Failed),
            Timeout = runs.Sum(r => r.Timeout),
            NotExecuted = runs.Sum(r => r.NotExecuted),
            PassRate = total > 0 ? (double)passed / total * 100 : 0,
            TestResults = testResults,
            FailedTests = failedTests,
        };
    }

    private static void TrackTimeRange(List<TrxTestRun> runs, ref DateTime? earliest, ref DateTime? latest)
    {
        foreach (var r in runs)
        {
            if (r.StartTime > DateTime.MinValue)
            {
                if (earliest is null || r.StartTime < earliest)
                    earliest = r.StartTime;
            }
            if (r.EndTime > DateTime.MinValue)
            {
                if (latest is null || r.EndTime > latest)
                    latest = r.EndTime;
            }
        }
    }
}
