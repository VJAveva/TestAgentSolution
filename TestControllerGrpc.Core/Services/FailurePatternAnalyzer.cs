using System.IO;
using System.Text.RegularExpressions;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Analyzes test failure patterns across multiple builds to identify
/// systemic regressions, flaky tests, cascading failures, and chronic issues.
/// </summary>
public sealed class FailurePatternAnalyzer
{
    private readonly TrxResultsParser _parser;
    private readonly BuildResultsConfig _config;

    public FailurePatternAnalyzer(TrxResultsParser parser, BuildResultsConfig config)
    {
        _parser = parser;
        _config = config;
    }

    /// <summary>
    /// Analyzes a specific test case across the last N builds to detect failure patterns.
    /// </summary>
    public FailureAnalysisReport AnalyzeTest(string testCaseName, int lookbackBuilds = 5)
    {
        // 1. Find the last N builds in chronological order (newest first)
        var buildFolders = _parser.DiscoverBuilds(_config.ResultsRootPath)
            .OrderByDescending(b => b.Modified)
            .Take(lookbackBuilds)
            .ToArray();

        // 2. Extract this test's outcome from each build
        var history = new List<TestExecutionRecord>();
        foreach (var (buildNumber, path, modified) in buildFolders)
        {
            foreach (var trx in EnumerateTrxSafe(path))
            {
                var record = ExtractTestResult(trx, testCaseName);
                if (record != null)
                {
                    record.BuildName = buildNumber;
                    record.BuildDate = modified;
                    history.Add(record);
                    break; // Found in this build
                }
            }
        }

        if (history.Count == 0)
        {
            return new FailureAnalysisReport
            {
                TestCaseName = testCaseName,
                Pattern = FailurePattern.None,
                Verdict = $"Test '{testCaseName}' not found in any of the last {lookbackBuilds} builds.",
                Confidence = 0,
                History = history,
            };
        }

        // 3. Compute consecutive failure count from newest
        int consecutiveFailures = 0;
        foreach (var record in history)
        {
            if (record.Outcome == "Failed")
                consecutiveFailures++;
            else
                break;
        }

        // 4. Extract signatures for each consecutive failure
        var failures = history
            .Take(consecutiveFailures)
            .Where(r => r.Outcome == "Failed")
            .Select(r => new FailureSignature
            {
                BuildName = r.BuildName,
                BuildDate = r.BuildDate,
                FailedStepIndex = ExtractFailedStepIndex(r.StackTrace, r.Steps),
                FailedStepName = ExtractFailedStepName(r.StackTrace, r.Steps),
                ErrorType = ExtractExceptionType(r.ErrorMessage),
                NormalizedMessage = NormalizeErrorMessage(r.ErrorMessage),
                TopStackFrame = ExtractTopStackFrame(r.StackTrace),
                Agent = r.Agent,
                Duration = r.Duration,
            }).ToList();

        // 5. Determine if all signatures match (same root cause)
        bool allSignaturesMatch = failures.Count > 1 &&
            failures.All(s =>
                s.FailedStepName == failures[0].FailedStepName &&
                s.ErrorType == failures[0].ErrorType &&
                s.NormalizedMessage == failures[0].NormalizedMessage);

        // 6. Find last passing build
        var lastPass = history.FirstOrDefault(r => r.Outcome == "Passed");

        // 7. Classify pattern
        var totalFailed = history.Count(r => r.Outcome == "Failed");
        FailurePattern pattern;

        if (history.All(r => r.Outcome == "Failed"))
            pattern = FailurePattern.ChronicFailure;
        else if (consecutiveFailures >= 3 && allSignaturesMatch)
            pattern = FailurePattern.SystemicRegression;
        else if (consecutiveFailures >= 3 && !allSignaturesMatch)
            pattern = FailurePattern.CascadingFailures;
        else if (totalFailed >= 3 && consecutiveFailures < 3)
            pattern = FailurePattern.FlakyTest;
        else if (consecutiveFailures == 0 && totalFailed > 0)
            pattern = FailurePattern.Resolved;
        else if (consecutiveFailures >= 2 && allSignaturesMatch)
            pattern = FailurePattern.SystemicRegression;
        else if (consecutiveFailures >= 2)
            pattern = FailurePattern.CascadingFailures;
        else if (consecutiveFailures == 1)
            pattern = FailurePattern.NewFailure;
        else
            pattern = FailurePattern.None;

        // 8. Build verdict text
        string verdict = pattern switch
        {
            FailurePattern.SystemicRegression =>
                $"Test has failed {consecutiveFailures} consecutive times " +
                $"with identical error signature. " +
                $"Last passing build: {lastPass?.BuildName ?? "unknown"}. " +
                "Likely a code regression — review commits between " +
                "last pass and first fail.",

            FailurePattern.CascadingFailures =>
                $"Test has failed {consecutiveFailures} times with " +
                "DIFFERENT error signatures each time. " +
                "Likely environmental issue (machine state, network, " +
                "test data corruption).",

            FailurePattern.FlakyTest =>
                $"Test alternates between pass and fail " +
                $"({totalFailed} of {history.Count} failed). " +
                "Likely a flaky test — investigate timing dependencies " +
                "or shared state.",

            FailurePattern.ChronicFailure =>
                $"Test has failed in ALL {history.Count} analyzed builds. " +
                "Test may be invalid, disabled, or never updated " +
                "after a major change.",

            FailurePattern.Resolved =>
                "Test previously failed but is now passing. No action needed.",

            FailurePattern.NewFailure =>
                $"Test failed in the most recent build ({history[0].BuildName}) " +
                $"but passed in {history.Count - 1} prior builds in the window. " +
                "Likely a NEW regression — re-run to confirm and inspect the latest changes.",

            _ => "No significant failure pattern detected.",
        };

        // 9. Compute confidence score
        int confidence = pattern switch
        {
            FailurePattern.SystemicRegression => Math.Min(95, 60 + consecutiveFailures * 10),
            FailurePattern.CascadingFailures => 70,
            FailurePattern.FlakyTest => 50,
            FailurePattern.ChronicFailure => 100,
            FailurePattern.Resolved => 90,
            FailurePattern.NewFailure => 40,
            _ => 0,
        };

        // 10. Find regression window
        string? firstFailBuild = null;
        if (consecutiveFailures > 0 && lastPass != null)
        {
            var lastPassIndex = history.IndexOf(lastPass);
            if (lastPassIndex > 0)
                firstFailBuild = history[lastPassIndex - 1].BuildName;
        }

        return new FailureAnalysisReport
        {
            TestCaseName = testCaseName,
            Pattern = pattern,
            Verdict = verdict,
            Confidence = confidence,
            ConsecutiveFailures = consecutiveFailures,
            TotalBuildsAnalyzed = history.Count,
            TotalFailures = totalFailed,
            FlakeRate = history.Count > 0 ? (double)totalFailed / history.Count * 100 : 0,
            History = history,
            FailureSignatures = failures,
            LastPassBuild = lastPass?.BuildName,
            FirstFailBuild = firstFailBuild,
            AllSignaturesMatch = allSignaturesMatch,
            SuggestedAction = BuildSuggestedAction(pattern, lastPass, failures.FirstOrDefault()),
        };
    }

