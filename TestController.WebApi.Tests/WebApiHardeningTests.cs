using System.Text.Json;

namespace TestController.WebApi.Tests;

/// <summary>
/// Integration tests for WebApi hardening features:
/// - Telemetry offline metadata (isOffline, error, lastSeenUtc)
/// - Execution preflight check
/// - Rate limiting
/// </summary>
public class WebApiHardeningTests : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client;

    public WebApiHardeningTests(TestWebAppFactory factory)
    {
        _client = factory.CreateClient();
    }

    // ── Telemetry Offline Response ─────────────────────────────────────

    [Fact]
    public async Task GetTelemetry_ReturnsOfflineResponse_WhenGrpcUnavailable()
    {
        // Agent1 is registered but not running — gRPC call will fail
        var response = await _client.GetAsync("/api/agents/Agent1/telemetry");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Verify offline metadata fields
        Assert.True(json.GetProperty("isOffline").GetBoolean());
        Assert.Equal("Offline", json.GetProperty("state").GetString());
        Assert.True(json.TryGetProperty("error", out var errorProp));
        Assert.NotEqual(JsonValueKind.Null, errorProp.ValueKind);
        Assert.True(json.TryGetProperty("timestamp", out _));
    }

    [Fact]
    public async Task GetTelemetry_ReturnsWarningHeader_WhenAgentOffline()
    {
        var response = await _client.GetAsync("/api/agents/Agent1/telemetry");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Agent-Warning"));
        Assert.Equal("agent-offline", response.Headers.GetValues("X-Agent-Warning").First());
    }

    [Fact]
    public async Task GetTelemetry_ReturnsNotFound_WhenAgentNotRegistered()
    {
        var response = await _client.GetAsync("/api/agents/NonExistentAgent/telemetry");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetTelemetry_ContainsAgentName_InOfflineResponse()
    {
        var response = await _client.GetAsync("/api/agents/Agent1/telemetry");

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Agent1", json.GetProperty("agentName").GetString());
    }

    // ── Execution Preflight ────────────────────────────────────────────

    [Fact]
    public async Task PreflightCheck_ReturnsNotFound_WhenTagNotExists()
    {
        var response = await _client.PostAsync("/api/execution/preflight/NonExistentTag", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PreflightCheck_ReturnsOk_WhenNoRemoteAgentsRequired()
    {
        // "TestBuild" has only local actions (no AgentName specified in minimal test XML)
        var response = await _client.PostAsync("/api/execution/preflight/TestBuild", null);

        // Should be OK since the test WatchList has no AgentName on the Action
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("ready").GetBoolean());
    }

    // ── Rate Limiting ──────────────────────────────────────────────────

    [Fact]
    public async Task AgentEndpoints_AcceptNormalTraffic()
    {
        // Make a few requests — should all succeed under the 60/min limit
        for (int i = 0; i < 5; i++)
        {
            var response = await _client.GetAsync("/api/agents/Agent1/telemetry");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    // ── Telemetry Response Contract ────────────────────────────────────

    [Fact]
    public async Task GetTelemetry_ResponseContract_HasRequiredFields()
    {
        var response = await _client.GetAsync("/api/agents/Agent1/telemetry");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        // All telemetry responses must include these fields
        Assert.True(json.TryGetProperty("agentName", out _));
        Assert.True(json.TryGetProperty("state", out _));
        Assert.True(json.TryGetProperty("currentActivity", out _));
        Assert.True(json.TryGetProperty("cpuUsagePct", out _));
        Assert.True(json.TryGetProperty("memoryUsedMb", out _));
        Assert.True(json.TryGetProperty("diskFreeGb", out _));
        Assert.True(json.TryGetProperty("isOffline", out _));
        Assert.True(json.TryGetProperty("timestamp", out _));
    }

    [Fact]
    public async Task GetTelemetry_OfflineResponse_HasZeroMetrics()
    {
        var response = await _client.GetAsync("/api/agents/Agent1/telemetry");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(0, json.GetProperty("cpuUsagePct").GetDouble());
        Assert.Equal(0, json.GetProperty("memoryUsedMb").GetDouble());
        Assert.Equal(0, json.GetProperty("activeProcessCount").GetInt32());
    }
}
