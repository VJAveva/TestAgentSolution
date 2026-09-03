using System.IO;
using ClosedXML.Excel;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Ado.Reporting;

/// <summary>ClosedXML implementation of <see cref="IChurnXlsxBuilder"/>.</summary>
public sealed class ChurnXlsxBuilder : IChurnXlsxBuilder
{
    public byte[] BuildXlsx(ChurnReport report, IReadOnlyList<ImpactedTestCaseMatch>? testCaseMatches = null)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Code Churn");

        ws.Cell(1, 1).Value = "Code Churn & Regression Scope";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Cell(2, 1).Value = $"{report.ScopeLabel}  \u00b7  {report.RangeText}  \u00b7  generated {report.GeneratedUtc.LocalDateTime:yyyy-MM-dd HH:mm}";
        ws.Cell(2, 1).Style.Font.FontColor = XLColor.Gray;

        var headers = new[]
        {
            "#", "Component", "Category", "Repository", "Branch", "Activity",
            "Risk", "Latest OK", "Summary", "Work Items", "Modified Files (.h/.cpp/.cs)", "Change Links",
            "Impacted Functionality", "Test Use Cases",
        };
        const int headerRow = 4;
        for (var c = 0; c < headers.Length; c++)
        {
            var cell = ws.Cell(headerRow, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#232140");
            cell.Style.Font.FontColor = XLColor.White;
        }

        var row = headerRow + 1;
        var n = 1;
        foreach (var r in report.Rows)
        {
            var wis = DistinctWorkItems(r);
            var summary = r.Changes.Count == 0 ? "" : r.Changes[0].Summary;
            var prCount = r.Changes.Count(c => c.Kind == RegressionChangeKind.PullRequest);
            var commitCount = r.Changes.Count(c => c.Kind == RegressionChangeKind.Commit);
            var autoCount = r.Changes.Count(c => c.Kind == RegressionChangeKind.Automated);
            var sourceFiles = r.FilesModified.Where(FileNoiseFilter.IsSourceFile).ToList();
            var changeLinks = r.Changes.Select(c => c.Url).Where(u => !string.IsNullOrEmpty(u)).Distinct().ToList();
            var firstWiUrl = wis.Select(w => w.Url).FirstOrDefault(u => !string.IsNullOrEmpty(u));

            ws.Cell(row, 1).Value = n;
            ws.Cell(row, 2).Value = r.Component;
            ws.Cell(row, 3).Value = r.Category.ToString();

            var repoCell = ws.Cell(row, 4);
            repoCell.Value = r.Repository ?? "";
            Link(repoCell, r.RepositoryUrl);

            ws.Cell(row, 5).Value = r.DefaultBranch ?? "";

            // Clubbed activity counts, one metric per line.
            ws.Cell(row, 6).Value =
                $"Files changed: {r.TotalFilesModified}\nPR's: {prCount}\nCommits: {commitCount}\nWI: {wis.Count}\nAuto: {autoCount}";

            ws.Cell(row, 7).Value = r.RiskTier ?? "";

            var okCell = ws.Cell(row, 8);
            okCell.Value = r.LatestSuccessfulBuild ?? "";
            Link(okCell, r.LatestSuccessfulBuildUrl);

            ws.Cell(row, 9).Value = summary;

            // Work items — one per line; the cell links to the first. The "Work Items" sheet has per-item links.
            var wiCell = ws.Cell(row, 10);
            wiCell.Value = wis.Count > 0 ? string.Join("\n", wis.Select(WorkItemLabel)) : "";
            Link(wiCell, firstWiUrl);

            // Modified source files only (.h/.cpp/.cs), full paths, one per line.
            ws.Cell(row, 11).Value = sourceFiles.Count > 0 ? string.Join("\n", sourceFiles) : "";

            var changeCell = ws.Cell(row, 12);
            changeCell.Value = changeLinks.Count > 0 ? string.Join("\n", changeLinks) : "";
            Link(changeCell, changeLinks.FirstOrDefault());

            ws.Cell(row, 13).Value = Join(r.RegressionAreas);
            ws.Cell(row, 14).Value = Join(r.UseCases);

            // Size the row so every wrapped line is visible (capped to avoid oversized rows).
            var maxLines = Math.Max(5, Math.Max(wis.Count, Math.Max(sourceFiles.Count, changeLinks.Count)));
            ws.Row(row).Height = Math.Min(maxLines, 30) * 15.0;

            row++;
            n++;
        }

        // Top-align + wrap every data cell so multi-line content stays readable.
        if (row > headerRow + 1)
        {
            var dataStyle = ws.Range(headerRow + 1, 1, row - 1, headers.Length).Style;
            dataStyle.Alignment.WrapText = true;
            dataStyle.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        }

        // Navigation: autofilter + freeze the header and the # / Component columns.
        ws.Range(headerRow, 1, Math.Max(headerRow, row - 1), headers.Length).SetAutoFilter();
        ws.SheetView.FreezeRows(headerRow);
        ws.SheetView.FreezeColumns(2);

        double[] widths = [5, 22, 12, 26, 14, 18, 12, 16, 42, 22, 55, 50, 28, 24];
        for (var c = 0; c < widths.Length; c++)
            ws.Column(c + 1).Width = widths[c];

        AddWorkItemsSheet(wb, report);
        AddChangesSheet(wb, report);

        if (testCaseMatches is { Count: > 0 })
            AddTestCaseMatchesSheet(wb, testCaseMatches);

        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Second worksheet: one row per Test Case matched to an impacted component, mirroring the grid's
    /// row-expand (impacted area, TC id + link, title, parent feature, match type, confidence, reason).
    /// </summary>
    private static void AddTestCaseMatchesSheet(XLWorkbook wb, IReadOnlyList<ImpactedTestCaseMatch> matches)
    {
        var ws = wb.Worksheets.Add("Impacted Test Cases");

        ws.Cell(1, 1).Value = "Impacted Test Cases";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Cell(2, 1).Value = $"{matches.Count} recommended test case(s) across impacted components";
        ws.Cell(2, 1).Style.Font.FontColor = XLColor.Gray;

        var headers = new[]
        {
            "Impacted Area", "TC ID", "TC Title", "Parent Feature ID", "Match Type", "Confidence", "Match Reason",
        };
        const int headerRow = 4;
        for (var c = 0; c < headers.Length; c++)
        {
            var cell = ws.Cell(headerRow, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#232140");
            cell.Style.Font.FontColor = XLColor.White;
        }

        var row = headerRow + 1;
        foreach (var m in matches)
        {
            ws.Cell(row, 1).Value = m.ImpactedArea;

            var idCell = ws.Cell(row, 2);
            idCell.Value = m.TestCaseId;
            if (!string.IsNullOrEmpty(m.TestCaseUrl)) idCell.SetHyperlink(new XLHyperlink(m.TestCaseUrl));

            ws.Cell(row, 3).Value = m.TestCaseTitle;
            ws.Cell(row, 4).Value = m.ParentFeatureId > 0 ? m.ParentFeatureId.ToString() : "does not exist";
            ws.Cell(row, 5).Value = m.MatchType;

            var confCell = ws.Cell(row, 6);
            confCell.Value = m.ConfidencePercent / 100.0;
            confCell.Style.NumberFormat.Format = "0%";

            var reasonCell = ws.Cell(row, 7);
            reasonCell.Value = m.MatchReason;
            reasonCell.Style.Alignment.WrapText = true;
            reasonCell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;

            row++;
        }

        if (row > headerRow + 1)
        {
            var dataStyle = ws.Range(headerRow + 1, 1, row - 1, headers.Length).Style;
            dataStyle.Alignment.WrapText = true;
            dataStyle.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        }

        ws.Range(headerRow, 1, Math.Max(headerRow, row - 1), headers.Length).SetAutoFilter();
        ws.SheetView.FreezeRows(headerRow);

        double[] widths = [34, 10, 52, 16, 12, 12, 70];
        for (var c = 0; c < widths.Length; c++)
            ws.Column(c + 1).Width = widths[c];
    }

    /// <summary>Dedicated sheet: one row per (component, work item) so every ID is an individually clickable link.</summary>
    private static void AddWorkItemsSheet(XLWorkbook wb, ChurnReport report)
    {
        var ws = wb.Worksheets.Add("Work Items");
        ws.Cell(1, 1).Value = "Work Items";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;

        var headers = new[] { "Component", "Type", "ID", "Title" };
        const int headerRow = 3;
        WriteHeaders(ws, headerRow, headers);

        var row = headerRow + 1;
        foreach (var r in report.Rows)
            foreach (var w in DistinctWorkItems(r))
            {
                ws.Cell(row, 1).Value = r.Component;
                ws.Cell(row, 2).Value = w.Kind.ToString();
                var idCell = ws.Cell(row, 3);
                idCell.Value = w.Id;
                Link(idCell, w.Url);
                ws.Cell(row, 4).Value = w.Title;
                row++;
            }

        FinishSheet(ws, headerRow, row, headers.Length, [24, 12, 12, 90]);
    }

    /// <summary>Dedicated sheet: one row per (component, change) so every PR/commit is an individually clickable link.</summary>
    private static void AddChangesSheet(XLWorkbook wb, ChurnReport report)
    {
        var ws = wb.Worksheets.Add("Changes");
        ws.Cell(1, 1).Value = "Changes";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;

        var headers = new[] { "Component", "Kind", "When", "Link", "Summary" };
        const int headerRow = 3;
        WriteHeaders(ws, headerRow, headers);

        var row = headerRow + 1;
        foreach (var r in report.Rows)
            foreach (var ch in r.Changes)
            {
                ws.Cell(row, 1).Value = r.Component;
                ws.Cell(row, 2).Value = ch.Kind.ToString();
                var whenCell = ws.Cell(row, 3);
                whenCell.Value = ch.ObservedUtc.LocalDateTime;
                whenCell.Style.NumberFormat.Format = "yyyy-mm-dd hh:mm";
                var linkCell = ws.Cell(row, 4);
                linkCell.Value = string.IsNullOrEmpty(ch.Url) ? "" : "open";
                Link(linkCell, ch.Url);
                ws.Cell(row, 5).Value = ch.Summary;
                row++;
            }

        FinishSheet(ws, headerRow, row, headers.Length, [24, 12, 18, 10, 90]);
    }

    private static void WriteHeaders(IXLWorksheet ws, int headerRow, string[] headers)
    {
        for (var c = 0; c < headers.Length; c++)
        {
            var cell = ws.Cell(headerRow, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#232140");
            cell.Style.Font.FontColor = XLColor.White;
        }
    }

    private static void FinishSheet(IXLWorksheet ws, int headerRow, int nextRow, int cols, double[] widths)
    {
        if (nextRow > headerRow + 1)
        {
            var style = ws.Range(headerRow + 1, 1, nextRow - 1, cols).Style;
            style.Alignment.WrapText = true;
            style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            ws.Range(headerRow, 1, nextRow - 1, cols).SetAutoFilter();
        }
        ws.SheetView.FreezeRows(headerRow);
        for (var c = 0; c < widths.Length; c++)
            ws.Column(c + 1).Width = widths[c];
    }

    private static IReadOnlyList<RegressionWorkItemRef> DistinctWorkItems(SubsystemRow r) =>
        r.Changes.SelectMany(c => c.WorkItems).GroupBy(w => w.Id).Select(g => g.First()).ToList();

    private static string WorkItemLabel(RegressionWorkItemRef w) => w.Kind switch
    {
        RegressionWorkItemKind.Bug => $"Bug {w.Id}",
        RegressionWorkItemKind.Story => $"User Story {w.Id}",
        RegressionWorkItemKind.Feature => $"Feature {w.Id}",
        RegressionWorkItemKind.Ims => $"IMS {w.Id}",
        _ => $"Work Item {w.Id}",
    };

    // Excel allows one hyperlink per cell — style it blue/underlined so multi-line cells read as links.
    private static void Link(IXLCell cell, string? url)
    {
        if (string.IsNullOrEmpty(url))
            return;
        cell.SetHyperlink(new XLHyperlink(url));
        cell.Style.Font.FontColor = XLColor.FromHtml("#0563C1");
        cell.Style.Font.Underline = XLFontUnderlineValues.Single;
    }

    private static string Join(IReadOnlyList<string>? items) =>
        items is { Count: > 0 } ? string.Join("; ", items) : "";
}
