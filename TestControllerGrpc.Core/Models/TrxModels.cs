namespace TestControllerGrpc.Models;

// ?? Hierarchical data models: Build ? UseCase ? TestResult ??????????

/// <summary>Top-level build node containing all use-case results.</summary>
public record BuildNode
{
    public string BuildNumber { get; init; } = "";
    public string RootPath { get; init; } = "";
    public DateTime? EarliestRun { get; init; }
    public DateTime? LatestRun { get; init; }
    public TimeSpan TotalDuration { get; init; }
    public int TotalTests { get; init; }
    public int PassedTests { get; init; }
    public int FailedTests { get; init; }
    public int TimeoutTests { get; init; }
    public int NotExecutedTests { get; init; }
    public double PassRate { get; init; }
    public HealthStatus Health { get; init; } = HealthStatus.Unknown;
    public List<UseCaseNode> UseCases { get; init; } = new();
    public List<TestResult> AllFailedTests { get; init; } = new();
}

/// <summary>A use-case (feature) aggregating one or more .trx file results.</summary>
public record UseCaseNode
{
    public string UseCaseName { get; init; } = "";
    /// <summary>The agent/machine that executed this use case (derived from TRX metadata).</summary>
    public string Agent { get; init; } = "";
    public TimeSpan Duration { get; init; }
    public int Total { get; init; }
    public int Passed { get; init; }
    public int Failed { get; init; }
    public int Timeout { get; init; }
    public int NotExecuted { get; init; }
    public double PassRate { get; init; }
    public List<TestResult> TestResults { get; init; } = new();
    public List<TestResult> FailedTests { get; init; } = new();
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
    /// <summary>Machine that executed the run, from the TRX computerName attribute.</summary>
    public string ComputerName { get; init; } = "";
    public List<TrxTestCase> TestCases { get; init; } = new();
}

public record TrxTestCase
{
    public string TestName { get; init; } = "";
    public string ClassName { get; init; } = "";
    public string Outcome { get; init; } = "";
    public TimeSpan Duration { get; init; }
    /// <summary>Machine that executed the test, from the TRX computerName attribute.</summary>
    public string ComputerName { get; init; } = "";
    public string? ErrorMessage { get; init; }
    public string? StackTrace { get; init; }
    public string? StdOut { get; init; }
    public string TrxFileName { get; init; } = "";
    public List<TestStep> ExecutionSteps { get; init; } = new();
    public string DebugTrace { get; init; } = "";
}

public enum HealthStatus { Unknown, Good, Warning, Bad }
