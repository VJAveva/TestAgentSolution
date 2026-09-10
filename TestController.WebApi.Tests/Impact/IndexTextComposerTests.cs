using TestControllerGrpc.Core.Impact;

namespace TestController.WebApi.Tests.Impact;

public sealed class IndexTextComposerTests
{
    private static TestCaseCandidate TestCase(string title, string? description, string? steps, params string[] tags)
        => new(new AdoWorkItemRef(1, "Test Case", title, null, "Design", 1), steps, "Automated", 900, tags,
            Description: description);

    [Fact]
    public void ForTestCase_Should_IncludeDescription()
    {
        // Requirement-id titles carry no matchable vocabulary; description is the only functional prose.
        string text = IndexTextComposer.ForTestCase(
            TestCase("FR 12345", "Verifies galaxy node failover", "Open console. Fail the node."));

        Assert.Contains("galaxy node failover", text, StringComparison.Ordinal);
        Assert.Contains("FR 12345", text, StringComparison.Ordinal);
        Assert.Contains("Fail the node", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ForTestCase_Should_SkipEmptyParts()
    {
        string text = IndexTextComposer.ForTestCase(TestCase("FR 1", null, null));

        Assert.Equal("FR 1", text);
    }

    [Fact]
    public void ForTestCase_Should_IncludeTags()
    {
        string text = IndexTextComposer.ForTestCase(TestCase("FR 1", null, null, "smoke", "galaxy"));

        Assert.Contains("smoke galaxy", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ForFeature_Should_JoinTitleAndDescription()
    {
        var feature = new FeatureCandidate(
            new AdoWorkItemRef(900, "Feature", "Galaxy Deploy", null, "Active", 1),
            "Deploys the galaxy", FeatureDiscoveryPath.DirectFeatureSearch, [], 3);

        Assert.Equal("Galaxy Deploy\nDeploys the galaxy", IndexTextComposer.ForFeature(feature));
    }
}
