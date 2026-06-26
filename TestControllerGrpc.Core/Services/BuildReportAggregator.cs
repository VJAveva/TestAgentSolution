using System.IO;
using System.Diagnostics;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Reads TRX files from the ReportResults root (one CI per top-level folder,
/// all nested subfolders consolidated) and produces a single
/// <see cref="BuildReportCard"/>: per-CI and per-agent breakdowns, a prioritized
/// failures table, a configurable letter grade, a trend strip, and PSR cards
/// (dummy until the PSR Excel format is finalised).
///
/// TRX parsing is done in parallel (one task per CI folder); the underlying
/// <see cref="TrxResultsParser"/> caches per file and is thread-safe.
/// </summary>
public sealed class BuildReportAggregator
{
    private readonly TrxResultsParser _parser;
    private readonly GradeCalculator _grader;
    private readonly CiOwnerResolver _owners;
    private readonly BuildSummaryStore _summaryStore;
    private readonly FailurePatternAnalyzer _patternAnalyzer;
    private readonly BuildReportCardConfig _config;
    private readonly IAppLogger _logger;

    public BuildReportAggregator(
        TrxResultsParser parser,
        GradeCalculator grader,
        CiOwnerResolver owners,
        BuildSummaryStore summaryStore,
        FailurePatternAnalyzer patternAnalyzer,
        BuildReportCardConfig config,
        IAppLogger logger)
    {
        _parser = parser;
        _grader = grader;
        _owners = owners;
        _summaryStore = summaryStore;
        _patternAnalyzer = patternAnalyzer;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Lists the available build folders under the ReportResults root (newest
    /// first). Each build folder contains the CI subfolders. Internal
    /// bookkeeping folders (e.g. ".reportcard") are excluded.
    /// </summary>
    public IReadOnlyList<string> ListBuilds()
    {
        var root = _config.ReportResultsRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return Array.Empty<string>();

        try
        {
            return new DirectoryInfo(root)
                .GetDirectories()
                .Where(d => !d.Name.StartsWith('.')
                            && !d.Name.Equals(_config.PsrFolderName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .Select(d => d.Name)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.Warn("BuildReportCard", $"Failed to list builds under {root}: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>Aggregates the report card on a background thread.</summary>
    public Task<BuildReportCard> AggregateAsync(string? buildNumber = null, CancellationToken ct = default)
        => Task.Run(() => Aggregate(buildNumber, ct), ct);

    public BuildReportCard Aggregate(string? buildNumber = null, CancellationToken ct = default)
    {
        var root = _config.ReportResultsRoot;
        var generatedUtc = DateTime.UtcNow;
        var swTotal = Stopwatch.StartNew();
        var corr = string.IsNullOrWhiteSpace(buildNumber) ? "(latest)" : buildNumber;
        _logger.Info("BuildReportCard", $"Aggregate START build='{corr}' root='{root}'");

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            _logger.Warn("BuildReportCard", $"ReportResults root not found: {root}");
            return EmptyCard(string.IsNullOrWhiteSpace(buildNumber) ? "ReportResults" : buildNumber, generatedUtc);
        }

        // ── Resolve the build folder that holds the CI subfolders ─────────
        // Layout: {root}\{buildNumber}\{CI}\... If no build is specified, pick
        // the most recently modified build folder. Fall back to the root
        // itself when CI folders sit directly under it (legacy layout).
        if (string.IsNullOrWhiteSpace(buildNumber))
            buildNumber = ListBuilds().FirstOrDefault();

        string baseDir;
        if (!string.IsNullOrWhiteSpace(buildNumber) && Directory.Exists(Path.Combine(root, buildNumber)))
        {
            baseDir = Path.Combine(root, buildNumber);
        }
        else if (_config.CiFolders.Any(ci => Directory.Exists(Path.Combine(root, ci))))
        {
            baseDir = root;
            buildNumber ??= Path.GetFileName(root.TrimEnd('\\', '/'));
        }
        else
        {
            _logger.Warn("BuildReportCard", $"No build folder found under {root}");
            return EmptyCard(string.IsNullOrWhiteSpace(buildNumber) ? "ReportResults" : buildNumber, generatedUtc);
        }

        // ── Determine the CI folders to scan ─────────────────────────────
        // Reflect the actual subfolders present under the build folder so the
        // grid matches what is really on disk (deduplicated; PSR and dot
        // folders excluded). Falls back to the configured CiFolders list.
        var swDiscover = Stopwatch.StartNew();
        var ciNames = DiscoverCiFolders(baseDir);
        swDiscover.Stop();
        _logger.Log(LogLevel.Information, "BuildReportCard",
            $"Discovered {ciNames.Count} CI folder(s) under '{baseDir}'", corr, swDiscover.ElapsedMilliseconds);

        // ── Parse each CI folder in parallel ──────────────────────────────
        var swParse = Stopwatch.StartNew();
        var ciAccumulators = new ConcurrentBag<CiAccumulator>();
        Parallel.ForEach(ciNames, new ParallelOptions { CancellationToken = ct }, ciName =>
        {
            var ciPath = Path.Combine(baseDir, ciName);
            if (!Directory.Exists(ciPath))
            {
                _logger.Info("BuildReportCard", $"CI folder missing, skipped: {ciName}");
                return;
            }
            var swCi = Stopwatch.StartNew();
            var acc = ParseCiFolder(ciName, ciPath, ct);
            swCi.Stop();
            _logger.Log(LogLevel.Information, "BuildReportCard",
                $"Parsed CI '{ciName}': {acc.FileCount} trx file(s), {acc.Total} tests", corr, swCi.ElapsedMilliseconds);
            ciAccumulators.Add(acc);
        });
        swParse.Stop();

        var accumulators = ciAccumulators
            .Where(a => a is not null)
            .OrderBy(a => a.CiName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalFiles = accumulators.Sum(a => a.FileCount);
        _logger.Log(LogLevel.Information, "BuildReportCard",
            $"Parsed {totalFiles} trx file(s) across {accumulators.Count} CI folder(s)", corr, swParse.ElapsedMilliseconds);

        var anyData = accumulators.Any(a => a.Total > 0);

        // ── CI results ────────────────────────────────────────────────────
        var cis = accumulators
            .Select(a => new CiResult
            {
                Name = a.CiName,
                Total = a.Total,
                Passed = a.Passed,
                Failed = a.Failed,
                Skipped = a.Skipped,
                PassRate = Rate(a.Passed, a.Total),
                Severity = SeverityFor(Rate(a.Passed, a.Total)),
            })
            .OrderByDescending(c => c.PassRate)
            .ToList();

        // ── Execution units (one row per use case / agent subfolder) ──────
        var agents = accumulators
            .SelectMany(acc => acc.Agents.Values.Select(ag =>
            {
                var total = ag.Passed + ag.Failed + ag.Skipped;
                var rate = Rate(ag.Passed, total);
                return new AgentResult
                {
                    UseCase = ag.UseCase,
                    AgentName = ag.Agent,
                    Ci = acc.CiName,
                    Passed = ag.Passed,
                    Failed = ag.Failed,
                    Skipped = ag.Skipped,
                    Duration = TimeSpan.FromTicks(ag.DurationTicks),
                    PassRate = rate,
                    Severity = SeverityFor(rate),
                };
            }))
            .OrderByDescending(a => a.Failed)
            .ThenBy(a => a.UseCase, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.AgentName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // ── Failures (classified + sorted by triage urgency) ──────────────
        var failures = BuildFailures(accumulators);
        var regressionCount = failures.Count(f => f.Pattern == FailurePatternKind.Regression);
        var flakyCount = failures.Count(f => f.Pattern == FailurePatternKind.Flaky);

        // ── PSR (dummy until Excel format is finalised) ───────────────────
        var psrs = BuildPsrCards();
        var psrFailures = psrs.Count(p => p.Outcome == PsrOutcome.Failed);
        var psrWarnings = psrs.Count(p => p.Outcome == PsrOutcome.PassedWithWarnings);
        var psrPass = psrs.Count(p => p.Outcome is PsrOutcome.Passed or PsrOutcome.PassedWithWarnings);

        // ── Totals + grade ────────────────────────────────────────────────
        var totalTests = accumulators.Sum(a => a.Total);
        var passedTests = accumulators.Sum(a => a.Passed);
        var failedTests = accumulators.Sum(a => a.Failed);
        var skippedTests = accumulators.Sum(a => a.Skipped);
        var passRate = Rate(passedTests, totalTests);
        var cisBelow90 = cis.Count(c => c.PassRate < 90.0);

        var grade = _grader.Compute(passRate, regressionCount, psrFailures, psrWarnings, cisBelow90);

        // ── Trend + vs-last delta ─────────────────────────────────────────
        // Persisting the trend touches the (possibly slow or locked) results
        // share. The card itself is already fully computed above, so bound the
        // write: if the share is unresponsive, degrade to a single-point trend
        // rather than letting a hung File I/O call freeze the whole report card.
        var currentRecord = new BuildSummaryRecord
        {
            BuildNumber = buildNumber,
            GeneratedUtc = generatedUtc,
            PassRate = passRate,
        };

        List<BuildSummaryRecord> history;
        var saveTask = Task.Run(() => _summaryStore.Save(currentRecord), ct);
        if (saveTask.Wait(TimeSpan.FromSeconds(5), ct))
        {
            history = saveTask.Result;
        }
        else
        {
            // Abandon the slow write (it is best-effort and self-recovers next run).
            history = new List<BuildSummaryRecord> { currentRecord };
            _logger.Warn("BuildReportCard",
                "Summary store write exceeded 5s (slow/locked results share); " +
                "trend limited to the current build for this load.");
        }

        var (trend, delta) = BuildTrend(history, buildNumber);
        _logger.Log(LogLevel.Information, "BuildReportCard",
            $"Post-processing complete: {cis.Count} CIs, {agents.Count} agents, {failures.Count} failures",
            corr, swTotal.ElapsedMilliseconds - swParse.ElapsedMilliseconds - swDiscover.ElapsedMilliseconds);

        // ── Window / timing ───────────────────────────────────────────────
        var earliest = accumulators.Where(a => a.Earliest.HasValue).Select(a => a.Earliest!.Value).DefaultIfEmpty().Min();
        var latest = accumulators.Where(a => a.Latest.HasValue).Select(a => a.Latest!.Value).DefaultIfEmpty().Max();
        var duration = TimeSpan.FromTicks(accumulators.Sum(a => a.DurationTicks));

        swTotal.Stop();
        _logger.Log(LogLevel.Information, "BuildReportCard",
            $"Aggregate DONE build='{buildNumber}': {totalTests} tests, {totalFiles} files, {cis.Count} CIs / {agents.Count} agents → Grade {grade.Letter} ({passRate:0.0}%)",
            corr, swTotal.ElapsedMilliseconds);

        return new BuildReportCard
        {
            BuildNumber = buildNumber,
            GeneratedUtc = generatedUtc,
            TriggeredBy = Environment.UserName,
            StartedUtc = earliest == default ? null : earliest,
            CompletedUtc = latest == default ? null : latest,
            Duration = duration,
            Grade = grade,
            TotalTests = totalTests,
            PassedTests = passedTests,
            FailedTests = failedTests,
            SkippedTests = skippedTests,
            PassRate = passRate,
            RegressionCount = regressionCount,
            FlakyCount = flakyCount,
            PsrErrorCount = psrFailures,
            DeltaVsLast = delta,
            PsrPassCount = psrPass,
            PsrTotalCount = psrs.Count,
            Cis = cis,
            Agents = agents,
            Psrs = psrs,
            Failures = failures,
            Trend = trend,
            HasData = anyData,
        };
    }

    // ── CI folder discovery ──────────────────────────────────────────────
    /// <summary>
    /// Returns the distinct CI folder names present under the build folder
    /// (PSR and dot/bookkeeping folders excluded). When the directory cannot
    /// be enumerated or contains no subfolders, falls back to the configured
    /// CiFolders list.
    /// </summary>
    private IReadOnlyList<string> DiscoverCiFolders(string baseDir)
    {
        try
        {
            var found = new DirectoryInfo(baseDir)
                .GetDirectories()
                .Select(d => d.Name)
                .Where(name => !name.StartsWith('.')
                               && !name.Equals(_config.PsrFolderName, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (found.Count > 0) return found;
        }
        catch (Exception ex)
        {
            _logger.Warn("BuildReportCard", $"Failed to discover CI folders under {baseDir}: {ex.Message}");
        }

        // Fallback: configured list, deduplicated.
        return _config.CiFolders
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── CI folder parsing (consolidate all nested subfolders) ────────────
    // Layout: {CI}\{UseCase}\[{Agent}\]*.trx — the immediate subfolder is a
    // use case; an optional nested subfolder is the agent/machine.
    private CiAccumulator ParseCiFolder(string ciName, string ciPath, CancellationToken ct)
    {
        var acc = new CiAccumulator { CiName = ciName };

        var useCaseDirs = Directory.GetDirectories(ciPath);
        if (useCaseDirs.Length > 0)
        {
            foreach (var ucDir in useCaseDirs)
            {
                ct.ThrowIfCancellationRequested();
                var useCase = Path.GetFileName(ucDir);

                var agentDirs = Directory.GetDirectories(ucDir);
                if (agentDirs.Length > 0)
                {
                    // Nested subfolders are agents/machines for this use case.
                    foreach (var agentDir in agentDirs)
                    {
                        var agentName = Path.GetFileName(agentDir);
                        var runs = ParseTrxRecursive(agentDir, acc);
                        AccumulateRuns(acc, useCase, agentName, ciName, runs);
                    }
                }
                else
                {
                    // No agent dimension — the use case is the execution unit.
                    var runs = ParseTrxRecursive(ucDir, acc);
                    AccumulateRuns(acc, useCase, "—", ciName, runs);
                }
            }
        }
        else
        {
            // Flat: TRX directly under the CI folder → single execution unit.
            var runs = ParseTrxRecursive(ciPath, acc);
            AccumulateRuns(acc, ciName, "—", ciName, runs);
        }

        return acc;
    }

    private List<TrxTestRun> ParseTrxRecursive(string folder, CiAccumulator acc)
    {
        var files = Directory.GetFiles(folder, "*.trx", SearchOption.AllDirectories);
        acc.FileCount += files.Length;
        var runs = new List<TrxTestRun>(files.Length);
        foreach (var f in files)
        {
            try { runs.Add(_parser.ParseFile(f)); }
            catch (Exception ex) { _logger.Warn("BuildReportCard", $"Failed to parse {Path.GetFileName(f)}: {ex.Message}"); }
        }
        return runs;
    }

    private static void AccumulateRuns(CiAccumulator acc, string useCase, string folderAgent, string ciName, List<TrxTestRun> runs)
    {
        foreach (var run in runs)
        {
            // Agent name comes from the TRX computerName; fall back to the
            // folder-derived agent, then "—" when neither is available.
            var agentName = !string.IsNullOrWhiteSpace(run.ComputerName)
                ? run.ComputerName
                : (!string.IsNullOrWhiteSpace(folderAgent) ? folderAgent : "—");

            var key = $"{useCase}\u0001{agentName}";
            if (!acc.Agents.TryGetValue(key, out var ag))
            {
                ag = new AgentAccumulator { UseCase = useCase, Agent = agentName };
                acc.Agents[key] = ag;
            }

            var passed = run.Passed;
            var failed = run.Failed + run.Timeout;
            var skipped = run.NotExecuted;

            acc.Passed += passed;
            acc.Failed += failed;
            acc.Skipped += skipped;
            acc.Total += run.Total;
            acc.DurationTicks += run.Duration.Ticks;

            ag.Passed += passed;
            ag.Failed += failed;
            ag.Skipped += skipped;
            ag.DurationTicks += run.Duration.Ticks;

            if (run.StartTime > DateTime.MinValue && (acc.Earliest is null || run.StartTime < acc.Earliest))
                acc.Earliest = run.StartTime;
            if (run.EndTime > DateTime.MinValue && (acc.Latest is null || run.EndTime > acc.Latest))
                acc.Latest = run.EndTime;

            foreach (var tc in run.TestCases)
            {
                if (tc.Outcome is "Failed" or "Timeout")
                {
                    var failAgent = !string.IsNullOrWhiteSpace(tc.ComputerName) ? tc.ComputerName : agentName;
                    acc.Failures.Add(new RawFailure(tc.TestName, ciName, failAgent));
                }
            }
        }
    }

    // ── Failures table ───────────────────────────────────────────────────
    private List<FailureEntry> BuildFailures(List<CiAccumulator> accumulators)
    {
        var all = accumulators.SelectMany(a => a.Failures).ToList();
        if (all.Count == 0) return [];

        var grouped = all
            .GroupBy(f => f.TestName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var ci = g.Select(x => x.Ci).First();
                var failedAgents = g.Select(x => x.Agent)
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var (kind, label) = ClassifyFailure(g.Key);

                return new FailureEntry
                {
                    TestName = g.Key,
                    Ci = ci,
                    FailedOnAgents = failedAgents,
                    Pattern = kind,
                    PatternLabel = label,
                    Owner = _owners.ResolveTeam(ci),
                    FirstSeen = FirstSeenFor(kind),
                };
            })
            .OrderBy(f => SortRank(f.Pattern))
            .ThenByDescending(f => f.FailedOnAgents.Count)
            .ThenBy(f => f.TestName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return grouped;
    }

    private (FailurePatternKind Kind, string Label) ClassifyFailure(string testName)
    {
        // FailurePatternAnalyzer mines build history under BuildResults:ResultsRootPath.
        // When that history is absent it returns "NEW FAILURE"/"—"; treat anything
        // unrecognised as a NEW failure so it still surfaces near the top.
        try
        {
            var label = _patternAnalyzer.GetCompactLabel(testName);
            var kind = label switch
            {
                _ when label.StartsWith("REGRESSION", StringComparison.OrdinalIgnoreCase) => FailurePatternKind.Regression,
                _ when label.StartsWith("CASCADING", StringComparison.OrdinalIgnoreCase) => FailurePatternKind.Cascading,
                _ when label.StartsWith("FLAKY", StringComparison.OrdinalIgnoreCase) => FailurePatternKind.Flaky,
                _ when label.StartsWith("CHRONIC", StringComparison.OrdinalIgnoreCase) => FailurePatternKind.Chronic,
                _ when label.StartsWith("RESOLVED", StringComparison.OrdinalIgnoreCase) => FailurePatternKind.Resolved,
                _ when label.StartsWith("NEW", StringComparison.OrdinalIgnoreCase) => FailurePatternKind.New,
                _ => FailurePatternKind.New,
            };
            var display = kind == FailurePatternKind.New ? "NEW" : label;
            return (kind, display);
        }
        catch (Exception ex)
        {
            _logger.Warn("BuildReportCard", $"Pattern classification failed for {testName}: {ex.Message}");
            return (FailurePatternKind.New, "NEW");
        }
    }

    private static int SortRank(FailurePatternKind kind) => kind switch
    {
        FailurePatternKind.Regression => 0,
        FailurePatternKind.New => 1,
        FailurePatternKind.Flaky => 2,
        FailurePatternKind.Cascading => 3,
        FailurePatternKind.Chronic => 4,
        _ => 5,
    };

    private static string FirstSeenFor(FailurePatternKind kind) => kind switch
    {
        FailurePatternKind.Regression or FailurePatternKind.New => "this build",
        FailurePatternKind.Flaky => "recent builds",
        FailurePatternKind.Chronic => "many builds ago",
        _ => "—",
    };

    // ── PSR placeholder cards ────────────────────────────────────────────
    private List<PsrResult> BuildPsrCards()
    {
        // PSR result files (Excel) are copied in later; until the schema is
        // finalised we render representative placeholder cards.
        return
        [
            new PsrResult { Name = "PSR-01", Scenario = "Refinery", Outcome = PsrOutcome.Pending, Duration = TimeSpan.Zero, Tags = 0, Throughput = "—", Errors = 0 },
            new PsrResult { Name = "PSR-02", Scenario = "Pharma", Outcome = PsrOutcome.Pending, Duration = TimeSpan.Zero, Tags = 0, Throughput = "—", Errors = 0 },
            new PsrResult { Name = "PSR-03", Scenario = "Power Grid", Outcome = PsrOutcome.Pending, Duration = TimeSpan.Zero, Tags = 0, Throughput = "—", Errors = 0 },
            new PsrResult { Name = "PSR-04", Scenario = "Water", Outcome = PsrOutcome.Pending, Duration = TimeSpan.Zero, Tags = 0, Throughput = "—", Errors = 0 },
            new PsrResult { Name = "PSR-05", Scenario = "Mining", Outcome = PsrOutcome.Pending, Duration = TimeSpan.Zero, Tags = 0, Throughput = "—", Errors = 0 },
        ];
    }

    // ── Trend ────────────────────────────────────────────────────────────
    private (List<TrendPoint> Trend, double? Delta) BuildTrend(List<BuildSummaryRecord> history, string currentBuild)
    {
        var window = Math.Max(1, _config.TrendWindow);
        var recent = history.TakeLast(window).ToList();

        var points = recent.Select((r, i) => new TrendPoint
        {
            Label = $".{i + 1}",
            PassRate = r.PassRate,
            IsCurrent = string.Equals(r.BuildNumber, currentBuild, StringComparison.OrdinalIgnoreCase),
        }).ToList();

        double? delta = null;
        if (recent.Count >= 2)
        {
            var avg = recent.Take(recent.Count - 1).Average(r => r.PassRate);
            delta = recent[^1].PassRate - avg;
        }
        return (points, delta);
    }

    // ── Helpers ──────────────────────────────────────────────────────────
    private static double Rate(int passed, int total) => total > 0 ? (double)passed / total * 100 : 0;

    private ReportSeverity SeverityFor(double passRate) =>
        passRate >= _config.GreenThreshold ? ReportSeverity.Pass :
        passRate >= _config.BlueThreshold ? ReportSeverity.Info :
        passRate >= _config.AmberThreshold ? ReportSeverity.Warn :
        ReportSeverity.Fail;

    private BuildReportCard EmptyCard(string buildNumber, DateTime generatedUtc) => new()
    {
        BuildNumber = buildNumber,
        GeneratedUtc = generatedUtc,
        TriggeredBy = Environment.UserName,
        Grade = _grader.Compute(0, 0, 0, 0, 0),
        Psrs = BuildPsrCards(),
        PsrTotalCount = 5,
        HasData = false,
    };

    // ── Private accumulators ─────────────────────────────────────────────
    private sealed class CiAccumulator
    {
        public string CiName = "";
        public int Total, Passed, Failed, Skipped;
        public int FileCount;
        public long DurationTicks;
        public DateTime? Earliest, Latest;
        public readonly Dictionary<string, AgentAccumulator> Agents = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<RawFailure> Failures = [];
    }

    private sealed class AgentAccumulator
    {
        public string UseCase = "";
        public string Agent = "";
        public int Passed, Failed, Skipped;
        public long DurationTicks;
    }

    private readonly record struct RawFailure(string TestName, string Ci, string Agent);
}
