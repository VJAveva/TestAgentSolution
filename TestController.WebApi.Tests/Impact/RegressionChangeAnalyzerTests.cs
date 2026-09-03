using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Models;

namespace TestController.WebApi.Tests.Impact;

/// <summary>
/// Unit tests for <see cref="RegressionChangeAnalyzer"/> — the offline change summary + recommended-tests
/// fallback shown when the impact-mapping engine retrieves no ADO test cases. Modeled on the live AAMxCore
/// merge (Polaris/TLS/LMX) from the "nothing retrieved" report.
/// </summary>
public class RegressionChangeAnalyzerTests
{
    private static SubsystemRow Row(IReadOnlyList<string>? useCases, IReadOnlyList<string>? areas, params RegressionChangeRef[] changes) =>
        new(
            Component: "AAMxCore", Subsystem: "AAMxCore", Category: RegressionCategoryKind.Runtime,
            CategoryConfidence: RegressionEvidenceKind.Declared, FilesModified: [], TotalFilesModified: 33,
            Changes: changes, RiskTier: "succeeded", AutomatedSuites: [], ManualSuites: [],
            EstimatedMinutes: 0, IsEstimate: true, RegressionAreas: areas, UseCases: useCases);

    private static RegressionChangeRef Pr(string summary, params RegressionWorkItemRef[] wi) =>
        new(Guid.NewGuid().ToString("N"), summary, DateTimeOffset.UtcNow, [], wi, RegressionChangeKind.PullRequest);

    [Fact]
    public void Summarize_Should_LeadWithPrCountAndStripMergedPrPrefix()
    {
        var bug = new RegressionWorkItemRef(5011025, RegressionWorkItemKind.Bug, "Non-Warm Redundant Engine Displays Missed Heartbeats after LMX Rejection", null);
        var row = Row(null, null,
            Pr("Merged PR 1354537: [LMX] Backported fixes and minor optimizations from SP 2026", bug),
            Pr("Merged PR 1354567: Changes to fix tls handshake issues"));

        var s = RegressionChangeAnalyzer.Summarize(row);

        Assert.Contains("2 merged PR(s) in AAMxCore touching 33 file(s)", s);
        Assert.Contains("[LMX] Backported fixes and minor optimizations from SP 2026", s);
        Assert.DoesNotContain("Merged PR 1354537:", s); // bookkeeping prefix stripped
        Assert.Contains("Fixes bug(s) (prioritise): Non-Warm Redundant Engine", s);
    }

    [Fact]
    public void RecommendTests_Should_FlagAndOrderRelevantFirst_When_NamesMatchChangeText()
    {
        var bug = new RegressionWorkItemRef(1, RegressionWorkItemKind.Bug, "Non-Warm Redundant Engine displays missed heartbeats; LMX Rejection Messages observed", null);
        var row = Row(["UC580", "Warm", "NonWarm", "Lmx", "CheckPointer"], null,
            Pr("Merged PR 1: [LMX] Backported fixes and minor optimizations", bug));

        var recs = RegressionChangeAnalyzer.RecommendTests(row);
        var relevant = recs.Where(r => r.Relevant).Select(r => r.Name).ToList();

        Assert.Contains("Lmx", relevant);
        Assert.Contains("Warm", relevant);      // matches "Non-Warm"
        Assert.Contains("NonWarm", relevant);
        Assert.DoesNotContain("CheckPointer", relevant);
        Assert.DoesNotContain("UC580", relevant);
        // Relevant tests are surfaced before the rest.
        Assert.Equal(relevant.Count, recs.TakeWhile(r => r.Relevant).Count());
    }

    [Fact]
    public void RecommendTests_Should_DedupeUseCasesAndRegressionAreas()
    {
        var row = Row(["Warm", "Lmx"], ["Lmx", "CheckPointer"], Pr("Merged PR 1: fix"));

        var names = RegressionChangeAnalyzer.RecommendTests(row).Select(r => r.Name).ToList();

        Assert.Equal(3, names.Count); // Warm, Lmx, CheckPointer (Lmx deduped)
        Assert.Equal(names.Distinct(StringComparer.OrdinalIgnoreCase).Count(), names.Count);
    }

    [Fact]
    public void RecommendTests_Should_ReturnEmpty_When_NoUseCasesOrAreas()
    {
        var row = Row(null, null, Pr("Merged PR 1: fix"));

        Assert.Empty(RegressionChangeAnalyzer.RecommendTests(row));
    }
}
