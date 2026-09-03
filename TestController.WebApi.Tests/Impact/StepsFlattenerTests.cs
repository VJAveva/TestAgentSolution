using TestControllerGrpc.Core.Impact.Ado;
using Xunit;

namespace TestController.WebApi.Tests.Impact;

/// <summary>Unit tests for the Test Case steps-XML flattener (P05).</summary>
public class StepsFlattenerTests
{
    [Fact]
    public void Flatten_Should_JoinActionAndExpected_WithArrow()
    {
        const string xml =
            "<steps id=\"0\" last=\"2\">" +
            "<step id=\"2\" type=\"ActionStep\">" +
            "<parameterizedString isformatted=\"true\">&lt;P&gt;Click Deploy&lt;/P&gt;</parameterizedString>" +
            "<parameterizedString isformatted=\"true\">&lt;P&gt;Galaxy deploys&lt;/P&gt;</parameterizedString>" +
            "</step></steps>";

        Assert.Equal("Click Deploy -> Galaxy deploys", StepsFlattener.Flatten(xml));
    }

    [Fact]
    public void Flatten_Should_JoinMultipleSteps_WithNewline()
    {
        const string xml =
            "<steps>" +
            "<step><parameterizedString>&lt;P&gt;One&lt;/P&gt;</parameterizedString><parameterizedString>&lt;P&gt;A&lt;/P&gt;</parameterizedString></step>" +
            "<step><parameterizedString>&lt;P&gt;Two&lt;/P&gt;</parameterizedString><parameterizedString>&lt;P&gt;B&lt;/P&gt;</parameterizedString></step>" +
            "</steps>";

        Assert.Equal("One -> A\nTwo -> B", StepsFlattener.Flatten(xml));
    }

    [Fact]
    public void Flatten_Should_ReturnNull_OnMalformedXml()
    {
        Assert.Null(StepsFlattener.Flatten("<steps><step>oops"));
    }

    [Fact]
    public void Flatten_Should_ReturnNull_OnEmptyOrNull()
    {
        Assert.Null(StepsFlattener.Flatten(null));
        Assert.Null(StepsFlattener.Flatten("   "));
    }
}
