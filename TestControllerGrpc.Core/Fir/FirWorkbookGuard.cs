namespace TestControllerGrpc.Core.Fir;

public sealed record FirWorkbookCheck(bool Matches, string? Mismatch)
{
    public static FirWorkbookCheck Ok { get; } = new(true, null);
}

/// <summary>
/// Proves a workbook belongs to THIS run before any row is attributed to it. A sheet that has gained or
/// lost a row since the run still opens cleanly and still has a row N - it is simply a different step.
/// Attributing the wrong step to a failure is worse than showing no step at all, so any mismatch
/// discards the workbook.
/// </summary>
public static class FirWorkbookGuard
{
    public static FirWorkbookCheck Verify(
        IReadOnlyDictionary<int, FirDataStep> steps, FirParsedLog log, FirRuleSet rules)
    {
        foreach (var row in log.Rows)
        {
            if (!steps.TryGetValue(row.Number, out var step))
                return new FirWorkbookCheck(false, $"the sheet has no step at row {row.Number}");

            // A sheet keyword outside the map makes no claim about log names, so it cannot contradict one.
            var expected = rules.LogKeywordsFor(step.Keyword);
            if (expected.Count == 0) continue;

            var logged = row.Steps.Select(s => s.Keyword).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (logged.Count == 0) continue;

            if (!expected.Any(logged.Contains))
                return new FirWorkbookCheck(false,
                    $"row {row.Number} is '{step.Keyword}' in the sheet but the log ran {string.Join(", ", logged)}");
        }

        return FirWorkbookCheck.Ok;
    }
}
