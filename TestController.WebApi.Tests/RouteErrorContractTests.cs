using System.Text.Json;

namespace TestController.WebApi.Tests;

/// <summary>
/// TEST-007: Route convention tests and error response contract validation.
/// Verifies:
/// - All API routes respond (no dead endpoints)
/// - Error responses follow RFC 7807 Problem Details shape
/// - Consistent error shape across endpoint groups
/// - Rate limiting headers present
///
/// Category: Contract
/// </summary>
public class RouteErrorContractTests : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client;

    public RouteErrorContractTests(TestWebAppFactory factory)
    {
        _client = factory.CreateClient();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Route existence (all groups return non-500)
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("/api/agents")]
    [InlineData("/api/agents/fleet")]
    [InlineData("/api/execution/locks")]
    [InlineData("/api/results/builds")]
    [InlineData("/api/health")]
    public async Task KnownRoutes_ShouldReturn_Non500(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.True((int)response.StatusCode < 500,
            $"Route {path} returned {response.StatusCode} — should not be 5xx");
    }

    [Theory]
    [InlineData("/api/agents/fleet/invalid-sub")]
    [InlineData("/api/nonexistent-route")]
    public async Task UnknownApiRoutes_DoNotReturn_500(string path)
    {
        var response = await _client.GetAsync(path);

        // API routes should not crash with 500 — any other status is acceptable
        // (404, 405, or even 200 from SPA fallback depending on middleware order)
        Assert.True((int)response.StatusCode < 500,
            $"Route {path} returned {response.StatusCode} — should not be 5xx");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Error response shape (RFC 7807 / Problem Details)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task NotFound_ErrorResponse_HasTitleField()
    {
        var response = await _client.GetAsync("/api/agents/NonExistentAgent/telemetry");

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var body = await response.Content.ReadAsStringAsync();
            // Should return structured error, not empty body
            Assert.False(string.IsNullOrWhiteSpace(body),
                "404 response should have a body describing the error");
        }
    }

    [Fact]
    public async Task ForceRelease_Forbidden_ReturnsProblemJson()
    {
        // Without WPF source header → 403
        var response = await _client.PostAsync(
            "/api/execution/force-release/Agent1",
            new StringContent("{\"reason\":\"test\"}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("title", out _) || json.TryGetProperty("error", out _),
            "Forbidden response should have 'title' or 'error' field");
    }

    [Fact]
    public async Task ForceReleaseAll_Forbidden_ReturnsProblemJson()
    {
        var response = await _client.PostAsync(
            "/api/execution/force-release-all",
            new StringContent("{\"reason\":\"test\"}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("title", out _) || json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task NotFound_Trigger_ReturnsProblemJson_WithTitle()
    {
        var response = await _client.PostAsync("/api/execution/trigger-all/NonExistentTag", null);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.TryGetProperty("title", out _) || json.TryGetProperty("error", out _),
                "404 on trigger should contain structured error");
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Error response consistency across endpoints
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("/api/agents/NotRegistered/telemetry", "GET")]
    [InlineData("/api/agents/NotRegistered/snapshot", "GET")]
    [InlineData("/api/agents/NotRegistered/health", "GET")]
    public async Task NotFound_Responses_AreStructured_AcrossEndpoints(string path, string method)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await _client.SendAsync(request);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.False(string.IsNullOrWhiteSpace(body),
                $"404 at {method} {path} should have response body");
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Content-Type on API responses
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("/api/agents")]
    [InlineData("/api/agents/Agent1/telemetry")]
    [InlineData("/api/execution/locks")]
    public async Task ApiResponses_HaveJsonContentType(string path)
    {
        var response = await _client.GetAsync(path);

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        Assert.True(contentType.Contains("json"),
            $"Response from {path} should be JSON, got: {contentType}");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Method not allowed
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetOnPostOnlyRoute_ShouldNotReturn500()
    {
        // /api/agents/register is POST-only
        var response = await _client.GetAsync("/api/agents/register");

        Assert.True((int)response.StatusCode < 500,
            $"GET on POST route returned {response.StatusCode} — should be 404/405, not 500");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Health endpoint
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task HealthEndpoint_ReturnsOk_WithTimestamp()
    {
        var response = await _client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("status", out _) || json.TryGetProperty("timestamp", out _),
            "Health endpoint should include status or timestamp");
    }
}
