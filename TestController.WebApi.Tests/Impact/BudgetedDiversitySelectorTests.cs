using Microsoft.Extensions.Options;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Index;
using TestControllerGrpc.Core.Impact.Selection;

namespace TestController.WebApi.Tests.Impact;

public sealed class BudgetedDiversitySelectorTests
{
    private static BudgetedDiversitySelector Selector(ImpactMappingOptions? options = null)
        => new(Options.Create(options ?? new ImpactMappingOptions()));

    private static ImpactedArea Area(RiskTier tier = RiskTier.High)
        => new("A", "Name", "Sub", "Vob", [], [], tier, new ChurnMetrics(0, 0, 0, 0, 0, DateTimeOffset.UnixEpoch));

    private static Scored<TestCaseCandidate> Tc(int id, int? parent, double score, string automation = "Automated")
        => new(new TestCaseCandidate(new AdoWorkItemRef(id, "Test Case", $"TC{id}", null, "Design", 1), "steps", automation, parent, []), score, []);

    private static Scored<FeatureCandidate> Feat(int id, FeatureDiscoveryPath path = FeatureDiscoveryPath.DirectFeatureSearch)
        => new(new FeatureCandidate(new AdoWorkItemRef(id, "Feature", $"F{id}", null, "Active", 1), "d", path, [], 0), 1.0, []);

    private static RelevanceJudgement J(int grade, double confidence = 0.9)
        => new(grade, confidence, "r", ["signal"]);

    private static AnchorResult NoAnchors() => new([], 0, false);

    [Fact]
    public void Select_Should_DropGrade0Candidates()
    {
        var judgements = new Dictionary<int, RelevanceJudgement> { [101] = J(3), [102] = J(0) };

        IReadOnlyList<MappedTestCase> result = Selector().Select(
            Area(), [], [Tc(101, 900, 0.9), Tc(102, 900, 0.5)], judgements, NoAnchors(),
            new Dictionary<int, double>(), new Dictionary<int, TimeSpan>(), IndexSnapshot.Empty,
            SelectionTier.Full, TimeSpan.MaxValue, out SelectionDiagnostics diagnostics);

        Assert.Contains(result, m => m.TestCase.Item.Id == 101);
        Assert.DoesNotContain(result, m => m.TestCase.Item.Id == 102);
        Assert.Equal(1, diagnostics.DroppedByGrade);
    }

    [Fact]
    public void Select_Should_KeepLinkedAnchor_EvenIfGrade0()
    {
        var judgements = new Dictionary<int, RelevanceJudgement> { [101] = J(0) };
        var anchors = new AnchorResult([new AnchorEdge(101, 900, AnchorSource.LinkedWorkItem, 1.0, "linked")], 1.0, false);

        IReadOnlyList<MappedTestCase> result = Selector().Select(
            Area(), [], [Tc(101, 900, 0.1)], judgements, anchors,
            new Dictionary<int, double>(), new Dictionary<int, TimeSpan>(), IndexSnapshot.Empty,
            SelectionTier.Targeted, TimeSpan.MaxValue, out _);

        MappedTestCase mapped = Assert.Single(result);
        Assert.Equal(101, mapped.TestCase.Item.Id);
        Assert.Equal(MappingConfidence.Observed, mapped.Confidence);
        Assert.Equal(AnchorSource.LinkedWorkItem, mapped.Anchor);
    }

    [Fact]
    public void Select_Should_DropBelowMinFinalScore()
    {
        var options = new ImpactMappingOptions();
        options.Selection.MinFinalScore = 0.5;
        var judgements = new Dictionary<int, RelevanceJudgement> { [101] = J(3), [102] = J(1, 0.1) };

        IReadOnlyList<MappedTestCase> result = Selector(options).Select(
            Area(), [], [Tc(101, 900, 1.0), Tc(102, 901, 0.01)], judgements, NoAnchors(),
            new Dictionary<int, double>(), new Dictionary<int, TimeSpan>(), IndexSnapshot.Empty,
            SelectionTier.Full, TimeSpan.MaxValue, out _);

        Assert.Contains(result, m => m.TestCase.Item.Id == 101);
        Assert.DoesNotContain(result, m => m.TestCase.Item.Id == 102);
    }

