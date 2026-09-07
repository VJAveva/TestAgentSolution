using System.IO;
using ClosedXML.Excel;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Ado.Reporting;

/// <summary>ClosedXML implementation of <see cref="IChurnXlsxBuilder"/>.</summary>
public sealed class ChurnXlsxBuilder : IChurnXlsxBuilder
{
    public byte[] BuildXlsx(
        ChurnReport report,
        IReadOnlyList<ImpactedTestCaseMatch>? testCaseMatches = null,
        CodeChurnReportModel? policy = null)
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
            "Work Items", "Modified Files (.h/.cpp/.cs)",
            "Impacted Functionality", "Test Use Cases", "Build Status", "Manual Test Cases",
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
            var prCount = r.Changes.Count(c => c.Kind == RegressionChangeKind.PullRequest);
            var commitCount = r.Changes.Count(c => c.Kind == RegressionChangeKind.Commit);
            var autoCount = r.Changes.Count(c => c.Kind == RegressionChangeKind.Automated);
            var sourceFiles = r.FilesModified.Where(FileNoiseFilter.IsSourceFile).ToList();
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

            // Work items grouped Bugs / IMS / User Stories, each under its own heading. A single Excel cell
            // can hold only one hyperlink, so the per-item links live on the "Work Items" sheet.
            var groups = GroupWorkItems(r);
            var wiCell = ws.Cell(row, 7);
            wiCell.Value = groups.Count == 0
                ? ""
                : string.Join("\n\n", groups.Select(g =>
                    $"{g.Heading}\n{new string('-', g.Heading.Length)}\n" +
                    string.Join("\n", g.Items.Select(WorkItemLabel))));
            Link(wiCell, firstWiUrl);
            var wiLines = groups.Sum(g => g.Items.Count + 3);

            // Modified source files only (.h/.cpp/.cs), full paths, one per line.
            ws.Cell(row, 8).Value = sourceFiles.Count > 0 ? string.Join("\n", sourceFiles) : "";

            ws.Cell(row, 9).Value = Join(r.RegressionAreas);
            ws.Cell(row, 10).Value = Join(r.UseCases);

            // Build status: latest OK build number, linked, with its result underneath.
            var statusCell = ws.Cell(row, 11);
            var result = r.RiskTier ?? "";
            var build = r.LatestSuccessfulBuild ?? "";
            statusCell.Value = string.IsNullOrEmpty(build) ? result : $"{build}\n{Capitalize(result)}";
            Link(statusCell, r.LatestSuccessfulBuildUrl);
            if (!string.IsNullOrEmpty(result))
            {
                var succeeded = result.Equals("succeeded", StringComparison.OrdinalIgnoreCase);
                statusCell.Style.Font.FontColor = succeeded ? XLColor.FromHtml("#2E7D32") : XLColor.FromHtml("#C62828");
                statusCell.Style.Font.Bold = true;
            }

            var manualCell = ws.Cell(row, 12);
            manualCell.Value = string.Join("\n", r.ManualSuites.Select(s =>
                string.IsNullOrWhiteSpace(s.Title) ? $"Test Case {s.SuiteId}" : $"Test Case {s.SuiteId} - {s.Title}"));
            Link(manualCell, r.ManualSuites.Select(s => s.Url).FirstOrDefault(u => !string.IsNullOrEmpty(u)));

            // Size the row so every wrapped line is visible (capped to avoid oversized rows).
            var maxLines = Math.Max(5, Math.Max(wiLines, Math.Max(sourceFiles.Count, r.ManualSuites.Count)));
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

        double[] widths = [5, 22, 12, 26, 14, 18, 26, 55, 28, 24, 18, 34];
        for (var c = 0; c < widths.Length; c++)
            ws.Column(c + 1).Width = widths[c];

        AddWorkItemsSheet(wb, report);
        AddChangesSheet(wb, report);

        if (testCaseMatches is { Count: > 0 })
            AddTestCaseMatchesSheet(wb, testCaseMatches);

