using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Query;

namespace TestController.WebApi.Tests.Impact;

public sealed class ChangeDocumentBuilderTests
{
    private static ChangeDocumentBuilder Builder() => new(Options.Create(new ImpactMappingOptions()));

    private static ImpactedArea Area() => new(
        "AREA-1", "Galaxy Deploy", "Deploy", "GalaxyVob",
        ChangedPaths: [], DeclaredRegressionAreas: [], RiskTier.High,
        new ChurnMetrics(0, 0, 0, 0, 0, DateTimeOffset.UnixEpoch));

    private static ChangePayload Payload(string path, string hunk, string? title = null, string? description = null)
        => new(
            PullRequestId: 42, PrTitle: title, PrDescription: description, CommitMessages: [],
            Diffs: [new FileDiff(path, [new DiffHunk(1, hunk.Split('\n').Length, hunk)])],
            LinkedWorkItemIds: []);

    [Fact]
    public void Build_Should_ExtractHumanLiteral_PublicApi_And_Symbols_FromCSharp()
    {
        const string hunk =
            "+    public async Task<int> DeployGalaxy(string vobName)\n" +
            "+    {\n" +
            "+        _logger.Info(\"Deploy\", \"Starting galaxy deployment for the selected node\");\n" +
            "+        return await _engine.RunDeployment(vobName);\n" +
            "+    }\n";

        ChangeDocument doc = Builder().Build(Area(), Payload("src/Deploy/GalaxyEngine.cs", hunk));

        Assert.Contains("Starting galaxy deployment for the selected node", doc.ChangedLiterals);
        Assert.Contains("DeployGalaxy(string) -> Task<int>", doc.PublicApiChanges);
        Assert.Contains("DeployGalaxy", doc.ChangedSymbols);
        Assert.Contains("RunDeployment", doc.ChangedSymbols);
    }

    [Fact]
    public void Build_Should_ExcludePathsGuidsFormatsAndDottedIds_FromLiterals()
    {
        const string hunk =
            "+        var path = \"src/main/deploy.cs\";\n" +
            "+        var id = \"550e8400-e29b-41d4-a716-446655440000\";\n" +
            "+        var ns = \"System.Text.Json\";\n" +
            "+        _log.LogInformation(\"Processed {Count} items\", count);\n" +
            "+        var tiny = \"abc\";\n";

        ChangeDocument doc = Builder().Build(Area(), Payload("src/Deploy/Thing.cs", hunk));

        Assert.Empty(doc.ChangedLiterals);
    }

    [Fact]
    public void Build_Should_NotThrow_And_ProduceDocument_When_CSharpMalformed()
    {
        const string hunk =
            "+    public void Broken( {{{ not valid c#\n" +
            "+    garbage ]]] <<<\n";

        ChangeDocument doc = Builder().Build(Area(), Payload("src/Deploy/Broken.cs", hunk));

        Assert.NotNull(doc);
        Assert.NotEmpty(doc.Fingerprint);
    }

    [Fact]
    public void Build_Should_RepeatLiteralsInRawText_ForTermFrequency()
    {
        const string literal = "Starting galaxy deployment for the selected node";
        const string hunk =
            "+        _logger.Info(\"Deploy\", \"Starting galaxy deployment for the selected node\");\n";

        ChangeDocument doc = Builder().Build(Area(), Payload("src/Deploy/GalaxyEngine.cs", hunk));

        int occurrences = doc.RawText.Split(literal).Length - 1;
        Assert.True(occurrences >= 3, $"Expected the literal to be repeated at least 3 times, saw {occurrences}.");
    }

    [Fact]
    public void Build_Should_ProduceLowercaseSha256Fingerprint_OfRawText()
    {
        ChangeDocument doc = Builder().Build(Area(),
            Payload("src/Deploy/GalaxyEngine.cs", "+        _engine.RunDeployment(vob);\n"));

        string expected = Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(doc.RawText)))
            .ToLowerInvariant();

        Assert.Equal(expected, doc.Fingerprint);
    }

    [Fact]
    public void Build_Should_BeDeterministic_ForSameInput()
    {
        ChangePayload payload = Payload("src/Deploy/GalaxyEngine.cs", "+        _engine.RunDeployment(vob);\n");

        ChangeDocument first = Builder().Build(Area(), payload);
        ChangeDocument second = Builder().Build(Area(), payload);

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(first.RawText, second.RawText);
    }

    [Fact]
    public void Build_Should_StripNoise_FromPrNarrative()
    {
        ChangePayload payload = Payload(
            "src/Deploy/GalaxyEngine.cs",
            "+        _engine.RunDeployment(vob);\n",
            title: "Fix login bug",
            description: "See #123 and https://ado/foo. **Important** update to `LoginService` per [docs](http://x).");

        ChangeDocument doc = Builder().Build(Area(), payload);

        Assert.NotNull(doc.PrNarrative);
        Assert.Contains("LoginService", doc.PrNarrative);
        Assert.Contains("docs", doc.PrNarrative);
        Assert.DoesNotContain("#123", doc.PrNarrative);
        Assert.DoesNotContain("http", doc.PrNarrative);
    }

    [Fact]
    public void Build_Should_ExtractLiterals_FromNonCSharpFile()
    {
        const string hunk = "+  \"welcomeMessage\": \"Welcome to the deployment console\"\n";

        ChangeDocument doc = Builder().Build(Area(), Payload("config/app.json", hunk));

        Assert.Contains("Welcome to the deployment console", doc.ChangedLiterals);
    }

    [Fact]
    public void Build_Should_TokenizePaths()
    {
        ChangeDocument doc = Builder().Build(Area(),
            Payload("TestControllerGrpc.Core/Ado/AdoClient.cs", "+        _engine.RunDeployment(vob);\n"));

        Assert.Contains("client", doc.PathTokens);
    }
}
