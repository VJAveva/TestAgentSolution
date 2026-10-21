namespace TestControllerGrpc.Core.Fir;

public enum FirRowTag
{
    /// <summary>The first failing row. Everything else is read relative to it.</summary>
    Root,

    /// <summary>Failing, but every keyword logged Passed - only the product output disagreed.</summary>
    Masked,

    Cascade,
    Recovered,

    /// <summary>Succeeded, shown because it touches an object the root failed on.</summary>
    Passed,
}

public sealed record FirReason(string Text, int Count);

public sealed record FirCategoryCount(string Name, int Count);

/// <summary>How confidently a row is tied to the root: same object or host, versus merely later.</summary>
public enum FirLink
{
    None,
    Medium,
    High,
}

public sealed record FirReportRow
{
    public int Row { get; init; }
    public FirRowTag Tag { get; init; }
    public bool IsRoot { get; init; }
    public bool IsSlow { get; init; }
    public string Keyword { get; init; } = "";
    public string? SourceFileLine { get; init; }
    public DateTime? Time { get; init; }
    public TimeSpan? Duration { get; init; }
    public string Evidence { get; init; } = "";
    public string Why { get; init; } = "";
    public IReadOnlyList<FirReason> Reasons { get; init; } = [];

    /// <summary>The sheet row this log row came from, when a workbook was available.</summary>
    public FirDataStep? DataStep { get; init; }

    public FirLink Link { get; init; }
}

public sealed record FirClassification
{
    public FirReportRow? Root { get; init; }
    public IReadOnlyList<FirReportRow> Rows { get; init; } = [];
    public IReadOnlyList<FirCategoryCount> Categories { get; init; } = [];
    public int FailedSteps { get; init; }
    public IReadOnlyList<int> SlowRows { get; init; } = [];

    /// <summary>A gap in the evidence, stated rather than guessed at.</summary>
    public string? OpenPoint { get; init; }
}

/// <summary>
/// Decides which row caused the failure and what followed from it. Per the requirement the root is the
/// FIRST failing row even when its keyword logged Passed - trusting the keyword is exactly how a run
/// ends up reporting a consequence four minutes later instead of the cause.
/// </summary>
public static class FirClassifier
{
    public static FirClassification Classify(
        FirParsedLog log,
        FirRuleSet rules,
        IReadOnlyDictionary<int, FirDataStep>? dataSteps = null)
    {
        if (log.Rows.Count == 0) return new FirClassification();

        var failing = log.Rows.Where(r => IsFailing(r, rules)).ToList();
        var rootRow = failing.FirstOrDefault();
        var rootStep = Step(dataSteps, rootRow);
        var rootObjects = rootRow is null ? [] : ObjectsOf(rootRow, rules, rootStep);
        var rootKeyword = rootRow is null ? "" : KeywordOf(rootRow, rules);
        var firstCascade = failing.Skip(1).FirstOrDefault()?.Number;

        var rows = new List<FirReportRow>();
        foreach (var row in log.Rows)
        {
            var step = Step(dataSteps, row);
            var isFailing = failing.Contains(row);
            var masked = isFailing && !HasFailingStep(row, rules);
            var slow = row.Duration is { } d && d.TotalSeconds >= rules.SlowStepSeconds;

            if (isFailing)
            {
                var isRoot = ReferenceEquals(row, rootRow);
                var link = isRoot ? FirLink.None
                    : MentionsRootObject(row, rootObjects) ? FirLink.High
                    : FirLink.Medium;

                rows.Add(Build(row, rules, masked ? FirRowTag.Masked : isRoot ? FirRowTag.Root : FirRowTag.Cascade,
                    isRoot, slow, step, link,
                    isRoot
                        ? "first failing row"
                        : masked ? "keyword logged Passed; the product disagreed"
                        : link == FirLink.High ? "same object or host as the root" : "later in time only"));
                continue;
            }

            // Supporting context is only meaningful after the root; a success before it proves nothing.
            if (rootRow is null || row.Number <= rootRow.Number) continue;

            var touches = TouchesRootObject(row, rootObjects, step);
            var sameOperation = MatchesRootKeyword(row, rules, step, rootKeyword);
            if (!touches && !sameOperation) continue;

            rows.Add(Build(row, rules,
                touches && sameOperation ? FirRowTag.Recovered : FirRowTag.Passed,
                false, slow, step, touches ? FirLink.High : FirLink.Medium,
                touches && sameOperation
                    ? "the same operation later succeeded on an object the root failed on"
                    : touches ? "succeeded on an object the root failed on"
                    : "the same operation succeeded elsewhere, so the operation itself works"));
        }

        return new FirClassification
        {
            Root = rows.FirstOrDefault(r => r.IsRoot),
            Rows = rows,
            Categories = Categorize(failing, rules, rows.Count(r => r.Tag == FirRowTag.Masked)),
            FailedSteps = log.Rows.Sum(r => r.Steps.Count(s => rules.FailLevels.Contains(s.Level))),
            SlowRows = [.. log.Rows.Where(r => r.Duration is { } d && d.TotalSeconds >= rules.SlowStepSeconds).Select(r => r.Number)],
            OpenPoint = OpenPointFor(rows, rootRow, firstCascade, rules),
        };
    }

