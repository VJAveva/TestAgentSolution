using TestControllerGrpc.Models;

namespace TestControllerGrpc.Core.Fir;

/// <summary>
/// One failed test as the analyzer sees it. The TRX alone must be enough: older builds kept no sidecar
/// log, so <see cref="LogText"/> is an extra, never a requirement.
/// </summary>
public sealed record FirTrxInput
{
    public string TestName { get; init; } = "";
    public string ClassName { get; init; } = "";
    public string Outcome { get; init; } = "";
    public TimeSpan Duration { get; init; }
    public string? ErrorMessage { get; init; }
    public string? StackTrace { get; init; }
    public string? StdOut { get; init; }
    public string? DebugTrace { get; init; }
    public string Computer { get; init; } = "";

    /// <summary>From the TRX TestMethod codeBase; used only to select rule packs.</summary>
    public string TestAssembly { get; init; } = "";

    /// <summary>Folder holding the TRX; the first place a test-data workbook is looked for.</summary>
    public string? ResultsFolder { get; init; }

    public string? LogText { get; init; }

    public static FirTrxInput FromTestResult(TestResult result, string computer = "", string? logText = null) => new()
    {
        TestName = result.TestName,
        ClassName = result.ClassName,
        Outcome = result.Outcome,
        Duration = result.Duration,
        ErrorMessage = result.ErrorMessage,
        StackTrace = result.StackTrace,
        StdOut = result.StdOut,
        DebugTrace = string.IsNullOrEmpty(result.DebugTrace) ? null : result.DebugTrace,
        Computer = computer,
        LogText = logText,
    };

    /// <summary>Output in TRX document order - StdOut then DebugTrace - with the optional log last.</summary>
    public IEnumerable<string> OutputSections()
    {
        if (!string.IsNullOrEmpty(StdOut)) yield return StdOut;
        if (!string.IsNullOrEmpty(DebugTrace)) yield return DebugTrace;
        if (!string.IsNullOrEmpty(LogText)) yield return LogText;
    }

    /// <summary>Every stream a secret could hide in, including the failure text itself.</summary>
    public IEnumerable<string> AllText()
    {
        if (!string.IsNullOrEmpty(ErrorMessage)) yield return ErrorMessage;
        if (!string.IsNullOrEmpty(StackTrace)) yield return StackTrace;
        foreach (var section in OutputSections()) yield return section;
    }
}
