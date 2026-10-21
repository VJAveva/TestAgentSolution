using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TestControllerGrpc.Core.Fir;

/// <summary>
/// Layer 2: turns a trace into rows and steps using the framework pack's markers. Takes plain text so
/// the same parse serves a TRX DebugTrace and a vstest .log - older builds only have one of the two.
/// </summary>
public static class FirStepParser
{
    private static readonly string[] TimestampFormats =
    [
        "yyyy/MM/dd HH:mm:ss.fff",
        "yyyy/MM/dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.fff",
        "yyyy-MM-dd HH:mm:ss",
    ];

    public static FirParsedLog Parse(string? text, FirRuleSet rules)
    {
        if (string.IsNullOrEmpty(text) || !rules.CanParseSteps) return new FirParsedLog();

        var rows = new List<FirLogRow>();
        var builder = (Row: (FirLogRow?)null, Steps: new List<FirLogStep>(), Errors: new List<FirProductError>(), Counts: new List<FirDeployCount>(), Last: (DateTime?)null, Text: new StringBuilder());
        FirReasonCounts? reason = null;
        string? workbook = null, sheet = null;

        void Close(DateTime? nextStart)
        {
            if (builder.Row is null) return;
            rows.Add(builder.Row with
            {
                End = nextStart ?? builder.Last,
                Steps = [.. builder.Steps],
                ProductErrors = [.. builder.Errors],
                Counts = [.. builder.Counts],
                RawText = builder.Text.ToString(),
            });
        }

        foreach (var lineSpan in text.AsSpan().EnumerateLines())
        {
            if (lineSpan.IsWhiteSpace()) continue;
            var line = lineSpan.ToString();

            // The row marker is also a step line, so it must be claimed first or every row opens with
            // a phantom Info step.
            if (SafeMatch(rules.RowMarker, line) is { Success: true } marker)
            {
                var at = TimestampOf(rules.StepLine, line);
                Close(at);
                builder = (
                    new FirLogRow { Number = IntOf(marker, "row"), Start = at },
                    [], [], [], at, new StringBuilder(line).AppendLine());
                continue;
            }

            if (builder.Row is not null) builder.Text.AppendLine(line);

            if (SafeMatch(rules.TestDataLine, line) is { Success: true } data)
            {
                workbook = GroupOf(data, "path") ?? workbook;
                sheet = GroupOf(data, "sheet") ?? sheet;
                continue;
            }

            if (reason is null && SafeMatch(rules.ReasonLine, line) is { Success: true } counts)
            {
                reason = new FirReasonCounts(IntOf(counts, "v"), IntOf(counts, "e"), IntOf(counts, "w"));
                continue;
            }

            if (SafeMatch(rules.StepLine, line) is { Success: true } step)
            {
                var at = ParseTimestamp(GroupOf(step, "ts"));
                if (at is not null) builder.Last = at;
                builder.Steps.Add(new FirLogStep(
                    GroupOf(step, "level") ?? "",
                    GroupOf(step, "kw") ?? "",
                    GroupOf(step, "msg") ?? "",
                    at,
                    GroupOf(step, "file"),
                    step.Groups["line"].Success && int.TryParse(step.Groups["line"].Value, out var n) ? n : null));
                continue;
            }

            if (SafeMatch(rules.ProductCount, line) is { Success: true } count)
            {
                builder.Counts.Add(new FirDeployCount(IntOf(count, "done"), IntOf(count, "total")));
                continue;
            }

            foreach (var pattern in rules.ProductError)
            {
                if (SafeMatch(pattern, line) is not { Success: true } error) continue;
                builder.Errors.Add(new FirProductError(GroupOf(error, "obj") ?? "", (GroupOf(error, "reason") ?? "").Trim()));
                break;
            }
        }

        Close(null);

        return new FirParsedLog
        {
            Rows = rows,
            Reason = reason,
            DataWorkbook = workbook,
            DataSheet = sheet,
        };
    }

    private static DateTime? TimestampOf(Regex? stepLine, string line)
        => SafeMatch(stepLine, line) is { Success: true } m ? ParseTimestamp(GroupOf(m, "ts")) : null;

    internal static DateTime? ParseTimestamp(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && DateTime.TryParseExact(value.Trim(), TimestampFormats, CultureInfo.InvariantCulture,
               DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    private static Match SafeMatch(Regex? regex, string line)
    {
        if (regex is null) return Match.Empty;

        try
        {
            return regex.Match(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return Match.Empty;
        }
    }

    private static string? GroupOf(Match match, string name)
        => match.Groups[name].Success ? match.Groups[name].Value : null;

    private static int IntOf(Match match, string name)
        => match.Groups[name].Success && int.TryParse(match.Groups[name].Value, out var n) ? n : 0;
}
