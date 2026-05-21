using System.Text.Json;

namespace TestController.WebApi.Tests;

/// <summary>
/// TEST-002: Integration tests for WebApi telemetry endpoint behavior
/// under different agent states: offline (gRPC unavailable), cached
/// (agent locked/executing), and active execution.
///
/// Category: Contract, Safety
/// </summary>
public class TelemetryFallbackTests : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client;

    public TelemetryFallbackTests(TestWebAppFactory factory)
    {
        _client = factory.CreateClient();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Offline fallback (gRPC unavailable)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetTelemetry_ReturnsOk_WithOfflinePayload_WhenAgentUnreachable()
    {
        var response = await _client.GetAsync("/api/agents/Agent1/telemetry");

        // Should not return 502 or 500 — always 200 with offline metadata
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("isOffline").GetBoolean());
    }

    [Fact]
    public async Task GetTelemetry_OfflineResponse_HasStateOffline()
    {
        var response = await _client.GetAsync("/api/agents/Agent1/telemetry");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Offline", json.GetProperty("state").GetString());
    }

    [Fact]
    public async Task GetTelemetry_OfflineResponse_IncludesErrorMessage()
    {
        var response = await _client.GetAsync("/api/agents/Agent1/telemetry");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(json.TryGetProperty("error", out var errorProp));
        Assert.NotEqual(JsonValueKind.Null, errorProp.ValueKind);
        Assert.False(string.IsNullOrEmpty(errorProp.GetString()));
    }

    [Fact]
    public async Task GetTelemetry_OfflineResponse_HasWarningHeader()
    {
        var response = await _client.GetAsync("/api/agents/Agent1/telemetry");

        Assert.True(response.Headers.Contains("X-Agent-Warning"));
        var values = response.Headers.GetValues("X-Agent-Warning").ToList();
        Assert.Contains("agent-offline", values);
    }

    [Fact]
    public async Task GetTelemetry_OfflineResponse_HasZeroMetrics()
    {
        var response = await _client.GetAsync("/api/agents/Agent1/telemetry");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(0, json.GetProperty("cpuUsagePct").GetDouble());
        Assert.Equal(0, json.GetProperty("memoryUsedMb").GetDouble());
        Assert.Equal(0, json.GetProperty("memoryTotalMb").GetDouble());
        Assert.Equal(0, json.GetProperty("diskFreeGb").GetDouble());
        Assert.Equal(0, json.GetProperty("activeProcessCount").GetInt32());
    }

    [Fact]
    public async Task GetTelemetry_OfflineResponse_ContainsTimestamp()
    {
        var response = await _client.GetAsync("/api/agents/Agent1/telemetry");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(json.TryGetProperty("timestamp", out var ts));
        // Should be a valid ISO date string
        Assert.True(DateTime.TryParse(ts.GetString(), out _));
    }

    [Fact]
    public async Task GetTelemetry_OfflineResponse_PreservesAgentName()
    {
        var response = await _client.GetAsync("/api/agents/Agent1/telemetry");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Agent1", json.GetProperty("agentName").GetString());
    }

    // ═══════════════════════════════════════════════════════════════════
    // Not Found (agent not registered)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetTelemetry_Returns404_WhenAgentNotRegistered()
    {
        var response = await _client.GetAsync("/api/agents/UnknownAgent/telemetry");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetTelemetry_Returns404_ForEmptyName()
    {
        var response = await _client.GetAsync("/api/agents/%20/telemetry");

        // Should be 404 since whitespace-only name is not registered
        Assert.True(
            response.StatusCode == HttpStatusCode.NotFound ||
            response.StatusCode == HttpStatusCode.BadRequest);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Response contract compliance
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetTelemetry_ResponseShape_MatchesContract_ForOfflineAgent()
    {
        var response = await _client.GetAsync("/api/agents/Agent1/telemetry");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        // All telemetry responses — online or offline — must have these fields
        var requiredFields = new[]
        {
            "agentName", "state", "currentActivity", "currentCommand",
            "executionsCompleted", "executionsFailed",
            "cpuUsagePct", "memoryUsedMb", "memoryTotalMb", "diskFreeGb",
            "activeProcessCount", "timestamp", "isOffline"
        };

        foreach (var field in requiredFields)
        {
            Assert.True(json.TryGetProperty(field, out _),
                $"Telemetry response missing required field: '{field}'");
        }
    }

    [Fact]
    public async Task GetTelemetry_NeverReturns502_EvenOnGrpcFailure()
    {
        // Multiple rapid requests — all should return 200, never 502
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => _client.GetAsync("/api/agents/Agent1/telemetry"));

        var responses = await Task.WhenAll(tasks);

        foreach (var r in responses)
        {
            Assert.NotEqual(HttpStatusCode.BadGateway, r.StatusCode);
            Assert.NotEqual(HttpStatusCode.InternalServerError, r.StatusCode);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Successive calls stability
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetTelemetry_SuccessiveCalls_AllReturnConsistentOfflineState()
    {
        // Agent is down — multiple sequential polls should all be consistent
        for (int i = 0; i < 5; i++)
        {
            var response = await _client.GetAsync("/api/agents/Agent1/telemetry");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("isOffline").GetBoolean());
            Assert.Equal("Offline", json.GetProperty("state").GetString());
        }
    }
}
