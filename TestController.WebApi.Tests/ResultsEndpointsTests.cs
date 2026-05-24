using System.Text.Json;

namespace TestController.WebApi.Tests;

/// <summary>
/// Integration tests for /api/results endpoints.
/// Uses a dedicated factory instance to seed test .trx data.
/// </summary>
public class ResultsEndpointsTests : IDisposable
{
    private readonly TestWebAppFactory _factory;
    private readonly HttpClient _client;

    public ResultsEndpointsTests()
    {
        _factory = new TestWebAppFactory();

        // Seed build results before creating the client
        _factory.SeedBuildResult("Build100", total: 20, passed: 18, failed: 2);
        _factory.SeedBuildResult("Build101", total: 10, passed: 10, failed: 0);

        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // ?????????????????????????????????????????????????????????????????
    // GET /api/results/builds
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public async Task ListBuilds_Should_ReturnOk_When_BuildsExist()
    {
        var response = await _client.GetAsync("/api/results/builds");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Build100", body);
        Assert.Contains("Build101", body);
    }

    [Fact]
    public async Task ListBuilds_Should_ReturnBuildStats_When_Called()
    {
        var response = await _client.GetAsync("/api/results/builds");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(JsonValueKind.Object, json.ValueKind);
        var items = json.GetProperty("items");
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        Assert.True(json.GetProperty("totalCount").GetInt32() > 0);
        Assert.Equal(1, json.GetProperty("page").GetInt32());

        // Find Build100
        var found = false;
        foreach (var item in items.EnumerateArray())
        {
            if (item.GetProperty("buildNumber").GetString() == "Build100")
            {
                Assert.Equal(20, item.GetProperty("totalTests").GetInt32());
                Assert.Equal(18, item.GetProperty("passedTests").GetInt32());
                Assert.Equal(2, item.GetProperty("failedTests").GetInt32());
                found = true;
                break;
            }
        }
        Assert.True(found, "Build100 not found in results.");
    }

    [Fact]
    public async Task ListBuilds_Should_ReturnHealthStatus_When_Called()
    {
        var response = await _client.GetAsync("/api/results/builds");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        foreach (var item in json.GetProperty("items").EnumerateArray())
        {
            Assert.True(item.TryGetProperty("health", out var health));
            var healthStr = health.GetString();
            Assert.True(
                healthStr == "Good" || healthStr == "Warning" || healthStr == "Bad",
                $"Unexpected health: {healthStr}");
        }
    }

    // ?????????????????????????????????????????????????????????????????
    // GET /api/results/builds/{buildNumber}
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public async Task GetBuildResults_Should_ReturnOk_When_BuildExists()
    {
        var response = await _client.GetAsync("/api/results/builds/Build100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Build100", json.GetProperty("buildNumber").GetString());
    }

    [Fact]
    public async Task GetBuildResults_Should_ReturnNotFound_When_BuildDoesNotExist()
    {
        var response = await _client.GetAsync("/api/results/builds/NonExistent");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetBuildResults_Should_ReturnUseCases_When_BuildExists()
    {
        var response = await _client.GetAsync("/api/results/builds/Build100");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(json.TryGetProperty("useCases", out var useCases));
        Assert.Equal(JsonValueKind.Array, useCases.ValueKind);
        Assert.True(useCases.GetArrayLength() > 0);
    }

    [Fact]
    public async Task GetBuildResults_Should_ReturnCaseInsensitive_When_DifferentCase()
    {
        var response = await _client.GetAsync("/api/results/builds/build100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ?????????????????????????????????????????????????????????????????
    // GET /api/results/trends
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public async Task GetTrends_Should_ReturnOk_When_Called()
    {
        var response = await _client.GetAsync("/api/results/trends");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("builds", out var builds));
        Assert.Equal(JsonValueKind.Array, builds.ValueKind);
    }

    [Fact]
    public async Task GetTrends_Should_ReturnBuildEntries_When_DataExists()
    {
        var response = await _client.GetAsync("/api/results/trends");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        var builds = json.GetProperty("builds");
        Assert.True(builds.GetArrayLength() >= 2);
    }

    [Fact]
    public async Task GetTrends_Should_ReturnWeeklySummaries_When_DataExists()
    {
        var response = await _client.GetAsync("/api/results/trends");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(json.TryGetProperty("weeklySummaries", out var weekly));
        Assert.Equal(JsonValueKind.Array, weekly.ValueKind);
    }

    [Fact]
    public async Task GetTrends_Should_ReturnMonthlySummaries_When_DataExists()
    {
        var response = await _client.GetAsync("/api/results/trends");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(json.TryGetProperty("monthlySummaries", out var monthly));
        Assert.Equal(JsonValueKind.Array, monthly.ValueKind);
    }

    // ?????????????????????????????????????????????????????????????????
    // GET /api/results/alerts
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public async Task GetAlerts_Should_ReturnOk_When_Called()
    {
        var response = await _client.GetAsync("/api/results/alerts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, json.ValueKind);
    }

    // ?????????????????????????????????????????????????????????????????
    // GET /api/results/export/{buildNumber}
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public async Task ExportBuildReport_Should_ReturnNotFound_When_BuildDoesNotExist()
    {
        var response = await _client.GetAsync("/api/results/export/NonExistent");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ExportBuildReport_Should_ReturnHtml_When_DefaultFormat()
    {
        var response = await _client.GetAsync("/api/results/export/Build100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("<!DOCTYPE html>", body);
        Assert.Contains("Build100", body);
    }

    [Fact]
    public async Task ExportBuildReport_Should_ReturnHtml_When_FormatIsHtml()
    {
        var response = await _client.GetAsync("/api/results/export/Build100?format=html");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("<html>", body);
    }

    [Fact]
    public async Task ExportBuildReport_Should_ReturnCsv_When_FormatIsCsv()
    {
        var response = await _client.GetAsync("/api/results/export/Build100?format=csv");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Build,Build100", body);
        Assert.Contains("Total,20", body);
        Assert.Contains("Passed,18", body);
        Assert.Contains("Failed,2", body);
    }

    // ?????????????????????????????????????????????????????????????????
    // POST /api/results/send-report
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public async Task SendReport_Should_ReturnNotFound_When_BuildDoesNotExist()
    {
        var payload = new { BuildNumber = "NonExistent", Recipients = "test@example.com" };
        var response = await _client.PostAsJsonAsync("/api/results/send-report", payload);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SendReport_Should_ReturnBadRequest_When_NoRecipients()
    {
        var payload = new { BuildNumber = "Build100", Recipients = (string?)null };
        var response = await _client.PostAsJsonAsync("/api/results/send-report", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
