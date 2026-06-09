using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TestController.WebApi.Tests;

/// <summary>
/// Integration tests for the deployment operations endpoints:
/// maintenance mode, preflight checks, and capabilities.
/// </summary>
public class DeploymentEndpointsTests : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client;

    public DeploymentEndpointsTests(TestWebAppFactory factory)
    {
        _client = factory.CreateClient();
    }

    // ─── GET /api/deployment/status ───

    [Fact]
    public async Task GetDeploymentStatus_Should_ReturnOk()
    {
        var response = await _client.GetAsync("/api/deployment/status");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("maintenanceMode", out var mode));
        Assert.Equal(JsonValueKind.False, mode.ValueKind);
    }

    [Fact]
    public async Task GetDeploymentStatus_Should_IncludeActiveSessionCount()
    {
        var response = await _client.GetAsync("/api/deployment/status");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(json.TryGetProperty("activeSessionCount", out var count));
        Assert.Equal(0, count.GetInt32());
    }

    // ─── POST /api/deployment/maintenance/enable + disable ───

    [Fact]
    public async Task MaintenanceEnable_Should_ActivateMaintenanceMode()
    {
        var response = await _client.PostAsync("/api/deployment/maintenance/enable?reason=Test", null);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("maintenanceMode").GetBoolean());
        Assert.Equal("Test", json.GetProperty("reason").GetString());

        // Verify status reflects it
        var status = await _client.GetFromJsonAsync<JsonElement>("/api/deployment/status");
        Assert.True(status.GetProperty("maintenanceMode").GetBoolean());

        // Disable for other tests
        await _client.PostAsync("/api/deployment/maintenance/disable", null);
    }

    [Fact]
    public async Task MaintenanceDisable_Should_DeactivateMaintenanceMode()
    {
        // Enable first
        await _client.PostAsync("/api/deployment/maintenance/enable?reason=Cleanup", null);

        // Disable
        var response = await _client.PostAsync("/api/deployment/maintenance/disable", null);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("maintenanceMode").GetBoolean());
        Assert.Equal("Cleanup", json.GetProperty("previousReason").GetString());
    }

    // ─── GET /api/deployment/preflight ───

    [Fact]
    public async Task PreflightCheck_Should_ReturnSafe_When_NoActiveSessions()
    {
        var response = await _client.GetAsync("/api/deployment/preflight");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("safe").GetBoolean());
        Assert.Contains("safe to deploy", json.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task PreflightCheck_Should_IncludeAgentCounts()
    {
        var response = await _client.GetAsync("/api/deployment/preflight");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        var agents = json.GetProperty("agents");
        Assert.True(agents.GetProperty("total").GetInt32() > 0);
    }

    // ─── GET /api/agents/{name}/capabilities ───

    [Fact]
    public async Task GetCapabilities_Should_Return502_When_AgentUnreachable()
    {
        // Agent1 is configured but not running in tests
        var response = await _client.GetAsync("/api/agents/Agent1/capabilities");

        // Expect 502 since we can't actually reach Agent1 in test environment
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task GetCapabilities_Should_Return404_When_AgentNotFound()
    {
        var response = await _client.GetAsync("/api/agents/NonExistentAgent/capabilities");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
