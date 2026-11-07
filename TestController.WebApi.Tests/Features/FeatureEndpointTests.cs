using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.Features;

namespace TestController.WebApi.Tests.Features;

public sealed class FeatureEndpointTests : IClassFixture<TestWebAppFactory>, IDisposable
{
    private readonly TestWebAppFactory _factory;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"FGW_{Guid.NewGuid():N}");

    public FeatureEndpointTests(TestWebAppFactory factory)
    {
        _factory = factory;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private HttpClient ClientWith(string featuresJson)
    {
        var file = Path.Combine(_dir, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(file, featuresJson);
        return _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                foreach (var d in services.Where(s => s.ServiceType == typeof(IFeatureGate)).ToList())
                    services.Remove(d);
                services.AddSingleton<IFeatureGate>(new FeatureGate(file, watch: false));
            })).CreateClient();
    }

    [Fact]
    public async Task CodeChurnEndpoint_Should_Return404ProblemDetail_When_CodeChurnOff()
    {
        var response = await ClientWith("""{ "Features": { "CodeChurn": false } }""").GetAsync("/api/impact/branches");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Feature disabled", body);
        Assert.Contains("CodeChurn", body);
    }

    [Fact]
    public async Task CodeChurnEndpoint_Should_NotReturnFeatureDisabled_When_CodeChurnOn()
    {
        var response = await ClientWith("""{ "Features": { } }""").GetAsync("/api/impact/branches");

        Assert.DoesNotContain("Feature disabled", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task FirEndpoint_Should_Return404_When_ResultsOffEvenIfFirConfiguredOn()
    {
        var response = await ClientWith("""{ "Features": { "Results": false, "FirReport": true } }""")
            .GetAsync("/api/results/builds/B1/test/T1/fir");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Feature disabled", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ImpactMappingMinimalApi_Should_Return404_When_ImpactMappingOff()
    {
        var response = await ClientWith("""{ "Features": { "ImpactMapping": false } }""")
            .GetAsync("/api/impact-mapping/index/status");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Feature disabled", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GetFeatures_Should_ReturnMapWithDependencyReason_When_Requested()
    {
        var response = await ClientWith("""{ "TeamName": "WAS-QA", "Features": { "Results": false } }""")
            .GetAsync("/api/features");

        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("WAS-QA", root.GetProperty("teamName").GetString());
        Assert.False(root.GetProperty("map").GetProperty("Results").GetBoolean());
        Assert.False(root.GetProperty("map").GetProperty("FirReport").GetBoolean());
        Assert.True(root.GetProperty("map").GetProperty("CodeChurn").GetBoolean());
        var fir = root.GetProperty("features").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "FirReport");
        Assert.Equal("off because Results is off", fir.GetProperty("offBecause").GetString());
    }

    private sealed class StubCapabilityResolver(IReadOnlySet<string> capabilities) : TestController.Api.Security.IRemoteCapabilityResolver
    {
        public Task<IReadOnlySet<string>?> GetCapabilitiesAsync(string? authorizationHeader, CancellationToken ct)
            => Task.FromResult<IReadOnlySet<string>?>(capabilities);
    }

    private HttpClient SecuredClient(params string[] capabilities) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RBAC:Enabled", "true");
            builder.ConfigureTestServices(services =>
            {
                foreach (var d in services.Where(s => s.ServiceType == typeof(TestController.Api.Security.IRemoteCapabilityResolver)).ToList())
                    services.Remove(d);
                services.AddSingleton<TestController.Api.Security.IRemoteCapabilityResolver>(
                    new StubCapabilityResolver(capabilities.ToHashSet(StringComparer.Ordinal)));
            });
        }).CreateClient();

    private static Task<HttpResponseMessage> PutFeatures(HttpClient client, object features) =>
        client.PutAsJsonAsync("/api/features", new { features });

    [Fact]
    public async Task PutFeatures_Should_Return403_When_CallerIsNotAdmin()
    {
        var response = await PutFeatures(SecuredClient("Pipeline_View", "CodeChurn_View"), new { CodeChurn = false });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PutFeatures_Should_Return403_When_DefaultModeWebClient()
    {
        var response = await PutFeatures(_factory.CreateClient(), new { CodeChurn = false });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PutFeatures_Should_PassTheGateAndForward_When_CallerIsAdmin()
    {
        var response = await PutFeatures(SecuredClient("System_ConfigureFeatures"), new { CodeChurn = false });

        // No controller in the test host, so the forward fails - but it got past the permission check.
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("No controller is configured", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PutFeatures_Should_Return400_When_KeyIsNotToggleable()
    {
        var response = await PutFeatures(SecuredClient("System_ConfigureFeatures"), new { Pipelines = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
