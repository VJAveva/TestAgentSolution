using TestControllerGrpc.Core.Impact.Ado;
using Xunit;

namespace TestController.WebApi.Tests.Impact;

/// <summary>Unit tests for the WIQL literal escaper (P05 safety).</summary>
public class WiqlEscaperTests
{
    [Fact]
    public void EscapeLiteral_Should_DoubleSingleQuotes()
    {
        Assert.Equal("O''Brien", WiqlEscaper.EscapeLiteral("O'Brien"));
    }

    [Fact]
    public void EscapeLiteral_Should_ReturnEmpty_ForNull()
    {
        Assert.Equal("", WiqlEscaper.EscapeLiteral(null));
    }

    [Theory]
    [InlineData("line1\nline2")]
    [InlineData("tab\there")]
    public void EscapeLiteral_Should_Throw_OnControlCharacters(string value)
    {
        Assert.Throws<ArgumentException>(() => WiqlEscaper.EscapeLiteral(value));
    }
}
