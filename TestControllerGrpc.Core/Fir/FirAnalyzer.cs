namespace TestControllerGrpc.Core.Fir;

/// <summary>
/// Entry point for a report. The generic layer always runs; rule packs stack on top of its result
/// rather than replace it, so a test from a product with no pack still gets everything the TRX knows.
/// </summary>
public sealed class FirAnalyzer
{
    private readonly IReadOnlyList<FirRulePack> _packs;
    private readonly ITestDataSheetReader? _sheets;
    private readonly FirOptions _options;

    public FirAnalyzer(
        IReadOnlyList<FirRulePack>? packs = null,
        ITestDataSheetReader? sheets = null,
        FirOptions? options = null)
    {
        _packs = packs ?? [];
        _sheets = sheets;
        _options = options ?? FirOptions.Default;
    }

    /// <summary>Returns null when the test did not fail - a passing test has nothing to report.</summary>
    public FirReport? Analyze(FirTrxInput input)
    {
        if (input is null || !IsFailure(input.Outcome)) return null;

        var rules = FirRuleSet.Create(FirPackSelector.Select(_packs, input));
        var report = FirGenericAnalyzer.Analyze(input, rules) with
        {
            RulePacks = [FirGenericAnalyzer.PackName, .. rules.Labels],
        };

        if (!rules.CanParseSteps) return report;

        // AllText, not OutputSections: the framework prints its reason counts in the TRX error message,
        // which is not part of the output streams.
        var parsed = FirStepParser.Parse(string.Join('\n', input.AllText()), rules);
        var data = ReadDataSheet(parsed, rules, input);
        var classification = FirClassifier.Classify(parsed, rules, data.Steps);

        report = report with
        {
            Reason = parsed.Reason,
            Root = classification.Root,
            Rows = classification.Rows,
            Categories = classification.Categories,
            FailedSteps = classification.FailedSteps,
            DataWorkbook = parsed.DataWorkbook,
            DataSheet = parsed.DataSheet,
            DataSource = data.Source,
            DataWarning = data.Warning,
            ResolvedWorkbook = data.Path,
            OpenPoint = classification.OpenPoint,
        };

        report = report with { Verdict = FirRedactor.Mask(FirSummary.Verdict(report, classification)) };
        return report with { Summary = FirSummary.Build(report, input, rules, classification) };
    }

    /// <summary>
    /// A missing, unreadable or stale workbook degrades the report; it never fails it. The guard runs for
    /// every copy, including one that came back with the run - a surprise there is still a surprise.
    /// </summary>
    private (IReadOnlyDictionary<int, FirDataStep>? Steps, FirDataSource Source, string? Warning, string? Path) ReadDataSheet(
        FirParsedLog parsed, FirRuleSet rules, FirTrxInput input)
    {
        if (_sheets is null || string.IsNullOrWhiteSpace(parsed.DataWorkbook) || string.IsNullOrWhiteSpace(parsed.DataSheet))
            return (null, FirDataSource.None, null, null);

        if (FirWorkbookResolver.Resolve(parsed.DataWorkbook, input.ResultsFolder, _options) is not { } match)
            return (null, FirDataSource.None, null, null);

        var steps = _sheets.Read(match.Path, parsed.DataSheet, rules.TestDataLayout);
        if (steps is null || steps.Count == 0) return (null, FirDataSource.None, null, null);

        var check = FirWorkbookGuard.Verify(steps, parsed, rules);
        return check.Matches
            ? (steps, match.Source, null, match.Path)
            // The path is still reported so a cached report can notice the workbook changing again.
            : (null, FirDataSource.None, $"test data found but it has changed since this run - {check.Mismatch}", match.Path);
    }

    /// <summary>
    /// NotExecuted and Inconclusive are excluded on purpose: they carry no failure evidence, so a report
    /// would be a confident-looking empty box.
    /// </summary>
    public static bool IsFailure(string? outcome) => outcome?.Trim().ToLowerInvariant() switch
    {
        "failed" or "timeout" or "aborted" or "error" => true,
        _ => false,
    };
}
