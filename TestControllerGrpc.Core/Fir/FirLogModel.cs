namespace TestControllerGrpc.Core.Fir;

/// <summary>One keyword line: "[LineNo:8622-File.cs][ts] Failed: (DeployOperation) message".</summary>
public sealed record FirLogStep(
    string Level,
    string Keyword,
    string Message,
    DateTime? Timestamp = null,
    string? SourceFile = null,
    int? SourceLine = null)
{
    public string? SourceFileLine => SourceFile is null
        ? null
        : SourceLine is null ? SourceFile : $"{SourceFile}:{SourceLine}";

    /// <summary>The object a keyword acted on - by convention the first token of the message.</summary>
    public string? Subject
    {
        get
        {
            var trimmed = Message.TrimStart();
            if (trimmed.Length == 0) return null;
            var end = trimmed.IndexOfAny([' ', '\t', ':']);
            return end <= 0 ? trimmed : trimmed[..end];
        }
    }
}

/// <summary>A product line saying an object could not be acted on.</summary>
public sealed record FirProductError(string Object, string Reason);

/// <summary>A product completion count. The banner may say SUCCESS while this says nothing was done.</summary>
public sealed record FirDeployCount(int Done, int Total)
{
    public bool IsShortfall => Done < Total;
}

public sealed record FirLogRow
{
    public int Number { get; init; }
    public DateTime? Start { get; init; }
    public DateTime? End { get; init; }
    public IReadOnlyList<FirLogStep> Steps { get; init; } = [];
    public IReadOnlyList<FirProductError> ProductErrors { get; init; } = [];
    public IReadOnlyList<FirDeployCount> Counts { get; init; } = [];

    /// <summary>
    /// Every line of the row. Needed because a cascade's link to the root often sits in a product line
    /// that is neither a keyword step nor an error - "... hosted by platform REP1_...".
    /// </summary>
    public string RawText { get; init; } = "";

    public TimeSpan? Duration => Start is { } s && End is { } e && e >= s ? e - s : null;
}

/// <summary>The counts the framework prints when it decides the verdict.</summary>
public sealed record FirReasonCounts(int Validation, int LoggerErrors, int LoggerWarnings);

public sealed record FirParsedLog
{
    public IReadOnlyList<FirLogRow> Rows { get; init; } = [];
    public FirReasonCounts? Reason { get; init; }
    public string? DataWorkbook { get; init; }
    public string? DataSheet { get; init; }
}