    /// <summary>
    /// A row that succeeded on the root's own object AFTER the root but BEFORE the first cascade is a gap
    /// in the evidence: the object was still answering, and nothing in the log says what changed.
    /// </summary>
    private static string? OpenPointFor(
        IReadOnlyList<FirReportRow> rows, FirLogRow? rootRow, int? firstCascade, FirRuleSet rules)
    {
        if (rootRow is null || firstCascade is not { } cascade) return null;

        var gap = rows.FirstOrDefault(r =>
            r.Tag == FirRowTag.Passed && r.Link == FirLink.High && r.Row > rootRow.Number && r.Row < cascade);

        if (gap is null) return null;

        var subject = rules.Shorten(gap.DataStep?.Object) is { Length: > 0 } s ? s : "the same object";
        return $"Row {gap.Row} passed on {subject} after the root at row {rootRow.Number}, so it was still "
               + $"answering then; the log does not show what changed between rows {gap.Row} and {cascade}.";
    }

    private static FirDataStep? Step(IReadOnlyDictionary<int, FirDataStep>? steps, FirLogRow? row)
        => row is not null && steps is not null && steps.TryGetValue(row.Number, out var step) ? step : null;

    /// <summary>Loose on purpose: the link often sits in a product line, not in a keyword step.</summary>
    private static bool MentionsRootObject(FirLogRow row, IReadOnlyList<string> rootObjects)
        => rootObjects.Any(o => row.RawText.Contains(o, StringComparison.Ordinal));

    /// <summary>
    /// Strict when the sheet is available: the row's own object must be the root's. Without a sheet there
    /// is nothing better than the looser text match.
    /// </summary>
    private static bool TouchesRootObject(FirLogRow row, IReadOnlyList<string> rootObjects, FirDataStep? step)
        => step?.Object is { } obj
            ? rootObjects.Any(o => Related(obj, o))
            : MentionsRootObject(row, rootObjects);

