using TestControllerGrpc.Ado;
using Xunit;

namespace TestController.WebApi.Tests.Regression;

/// <summary>
/// A conditional-access or sign-in block answers with a full HTML page carrying an inline base64 image.
/// Appending that verbatim put ~40 KB of markup into the Code Churn error banner, the app log and
/// /api/impact/health, which made the actual failure unreadable.
/// </summary>
public class AdoErrorBodySummaryTests
{
    private const string ConditionalAccessPage = """
        <!DOCTYPE html>
        <html><head><title>VS403463: The conditional access policy defined by your Microsoft Entra administrator has failed.</title>
        <style type="text/css">html { height: 100%; }</style></head>
        <body><img src="data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAA+gAAAPoCAYAAABNo9TkAAAACXBIWXMAABYIAAAWJQFJUiTw" />
        <div class="title">403 - Uh-oh, you do not have access.</div></body></html>
        """;

    [Fact]
    public void SummarizeErrorBody_Should_ReturnOnlyTheTitle_When_BodyIsAnHtmlPolicyPage()
    {
        var summary = AdoClient.SummarizeErrorBody(ConditionalAccessPage);

        Assert.Equal(
            "VS403463: The conditional access policy defined by your Microsoft Entra administrator has failed.",
            summary);
    }

    [Fact]
    public void SummarizeErrorBody_Should_DropBase64Payload_When_BodyIsAnHtmlPolicyPage()
    {
        var summary = AdoClient.SummarizeErrorBody(ConditionalAccessPage);

        Assert.DoesNotContain("base64", summary);
        Assert.DoesNotContain("<", summary);
        Assert.True(summary.Length < 300, $"summary was {summary.Length} chars");
    }

    [Fact]
    public void SummarizeErrorBody_Should_ExplainItself_When_HtmlHasNoTitle()
    {
        var summary = AdoClient.SummarizeErrorBody("<html><body>no title here</body></html>");

        Assert.Contains("HTML page instead of JSON", summary);
    }

    [Fact]
    public void SummarizeErrorBody_Should_PreserveText_When_BodyIsJson()
    {
        const string json = """{"message":"TF400813: The user is not authorized."}""";

        Assert.Equal(json, AdoClient.SummarizeErrorBody(json));
    }

    [Fact]
    public void SummarizeErrorBody_Should_Truncate_When_BodyIsLongNonHtml()
    {
        var summary = AdoClient.SummarizeErrorBody(new string('x', 4000));

        Assert.EndsWith("...", summary);
        Assert.True(summary.Length <= 503, $"summary was {summary.Length} chars");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SummarizeErrorBody_Should_ReturnEmpty_When_BodyIsBlank(string? body)
    {
        Assert.Equal(string.Empty, AdoClient.SummarizeErrorBody(body));
    }
}
