using System.Net;
using System.Text.Json;

namespace TestController.WebApi.Tests;

/// <summary>
/// Tests for API hardening: RFC 7807 problem responses, audit trails,
/// and force-release/cancel behavior.
/// </summary>
public class ApiHardeningTests : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client;
    private readonly TestWebAppFactory _factory;

    public ApiHardeningTests(TestWebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ─────────────────────────────────────────────────────────────────
    // API-002: Consistent problem+json errors for invalid tags
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Trigger_InvalidTag_ReturnsProblemJson()
    {
        var response = await _client.PostAsync("/api/execution/trigger/NonExistentTag99", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("title", out var title));
        Assert.Equal("WatchItem not found", title.GetString());
        Assert.True(json.TryGetProperty("status", out var status));
        Assert.Equal(404, status.GetInt32());
        Assert.True(json.TryGetProperty("detail", out _));
    }

    [Fact]
    public async Task CanTrigger_InvalidTag_ReturnsProblemJson()
    {
        var response = await _client.GetAsync("/api/execution/can-trigger/NonExistentTag99");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("title", out var title));
        Assert.Equal("WatchItem not found", title.GetString());
        Assert.True(json.TryGetProperty("status", out var status));
        Assert.Equal(404, status.GetInt32());
    }

    [Fact]
    public async Task WatchList_Parameters_InvalidTag_ReturnsProblemJson()
    {
        var response = await _client.GetAsync("/api/watchlist/TestBuild/parameters");
        // TestBuild exists, so this should work fine
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Now test with non-existent tag
        var response2 = await _client.GetAsync("/api/watchlist/NonExistentTag99/parameters");
        Assert.Equal(HttpStatusCode.NotFound, response2.StatusCode);
        var json = await response2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("title", out var title));
        Assert.Equal("WatchItem not found", title.GetString());
    }

    // ─────────────────────────────────────────────────────────────────
    // API-003: Force-release audit trail (existence verification)
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ForceReleaseAll_WPF_ReturnsOk_WithAuditableResponse()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/execution/force-release-all");
        request.Headers.Add("X-Source", "WPF");
        request.Headers.Add("X-User-Id", "admin-user");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("message", out _));
    }

    [Fact]
    public async Task ForceRelease_403_ReturnsProblemJson_WhenNotAdmin()
    {
        using var nonAdminClient = _factory.CreateNonAdminClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/execution/force-release/SomeAgent");

        var response = await nonAdminClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("title", out var title));
        Assert.Equal("Forbidden", title.GetString());
        Assert.True(json.TryGetProperty("status", out var status));
        Assert.Equal(403, status.GetInt32());
    }

    [Fact]
    public async Task ForceRelease_404_ReturnsProblemJson_WhenAgentNotLocked()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/execution/force-release/UnknownAgent");
        request.Headers.Add("X-Source", "WPF");
        request.Headers.Add("X-User-Id", "admin");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("title", out var title));
        Assert.Equal("Resource not found", title.GetString());
        Assert.True(json.TryGetProperty("status", out var status));
        Assert.Equal(404, status.GetInt32());
    }

    // ─────────────────────────────────────────────────────────────────
    // API-004: Cancel session errors are RFC 7807 compliant
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelSession_NotFound_ReturnsProblemJson()
    {
        var response = await _client.PostAsync("/api/execution/nosession123/cancel", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("title", out var title));
        Assert.Equal("Resource not found", title.GetString());
        Assert.True(json.TryGetProperty("status", out var status));
        Assert.Equal(404, status.GetInt32());
    }

    [Fact]
    public async Task GetRecentLogs_NotFound_ReturnsProblemJson()
    {
        var response = await _client.GetAsync("/api/execution/nosession123/recent-logs");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("title", out var title));
        Assert.Equal("Resource not found", title.GetString());
    }
}
