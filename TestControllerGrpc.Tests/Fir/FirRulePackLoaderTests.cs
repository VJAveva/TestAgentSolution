using TestControllerGrpc.Core.Fir;

namespace TestControllerGrpc.Tests.Fir;

/// <summary>
/// The engine must carry no product knowledge, so a new team's support has to arrive as a file. These
/// use a made-up framework with a trace format unlike WASSmokeTest's on purpose.
/// </summary>
public class FirRulePackLoaderTests
{
    private const string ContosoPack =
        """
        {
          "id": "Contoso", "kind": "Framework", "version": 2,
          "appliesTo": { "outputContains": [ "CONTOSO-RUN" ] },
          "rowMarker": "=== STEP (?<row>\\d+) ===",
          "stepLine": "^(?<level>OK|BAD): \\[(?<kw>[^\\]]+)\\] (?<msg>.*)$",
          "failLevels": [ "BAD" ]
        }
        """;

    private const string ContosoTrace =
        """
        CONTOSO-RUN start
        === STEP 1 ===
        OK: [Login] user signed in
        === STEP 2 ===
        BAD: [Checkout] cart total mismatch
        """;

    private static FirTrxInput Failed(string stdOut) => new()
    {
        TestName = "Checkout_Should_Total",
        ClassName = "Contoso.Shop.Tests.CheckoutTests",
        Outcome = "Failed",
        ErrorMessage = "Assert.AreEqual failed.",
        StdOut = stdOut,
    };

    private static IReadOnlyList<FirRulePack> Load(TempDirectory dir)
    {
        var result = FirRulePackLoader.LoadDirectory(dir.Path);
        return result.Packs;
    }

    [Fact]
    public void Analyze_Should_UseANewPack_When_ItIsDroppedInWithNoCodeChange()
    {
        using var dir = new TempDirectory("FirPacks");
        dir.CreateFile("Contoso" + FirRulePackLoader.FileSuffix, ContosoPack);

        var report = new FirAnalyzer(Load(dir)).Analyze(Failed(ContosoTrace))!;

        Assert.Equal(["generic", "Contoso@2"], report.RulePacks);
        Assert.Equal(2, report.Root!.Row);
        Assert.Equal("Checkout", report.Root.Keyword);
    }

    [Fact]
    public void Analyze_Should_IgnoreThePack_When_ItsFingerprintIsAbsentFromTheOutput()
    {
        using var dir = new TempDirectory("FirPacks");
        dir.CreateFile("Contoso" + FirRulePackLoader.FileSuffix, ContosoPack);

        var report = new FirAnalyzer(Load(dir)).Analyze(Failed("a trace from some other product"))!;

        Assert.Equal(["generic"], report.RulePacks);
        Assert.Empty(report.Rows);
    }

    [Fact]
    public void Select_Should_RequireEveryFingerprintEntry_When_AppliesToListsSeveral()
    {
        var pack = new FirRulePack
        {
            Id = "Two",
            AppliesTo = new FirPackAppliesTo { OutputContains = ["alpha", "omega"] },
        };

        Assert.False(FirPackSelector.Applies(pack, Failed("alpha only"), "alpha only"));
        Assert.True(FirPackSelector.Applies(pack, Failed("alpha and omega"), "alpha and omega"));
    }

    [Fact]
    public void LoadDirectory_Should_KeepTheGoodPacks_When_AnotherTeamsFileIsMalformed()
    {
        using var dir = new TempDirectory("FirPacks");
        dir.CreateFile("Contoso" + FirRulePackLoader.FileSuffix, ContosoPack);
        dir.CreateFile("Broken" + FirRulePackLoader.FileSuffix, "{ not json");

        var result = FirRulePackLoader.LoadDirectory(dir.Path);

        Assert.Equal(["Contoso"], result.Packs.Select(p => p.Id));
        Assert.Single(result.Errors);
        Assert.Contains("Broken", result.Errors[0]);
    }

    [Fact]
    public void LoadDirectory_Should_RejectThePack_When_ItDeclaresNoAppliesTo()
    {
        using var dir = new TempDirectory("FirPacks");
        dir.CreateFile("Greedy" + FirRulePackLoader.FileSuffix, """{ "id": "Greedy", "kind": "Product" }""");

        var result = FirRulePackLoader.LoadDirectory(dir.Path);

        // Without appliesTo a stray file would claim every test it was offered.
        Assert.Empty(result.Packs);
        Assert.Contains("never be selected", result.Errors[0]);
    }

    [Fact]
    public void LoadDirectory_Should_PutFrameworkBeforeProduct_When_BothApply()
    {
        using var dir = new TempDirectory("FirPacks");
        dir.CreateFile("Zeta" + FirRulePackLoader.FileSuffix,
            """{ "id": "Zeta", "kind": "Product", "appliesTo": { "outputContains": [ "z" ] } }""");
        dir.CreateFile("Alpha" + FirRulePackLoader.FileSuffix, ContosoPack);

        var packs = FirRulePackLoader.LoadDirectory(dir.Path).Packs;

        Assert.Equal([FirPackKind.Framework, FirPackKind.Product], packs.Select(p => p.Kind));
    }

    [Fact]
    public void LoadDirectory_Should_ReturnNothing_When_TheRulesFolderIsAbsent()
    {
        var result = FirRulePackLoader.LoadDirectory(Path.Combine(Path.GetTempPath(), "no-such-fir-rules"));

        Assert.Empty(result.Packs);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Create_Should_RecordAnError_When_APackPatternDoesNotCompile()
    {
        var pack = new FirRulePack
        {
            Id = "Bad",
            AppliesTo = new FirPackAppliesTo { OutputContains = ["x"] },
            RowMarker = "=== (?<row>\\d+ ===",
        };

        var rules = FirRuleSet.Create([pack]);

        Assert.NotEmpty(rules.Errors);
        Assert.False(rules.CanParseSteps);
    }

    [Fact]
    public void Analyze_Should_StillProduceAReport_When_ThePacksFolderIsEmpty()
    {
        using var dir = new TempDirectory("FirPacks");

        var report = new FirAnalyzer(Load(dir)).Analyze(Failed(ContosoTrace))!;

        Assert.Equal(["generic"], report.RulePacks);
        Assert.NotEmpty(report.Verdict);
    }
}
