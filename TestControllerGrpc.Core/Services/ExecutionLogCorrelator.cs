using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Builds a merged execution log for a specific test case in a specific build,
/// correlating data from three sources:
///   1. TRX file — test framework events and step results
///   2. Agent log file — agent stdout/stderr during the test
///   3. Controller log file — gRPC events, progress updates
///
/// All three are filtered to the test's time window (startTime ? endTime
/// from the TRX) so the user sees only relevant lines.
/// </summary>
public sealed class ExecutionLogCorrelator
{
    private readonly BuildResultsConfig _config;
    private readonly ILogger<ExecutionLogCorrelator>? _logger;

    /// <summary>Optional override for agent log root (defaults to <c>C:\TestAgentService\Logs</c>).</summary>
    public string AgentLogRoot { get; set; } = @"C:\TestAgentService\Logs";

    /// <summary>Optional override for controller log root (defaults to <c>C:\TestControllerService\Logs</c>).</summary>
    public string ControllerLogRoot { get; set; } = @"C:\TestControllerService\Logs";

    public ExecutionLogCorrelator(
        BuildResultsConfig config,
        ILogger<ExecutionLogCorrelator>? logger = null)
    {
        _config = config;
        _logger = logger;
    }

    public ExecutionLogReport BuildReport(
        string buildName, string testCaseName, int? failedStepIndex = null)
    {
        var report = new ExecutionLogReport
        {
            BuildName = buildName,
            TestCaseName = testCaseName,
        };

        var buildFolder = Path.Combine(_config.ResultsRootPath, buildName);
        if (!Directory.Exists(buildFolder))
        {
            report.Error = $"Build folder not found: {buildName}";
            return report;
        }

        var trxData = ExtractTrxData(buildFolder, testCaseName);
        if (trxData == null)
        {
            report.Error = $"Test '{testCaseName}' not found in build {buildName}";
            return report;
        }

        report.Outcome = trxData.Outcome;
        report.Agent = trxData.Agent;
        report.StartTime = trxData.StartTime;
        report.EndTime = trxData.EndTime;
        report.Duration = trxData.Duration;
        report.Steps = trxData.Steps;
        report.FailedStepIndex = failedStepIndex ?? trxData.FailedStepIndex;
        report.ErrorMessage = trxData.ErrorMessage;
        report.StackTrace = trxData.StackTrace;

        if (trxData.StartUtc.HasValue && trxData.EndUtc.HasValue)
        {
            report.AgentLogLines = ReadAgentLogWindow(
                trxData.Agent, trxData.StartUtc.Value, trxData.EndUtc.Value);

            report.ControllerLogLines = ReadControllerLogWindow(
                trxData.StartUtc.Value, trxData.EndUtc.Value);
        }

        report.MergedTimeline = MergeTimeline(
            trxData.TrxEvents, report.AgentLogLines, report.ControllerLogLines);

        return report;
    }

    // ?? TRX extraction ??????????????????????????????????????????????

    private TrxExtraction? ExtractTrxData(string buildFolder, string testCaseName)
    {
        var trxFiles = Directory.GetFiles(buildFolder, "*.trx", SearchOption.AllDirectories);

        foreach (var trxPath in trxFiles)
        {
            try
            {
                var doc = XDocument.Load(trxPath);
                var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

                var result = doc.Descendants(ns + "UnitTestResult")
                    .FirstOrDefault(e => string.Equals(
                        e.Attribute("testName")?.Value,
                        testCaseName, StringComparison.OrdinalIgnoreCase));

                if (result == null) continue;

                var output = result.Element(ns + "Output");
                var errorInfo = output?.Element(ns + "ErrorInfo");
                var stdout = output?.Element(ns + "StdOut")?.Value ?? "";

                var startStr = result.Attribute("startTime")?.Value ?? "";
                var endStr = result.Attribute("endTime")?.Value ?? "";

                DateTime? startUtc = null, endUtc = null;
                if (DateTime.TryParse(startStr, out var s)) startUtc = s.ToUniversalTime();
                if (DateTime.TryParse(endStr, out var e)) endUtc = e.ToUniversalTime();

                var errorMessage = errorInfo?.Element(ns + "Message")?.Value ?? "";

                return new TrxExtraction
                {
                    TestName = testCaseName,
                    Outcome = result.Attribute("outcome")?.Value ?? "",
                    StartTime = startStr,
                    EndTime = endStr,
                    StartUtc = startUtc,
                    EndUtc = endUtc,
                    Duration = result.Attribute("duration")?.Value ?? "",
                    Agent = result.Attribute("computerName")?.Value
                        ?? Path.GetFileNameWithoutExtension(trxPath),
                    ErrorMessage = errorMessage,
                    StackTrace = errorInfo?.Element(ns + "StackTrace")?.Value ?? "",
                    Steps = ExtractSteps(stdout),
                    FailedStepIndex = ExtractFailedStepIndex(errorMessage, stdout),
                    TrxEvents = ParseTrxStdoutAsEvents(stdout, startUtc),
                };
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to parse TRX {Path}", trxPath);
            }
        }

        return null;
    }

