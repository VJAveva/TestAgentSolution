using TestControllerGrpc.Models;

namespace TestControllerGrpc.Ado.Reporting;

/// <summary>
/// Immutable snapshot of the current regression scope, fed to the summarizer and the CSV/HTML
/// exporters. Built from the rows currently shown in the grid so exports match what the user sees.
/// </summary>
public sealed record ChurnReport(
    string ScopeLabel,
    string RangeText,
    DateOnly From,
    DateOnly To,
    DateTimeOffset GeneratedUtc,
    IReadOnlyList<SubsystemRow> Rows);

/// <summary>A computed, natural-language digest of a <see cref="ChurnReport"/>.</summary>
public sealed record ChurnSummary(
    string Headline,
    IReadOnlyList<string> Highlights,
    string Narrative);

/// <summary>Produces a deterministic, offline natural-language summary of a churn report.</summary>
public interface IChurnSummarizer
{
    ChurnSummary Summarize(ChurnReport report);

    /// <summary>A concise, component-scoped summary (used in the grid's expanded row detail).</summary>
    string SummarizeComponent(SubsystemRow row);
}

/// <summary>Renders a churn report to shareable CSV / HTML.</summary>
public interface IChurnReportBuilder
{
    /// <param name="policy">
    /// Feature roll-up and exclusion accounting. Optional so existing callers are unaffected; when supplied,
    /// the renderer adds the Feature grouping and the exclusion footer without filtering anything itself.
    /// </param>
    string BuildCsv(ChurnReport report, CodeChurnReportModel? policy = null);

    string BuildHtml(ChurnReport report, CodeChurnReportModel? policy = null);
}
