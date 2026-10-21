namespace TestControllerGrpc.Core.Fir;

/// <summary>One row of the test-data sheet: what the test was told to do at that row.</summary>
public sealed record FirDataStep(int Row, string Component, string Keyword, IReadOnlyList<string> Params)
{
    /// <summary>By convention the first parameter is the object the step acts on.</summary>
    public string? Object => Params.Count > 0 && !string.IsNullOrWhiteSpace(Params[0]) ? Params[0] : null;

    public string Display => string.Join(" | ", new[] { Component, Keyword }.Concat(Params).Where(p => !string.IsNullOrWhiteSpace(p)));
}

/// <summary>
/// Reads a test-data sheet. The implementation lives in TestController.Reporting because that is where
/// ClosedXML is referenced - Core stays dependency-light, and Reporting already references Core, so the
/// dependency can only run that way.
/// </summary>
public interface ITestDataSheetReader
{
    /// <summary>
    /// Rows keyed by sheet row number. Returns null when the workbook or sheet cannot be read - a missing
    /// workbook degrades the report, it never fails it.
    /// </summary>
    IReadOnlyDictionary<int, FirDataStep>? Read(string workbookPath, string sheetName, FirTestDataLayout layout);
}

/// <summary>
/// Where the columns are. Defaults suit a plain "component, keyword, params" sheet; a framework pack
/// overrides them so the reader needs no knowledge of any one sheet convention.
/// </summary>
public sealed record FirTestDataLayout
{
    public static FirTestDataLayout Default { get; } = new();

    public int ComponentColumn { get; init; } = 1;
    public int KeywordColumn { get; init; } = 2;
    public int ParamsColumn { get; init; } = 3;
    public string ParamSeparator { get; init; } = "|";

    /// <summary>Rows whose component matches are structural (region markers), not steps.</summary>
    public string? SkipRow { get; init; }

    /// <summary>Unwraps a decorated keyword, e.g. "&lt;modifyovvalue&gt;"; must capture a "kw" group.</summary>
    public string? KeywordPattern { get; init; }
}
