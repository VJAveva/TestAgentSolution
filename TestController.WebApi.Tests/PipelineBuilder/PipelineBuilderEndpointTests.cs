using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TestController.WebApi.Services;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Core.PipelineBuilder;
using TestControllerGrpc.Features;
using TestControllerGrpc.Identity;
using TestControllerGrpc.Models;

namespace TestController.WebApi.Tests.PipelineBuilder;

/// <summary>
/// /api/pipeline-builder/* over the real WebApi pipeline with a builder host registered, the way the
/// controller app's embedded API serves it.
/// </summary>
public sealed class PipelineBuilderEndpointTests : IClassFixture<TestWebAppFactory>, IDisposable
{
    private const string ConfigSecret = "pb-config-secret-4410";
    private const string TemplateSecret = "pb-template-secret-7723";

    private readonly TestWebAppFactory _factory;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"PBE_{Guid.NewGuid():N}");
    private readonly RecordingAudit _audit = new();

    private static readonly object Selection = new
    {
        targetId = "SP2026R2-pipeline-config",
        recipeName = "SmokeE2E",
        agentNames = new[] { "agentA", "agentB" },
    };

    private static readonly string[] Routes = ["catalog", "preview", "validate", "create"];

    public PipelineBuilderEndpointTests(TestWebAppFactory factory)
    {
        _factory = factory;
        var parameters = Path.Combine(_root, "Parameters", "SP2026R2");
        Directory.CreateDirectory(parameters);
        Directory.CreateDirectory(Path.Combine(_root, "recipes"));
        Directory.CreateDirectory(Path.Combine(_root, "templates"));

        File.WriteAllText(Path.Combine(parameters, "SP2026R2-pipeline-config.json"), $$"""
        {
          "version": 1,
          "global": { "_ReleaseName": "SP2026R2", "_RcloudUser": "svc-user", "_RcloudPassword": "{{ConfigSecret}}" },
          "profiles": { "Sanity": { "_Agent1": "jvgr1" } }
        }
        """);
        File.WriteAllText(Path.Combine(_root, "recipes", "smoke.json"), """
        { "name": "SmokeE2E", "stages": [ { "name": "Install", "templateId": "Install", "agentMapping": "pool-fanout" } ] }
        """);
        File.WriteAllText(Path.Combine(_root, "templates", "stages.xml"), $$"""
        <Templates>
          <Template ID="Install">
            <Action Type="RunRemoteCommand" Tag="install" AgentName="[_Agent]" Command="C:\Scripts\Install.bat"
                    Parameters="[_ReleaseName]" UserName="svc" Password="{{TemplateSecret}}" Timeout="600" />
          </Template>
        </Templates>
        """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Fragments => Path.Combine(_root, "fragments");

    private PipelineBuilderHost BuilderHost() => new(
        new PipelineBuilderService(
            new DerivedTargetCatalog(Path.Combine(_root, "Parameters")),
            new FolderRecipeSource(Path.Combine(_root, "recipes")),
            new FolderStageTemplateSource(Path.Combine(_root, "templates")),
            new AllowAll(),
            _audit,
            Path.Combine(_root, "Triggers")),
        new FixedAgents(),
        () => new WatchListConfig(),
        Fragments);

    private HttpClient Client(bool withBuilder = true, string? featuresJson = null, string[]? securedCapabilities = null) =>
        _factory.WithWebHostBuilder(builder =>
        {
            if (securedCapabilities is not null) builder.UseSetting("RBAC:Enabled", "true");
            builder.ConfigureTestServices(services =>
            {
                if (withBuilder) services.AddSingleton(BuilderHost());
                if (featuresJson is not null)
                {
                    var file = Path.Combine(_root, $"{Guid.NewGuid():N}.json");
                    File.WriteAllText(file, featuresJson);
                    foreach (var d in services.Where(s => s.ServiceType == typeof(IFeatureGate)).ToList()) services.Remove(d);
                    services.AddSingleton<IFeatureGate>(new FeatureGate(file, watch: false));
                }
                if (securedCapabilities is not null)
                {
                    foreach (var d in services.Where(s => s.ServiceType == typeof(TestController.Api.Security.IRemoteCapabilityResolver)).ToList())
                        services.Remove(d);
                    services.AddSingleton<TestController.Api.Security.IRemoteCapabilityResolver>(
                        new StubCapabilityResolver(securedCapabilities.ToHashSet(StringComparer.Ordinal)));
                }
            });
        }).CreateClient();

    private static HttpClient AsWpf(HttpClient client)
    {
        client.DefaultRequestHeaders.Add("X-Source", "WPF");
        return client;
    }

    private static Task<HttpResponseMessage> Call(HttpClient client, string route) =>
        route == "catalog"
            ? client.GetAsync("/api/pipeline-builder/catalog")
            : client.PostAsJsonAsync($"/api/pipeline-builder/{route}", Selection);

    // ── permission and feature gate ─────────────────────────────────────

    [Theory]
    [InlineData("catalog")]
    [InlineData("preview")]
    [InlineData("validate")]
    [InlineData("create")]
    public async Task Endpoint_Should_Return403_When_SecuredCallerLacksPipelineAuthor(string route)
    {
        var response = await Call(Client(securedCapabilities: ["Pipeline_View", "Pipeline_Trigger"]), route);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(Directory.Exists(Fragments));
    }

    [Theory]
    [InlineData("catalog")]
    [InlineData("preview")]
    [InlineData("validate")]
    public async Task Endpoint_Should_Return200_When_SecuredCallerHasPipelineAuthor(string route)
    {
        var response = await Call(Client(securedCapabilities: ["Pipeline_Author"]), route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("catalog")]
    [InlineData("preview")]
    [InlineData("validate")]
    [InlineData("create")]
    public async Task Endpoint_Should_Return404_When_PipelineBuilderFeatureIsOff(string route)
    {
        var response = await Call(AsWpf(Client(featuresJson: """{ "Features": { "PipelineBuilder": false } }""")), route);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Feature disabled", await response.Content.ReadAsStringAsync());
        Assert.False(Directory.Exists(Fragments));
    }

    [Fact]
    public async Task Catalog_Should_Return404_When_ThisHostHasNoBuilder()
    {
        var response = await Call(Client(withBuilder: false), "catalog");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("served by the controller app", await response.Content.ReadAsStringAsync());
    }

    // ── create ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_Should_Return403_When_DefaultModeWebClient()
    {
        var response = await Call(Client(), "create");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(Directory.Exists(Fragments));
    }

    [Fact]
    public async Task Create_Should_WriteTheFragmentAndAudit_When_CalledFromTheControllerApp()
    {
        var response = await Call(AsWpf(Client()), "create");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("written").GetBoolean());
        var fragment = doc.RootElement.GetProperty("fragmentPath").GetString()!;
        Assert.True(File.Exists(fragment));
        var entry = Assert.Single(_audit.Entries);
        Assert.Equal("Pipeline_Author", entry.ActionName);
        Assert.Equal(doc.RootElement.GetProperty("tag").GetString(), entry.ResourceId);
    }

    [Fact]
    public async Task Create_Should_Return422AndNotOverwrite_When_TheFragmentAlreadyExists()
    {
        var client = AsWpf(Client());
        Assert.Equal(HttpStatusCode.OK, (await Call(client, "create")).StatusCode);
        var fragment = Directory.GetFiles(Fragments).Single();
        var before = File.ReadAllText(fragment);

        var second = await Call(client, "create");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal(before, File.ReadAllText(fragment));
        Assert.Single(_audit.Entries);
    }

    [Theory]
    [InlineData("""{ "targetId": "", "recipeName": "SmokeE2E", "agentNames": ["a"] }""")]
    [InlineData("""{ "targetId": "..\\x", "recipeName": "SmokeE2E", "agentNames": ["a"] }""")]
    [InlineData("""{ "targetId": "SP2026R2-pipeline-config", "recipeName": "SmokeE2E", "agentNames": [] }""")]
    [InlineData("""{ "targetId": "SP2026R2-pipeline-config", "recipeName": "SmokeE2E", "agentNames": ["a & calc"] }""")]
    public async Task Preview_Should_Return400_When_TheSelectionIsInvalid(string json)
    {
        var response = await AsWpf(Client()).PostAsync("/api/pipeline-builder/preview",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── secrets never reach the browser ─────────────────────────────────

    [Fact]
    public async Task NoResponse_Should_ContainASecretValue_When_EveryEndpointIsCalled()
    {
        var client = AsWpf(Client());
        var bodies = new List<string>();
        foreach (var route in Routes)
        {
            var response = await Call(client, route);
            Assert.True(response.IsSuccessStatusCode, $"{route} -> {(int)response.StatusCode}");
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        foreach (var body in bodies)
        {
            Assert.DoesNotContain(ConfigSecret, body);
            Assert.DoesNotContain(TemplateSecret, body);
        }
        Assert.Contains(BuilderRedaction.Mask, bodies[1]);

        // Masking is display-only: the fragment on the server keeps the real value.
        Assert.Contains(TemplateSecret, File.ReadAllText(Directory.GetFiles(Fragments).Single()));
    }

    // ── standalone WebApi forwards to the controller ────────────────────

    [Theory]
    [InlineData("GET", "/api/pipeline-builder/catalog", true)]
    [InlineData("POST", "/api/pipeline-builder/create", true)]
    [InlineData("DELETE", "/api/pipeline-builder/create", false)]
    [InlineData("POST", "/api/pipelines/x/retry", false)]
    public void ShouldForward_Should_MatchOnlyBuilderReadsAndWrites(string method, string path, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;

        Assert.Equal(expected, PipelineBuilderForwardingMiddleware.ShouldForward(context.Request));
    }

    private sealed class StubCapabilityResolver(IReadOnlySet<string> capabilities) : TestController.Api.Security.IRemoteCapabilityResolver
    {
        public Task<IReadOnlySet<string>?> GetCapabilitiesAsync(string? authorizationHeader, CancellationToken ct)
            => Task.FromResult<IReadOnlySet<string>?>(capabilities);
    }

    private sealed class AllowAll : IAuthorizationService
    {
        public Task<AuthDecision> CanAsync(IUserContext user, Permission permission, string? resourceId = null, CancellationToken ct = default)
            => Task.FromResult(AuthDecision.Allow("test"));
    }

    private sealed class RecordingAudit : IAuditWriter
    {
        public List<AuditEntry> Entries { get; } = [];
        public void Enqueue(AuditEntry entry) { lock (Entries) Entries.Add(entry); }
    }

    private sealed class FixedAgents : IBuilderAgentSource
    {
        public IReadOnlyList<BuilderAgent> GetAgents() =>
            [new BuilderAgent("agentA", "Online", true, false), new BuilderAgent("agentB", "Busy", true, true)];
    }
}
