using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TestController.WebApi.Tests;

/// <summary>
/// Integration tests for the Health/Diagnostics API endpoints
/// (HealthController mapped via shared Api layer).
/// </summary>
public class HealthEndpointsTests : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client;

    public HealthEndpointsTests(TestWebAppFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_Should_ReturnOk_When_Called()
    {
        var response = await _client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_Should_ReturnExpectedShape_When_Called()
    {
        var response = await _client.GetAsync("/api/health");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("ok", json.GetProperty("status").GetString());
        Assert.True(json.TryGetProperty("timestamp", out _));
        Assert.True(json.TryGetProperty("server", out _));
        Assert.True(json.TryGetProperty("components", out var components));
        Assert.True(components.TryGetProperty("watchList", out _));
        Assert.True(components.TryGetProperty("agents", out _));
        Assert.True(components.TryGetProperty("execution", out _));
    }

    [Fact]
    public async Task Diagnostics_Should_ReturnOk_When_Called()
    {
        var response = await _client.GetAsync("/api/health/diagnostics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Diagnostics_Should_ReturnDetailedInfo_When_Called()
    {
        var response = await _client.GetAsync("/api/health/diagnostics");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(json.TryGetProperty("watchList", out _));
        Assert.True(json.TryGetProperty("agents", out _));
        Assert.True(json.TryGetProperty("execution", out _));
        Assert.True(json.TryGetProperty("environment", out var env));
        Assert.True(env.TryGetProperty("machineName", out _));
        Assert.True(env.TryGetProperty("dotnetVersion", out _));
    }

    [Fact]
    public async Task GetLogs_Should_ReturnOk_When_Called()
    {
        var response = await _client.GetAsync("/api/health/logs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetLogs_Should_ReturnEntries_When_Called()
    {
        var response = await _client.GetAsync("/api/health/logs?count=10");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(json.TryGetProperty("entries", out _));
        Assert.True(json.TryGetProperty("count", out _));
    }

    [Fact]
    public async Task GetLogs_Should_FilterByComponent_When_ComponentSpecified()
    {
        var response = await _client.GetAsync("/api/health/logs?component=NonExistentComponent");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(0, json.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task GetLogFiles_Should_ReturnOk_When_Called()
    {
        var response = await _client.GetAsync("/api/health/log-files");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
