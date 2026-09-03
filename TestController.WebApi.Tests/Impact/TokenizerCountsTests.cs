using System.Linq;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Text;

namespace TestController.WebApi.Tests.Impact;

public sealed class TokenizerCountsTests
{
    private static readonly ImpactMappingOptions.KeywordOptions Options = new();

    [Fact]
    public void TokenizeWithCounts_Should_CountRepeatedTerms_HigherThanSingle()
    {
        IReadOnlyDictionary<string, int> once = Tokenizer.TokenizeWithCounts("login logout", Options);
        IReadOnlyDictionary<string, int> twice = Tokenizer.TokenizeWithCounts("login login logout", Options);

        Assert.True(twice["login"] > once["login"]);
        Assert.Equal(once["logout"], twice["logout"]);
    }

    [Fact]
    public void TokenizeWithCounts_Should_ShareVocabularyWithTokenize()
    {
        const string text = "GalaxyDeployEngine deploy deploy";

        var distinct = Tokenizer.Tokenize(text, Options).ToHashSet();
        var counted = Tokenizer.TokenizeWithCounts(text, Options).Keys.ToHashSet();

        Assert.Equal(distinct, counted);
    }

    [Fact]
    public void TokenizeWithCounts_Should_ReturnEmpty_When_TextBlank()
    {
        Assert.Empty(Tokenizer.TokenizeWithCounts("   ", Options));
    }
}
