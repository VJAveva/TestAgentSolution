using Microsoft.AspNetCore.Http;
using TestController.WebApi.Services;
using Xunit;

namespace TestController.WebApi.Tests.Regression;

/// <summary>
/// Which requests get served by the controller instead of this host. Getting the set wrong is the difference
/// between Code Churn working and diagnostics reporting the wrong machine's state.
/// </summary>
public sealed class ImpactForwardingMiddlewareTests
{
    private static HttpRequest Request(string path, string method = "GET")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        return context.Request;
    }

    [Theory]
    [InlineData("/api/impact/consolidated")]
    [InlineData("/api/impact/scope")]
    [InlineData("/api/impact/summary")]
    [InlineData("/api/impact/ai-summary")]
    [InlineData("/api/impact/connection")]
    [InlineData("/api/impact/components")]
    [InlineData("/api/impact/components/4017/builds")]
    [InlineData("/api/impact/branches")]
    [InlineData("/api/impact/sync-status")]
    public void ShouldForward_Should_ReturnTrue_When_ImpactReadRequested(string path)
        => Assert.True(ImpactForwardingMiddleware.ShouldForward(Request(path)));

    [Theory]
    [InlineData("/api/impact/health")]
    [InlineData("/api/impact/ado-auth-config")]
    public void ShouldForward_Should_ReturnFalse_When_EndpointMustDescribeThisHost(string path)
        => Assert.False(ImpactForwardingMiddleware.ShouldForward(Request(path)));

    [Fact]
    public void ShouldForward_Should_IgnoreCase_When_MatchingExcludedPaths()
        => Assert.False(ImpactForwardingMiddleware.ShouldForward(Request("/API/Impact/Health")));

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public void ShouldForward_Should_ReturnFalse_When_RequestIsNotAGet(string method)
        => Assert.False(ImpactForwardingMiddleware.ShouldForward(Request("/api/impact/consolidated", method)));

    [Theory]
    [InlineData("/api/execution/sessions")]
    [InlineData("/api/watchlist")]
    [InlineData("/health/impact-index")]
    [InlineData("/api/impactful/thing")]
    public void ShouldForward_Should_ReturnFalse_When_PathIsOutsideImpact(string path)
        => Assert.False(ImpactForwardingMiddleware.ShouldForward(Request(path)));
}
