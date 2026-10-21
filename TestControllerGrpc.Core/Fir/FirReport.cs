namespace TestControllerGrpc.Core.Fir;

/// <summary>How the test reported its failure, as far as the TRX alone can tell.</summary>
public enum FirFailureType
{
    Unknown,
    Assertion,
    Exception,
    Timeout,

    /// <summary>
    /// An assertion raised by cleanup over accumulated results. The frame that threw is never the cause -
    /// the step that broke is earlier in the output.
    /// </summary>
    SummaryCheck,
}

/// <summary>Where the failing frame sits relative to the test, by method name and declaring type only.</summary>
public enum FirFrameKind
{
    Unknown,
    TestCode,
    Init,
    Cleanup,
    Library,
}

/// <summary>The first frame of the stack trace that is not test-harness plumbing.</summary>
public sealed record FirFailingFrame(
    FirFrameKind Kind,
    string Method,
    string DeclaringType = "",
    string? SourceFile = null,
    int? SourceLine = null)
{
    /// <summary>"TestBase.cs:142" - the leaf name only, so a build-machine path never reaches the UI.</summary>
    public string? SourceFileLine => SourceFile is null
        ? null
        : SourceLine is null
            ? Path.GetFileName(SourceFile)
            : $"{Path.GetFileName(SourceFile)}:{SourceLine}";
}

/// <summary>
/// What one failed test's report holds. Phase 1 fills the generic fields; rule packs add rows, categories
/// and a root step on top without changing these.
/// </summary>
public sealed record FirReport
{
    public string Test { get; init; } = "";
    public string Outcome { get; init; } = "";
    public TimeSpan Duration { get; init; }
    public string Computer { get; init; } = "";

    /// <summary>Which layers produced this report, generic first.</summary>
    public IReadOnlyList<string> RulePacks { get; init; } = [];

    public FirFailureType FailureType { get; init; }
    public FirFailingFrame? FailingFrame { get; init; }

    /// <summary>One sentence naming what failed and where.</summary>
    public string Verdict { get; init; } = "";

    /// <summary>The plain-text report a reader sees first: cause, effect, and which sources were used.</summary>
    public string Summary { get; init; } = "";

    /// <summary>The counts the framework printed when it decided the verdict. Needs a framework pack.</summary>
    public FirReasonCounts? Reason { get; init; }

    /// <summary>The first failing row. Null when no pack could read rows out of the trace.</summary>
    public FirReportRow? Root { get; init; }

    /// <summary>Root, masked, cascade, recovered and supporting passed rows, in log order.</summary>
    public IReadOnlyList<FirReportRow> Rows { get; init; } = [];

    public IReadOnlyList<FirCategoryCount> Categories { get; init; } = [];

    /// <summary>Count of failing keyword steps; should agree with the framework's own validation count.</summary>
    public int FailedSteps { get; init; }

    public string? DataWorkbook { get; init; }
    public string? DataSheet { get; init; }

    /// <summary>Which copy of the workbook the rows came from.</summary>
    public FirDataSource DataSource { get; init; }

    /// <summary>The workbook file actually read, as opposed to the path the log named.</summary>
    public string? ResolvedWorkbook { get; init; }

    /// <summary>Identifies the inputs this report was built from; see <see cref="FirCacheKey"/>.</summary>
    public string? CacheKey { get; init; }

    /// <summary>Set when a workbook was found but rejected, so the reader knows why there are no steps.</summary>
    public string? DataWarning { get; init; }

    /// <summary>A gap in the evidence, stated rather than guessed at.</summary>
    public string? OpenPoint { get; init; }

    /// <summary>First error-level line in the output, harness noise excluded. Masked.</summary>
    public string? FirstErrorLine { get; init; }

    /// <summary>Harness deployment warnings counted and dropped.</summary>
    public int NoiseLines { get; init; }

    /// <summary>Secrets masked across every input stream, so the UI can warn that the log leaked one.</summary>
    public int SecretsFound { get; init; }
}
