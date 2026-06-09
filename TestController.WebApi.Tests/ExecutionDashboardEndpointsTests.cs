using System.Net;
using System.Text.Json;

namespace TestController.WebApi.Tests;

/// <summary>
/// Integration tests for untested /api/execution endpoints:
/// dashboard-sessions, demo-sessions, cancel-session, GET session by id.
/// </summary>
public class ExecutionDashboardEndpointsTests : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client;

    public ExecutionDashboardEndpointsTests(TestWebAppFactory factory)
    {
        _client = factory.CreateClient();
    }

    // ═══════════════════════════════════════════════════════════════════
    // GET /api/execution/dashboard-sessions
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DashboardSessions_Should_ReturnOk_When_Called()
    {
        var response = await _client.GetAsync("/api/execution/dashboard-sessions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DashboardSessions_Should_ReturnObjectShape_When_Called()
    {
        var response = await _client.GetAsync("/api/execution/dashboard-sessions");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Object, json.ValueKind);
    }

    // ═══════════════════════════════════════════════════════════════════
    // GET /api/execution/demo-sessions
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DemoSessions_Should_ReturnOk_When_Called()
    {
        var response = await _client.GetAsync("/api/execution/demo-sessions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DemoSessions_Should_ReturnObjectShape_When_Called()
    {
        var response = await _client.GetAsync("/api/execution/demo-sessions");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(
            json.ValueKind == JsonValueKind.Object ||
            json.ValueKind == JsonValueKind.Array);
    }

    // ═══════════════════════════════════════════════════════════════════
    // GET /api/execution/{sessionId}
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetSession_Should_ReturnNotFound_When_IdDoesNotExist()
    {
        var response = await _client.GetAsync("/api/execution/non-existent-id");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // POST /api/execution/{sessionId}/cancel  (by session id)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CancelSession_Should_ReturnNotFound_When_SessionDoesNotExist()
    {
        var response = await _client.PostAsync(
            "/api/execution/fake-session-id/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