    /// <summary>
    /// Returns a short pattern label suitable for table cells or email columns.
    /// Examples: "REGRESSION (4x)", "FLAKY (3/10)", "CHRONIC FAILURE",
    /// "NEW FAILURE", "RESOLVED".
    /// </summary>
    public string GetCompactLabel(string testCaseName, int lookbackBuilds = 5)
    {
        var report = AnalyzeTest(testCaseName, lookbackBuilds);
        return report.Pattern switch
        {
            FailurePattern.SystemicRegression =>
                $"REGRESSION ({report.ConsecutiveFailures}x)",
            FailurePattern.CascadingFailures =>
                $"CASCADING ({report.ConsecutiveFailures}x)",
            FailurePattern.FlakyTest =>
                $"FLAKY ({report.TotalFailures}/{report.TotalBuildsAnalyzed})",
            FailurePattern.ChronicFailure => "CHRONIC FAILURE",
            FailurePattern.Resolved => "RESOLVED",
            FailurePattern.NewFailure => "NEW FAILURE",
            _ => "—",
        };
    }

    // ?? Extraction helpers ???????????????????????????????????????????

    private TestExecutionRecord? ExtractTestResult(string trxPath, string testCaseName)
    {
        try
        {
            var run = _parser.ParseFile(trxPath);
            var testCase = run.TestCases.FirstOrDefault(tc =>
                string.Equals(tc.TestName, testCaseName, StringComparison.OrdinalIgnoreCase));

            if (testCase == null) return null;

            return new TestExecutionRecord
            {
                TestName = testCase.TestName,
                Outcome = testCase.Outcome,
                Duration = testCase.Duration,
                ErrorMessage = testCase.ErrorMessage ?? "",
                StackTrace = testCase.StackTrace ?? "",
                Steps = testCase.ExecutionSteps,
                Agent = ExtractAgentFromTrx(trxPath),
                TrxFile = Path.GetFileName(trxPath),
            };
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractAgentFromTrx(string trxPath)
    {
        // Try to infer agent/machine from path or filename.
        var fileName = Path.GetFileNameWithoutExtension(trxPath);

        // Default MSTest naming: "<user>_<machine> YYYY-MM-DD HH_MM_SS"
        var match = Regex.Match(fileName, @"^[^_\s]+_([^\s_]+)\s+\d{4}-\d{2}-\d{2}");
        if (match.Success) return match.Groups[1].Value;

        // Alternative naming: "..._<MACHINE>_NNNN" (e.g. build pipeline output)
        match = Regex.Match(fileName, @"_([A-Z0-9][A-Z0-9\-]+)_\d{3,}", RegexOptions.IgnoreCase);
        if (match.Success) return match.Groups[1].Value;

        // Last resort: parent folder name often carries the agent for our layouts.
        var parent = Path.GetFileName(Path.GetDirectoryName(trxPath) ?? "");
        return parent;
    }

    private static int ExtractFailedStepIndex(string? stackTrace, List<TestStep> steps)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i].Outcome == "Failed")
                return i;
        }

        if (string.IsNullOrEmpty(stackTrace)) return -1;

        var match = Regex.Match(stackTrace, @"Step\s*(\d+)|TestStep\[(\d+)\]");
        if (!match.Success) return -1;

        var raw = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        return int.TryParse(raw, out var idx) ? idx : -1;
    }

    /// <summary>
    /// Enumerates *.trx files beneath <paramref name="root"/> in a fault-tolerant way:
    /// skips folders we cannot read (e.g., ACL-protected system directories) and
    /// avoids reparse points / junctions to prevent infinite loops.
    /// </summary>
    private static IEnumerable<string> EnumerateTrxSafe(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            string[] files;
            try { files = Directory.GetFiles(dir, "*.trx"); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var f in files)
                yield return f;

            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var s in subs)
            {
                // Skip reparse points / junctions (e.g., C:\ProgramData\Application Data -> itself).
                try
                {
                    var attrs = File.GetAttributes(s);
                    if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch (UnauthorizedAccessException) { continue; }
                catch (IOException) { continue; }

                stack.Push(s);
            }
        }
    }

    private static string ExtractFailedStepName(string? stackTrace, List<TestStep> steps)
    {
        // First try from structured steps
        var failedStep = steps.FirstOrDefault(s => s.Outcome == "Failed");
        if (failedStep != null) return failedStep.StepName;

        if (string.IsNullOrEmpty(stackTrace)) return "";

        // Try to extract from stack trace
        var match = Regex.Match(stackTrace, @"at\s+\S+\.(\w+)\(");
        return match.Success ? match.Groups[1].Value : "";
    }

    private static string ExtractExceptionType(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage)) return "";

        // Common patterns: "System.TimeoutException: ...", "Assert.AreEqual failed."
        var match = Regex.Match(errorMessage, @"^([\w.]+Exception)\b");
        if (match.Success) return match.Groups[1].Value;

        match = Regex.Match(errorMessage, @"^(Assert\.\w+)\s+failed");
        if (match.Success) return match.Groups[1].Value;

        // Fallback: first line truncated
        var firstLine = errorMessage.Split('\n')[0].Trim();
        return firstLine.Length > 60 ? firstLine[..60] : firstLine;
    }

    private static string ExtractTopStackFrame(string? stackTrace)
    {
        if (string.IsNullOrEmpty(stackTrace)) return "";

        var lines = stackTrace.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("at "))
                return trimmed;
        }
        return "";
    }

    public string NormalizeErrorMessage(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";

        var normalized = raw;
        // Replace GUIDs
        normalized = Regex.Replace(normalized,
            @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
            "{GUID}");
        // Replace dates
        normalized = Regex.Replace(normalized, @"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}", "{DATETIME}");
        normalized = Regex.Replace(normalized, @"\d{4}-\d{2}-\d{2}", "{DATE}");
        // Replace numbers (but not in exception type names)
        normalized = Regex.Replace(normalized, @"(?<![A-Za-z])\d+(?!\w*Exception)", "{N}");
        // Replace file paths
        normalized = Regex.Replace(normalized, @"[A-Z]:\\[^\s""']+", "{PATH}");

        return normalized.Trim();
    }

    private static string BuildSuggestedAction(
        FailurePattern pattern,
        TestExecutionRecord? lastPass,
        FailureSignature? firstSignature)
    {
        return pattern switch
        {
            FailurePattern.SystemicRegression =>
                lastPass != null
                    ? $"Review commits between build '{lastPass.BuildName}' (last pass) and the first failure. " +
                      $"Error type: {firstSignature?.ErrorType ?? "unknown"}. " +
                      "Consider reverting recent changes to this area."
                    : "Investigate the root cause of the recurring failure. " +
                      $"Error type: {firstSignature?.ErrorType ?? "unknown"}.",

            FailurePattern.CascadingFailures =>
                "Check the test environment for: machine restarts, disk space, " +
                "network connectivity, database state, or test data corruption. " +
                "Different signatures suggest infrastructure rather than code issues.",

            FailurePattern.FlakyTest =>
                "Investigate timing-dependent assertions, shared state between tests, " +
                "or race conditions. Consider adding retry logic or stabilization waits.",

            FailurePattern.ChronicFailure =>
                "This test has never passed in the analysis window. " +
                "Verify the test is still valid and the feature under test hasn't been redesigned.",

            FailurePattern.Resolved =>
                "No action needed — the test is currently passing.",

            _ => "",
        };
    }
}

