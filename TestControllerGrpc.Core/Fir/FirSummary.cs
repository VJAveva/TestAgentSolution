namespace TestControllerGrpc.Core.Fir;

/// <summary>
/// Builds the plain text a reader sees first. Fixed shape across every product, and it names which
/// sources it had - a report that silently omits a missing source reads like a complete one.
/// </summary>
internal static class FirSummary
{
    public static string Verdict(FirReport report, FirClassification classification)
    {
        if (classification.Root is not { } root) return report.Verdict;

        var masked = root.Tag == FirRowTag.Masked
            ? ", although the step logged Passed"
            : "";
        var followers = classification.Rows.Count(r => r.Tag is FirRowTag.Cascade or FirRowTag.Masked && !r.IsRoot);
        var effect = followers == 0 ? "" : $" {followers} later row(s) failed from it.";

        return $"Root cause: row {root.Row} ({root.Keyword}) - {root.Evidence}{masked}.{effect}";
    }

    public static string Build(
        FirReport report, FirTrxInput input, FirRuleSet rules, FirClassification classification)
    {
        var lines = new List<string>
        {
            $"{report.Test} failed ({FirGenericAnalyzer.Humanize(report.Duration)}{Where(report.Computer)}).",
            $"Cause: {Cause(report, rules, classification)}",
            $"Effect: {Effect(classification)}",
            $"Sources: {Sources(report, input)}",
        };

        if (classification.OpenPoint is { } open) lines.Add($"Open point: {open}");

        return FirRedactor.Mask(string.Join(Environment.NewLine, lines));
    }

    private static string Where(string computer)
        => string.IsNullOrWhiteSpace(computer) ? "" : $", {computer}";

    private static string Cause(FirReport report, FirRuleSet rules, FirClassification classification)
    {
        if (classification.Root is not { } root) return report.Verdict;

        var masked = root.Tag == FirRowTag.Masked ? ", although the step logged Passed" : "";
        var at = root.SourceFileLine is null ? "" : $" [{root.SourceFileLine}]";
        return $"row {root.Row} {StepText(root, rules)}{at} - {root.Evidence}{masked}.";
    }

    /// <summary>The sheet step when the workbook was read, otherwise the keyword the log used.</summary>
    private static string StepText(FirReportRow row, FirRuleSet rules)
        => row.DataStep is { } step
            ? $"`{step.Component} | {step.Keyword} | {rules.Shorten(step.Object)}`"
            : $"`{row.Keyword}`";

    private static string Effect(FirClassification classification)
    {
        var followers = classification.Rows
            .Where(r => !r.IsRoot && r.Tag is FirRowTag.Cascade or FirRowTag.Masked)
            .Select(r => r.Row)
            .ToList();

        var parts = new List<string>();
        parts.Add(followers.Count == 0
            ? "no later row failed from it"
            : $"rows {Ranges(followers)} failed after it");

        var recovered = classification.Rows.Where(r => r.Tag == FirRowTag.Recovered).Select(r => r.Row).ToList();
        if (recovered.Count > 0) parts.Add($"rows {Ranges(recovered)} later succeeded on the same objects");

        var slow = classification.Rows.Where(r => r.IsSlow).ToList();
        if (slow.Count > 0)
            parts.Add($"{FirGenericAnalyzer.Humanize(TimeSpan.FromSeconds(slow.Sum(r => r.Duration?.TotalSeconds ?? 0)))} spent waiting at rows {Ranges([.. slow.Select(r => r.Row)])}");

        return string.Join("; ", parts) + ".";
    }

    private static string Sources(FirReport report, FirTrxInput input)
    {
        var trx = new List<string>();
        if (!string.IsNullOrWhiteSpace(input.ErrorMessage)) trx.Add("error");
        if (!string.IsNullOrWhiteSpace(input.StackTrace)) trx.Add("trace");
        if (!string.IsNullOrWhiteSpace(input.DebugTrace)) trx.Add("debug trace");

        return $"TRX ({(trx.Count == 0 ? "outcome only" : string.Join(", ", trx))}), "
               + $"log ({(string.IsNullOrWhiteSpace(input.LogText) ? "not provided" : "used")}), "
               + $"test data ({TestData(report)}), "
               + $"packs ({string.Join(", ", report.RulePacks)}).";
    }

    private static string TestData(FirReport report)
    {
        if (report.DataSheet is null) return "not found";

        var named = $"{Path.GetFileName(report.DataWorkbook) ?? "workbook"} > {report.DataSheet}";
        if (report.DataWarning is not null) return $"{named}, {report.DataWarning}";

        return report.DataSource switch
        {
            FirDataSource.CopyFromRun => $"{named}, copy from run",
            FirDataSource.ControllerCopy => $"{named}, controller copy, matched to log",
            _ => $"{named}, not found",
        };
    }

    /// <summary>"29-32" reads far better than "29, 30, 31, 32" in a one-line summary.</summary>
    internal static string Ranges(IReadOnlyList<int> numbers)
    {
        if (numbers.Count == 0) return "";

        var sorted = numbers.Distinct().OrderBy(n => n).ToList();
        var parts = new List<string>();
        var start = sorted[0];
        var previous = start;

        foreach (var n in sorted.Skip(1))
        {
            if (n == previous + 1)
            {
                previous = n;
                continue;
            }

            parts.Add(Range(start, previous));
            start = previous = n;
        }

        parts.Add(Range(start, previous));
        return string.Join(", ", parts);

        static string Range(int from, int to) => from == to ? from.ToString() : $"{from}-{to}";
    }
}