        if (policy is not null)
            AddFeaturesSheet(wb, policy);

        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        return stream.ToArray();
    }

    /// <summary>Feature roll-up plus the exclusion footer, which every output is required to carry.</summary>
    private static void AddFeaturesSheet(XLWorkbook wb, CodeChurnReportModel policy)
    {
        var ws = wb.Worksheets.Add("Features");

        var headers = new[] { "Feature", "Work Items", "Changes", "Files" };
        for (var c = 0; c < headers.Length; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#232140");
            cell.Style.Font.FontColor = XLColor.White;
        }

        var row = 2;
        foreach (CodeChurnFeatureGroup group in policy.GroupsForDisplay)
        {
            ws.Cell(row, 1).Value = group.FeatureId is { } id ? $"{id} \u00b7 {group.FeatureTitle}" : group.FeatureTitle;
            ws.Cell(row, 2).Value = group.WorkItemCount;
            ws.Cell(row, 3).Value = group.ChangeCount;
            ws.Cell(row, 4).Value = group.FileCount;
            if (group.IsOrphanBucket)
                ws.Row(row).Style.Font.FontColor = XLColor.Gray;
            row++;
        }

        row++;
        ws.Cell(row, 1).Value = policy.ExclusionFooter;
        ws.Cell(row, 1).Style.Font.Italic = true;
        ws.Cell(row, 1).Style.Font.FontColor = XLColor.Gray;

        double[] widths = [46, 12, 12, 10];
        for (var c = 0; c < widths.Length; c++)
            ws.Column(c + 1).Width = widths[c];

        ws.SheetView.FreezeRows(1);
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
            "Linked Work Items",
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

            // Why this test case is in scope, in the reviewer's vocabulary: the Bug/IMS/Story it verifies.
            var linkedCell = ws.Cell(row, 8);
            IReadOnlyList<RegressionWorkItemRef> linked = m.LinkedWorkItems ?? [];
            linkedCell.Value = string.Join("\n", linked.Select(WorkItemLabel));
            Link(linkedCell, linked.Select(w => w.Url).FirstOrDefault(u => !string.IsNullOrEmpty(u)));

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

        double[] widths = [34, 10, 52, 16, 12, 12, 70, 40];
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

        var headers = new[] { "Component", "Category", "Type", "ID", "Title" };
        const int headerRow = 3;
        WriteHeaders(ws, headerRow, headers);

        var row = headerRow + 1;
        foreach (var r in report.Rows)
            foreach (var (heading, items) in GroupWorkItems(r))
                foreach (var w in items)
                {
                    ws.Cell(row, 1).Value = r.Component;
                    ws.Cell(row, 2).Value = heading;
                    ws.Cell(row, 3).Value = w.Kind.ToString();
                    var idCell = ws.Cell(row, 4);
                    idCell.Value = w.Id;
                    Link(idCell, w.Url);
                    ws.Cell(row, 5).Value = w.Title;
                    row++;
                }

        FinishSheet(ws, headerRow, row, headers.Length, [24, 14, 12, 12, 90]);
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
        r.Changes.SelectMany(c => c.WorkItems)
            .Where(w => !IsReportTask(w) && w.Kind != RegressionWorkItemKind.Feature)
            .GroupBy(w => w.Id).Select(g => g.First()).ToList();

    private static bool IsReportTask(RegressionWorkItemRef workItem) =>
        string.Equals(workItem.WorkItemType, "Task", StringComparison.OrdinalIgnoreCase);

    /// <summary>Work items grouped for display, in the required order: Bugs, IMS, User Stories, then anything else.</summary>
    private static IReadOnlyList<(string Heading, IReadOnlyList<RegressionWorkItemRef> Items)> GroupWorkItems(SubsystemRow r)
    {
        var all = DistinctWorkItems(r);
        var groups = new List<(string, IReadOnlyList<RegressionWorkItemRef>)>();

        void Add(string heading, Func<RegressionWorkItemRef, bool> match)
        {
            var items = all.Where(match).OrderBy(w => w.Id).ToList();
            if (items.Count > 0) groups.Add((heading, items));
        }

        Add("Bugs", w => w.Kind == RegressionWorkItemKind.Bug);
        Add("IMS", w => w.Kind == RegressionWorkItemKind.Ims);
        Add("User Stories", w => w.Kind == RegressionWorkItemKind.Story);
        Add("Other", w => w.Kind is RegressionWorkItemKind.Feature or RegressionWorkItemKind.Other);
        return groups;
    }

    private static string Capitalize(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static string WorkItemLabel(RegressionWorkItemRef w) => w.Kind switch
    {
        RegressionWorkItemKind.Bug => WorkItemLabel("Bug", w),
        RegressionWorkItemKind.Story => WorkItemLabel("User Story", w),
        RegressionWorkItemKind.Feature => WorkItemLabel("Feature", w),
        RegressionWorkItemKind.Ims => WorkItemLabel("IMS", w),
        _ => WorkItemLabel("Work Item", w),
    };

    private static string WorkItemLabel(string kind, RegressionWorkItemRef workItem) =>
        string.IsNullOrWhiteSpace(workItem.Title)
            ? $"{kind} {workItem.Id}"
            : $"{kind} {workItem.Id} - {workItem.Title}";

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