    private static List<TestStepInfo> ExtractSteps(string stdout)
    {
        var steps = new List<TestStepInfo>();
        if (string.IsNullOrEmpty(stdout)) return steps;

        var stepPattern = new Regex(
            @"Step\s+(\d+):\s*(\w+)\s*(?:\(([^)]+)\))?",
            RegexOptions.IgnoreCase);

        foreach (Match m in stepPattern.Matches(stdout))
        {
            var index = int.Parse(m.Groups[1].Value);
            var name = m.Groups[2].Value;
            var detail = m.Groups[3].Success ? m.Groups[3].Value : "";

            steps.Add(new TestStepInfo
            {
                Index = index,
                Name = name,
                Detail = detail,
                Outcome = stdout.Contains(
                    $"Step {index} FAILED", StringComparison.OrdinalIgnoreCase)
                    ? "Failed" : "Passed",
            });
        }

        return steps;
    }

    private static int ExtractFailedStepIndex(string err, string stdout)
    {
        var combined = (err ?? "") + "\n" + (stdout ?? "");
        var match = Regex.Match(combined,
            @"Step\s+(\d+)\s+(?:FAILED|failed)", RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups[1].Value) : -1;
    }

    private static List<MergedLogLine> ParseTrxStdoutAsEvents(
        string stdout, DateTime? testStart)
    {
        var events = new List<MergedLogLine>();
        if (string.IsNullOrEmpty(stdout)) return events;

        var lines = stdout.Split('\n');
        var currentTime = testStart ?? DateTime.UtcNow;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            var tsMatch = Regex.Match(trimmed,
                @"^\[?(\d{2}:\d{2}:\d{2}(?:\.\d+)?)\]?");

            if (tsMatch.Success && TimeSpan.TryParse(tsMatch.Groups[1].Value, out var ts))
            {
                currentTime = currentTime.Date + ts;
            }

            events.Add(new MergedLogLine
            {
                Timestamp = currentTime,
                Source = "TRX",
                Severity = trimmed.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                    ? "Error"
                    : trimmed.Contains("WARN", StringComparison.OrdinalIgnoreCase)
                        ? "Warning" : "Info",
                Message = trimmed,
            });
        }

