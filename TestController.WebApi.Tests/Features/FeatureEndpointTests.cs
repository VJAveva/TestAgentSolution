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

    // ── Code Churn repositories ────────────────────────────────────

    private const string TwoRepos = """
        { "CodeChurn": { "Repositories": [
          { "Name": "TeamX.Alpha", "Branches": ["main", "release/2026"], "CredentialRef": "CREDREF_NAME" },
          { "Name": "TeamX.Beta", "Branches": ["develop"] }
        ] } }
        """;

    private static object[] OneRepo(string? credentialRef = null) =>
        [new { name = "TeamX.Alpha", branches = new[] { "main" }, credentialRef }];

    [Fact]
    public async Task GetFeatures_Should_ReturnRepositoriesWithCredentialNameOnly_When_CodeChurnConfigured()
    {
        const string secret = "fgw-credential-value-never-returned";
        Environment.SetEnvironmentVariable("CREDREF_NAME", secret);
        try
        {
            var body = await (await ClientWith(TwoRepos).GetAsync("/api/features")).Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(body);
            var churn = doc.RootElement.GetProperty("codeChurn");
            Assert.True(churn.GetProperty("configured").GetBoolean());
            var alpha = churn.GetProperty("repositories")[0];
            Assert.Equal("TeamX.Alpha", alpha.GetProperty("name").GetString());
            Assert.Equal("CREDREF_NAME", alpha.GetProperty("credentialRef").GetString());
            Assert.DoesNotContain(secret, body);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CREDREF_NAME", null);
        }
    }

    [Fact]
    public async Task GetRepositories_Should_ListEachRepositoryWithItsBranchesAndNoSecret_When_Configured()
    {
        const string secret = "fgw-repo-endpoint-secret";
        Environment.SetEnvironmentVariable("CREDREF_NAME", secret);
        try
        {
            var response = await ClientWith(TwoRepos).GetAsync("/api/impact/repositories");

            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(secret, body);
            using var doc = JsonDocument.Parse(body);
            var repos = doc.RootElement.GetProperty("repositories").EnumerateArray().ToList();
            Assert.Equal(["TeamX.Alpha", "TeamX.Beta"], repos.Select(r => r.GetProperty("name").GetString()));
            Assert.Equal(["develop"], repos[1].GetProperty("branches").EnumerateArray().Select(b => b.GetString()));
            Assert.True(repos[0].GetProperty("credentialSet").GetBoolean());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CREDREF_NAME", null);
        }
    }

    [Fact]
    public async Task GetBranches_Should_ReturnOnlyTheRepositorysBranches_When_RepositoryGiven()
    {
        var branches = await ClientWith(TwoRepos).GetFromJsonAsync<string[]>("/api/impact/branches?repository=TeamX.Beta");

        Assert.Equal(["develop"], branches);
    }

    [Fact]
    public async Task GetRepositories_Should_SayNoRepositoriesConfigured_When_ListIsEmpty()
    {
        var body = await (await ClientWith("""{ "CodeChurn": { "Repositories": [] } }""").GetAsync("/api/impact/repositories"))
            .Content.ReadAsStringAsync();

        Assert.Contains("No repositories configured", body);
    }

    [Theory]
    [InlineData("/api/impact/consolidated?repository=TeamX.Alpha&branch=develop", "Branch 'develop' is not configured for repository 'TeamX.Alpha'")]
    [InlineData("/api/impact/consolidated?repository=TeamX.Unknown", "Repository 'TeamX.Unknown' is not configured")]
    [InlineData("/api/impact/scope?branch=feature/x", "Branch 'feature/x' is not configured for any Code Churn repository")]
    public async Task Churn_Should_Return400WithAClearMessage_When_RepositoryOrBranchIsNotConfigured(string url, string message)
    {
        var response = await ClientWith(TwoRepos).GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(message, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PutCodeChurn_Should_Return403_When_CallerIsNotAdmin()
    {
        var response = await SecuredClient("Pipeline_View", "CodeChurn_View")
            .PutAsJsonAsync("/api/features/code-churn", new { repositories = OneRepo() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PutCodeChurn_Should_Return403_When_DefaultModeWebClient()
    {
        var response = await _factory.CreateClient().PutAsJsonAsync("/api/features/code-churn", new { repositories = OneRepo() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PutCodeChurn_Should_PassTheGateAndForward_When_CallerIsAdmin()
    {
        var response = await SecuredClient("System_ConfigureFeatures")
            .PutAsJsonAsync("/api/features/code-churn", new { repositories = OneRepo("ADO_PAT") });

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("No controller is configured", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PutCodeChurn_Should_Return400_When_TokenIsPastedAsCredentialRef()
    {
        var response = await SecuredClient("System_ConfigureFeatures")
            .PutAsJsonAsync("/api/features/code-churn", new { repositories = OneRepo(new string('k', 52)) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("looks like a token", await response.Content.ReadAsStringAsync());
    }
}
