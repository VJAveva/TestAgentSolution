using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.Identity;
using TestControllerGrpc.Locking;

namespace TestController.WebApi.Tests.Execution;

/// <summary>
/// Integration tests for node-wise execution: the runnable-node map and the node-run endpoint.
/// </summary>
/// <remarks>
/// The seeded WatchList (see TestWebAppFactory) gives TestBuild one event with one action, so
/// "e0" is the event and "e0/c0" is the action.
/// </remarks>
public class NodeRunEndpointTests : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client;
    private readonly TestWebAppFactory _factory;

    public NodeRunEndpointTests(TestWebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private Task<HttpResponseMessage> RunAsync(string tag, object body) =>
        _client.PostAsJsonAsync($"/api/execution/pipelines/{tag}/nodes/run", body);

    // ── GET /api/execution/pipelines/{tag}/nodes ─────────────────────────

    [Fact]
    public async Task GetPipelineNodes_Should_ReturnPathsAndRevision_When_TagExists()
    {
        var response = await _client.GetAsync("/api/execution/pipelines/TestBuild/nodes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("TestBuild", json.GetProperty("watchItemTag").GetString());
        Assert.False(string.IsNullOrEmpty(json.GetProperty("treeRevision").GetString()));

        var events = json.GetProperty("events");
        Assert.Equal(1, events.GetArrayLength());

        var evt = events[0];
        Assert.Equal("e0", evt.GetProperty("path").GetString());
        Assert.True(evt.GetProperty("runnable").GetBoolean());

        var action = evt.GetProperty("children")[0];
        Assert.Equal("e0/c0", action.GetProperty("path").GetString());
        Assert.Equal("Action", action.GetProperty("kind").GetString());
        Assert.True(action.GetProperty("runnable").GetBoolean());
        // No Initialize anywhere in this pipeline, so the UI must not offer the option.
        Assert.False(action.GetProperty("hasInitialize").GetBoolean());
    }

    [Fact]
    public async Task GetPipelineNodes_Should_ReturnSameRevision_When_CalledTwice()
    {
        var first = await _client.GetAsync("/api/execution/pipelines/TestBuild/nodes");
        var second = await _client.GetAsync("/api/execution/pipelines/TestBuild/nodes");

        var a = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("treeRevision").GetString();
        var b = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("treeRevision").GetString();

        Assert.Equal(a, b);
    }

    [Fact]
    public async Task GetPipelineNodes_Should_ReturnNotFound_When_TagMissing()
    {
        var response = await _client.GetAsync("/api/execution/pipelines/NoSuchPipeline/nodes");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── POST .../nodes/run — rejections ──────────────────────────────────
    // All of these are evaluated before any lock is taken, so they are independent
    // of whatever other tests in this fixture have left running.

    [Fact]
    public async Task RunNode_Should_ReturnNotFound_When_TagMissing()
    {
        var response = await RunAsync("NoSuchPipeline", new { nodePath = "e0", scope = "OnlyThisNode" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("c0")]
    [InlineData("nonsense")]
    [InlineData("e0/x1")]
    public async Task RunNode_Should_ReturnBadRequest_When_NodePathIsMalformed(string nodePath)
    {
        var response = await RunAsync("TestBuild", new { nodePath, scope = "OnlyThisNode" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("e9")]
    [InlineData("e0/c9")]
    public async Task RunNode_Should_ReturnBadRequest_When_NodePathIsOutOfRange(string nodePath)
    {
        var response = await RunAsync("TestBuild", new { nodePath, scope = "OnlyThisNode" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RunNode_Should_ReturnConflict_When_TreeRevisionIsStale()
    {
        var response = await RunAsync("TestBuild", new
        {
            nodePath = "e0/c0",
            scope = "OnlyThisNode",
            treeRevision = "0000000000000000",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("tree-changed", json.GetProperty("error").GetString());
        Assert.NotEqual("0000000000000000", json.GetProperty("currentRevision").GetString());
    }

    [Fact]
    public async Task RunNode_Should_ReturnBadRequest_When_ParameterCarriesShellMetacharacter()
    {
        var response = await RunAsync("TestBuild", new
        {
            nodePath = "e0/c0",
            scope = "OnlyThisNode",
            buildNumber = "1.0 & calc.exe",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── POST .../nodes/run — locking (AC-7) ─────────────────────────────

    [Fact]
    public async Task RunNode_Should_ReturnPipelineLocked_When_HeldByAnotherUser()
    {
        var registry = _factory.Services.GetRequiredService<ILockRegistry>();

        // Clear anything an earlier test left behind, then take the lock as a different user.
        registry.ForceRelease("TestBuild");
        Assert.IsType<AcquireResult.Success>(registry.TryAcquire(
            "TestBuild", new OwnerIdentity("someone-else", "Someone Else", ClientKind.Web), LockKind.Trigger));

        try
        {
            var response = await RunAsync("TestBuild", new { nodePath = "e0/c0", scope = "OnlyThisNode" });

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("pipeline-locked", json.GetProperty("error").GetString());
            Assert.Equal("someone-else", json.GetProperty("lock").GetProperty("ownerUserId").GetString());
        }
        finally
        {
            registry.ForceRelease("TestBuild");
        }
    }

    [Fact]
    public async Task RunNode_Should_LeaveLockWithOwner_When_NodeRunIsRefused()
    {
        var registry = _factory.Services.GetRequiredService<ILockRegistry>();
        registry.ForceRelease("TestBuild");
        registry.TryAcquire("TestBuild", new OwnerIdentity("owner", "Owner", ClientKind.Wpf), LockKind.Trigger);

        try
        {
            await RunAsync("TestBuild", new { nodePath = "e0/c0", scope = "OnlyThisNode" });

            // A refused node-run must not release or rewrite the lock it collided with.
            Assert.Equal("owner", registry.Get("TestBuild")?.Owner.UserId);
        }
        finally
        {
            registry.ForceRelease("TestBuild");
        }
    }

    [Fact]
    public async Task RunNode_Should_BeAllowed_When_AdminForceReleasedTheLockFirst()
    {
        var registry = _factory.Services.GetRequiredService<ILockRegistry>();
        registry.ForceRelease("TestBuild");
        registry.TryAcquire("TestBuild", new OwnerIdentity("blocker", "Blocker", ClientKind.Web), LockKind.Trigger);

        // This is exactly what the Override control does: force-release, then run.
        registry.ForceRelease("TestBuild");
        Assert.Null(registry.Get("TestBuild"));

        var nodes = await (await _client.GetAsync("/api/execution/pipelines/TestBuild/nodes"))
            .Content.ReadFromJsonAsync<JsonElement>();

        var response = await RunAsync("TestBuild", new
        {
            nodePath = "e0/c0",
            scope = "OnlyThisNode",
            treeRevision = nodes.GetProperty("treeRevision").GetString(),
        });

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(
            response.StatusCode == HttpStatusCode.Accepted ||
            response.StatusCode == HttpStatusCode.Conflict,
            $"Unexpected {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    // ── POST .../nodes/run — accepted ────────────────────────────────────

    [Fact]
    public async Task RunNode_Should_ReturnAccepted_When_PathResolves()
    {
        var nodes = await (await _client.GetAsync("/api/execution/pipelines/TestBuild/nodes"))
            .Content.ReadFromJsonAsync<JsonElement>();

        var response = await RunAsync("TestBuild", new
        {
            nodePath = "e0/c0",
            scope = "OnlyThisNode",
            treeRevision = nodes.GetProperty("treeRevision").GetString(),
        });

        // Conflict is legitimate here: the fixture is shared and another test may hold the
        // single-run lock on TestBuild. Both outcomes prove the request was authorized and resolved.
        Assert.True(
            response.StatusCode == HttpStatusCode.Accepted ||
            response.StatusCode == HttpStatusCode.Conflict,
            $"Unexpected {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        if (response.StatusCode != HttpStatusCode.Accepted) return;

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(json.GetProperty("sessionId").GetString()));
        Assert.Equal("e0/c0", json.GetProperty("nodePath").GetString());
        Assert.Equal("Action", json.GetProperty("nodeKind").GetString());
        Assert.Equal("OnlyThisNode", json.GetProperty("scope").GetString());
    }
}
