using System.Linq;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Text;
using Xunit;

namespace TestController.WebApi.Tests.Impact;

/// <summary>Unit tests for the shared <see cref="Tokenizer"/> (P03 / P27).</summary>
public class TokenizerTests
{
    [Theory]
    [InlineData("GalaxyDeployEngine", new[] { "galaxy", "deploy", "engine" })]
    [InlineData("HTTPRequestHandler", new[] { "http", "request", "handler" })]
    [InlineData("deploy_engine_v2", new[] { "deploy", "engine", "v2" })]
    [InlineData("Runtime.Node.Manager", new[] { "runtime", "node", "manager" })]
    public void SplitIdentifier_Should_SplitAllConventions(string input, string[] expected)
    {
        Assert.Equal(expected, Tokenizer.SplitIdentifier(input));
    }

    [Fact]
    public void Tokenize_Should_EmitCompoundAndParts()
    {
        var t = Tokenizer.Tokenize("GalaxyDeployEngine", new ImpactMappingOptions.KeywordOptions());

        Assert.Contains("galaxydeployengine", t);
        Assert.Contains("galaxy", t);
        Assert.Contains("deploy", t);
        Assert.Contains("engine", t);
    }

    [Fact]
    public void Tokenize_Should_DropStopWordsShortAndNumeric()
    {
        var t = Tokenizer.Tokenize("src core 12345 deploy", new ImpactMappingOptions.KeywordOptions());

        Assert.DoesNotContain("src", t);   // stop word
        Assert.DoesNotContain("core", t);  // stop word
        Assert.DoesNotContain("12345", t); // pure numeric
        Assert.Contains("deploy", t);
    }

    [Fact]
    public void Tokenize_Should_BeDeterministicAndDeduplicated()
    {
        var opts = new ImpactMappingOptions.KeywordOptions();

        var a = Tokenizer.Tokenize("Deploy deploy DEPLOY galaxy", opts);
        var b = Tokenizer.Tokenize("Deploy deploy DEPLOY galaxy", opts);

        Assert.Equal(a, b);
        Assert.Single(a.Where(x => x == "deploy"));
    }
}
