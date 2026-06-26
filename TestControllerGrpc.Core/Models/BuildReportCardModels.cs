namespace TestControllerGrpc.Models;

// ── Build Report Card aggregated model ───────────────────────────────
// One consolidated, read-only assessment of a single build run.
// Populated by BuildReportAggregator from TRX files under the
// ReportResults root (one CI per top-level folder, subfolders merged).

/// <summary>Outcome of a single PSR (Production Scenario Run).</summary>
public enum PsrOutcome { Passed, PassedWithWarnings, Failed, Pending }

/// <summary>Failure pattern classification, ordered by triage urgency.</summary>
public enum FailurePatternKind { Regression, New, Flaky, Chronic, Cascading, Resolved, Unknown }

/// <summary>Severity bucket used to drive colour coding in the view.</summary>
public enum ReportSeverity { Pass, Info, Warn, Fail }

/// <summary>Per-CI (Configuration Item) aggregated result. One row/card per CI folder.</summary>
public sealed record CiResult
{
    public string Name { get; init; } = "";
    public int Total { get; init; }
    public int Passed { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
    public double PassRate { get; init; }
    public ReportSeverity Severity { get; init; }
}

/// <summary>Per-execution-unit aggregated result. One row per use case / agent subfolder.</summary>
public sealed record AgentResult
{
    /// <summary>The use case (immediate subfolder under the CI).</summary>
    public string UseCase { get; init; } = "";
    /// <summary>The agent/machine (nested subfolder under the use case); "—" when absent.</summary>
    public string AgentName { get; init; } = "";
    public string Ci { get; init; } = "";
    public int Passed { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
    public int Total => Passed + Failed + Skipped;
    public TimeSpan Duration { get; init; }
    public double PassRate { get; init; }
    public ReportSeverity Severity { get; init; }
}

/// <summary>One Production Scenario Run result (dummy until PSR excel format is finalised).</summary>
public sealed record PsrResult
{
    public string Name { get; init; } = "";
    public string Scenario { get; init; } = "";
    public PsrOutcome Outcome { get; init; }
    public TimeSpan Duration { get; init; }
    public int Tags { get; init; }
    public string Throughput { get; init; } = "";
    public int Errors { get; init; }
    public int Warnings { get; init; }
    public string? ErrorDetail { get; init; }
}

/// <summary>One prioritized failure row in the Top Failures table.</summary>
public sealed record FailureEntry
{
    public string TestName { get; init; } = "";
    public string Ci { get; init; } = "";
    public IReadOnlyList<string> FailedOnAgents { get; init; } = [];
    public FailurePatternKind Pattern { get; init; }
    public string PatternLabel { get; init; } = "";
    public string Owner { get; init; } = "";
    public string FirstSeen { get; init; } = "";
}

/// <summary>One bar in the last-N-builds pass-rate trend strip.</summary>
public sealed record TrendPoint
{
    public string Label { get; init; } = "";
    public double PassRate { get; init; }
    public bool IsCurrent { get; init; }
}

/// <summary>The computed letter grade plus an explainable breakdown.</summary>
public sealed record BuildGradeResult
{
    public string Letter { get; init; } = "";
    public double Score { get; init; }
    public double BasePassRate { get; init; }
    public string Verdict { get; init; } = "";
    public IReadOnlyList<string> BreakdownLines { get; init; } = [];
    public ReportSeverity Severity { get; init; }
}

/// <summary>The full aggregated report card for one build.</summary>
public sealed record BuildReportCard
{
    public string BuildNumber { get; init; } = "";
    public DateTime GeneratedUtc { get; init; }
    public string TriggeredBy { get; init; } = "";
    public DateTime? StartedUtc { get; init; }
    public DateTime? CompletedUtc { get; init; }
    public TimeSpan Duration { get; init; }

    public BuildGradeResult Grade { get; init; } = new();

    public int TotalTests { get; init; }
    public int PassedTests { get; init; }
    public int FailedTests { get; init; }
    public int SkippedTests { get; init; }
    public double PassRate { get; init; }

    public int RegressionCount { get; init; }
    public int FlakyCount { get; init; }
    public int PsrErrorCount { get; init; }
    public double? DeltaVsLast { get; init; }

    public int PsrPassCount { get; init; }
    public int PsrTotalCount { get; init; }

    public IReadOnlyList<CiResult> Cis { get; init; } = [];
    public IReadOnlyList<AgentResult> Agents { get; init; } = [];
    public IReadOnlyList<PsrResult> Psrs { get; init; } = [];
    public IReadOnlyList<FailureEntry> Failures { get; init; } = [];
    public IReadOnlyList<TrendPoint> Trend { get; init; } = [];

    /// <summary>False when no TRX data was found under the ReportResults root.</summary>
    public bool HasData { get; init; }
}

/// <summary>Persisted one-line summary of a build, used to compute trend and vs-last delta.</summary>
public sealed record BuildSummaryRecord
{
    public string BuildNumber { get; init; } = "";
    public DateTime GeneratedUtc { get; init; }
    public double PassRate { get; init; }
}