    private static bool Related(string a, string b)
        => a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal);

    private static bool MatchesRootKeyword(FirLogRow row, FirRuleSet rules, FirDataStep? step, string rootKeyword)
    {
        if (string.IsNullOrEmpty(rootKeyword)) return false;

        if (step is not null)
            return rules.LogKeywordsFor(step.Keyword).Contains(rootKeyword, StringComparer.OrdinalIgnoreCase);

        return row.Steps.Any(s => string.Equals(s.Keyword, rootKeyword, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsFailing(FirLogRow row, FirRuleSet rules)
        => HasFailingStep(row, rules) || row.ProductErrors.Count > 0 || row.Counts.Any(c => c.IsShortfall);

    private static bool HasFailingStep(FirLogRow row, FirRuleSet rules)
        => row.Steps.Any(s => rules.FailLevels.Contains(s.Level));

    private static string KeywordOf(FirLogRow row, FirRuleSet rules)
        => (row.Steps.FirstOrDefault(s => rules.FailLevels.Contains(s.Level)) ?? row.Steps.FirstOrDefault())?.Keyword ?? "";

    /// <summary>Objects the root failed on, from its failing steps, the product's error lines, and the sheet.</summary>
    private static List<string> ObjectsOf(FirLogRow row, FirRuleSet rules, FirDataStep? step)
    {
        var objects = row.Steps
            .Where(s => rules.FailLevels.Contains(s.Level))
            .Select(s => s.Subject)
            .Concat(row.ProductErrors.Select(e => e.Object))
            .Append(step?.Object)
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select(o => o!.TrimEnd(':'))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return objects;
    }

    private static FirReportRow Build(
        FirLogRow row, FirRuleSet rules, FirRowTag tag, bool isRoot, bool slow,
        FirDataStep? step, FirLink link, string why)
    {
        var failingStep = row.Steps.FirstOrDefault(s => rules.FailLevels.Contains(s.Level));
        var anchor = failingStep ?? row.Steps.FirstOrDefault();

        return new FirReportRow
        {
            Row = row.Number,
            Tag = tag,
            IsRoot = isRoot,
            IsSlow = slow,
            Keyword = anchor?.Keyword ?? "",
            SourceFileLine = failingStep?.SourceFileLine,
            Time = row.Start,
            Duration = row.Duration,
            Evidence = Evidence(row, rules),
            Why = why,
            Reasons = GroupReasons(row),
            DataStep = step,
            Link = link,
        };
    }

    private static string Evidence(FirLogRow row, FirRuleSet rules)
    {
        var parts = new List<string>();

        var shortfall = row.Counts.FirstOrDefault(c => c.IsShortfall);
        if (shortfall is not null) parts.Add($"completed {shortfall.Done} of {shortfall.Total}");

        var reasons = GroupReasons(row);
        if (reasons.Count > 0)
            parts.Add(string.Join("; ", reasons.Select(r => r.Count > 1 ? $"{r.Text} x{r.Count}" : r.Text)));

        var failed = row.Steps.Where(s => rules.FailLevels.Contains(s.Level)).ToList();
        if (failed.Count > 0 && reasons.Count == 0)
            parts.Add(failed.Count == 1
                ? Trim(failed[0].Message)
                : $"{failed.Count} failed steps, first: {Trim(failed[0].Message)}");

        if (parts.Count == 0 && row.Steps.Count > 0)
            parts.Add(Trim(row.Steps[0].Message));

        return string.Join(" - ", parts);

        string Trim(string message)
        {
            var subject = rules.Shorten(new FirLogStep("", "", message).Subject);
            var rest = message.TrimStart();
            var cut = rest.IndexOfAny([' ', '\t', ':']);
            return cut <= 0 || string.IsNullOrEmpty(subject) ? Clip(rest) : Clip(subject + rest[cut..]);
        }
    }

    private static IReadOnlyList<FirReason> GroupReasons(FirLogRow row)
        => [.. row.ProductErrors
            .GroupBy(e => e.Reason, StringComparer.OrdinalIgnoreCase)
            .Select(g => new FirReason(g.Key, g.Count()))
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.Text, StringComparer.Ordinal)];

    private static IReadOnlyList<FirCategoryCount> Categorize(IEnumerable<FirLogRow> failing, FirRuleSet rules, int maskedRows)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in failing)
            foreach (var step in row.Steps.Where(s => rules.FailLevels.Contains(s.Level)))
                if (rules.CategoryFor(step.Keyword) is { } category)
                    counts[category] = counts.GetValueOrDefault(category) + 1;

        if (maskedRows > 0) counts["Masked pass"] = maskedRows;

        return [.. counts.Select(c => new FirCategoryCount(c.Key, c.Value)).OrderByDescending(c => c.Count).ThenBy(c => c.Name, StringComparer.Ordinal)];
    }

    private static string Clip(string text, int max = 160)
        => text.Length <= max ? text.Trim() : string.Concat(text.AsSpan(0, max - 1).TrimEnd(), "\u2026");
}
