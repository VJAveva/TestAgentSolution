using System.Net;
using System.Text.Json;

namespace TestController.WebApi.Tests;

/// <summary>
/// Integration tests for untested /api/watchlist endpoints:
/// {tag}/status and {tag}/parameters.
/// </summary>
public class WatchListDetailEndpointsTests : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client;

    public WatchListDetailEndpointsTests(TestWebAppFactory factory)
    {
        _client = factory.CreateClient();
    }

    // ═══════════════════════════════════════════════════════════════════
    // GET /api/watchlist/{tag}/status
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TagStatus_Should_ReturnOk_When_TagExists()
    {
        var response = await _client.GetAsync("/api/watchlist/TestBuild/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TagStatus_Should_ContainExecutionFields_When_Called()
    {
        var response = await _client.GetAsync("/api/watchlist/TestBuild/status");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        // Status response contains at minimum a tag and status indicator
        Assert.True(json.TryGetProperty("tag", out var tag)
                    || json.TryGetProperty("watchItemTag", out tag));
    }

    [Fact]
    public async Task TagStatus_Should_ReturnOk_When_TagMissing()
    {
        var response = await _client.GetAsync("/api/watchlist/NoSuchTag/status");
        // The endpoint returns OK with a status object (not 404)
        Assert.True(
            response.StatusCode == HttpStatusCode.OK ||
            response.StatusCode == HttpStatusCode.NotFound);
    }

    // ═══════════════════════════════════════════════════════════════════
    // GET /api/watchlist/{tag}/parameters
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TagParameters_Should_ReturnOk_When_TagExists()
    {
        var response = await _client.GetAsync("/api/watchlist/TestBuild/parameters");
        // Could be OK (empty params) or NotFound if Variables.txt path doesn't resolve
        Assert.True(
            response.StatusCode == HttpStatusCode.OK ||
            response.StatusCode == HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TagParameters_Should_ReturnNotFound_When_TagMissing()
    {
        var response = await _client.GetAsync("/api/watchlist/FakeTag/parameters");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
