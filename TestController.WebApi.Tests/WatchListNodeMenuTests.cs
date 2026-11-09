using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TestController.Api.Security;

namespace TestController.WebApi.Tests;

/// <summary>ContextMenu_Requirement.md P8: the web menu and resolved command come from the server.</summary>
public class WatchListNodeMenuTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public WatchListNodeMenuTests(TestWebAppFactory factory) => _factory = factory;

    private sealed class StubCapabilityResolver(IReadOnlySet<string> capabilities) : IRemoteCapabilityResolver
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
                foreach (var d in services.Where(s => s.ServiceType == typeof(IRemoteCapabilityResolver)).ToList())
                    services.Remove(d);
                services.AddSingleton<IRemoteCapabilityResolver>(
                    new StubCapabilityResolver(capabilities.ToHashSet(StringComparer.Ordinal)));
            });
        }).CreateClient();

    private static async Task<JsonElement[]> MenuAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/watchlist/node-menu",
            new { watchItemTag = "TestBuild", nodePath = "e0/c0" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("items").EnumerateArray().ToArray();
    }

    [Fact]
    public async Task NodeMenu_Should_ReturnOnlyWebItemsEnabled_When_CallerMayTrigger()
    {
        var items = await MenuAsync(SecuredClient("Pipeline_Trigger"));

        var ids = items.Select(i => i.GetProperty("id").GetString()).ToList();
        Assert.Equal(["ExecuteAction", "ShowResolvedCommand"], ids);
        Assert.True(items[0].GetProperty("isEnabled").GetBoolean());
    }

    [Fact]
    public async Task NodeMenu_Should_DisableRunWithReason_When_CallerLacksPipelineTrigger()
    {
        var items = await MenuAsync(SecuredClient("Pipeline_View"));

        var run = items.Single(i => i.GetProperty("id").GetString() == "ExecuteAction");
        Assert.False(run.GetProperty("isEnabled").GetBoolean());
        Assert.Contains("permission", run.GetProperty("toolTip").GetString());
    }

    [Fact]
    public async Task ResolvedCommand_Should_ReturnTheCommandLine_When_TheNodeIsAnAction()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/watchlist/resolved-command",
            new { watchItemTag = "TestBuild", nodePath = "e0/c0" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("text").GetString();
        Assert.Contains("echo hello", text);
    }

    [Fact]
    public async Task ResolvedCommand_Should_ReturnNotFound_When_ThePipelineIsUnknown()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/watchlist/resolved-command",
            new { watchItemTag = "NoSuchPipeline", nodePath = "e0/c0" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
