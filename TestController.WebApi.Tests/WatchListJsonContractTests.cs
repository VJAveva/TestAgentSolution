using System.Text.Json;

namespace TestController.WebApi.Tests;

/// <summary>
/// Contract tests that verify the JSON shapes returned by /api/watchlist
/// match the TypeScript types expected by the React client.
/// These prevent regressions where server-side model changes silently
/// break the client (e.g. enum serialization, property casing, polymorphic
/// discriminators for IActionNode).
/// </summary>
public class WatchListJsonContractTests : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client;

    public WatchListJsonContractTests(TestWebAppFactory factory)
    {
        _client = factory.CreateClient();
    }

    // ?????????????????????????????????????????????????????????????????
    // Property casing — React expects camelCase
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public async Task WatchListConfig_Should_UseCamelCaseProperties_When_Serialized()
    {
        var response = await _client.GetAsync("/api/watchlist");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(json.TryGetProperty("watchItems", out _), "Expected 'watchItems' (camelCase)");
        Assert.True(json.TryGetProperty("templates", out _), "Expected 'templates'");
        Assert.True(json.TryGetProperty("filePath", out _), "Expected 'filePath' (camelCase)");
    }

    [Fact]
    public async Task WatchItemConfig_Should_UseCamelCaseProperties_When_Serialized()
    {
        var response = await _client.GetAsync("/api/watchlist");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        var firstItem = json.GetProperty("watchItems").EnumerateArray().First();

        Assert.True(firstItem.TryGetProperty("tag", out _), "Expected 'tag'");
        Assert.True(firstItem.TryGetProperty("path", out _), "Expected 'path'");
        Assert.True(firstItem.TryGetProperty("filter", out _), "Expected 'filter'");
        Assert.True(firstItem.TryGetProperty("events", out _), "Expected 'events'");
        Assert.True(firstItem.TryGetProperty("isEnabled", out _), "Expected 'isEnabled' (camelCase)");
        Assert.True(firstItem.TryGetProperty("buildNumberField", out _), "Expected 'buildNumberField' (camelCase)");
        Assert.True(firstItem.TryGetProperty("dropLocationField", out _), "Expected 'dropLocationField' (camelCase)");
    }

    // ?????????????????????????????????????????????????????????????????
    // Enum serialization — React expects string values, not integers
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public async Task ExecutionMode_Should_SerializeAsString_When_InEventConfig()
    {
        var response = await _client.GetAsync("/api/watchlist");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        var firstItem = json.GetProperty("watchItems").EnumerateArray().First();
        var firstEvent = firstItem.GetProperty("events").EnumerateArray().First();
        var execType = firstEvent.GetProperty("executionType");

        // Must be a string like "Sequential" or "Parallel", NOT an integer like 0 or 1
        Assert.Equal(JsonValueKind.String, execType.ValueKind);
        Assert.Contains(execType.GetString(), new[] { "Sequential", "Parallel" });
    }

    // ?????????????????????????????????????????????????????????????????
    // Polymorphic discriminator — React expects "nodeType" discriminator
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public async Task ActionNode_Should_HaveNodeTypeDiscriminator_When_Serialized()
    {
        var response = await _client.GetAsync("/api/watchlist");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        var firstItem = json.GetProperty("watchItems").EnumerateArray().First();
        var firstEvent = firstItem.GetProperty("events").EnumerateArray().First();
        var children = firstEvent.GetProperty("children");

        Assert.True(children.GetArrayLength() > 0, "Expected at least one child action node");

        var firstChild = children.EnumerateArray().First();
        Assert.True(firstChild.TryGetProperty("nodeType", out var nodeType),
            "Expected 'nodeType' discriminator on polymorphic IActionNode");
        Assert.Equal(JsonValueKind.String, nodeType.ValueKind);
        Assert.Contains(nodeType.GetString(), new[] { "ActionGroup", "Action", "Initialize", "Ref" });
    }

    [Fact]
    public async Task ActionConfig_Should_HaveExpectedShape_When_Serialized()
    {
        var response = await _client.GetAsync("/api/watchlist");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        var firstItem = json.GetProperty("watchItems").EnumerateArray().First();
        var firstEvent = firstItem.GetProperty("events").EnumerateArray().First();
        var firstChild = firstEvent.GetProperty("children").EnumerateArray().First();

        // Verify it's an Action node
        Assert.Equal("Action", firstChild.GetProperty("nodeType").GetString());

        // Verify ActionType enum is a string
        var actionType = firstChild.GetProperty("type");
        Assert.Equal(JsonValueKind.String, actionType.ValueKind);
        Assert.Contains(actionType.GetString(), new[] { "RunCommand", "RunRemoteCommand", "SendMail" });

        // Verify key properties exist with correct casing
        Assert.True(firstChild.TryGetProperty("command", out _), "Expected 'command'");
        Assert.True(firstChild.TryGetProperty("parameters", out _), "Expected 'parameters'");
        Assert.True(firstChild.TryGetProperty("failAndContinue", out _), "Expected 'failAndContinue' (camelCase)");
    }

    // ?????????????????????????????????????????????????????????????????
    // Null suppression — React should not see null noise
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public async Task NullProperties_Should_BeOmitted_When_WhenWritingNull()
    {
        var response = await _client.GetAsync("/api/watchlist");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        var firstItem = json.GetProperty("watchItems").EnumerateArray().First();

        // lastBuildNumber / lastDropLocation are null by default — should be absent
        Assert.False(firstItem.TryGetProperty("lastBuildNumber", out _),
            "Null 'lastBuildNumber' should be omitted with WhenWritingNull");
        Assert.False(firstItem.TryGetProperty("lastDropLocation", out _),
            "Null 'lastDropLocation' should be omitted with WhenWritingNull");
    }

    // ?????????????????????????????????????????????????????????????????
    // Round-trip — PUT the config back and GET should return equivalent
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public async Task WatchListConfig_Should_RoundTrip_When_PutAndGet()
    {
        // GET current config
        var getResponse = await _client.GetAsync("/api/watchlist");
        var originalJson = await getResponse.Content.ReadAsStringAsync();

        // PUT it back
        var putContent = new StringContent(originalJson, System.Text.Encoding.UTF8, "application/json");
        var putResponse = await _client.PutAsync("/api/watchlist", putContent);
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        // GET again
        var verifyResponse = await _client.GetAsync("/api/watchlist");
        var verifyJson = await verifyResponse.Content.ReadFromJsonAsync<JsonElement>();

        // Verify key structure survived the round-trip
        var items = verifyJson.GetProperty("watchItems");
        Assert.True(items.GetArrayLength() >= 2);

        var firstItem = items.EnumerateArray().First();
        Assert.Equal("TestBuild", firstItem.GetProperty("tag").GetString());

        // Verify polymorphic children survived
        var children = firstItem.GetProperty("events").EnumerateArray().First()
            .GetProperty("children");
        Assert.True(children.GetArrayLength() > 0);
        Assert.Equal("Action", children.EnumerateArray().First().GetProperty("nodeType").GetString());
    }
}
