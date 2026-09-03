using Microsoft.Extensions.Options;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Query;

namespace TestController.WebApi.Tests.Impact;

public sealed class KeywordExtractorTests
{
    private static KeywordExtractor Extractor(ImpactMappingOptions? options = null)
        => new(Options.Create(options ?? new ImpactMappingOptions()));

    private static ImpactedArea Area(
        string display = "Galaxy Deploy", string? vob = "GalaxyVob", string? subsystem = "Deploy",
        IReadOnlyList<string>? paths = null)
        => new("AREA-1", display, subsystem, vob, paths ?? [], [], RiskTier.High,
            new ChurnMetrics(0, 0, 0, 0, 0, DateTimeOffset.UnixEpoch));

    private static ChangeDocument Doc(
        IReadOnlyList<string>? literals = null, IReadOnlyList<string>? api = null)
        => new("AREA-1", PathTokens: [], ChangedSymbols: [], ChangedLiterals: literals ?? [],
            PublicApiChanges: api ?? [], PrNarrative: null, RawText: "", Fingerprint: "");

    [Fact]
    public void Extract_Should_ProduceIdentityAndLiteralsGroups_WithExpectedWeights()
    {
        IReadOnlyList<KeywordGroup> groups = Extractor().Extract(
            Area(), Doc(literals: ["Deploy galaxy engine"], api: ["RunDeployment(string) -> Task"]), hyde: null);

        Assert.Equal(1.00, groups.Single(g => g.Label == "identity").Weight);

        KeywordGroup literals = groups.Single(g => g.Label == "literals");
        Assert.Equal(0.95, literals.Weight);
        Assert.Contains("galaxy", literals.Terms);
    }

    [Fact]
    public void Extract_Should_IncludeExpandedGroup_When_HydeProvided()
    {
        var hyde = new HydeQuery("summary", ["a title"], "a body", ["rollback", "recovery"]);

        IReadOnlyList<KeywordGroup> groups = Extractor().Extract(Area(), Doc(literals: ["human phrase here"]), hyde);

        KeywordGroup expanded = groups.Single(g => g.Label == "expanded");
        Assert.Equal(0.60, expanded.Weight);
        Assert.Contains("rollback", expanded.Terms);
    }

    [Fact]
    public void Extract_Should_CapGroupCount_At_MaxKeywordGroups()
    {
        var options = new ImpactMappingOptions();
        options.Keywords.MaxKeywordGroups = 3;
        ImpactedArea area = Area(paths:
        [
            "Deploy/GalaxyEngine.cs", "Runtime/NodeManager.cs", "Security/AuthGuard.cs", "Reporting/ChurnReport.cs",
        ]);

        IReadOnlyList<KeywordGroup> groups = Extractor(options).Extract(
            area, Doc(literals: ["some human phrase"], api: ["Foo(int) -> void"]),
            new HydeQuery("s", [], "b", ["rollback"]));

        Assert.Equal(3, groups.Count);
        Assert.Contains(groups, g => g.Label == "identity");
        Assert.Contains(groups, g => g.Label == "literals");
    }

    [Fact]
    public void Extract_Should_NotDropIdentityOrLiterals_WhenCappedToTwo()
    {
        var options = new ImpactMappingOptions();
        options.Keywords.MaxKeywordGroups = 2;
        ImpactedArea area = Area(paths: ["Deploy/GalaxyEngine.cs", "Runtime/NodeManager.cs"]);

        IReadOnlyList<KeywordGroup> groups = Extractor(options).Extract(
            area, Doc(literals: ["some human phrase"], api: ["Foo(int) -> void"]), hyde: null);

        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, g => g.Label == "identity");
        Assert.Contains(groups, g => g.Label == "literals");
    }

    [Fact]
    public void Extract_Should_CapTermsPerGroup_At_MaxTermsPerGroup()
    {
        var options = new ImpactMappingOptions();
        options.Keywords.MaxTermsPerGroup = 2;

        IReadOnlyList<KeywordGroup> groups = Extractor(options).Extract(
            Area(), Doc(literals: ["alpha bravo charlie delta echo foxtrot"]), hyde: null);

        Assert.All(groups, g => Assert.True(g.Terms.Count <= 2));
    }

    [Fact]
    public void Extract_Should_OrderTermsBySpecificity_LongestFirst()
    {
        IReadOnlyList<KeywordGroup> groups = Extractor().Extract(
            Area(), Doc(literals: ["galaxydeployengine deploy"]), hyde: null);

        KeywordGroup literals = groups.Single(g => g.Label == "literals");
        Assert.Equal("galaxydeployengine", literals.Terms[0]);
    }

    [Fact]
    public void Extract_Should_ProduceStableGroupIds_AcrossRuns()
    {
        ImpactedArea area = Area();
        ChangeDocument doc = Doc(literals: ["Deploy galaxy engine"], api: ["RunDeployment(string) -> Task"]);

        IReadOnlyList<string> first = Extractor().Extract(area, doc, null).Select(g => g.GroupId).ToList();
        IReadOnlyList<string> second = Extractor().Extract(area, doc, null).Select(g => g.GroupId).ToList();

        Assert.Equal(first, second);
    }
}
