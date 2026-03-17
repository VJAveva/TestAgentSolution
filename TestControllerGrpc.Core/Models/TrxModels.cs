namespace TestControllerGrpc.Models;

// ?? Hierarchical data models: Build ? UseCase ? TestResult ??????????

/// <summary>Top-level build node containing all use-case results.</summary>
public record BuildNode
{
    public string BuildNumber { get; init; } = "";
    public string RootPath { get; init; } = "";
    public DateTime? EarliestRun { get; init; }
    public DateTime? LatestRun { get; init; }
    public TimeSpan TotalDuration => TimeSpan.FromTicks(UseCases.Sum(u => u.Duration.Ticks));
    public int TotalTests => UseCases.Sum(u => u.Total);
    public int PassedTests => UseCases.Sum(u => u.Passed);
    public int FailedTests => UseCases.Sum(u => u.Failed);
    public int TimeoutTests => UseCases.Sum(u => u.Timeout);
    public int NotExecutedTests => UseCases.Sum(u => u.NotExecuted);
    public double PassRate => TotalTests > 0 ? (double)PassedTests / TotalTests * 100 : 0;
    public HealthStatus Health { get; init; } = HealthStatus.Unknown;
    public List<UseCaseNode> UseCases { get; init; } = new();
    public List<TestResult> AllFailedTests => UseCases.SelectMany(u => u.TestResults.Where(t => t.Outcome == "Failed")).ToList();
}

/// <summary>A use-case (feature) aggregating one or more .trx file results.</summary>
public record UseCaseNode
{
    public string UseCaseName { get; init; } = "";
    public TimeSpan Duration { get; init; }
    public int Total { get; init; }
    public int Passed { get; init; }
    public int Failed { get; init; }
    public int Timeout { get; init; }
    public int NotExecuted { get; init; }
    public double PassRate => Total > 0 ? (double)Passed / Total * 100 : 0;
    public List<TestResult> TestResults { get; init; } = new();
    public List<TestResult> FailedTests => TestResults.Where(t => t.Outcome == "Failed").ToList();
}

/// <summary>Individual test result from a .trx file.</summary>
public record TestResult
{
    public string TestName { get; init; } = "";
    public string ClassName { get; init; } = "";
    public string Outcome { get; init; } = "";
    public TimeSpan Duration { get; init; }
    public string? ErrorMessage { get; init; }
    public string? StackTrace { get; init; }
    public string? StdOut { get; init; }
    public string TrxFileName { get; init; } = "";
    public string UseCaseName { get; init; } = "";
    public List<TestStep> ExecutionSteps { get; init; } = new();
    public string DebugTrace { get; init; } = "";
}

/// <summary>Represents a single execution step from TRX InnerResults.</summary>
public record TestStep
{
    public string StepName { get; init; } = "";
    public string Outcome { get; init; } = "";
    public TimeSpan Duration { get; init; }
    public string StdOut { get; init; } = "";
    public string? ErrorMessage { get; init; }
}

// ?? Raw TRX parse output (used internally by parser) ????????????????

public record TrxTestRun
{
    public string FileName { get; init; } = "";
    public string FeatureName { get; init; } = "";
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public TimeSpan Duration { get; init; }
    public int Total { get; init; }
    public int Passed { get; init; }
    public int Failed { get; init; }
    public int Timeout { get; init; }
    public int NotExecuted { get; init; }
    public List<TrxTestCase> TestCases { get; init; } = new();
}

public record TrxTestCase
{
    public string TestName { get; init; } = "";
    public string ClassName { get; init; } = "";
    public string Outcome { get; init; } = "";
    public TimeSpan Duration { get; init; }
    public string? ErrorMessage { get; init; }
    public string? StackTrace { get; init; }
    public string? StdOut { get; init; }
    public string TrxFileName { get; init; } = "";
    public List<TestStep> ExecutionSteps { get; init; } = new();
    public string DebugTrace { get; init; } = "";
}

public enum HealthStatus { Unknown, Good, Warning, Bad }