    [Fact]
    public void Select_Should_KeepAnchorBelowMinFinalScore()
    {
        // The score floor runs last precisely so it cannot strip a mandatory safety-net selection.
        var options = new ImpactMappingOptions();
        options.Selection.MinFinalScore = 0.9;
        var judgements = new Dictionary<int, RelevanceJudgement> { [101] = J(1, 0.1) };
        var anchors = new AnchorResult([new AnchorEdge(101, 900, AnchorSource.LinkedWorkItem, 1.0, "linked")], 1.0, false);

        IReadOnlyList<MappedTestCase> result = Selector(options).Select(
            Area(), [], [Tc(101, 900, 0.01)], judgements, anchors,
            new Dictionary<int, double>(), new Dictionary<int, TimeSpan>(), IndexSnapshot.Empty,
            SelectionTier.Full, TimeSpan.MaxValue, out _);

        Assert.Equal(101, Assert.Single(result).TestCase.Item.Id);
    }

    [Fact]
    public void Select_Should_RespectBudget()
    {
        var judgements = new Dictionary<int, RelevanceJudgement> { [101] = J(2, 0.7), [102] = J(2, 0.7), [103] = J(2, 0.7) };
        var durations = new Dictionary<int, TimeSpan>
        {
            [101] = TimeSpan.FromSeconds(60),
            [102] = TimeSpan.FromSeconds(60),
            [103] = TimeSpan.FromSeconds(60),
        };

        IReadOnlyList<MappedTestCase> result = Selector().Select(
            Area(), [], [Tc(101, 900, 0.9), Tc(102, 901, 0.8), Tc(103, 902, 0.7)], judgements, NoAnchors(),
            new Dictionary<int, double>(), durations, IndexSnapshot.Empty,
            SelectionTier.Targeted, TimeSpan.FromSeconds(90), out SelectionDiagnostics diagnostics);

        Assert.Single(result); // only one 60s test fits a 90s budget
        Assert.Equal(2, diagnostics.DroppedByBudget);
    }

    [Fact]
    public void Select_Should_AssignDeclaredConfidence_ForParentLink()
    {
        var judgements = new Dictionary<int, RelevanceJudgement> { [101] = J(3) };

        IReadOnlyList<MappedTestCase> result = Selector().Select(
            Area(), [Feat(900, FeatureDiscoveryPath.DirectFeatureSearch)], [Tc(101, 900, 0.9)], judgements, NoAnchors(),
            new Dictionary<int, double>(), new Dictionary<int, TimeSpan>(), IndexSnapshot.Empty,
            SelectionTier.Full, TimeSpan.MaxValue, out _);

        Assert.Equal(MappingConfidence.Declared, result.Single().Confidence);
    }

    [Fact]
    public void Select_Should_AssignAssumedConfidence_ForOrphan()
    {
        var judgements = new Dictionary<int, RelevanceJudgement> { [101] = J(2) };

        IReadOnlyList<MappedTestCase> result = Selector().Select(
            Area(), [], [Tc(101, null, 0.9)], judgements, NoAnchors(),
            new Dictionary<int, double>(), new Dictionary<int, TimeSpan>(), IndexSnapshot.Empty,
            SelectionTier.Full, TimeSpan.MaxValue, out _);

        Assert.Equal(MappingConfidence.Assumed, result.Single().Confidence);
    }

    [Fact]
    public void Select_Should_RespectMaxTestCasesPerFeature()
    {
        var options = new ImpactMappingOptions();
        options.Selection.MaxTestCasesPerFeature = 1;
        var judgements = new Dictionary<int, RelevanceJudgement> { [101] = J(2), [102] = J(2), [103] = J(2) };

        IReadOnlyList<MappedTestCase> result = Selector(options).Select(
            Area(), [], [Tc(101, 900, 0.9), Tc(102, 900, 0.8), Tc(103, 900, 0.7)], judgements, NoAnchors(),
            new Dictionary<int, double>(), new Dictionary<int, TimeSpan>(), IndexSnapshot.Empty,
            SelectionTier.Full, TimeSpan.MaxValue, out _);

        Assert.Single(result); // MMR per-feature cap keeps only one of three same-feature tests
    }
}