// ?? Models ??????????????????????????????????????????????????????????

public enum FailurePattern
{
    None,
    SystemicRegression,
    CascadingFailures,
    FlakyTest,
    ChronicFailure,
    Resolved,
    NewFailure,
}

public sealed class TestExecutionRecord
{
    public string TestName { get; set; } = "";
    public string BuildName { get; set; } = "";
    public DateTime BuildDate { get; set; }
    public string Outcome { get; set; } = "";
    public TimeSpan Duration { get; set; }
    public string ErrorMessage { get; set; } = "";
    public string StackTrace { get; set; } = "";
    public List<TestStep> Steps { get; set; } = new();
    public string Agent { get; set; } = "";
    public string TrxFile { get; set; } = "";
}

public sealed class FailureSignature
{
    public string BuildName { get; set; } = "";
    public DateTime BuildDate { get; set; }
    public int FailedStepIndex { get; set; } = -1;
    public string FailedStepName { get; set; } = "";
    public string ErrorType { get; set; } = "";
    public string NormalizedMessage { get; set; } = "";
    public string TopStackFrame { get; set; } = "";
    public string Agent { get; set; } = "";
    public TimeSpan Duration { get; set; }
}

public sealed class FailureAnalysisReport
{
    public string TestCaseName { get; set; } = "";
    public FailurePattern Pattern { get; set; }
    public string Verdict { get; set; } = "";
    public int Confidence { get; set; }
    public int ConsecutiveFailures { get; set; }
    public int TotalBuildsAnalyzed { get; set; }
    public int TotalFailures { get; set; }
    public double FlakeRate { get; set; }
    public List<TestExecutionRecord> History { get; set; } = new();
    public List<FailureSignature> FailureSignatures { get; set; } = new();
    public string? LastPassBuild { get; set; }
    public string? FirstFailBuild { get; set; }
    public bool AllSignaturesMatch { get; set; }
    public string SuggestedAction { get; set; } = "";
}
