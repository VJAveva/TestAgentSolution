namespace TestControllerGrpc.Ado.Reporting;

using TestControllerGrpc.Core.Impact;

/// <summary>
/// Renders a <see cref="ChurnReport"/> to a formatted, navigable .xlsx workbook (frozen header, autofilter,
/// sized columns, clickable Azure links). Lives in its own project so ClosedXML stays out of Core (and the agent).
/// </summary>
public interface IChurnXlsxBuilder
{
    /// <summary>
    /// Builds the workbook. When <paramref name="testCaseMatches"/> is non-empty, a second
    /// "Impacted Test Cases" worksheet is appended (one row per matched Test Case).
    /// </summary>
    byte[] BuildXlsx(ChurnReport report, IReadOnlyList<ImpactedTestCaseMatch>? testCaseMatches = null);
}
