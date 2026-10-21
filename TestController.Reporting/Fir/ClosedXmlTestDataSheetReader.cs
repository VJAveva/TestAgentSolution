using System.Text.RegularExpressions;
using ClosedXML.Excel;
using TestControllerGrpc.Core.Fir;

namespace TestControllerGrpc.Reporting.Fir;

/// <summary>
/// Reads a test-data sheet with ClosedXML. Lives here rather than in Core because Core is deliberately
/// dependency-light and Reporting already references it - the dependency can only run this way.
/// Strictly read-only: the workbook is opened shared so a live run holding it is never blocked.
/// </summary>
public sealed class ClosedXmlTestDataSheetReader : ITestDataSheetReader
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    public IReadOnlyDictionary<int, FirDataStep>? Read(string workbookPath, string sheetName, FirTestDataLayout layout)
    {
        if (string.IsNullOrWhiteSpace(workbookPath) || string.IsNullOrWhiteSpace(sheetName)) return null;
        if (!File.Exists(workbookPath)) return null;

        try
        {
            using var stream = new FileStream(workbookPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var workbook = new XLWorkbook(stream);

            return workbook.Worksheets.TryGetWorksheet(sheetName, out var sheet)
                ? ReadSheet(sheet, layout)
                : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A corrupt or locked workbook degrades the report to log-only; it must never fail it.
            return null;
        }
    }

    private static Dictionary<int, FirDataStep> ReadSheet(IXLWorksheet sheet, FirTestDataLayout layout)
    {
        var skip = Compile(layout.SkipRow);
        var keywordShape = Compile(layout.KeywordPattern);
        var steps = new Dictionary<int, FirDataStep>();

        foreach (var row in sheet.RowsUsed())
        {
            var component = Text(row, layout.ComponentColumn);
            if (component.Length == 0 || skip?.IsMatch(component) == true) continue;

            var keyword = Unwrap(Text(row, layout.KeywordColumn), keywordShape);
            if (keyword.Length == 0) continue;

            steps[row.RowNumber()] = new FirDataStep(
                row.RowNumber(), component, keyword, SplitParams(Text(row, layout.ParamsColumn), layout.ParamSeparator));
        }

        return steps;
    }

    private static string Text(IXLRow row, int column)
        => column < 1 ? "" : row.Cell(column).GetString().Trim();

    private static string Unwrap(string keyword, Regex? shape)
    {
        if (shape is null || keyword.Length == 0) return keyword;

        var match = shape.Match(keyword);
        return match.Success && match.Groups["kw"].Success ? match.Groups["kw"].Value.Trim() : keyword;
    }

    private static IReadOnlyList<string> SplitParams(string raw, string separator)
        => raw.Length == 0 || string.IsNullOrEmpty(separator)
            ? raw.Length == 0 ? [] : [raw]
            : [.. raw.Split(separator).Select(p => p.Trim())];

    private static Regex? Compile(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;

        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase, MatchTimeout);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
