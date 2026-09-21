using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TestController.Api.Security;

namespace TestController.WebApi.Tests.Execution;

/// <summary>
/// Authorization tests for node-wise execution (AC-6): a node-run is gated by the SAME permission,
/// on the SAME resource, as the root trigger — being granted the pipeline means being able to run any
/// node in it, and nothing else.
/// </summary>
public class NodeRunAuthzTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public NodeRunAuthzTests(TestWebAppFactory factory) => _factory = factory;

    /// <summary>Stands in for the primary host's answer to "what may this caller do?".</summary>
    private sealed class StubCapabilityResolver(IReadOnlySet<string>? capabilities) : IRemoteCapabilityResolver
    {
        public Task<IReadOnlySet<string>?> GetCapabilitiesAsync(string? authorizationHeader, CancellationToken ct)
            => Task.FromResult(capabilities);
    }

    /// <summary>
    /// A client on a host with RBAC switched ON. The standalone WebApi registers NullSessionStore, so no
    /// user resolves locally and the gate falls through to the remote capability resolver — which this
    /// replaces, making the decision deterministic without a running controller.
    /// </summary>
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

    private static Task<HttpResponseMessage> RunNodeAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/execution/pipelines/TestBuild/nodes/run",
            new { nodePath = "e0/c0", scope = "OnlyThisNode" });

    private static Task<HttpResponseMessage> TriggerRootAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/execution/trigger/TestBuild", new { });

    [Fact]
    public async Task RunNode_Should_ReturnForbidden_When_CallerLacksPipelineTrigger()
    {
        var response = await RunNodeAsync(SecuredClient("Pipeline_View", "Report_View"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RunNode_Should_ReturnForbidden_When_CallerHasNoCapabilitiesAtAll()
    {
        var response = await RunNodeAsync(SecuredClient());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RunNode_Should_NotReturnForbidden_When_CallerHasPipelineTrigger()
    {
        var response = await RunNodeAsync(SecuredClient("Pipeline_Trigger"));

        // Accepted normally; Conflict if another test in the fixture holds the single-run lock.
        // Either way the request got PAST the gate, which is what this asserts.
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(
            response.StatusCode == HttpStatusCode.Accepted ||
            response.StatusCode == HttpStatusCode.Conflict,
            $"Unexpected {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task RunNode_Should_MatchRootTrigger_When_PermissionIsDenied()
    {
        // The spec's rule is "same RBAC as the root trigger" — so the two endpoints must agree.
        // Asserting equivalence catches a drift that testing either one alone would not.
        var client = SecuredClient("Pipeline_View");

        var nodeResponse = await RunNodeAsync(client);
        var rootResponse = await TriggerRootAsync(client);

        Assert.Equal(HttpStatusCode.Forbidden, rootResponse.StatusCode);
        Assert.Equal(rootResponse.StatusCode, nodeResponse.StatusCode);
    }

    [Fact]
    public async Task RunNode_Should_NotBeGated_When_RbacIsDisabled()
    {
        // Default mode: the gate fails open exactly as it does for the root trigger, so node-run must
        // not become the one endpoint that starts demanding a permission.
        var response = await RunNodeAsync(_factory.CreateClient());

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