        return events;
    }

    // ?? Agent log reading ???????????????????????????????????????????

    private List<MergedLogLine> ReadAgentLogWindow(
        string agentName, DateTime startUtc, DateTime endUtc)
    {
        var result = new List<MergedLogLine>();
        if (string.IsNullOrEmpty(agentName)) return result;

        var lowerName = agentName.ToLowerInvariant();
        var datePart = startUtc.ToString("yyyyMMdd");

        var possiblePaths = new[]
        {
            Path.Combine(AgentLogRoot, $"agent-{lowerName}-{datePart}.log"),
            $@"\\{agentName}\C$\TestAgentService\Logs\agent-{lowerName}-{datePart}.log",
            Path.Combine(_config.ResultsRootPath, "agent-logs", $"agent-{lowerName}-{datePart}.log"),
        };

        var logPath = possiblePaths.FirstOrDefault(File.Exists);
        if (logPath == null) return result;

        try
        {
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var lineTime = ParseLogLineTimestamp(line);
                if (lineTime.HasValue && lineTime.Value >= startUtc && lineTime.Value <= endUtc)
                {
                    result.Add(new MergedLogLine
                    {
                        Timestamp = lineTime.Value,
                        Source = "Agent",
                        Severity = ExtractSeverity(line),
                        Message = StripTimestamp(line),
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read agent log {Path}", logPath);
        }

        return result;
    }

    // ?? Controller log reading ??????????????????????????????????????

    private List<MergedLogLine> ReadControllerLogWindow(
        DateTime startUtc, DateTime endUtc)
    {
        var result = new List<MergedLogLine>();
        if (!Directory.Exists(ControllerLogRoot)) return result;

        var datePart = startUtc.ToString("yyyyMMdd");
        var possiblePaths = new[]
        {
            Path.Combine(ControllerLogRoot, $"app-{datePart}.log"),
            Path.Combine(ControllerLogRoot, $"controller-{datePart}.log"),
        };

        var logPath = possiblePaths.FirstOrDefault(File.Exists);
        if (logPath == null) return result;

        try
        {
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var lineTime = ParseLogLineTimestamp(line);
                if (!lineTime.HasValue) continue;
                if (lineTime.Value < startUtc || lineTime.Value > endUtc) continue;

                result.Add(new MergedLogLine
                {
                    Timestamp = lineTime.Value,
                    Source = "Controller",
                    Severity = ExtractSeverity(line),
                    Message = StripTimestamp(line),
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read controller log {Path}", logPath);
        }

        return result;
    }

    // ?? Log line parsing helpers ????????????????????????????????????

    private static DateTime? ParseLogLineTimestamp(string line)
    {
        var match = Regex.Match(line,
            @"^(\d{4}-\d{2}-\d{2})\s+(\d{2}:\d{2}:\d{2}(?:\.\d+)?)");
        if (!match.Success) return null;

        var combined = match.Groups[1].Value + " " + match.Groups[2].Value;
        return DateTime.TryParse(combined, out var dt) ? dt.ToUniversalTime() : null;
    }

    private static string ExtractSeverity(string line)
    {
        if (line.Contains("[ERR]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains(" ERROR ", StringComparison.OrdinalIgnoreCase))
            return "Error";
        if (line.Contains("[WRN]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains(" WARN ", StringComparison.OrdinalIgnoreCase))
            return "Warning";
        return "Info";
    }

    private static string StripTimestamp(string line)
    {
        return Regex.Replace(line,
            @"^\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}(?:\.\d+)?\s*", "");
    }

    private static List<MergedLogLine> MergeTimeline(
        List<MergedLogLine> trx,
        List<MergedLogLine> agent,
        List<MergedLogLine> controller)
    {
        return trx.Concat(agent).Concat(controller)
            .OrderBy(l => l.Timestamp)
            .ToList();
    }

    // ?? Internal data carrier ???????????????????????????????????????

    private sealed class TrxExtraction
    {
        public string TestName { get; set; } = "";
        public string Outcome { get; set; } = "";
        public string StartTime { get; set; } = "";
        public string EndTime { get; set; } = "";
        public DateTime? StartUtc { get; set; }
        public DateTime? EndUtc { get; set; }
        public string Duration { get; set; } = "";
        public string Agent { get; set; } = "";
        public string ErrorMessage { get; set; } = "";
        public string StackTrace { get; set; } = "";
        public List<TestStepInfo> Steps { get; set; } = new();
        public int FailedStepIndex { get; set; }
        public List<MergedLogLine> TrxEvents { get; set; } = new();
    }
}

// ?? Public DTOs ?????????????????????????????????????????????????????

public sealed class ExecutionLogReport
{
    public string BuildName { get; set; } = "";
    public string TestCaseName { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string Agent { get; set; } = "";
    public string StartTime { get; set; } = "";
    public string EndTime { get; set; } = "";
    public string Duration { get; set; } = "";
    public int FailedStepIndex { get; set; } = -1;
    public string ErrorMessage { get; set; } = "";
    public string StackTrace { get; set; } = "";
    public List<TestStepInfo> Steps { get; set; } = new();
    public List<MergedLogLine> AgentLogLines { get; set; } = new();
    public List<MergedLogLine> ControllerLogLines { get; set; } = new();
    public List<MergedLogLine> MergedTimeline { get; set; } = new();
    public string Error { get; set; } = "";
}

public sealed class TestStepInfo
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string StartTime { get; set; } = "";
    public string Duration { get; set; } = "";
}

public sealed class MergedLogLine
{
    public DateTime Timestamp { get; set; }
    /// <summary>Origin: "TRX", "Agent", or "Controller".</summary>
    public string Source { get; set; } = "";
    /// <summary>Severity: "Info", "Warning", or "Error".</summary>
    public string Severity { get; set; } = "";
    public string Message { get; set; } = "";
}
