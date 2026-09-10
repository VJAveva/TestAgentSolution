using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Ado;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Ado;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Tests.Impact;

/// <summary>
/// Covers the ADO query surface the engine's recall depends on: the state predicate that keeps deleted test
/// cases out of the corpus, the hierarchy walk that produces weight-1.0 anchors, and the field list that
/// decides how much matchable text a document has.
/// </summary>
public sealed class AdoImpactWorkItemClientTests
{
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;

        public ScriptedHandler(params string[] responses) => _responses = new Queue<string>(responses);

        public List<string> Bodies { get; } = [];
        public List<string> Uris { get; } = [];

        /// <summary>The decoded WIQL of the first query request; System.Text.Json escapes ' as \u0027 on the wire.</summary>
        public string Wiql()
        {
            string body = Bodies.First(b => b.Contains("query", StringComparison.Ordinal));
            return JsonDocument.Parse(body).RootElement.GetProperty("query").GetString()!;
        }

        public bool HasQuery => Bodies.Any(b => b.Contains("query", StringComparison.Ordinal));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uris.Add(request.RequestUri!.ToString());
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));

            string payload = _responses.Count > 0 ? _responses.Dequeue() : """{"count":0,"value":[]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class FakeToken : IAdoTokenProvider
    {
        public Task<string> GetAuthHeaderAsync(CancellationToken ct) => Task.FromResult("Basic test");
        public string Describe() => "test";
    }

    private static AdoImpactWorkItemClient Build(ScriptedHandler handler, ImpactMappingOptions? impact = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://dev.azure.test/org/") };
        var ado = new AdoClient(
            http, new FakeToken(),
            Options.Create(new AdoOptions { Organization = "org", Project = "proj" }),
            new NoopAppLogger());
        return new AdoImpactWorkItemClient(ado, Options.Create(impact ?? new ImpactMappingOptions()), new NoopAppLogger());
    }

    private static string WorkItemsResponse(params (int Id, string Type, string Title, string? Description)[] items)
    {
        var value = items.Select(i => new Dictionary<string, object?>
        {
            ["id"] = i.Id,
            ["rev"] = 1,
            ["fields"] = new Dictionary<string, object?>
            {
                ["System.Title"] = i.Title,
                ["System.WorkItemType"] = i.Type,
                ["System.State"] = "Design",
                ["System.Rev"] = 1,
                ["System.Description"] = i.Description,
                ["Microsoft.VSTS.TCM.AutomationStatus"] = "Automated",
                ["Microsoft.VSTS.TCM.AutomatedTestName"] = $"Suite.Test{i.Id}",
            },
        });
        return JsonSerializer.Serialize(new { count = items.Length, value });
    }

    private static string LinkResponse(params (int Source, int Target)[] links)
    {
        var relations = links.Select(l => new
        {
            rel = "System.LinkTypes.Hierarchy-Forward",
            source = new { id = l.Source },
            target = new { id = l.Target },
        });
        return JsonSerializer.Serialize(new { workItemRelations = relations });
    }

    [Fact]
    public async Task EnumerateChangedSinceAsync_Should_ExcludeConfiguredStates()
    {
        var handler = new ScriptedHandler();
        var options = new ImpactMappingOptions();
        options.Ado.ExcludedStates = ["Removed", "Closed"];

        await foreach (int _ in AsIds(Build(handler, options))) { }

        Assert.Contains("[System.State] NOT IN ('Removed', 'Closed')", handler.Wiql(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnumerateChangedSinceAsync_Should_OmitStateClause_When_NoneConfigured()
    {
        var handler = new ScriptedHandler();
        var options = new ImpactMappingOptions();
        options.Ado.ExcludedStates = [];

        await foreach (int _ in AsIds(Build(handler, options))) { }

        Assert.DoesNotContain("System.State", handler.Wiql(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnumerateIdsInStatesAsync_Should_QueryOnlyThoseStates()
    {
        var handler = new ScriptedHandler();

        await foreach (int _ in Build(handler).EnumerateIdsInStatesAsync(
            "Test Case", ["Removed"], DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None))
        {
        }

        Assert.Contains("[System.State] IN ('Removed')", handler.Wiql(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnumerateIdsInStatesAsync_Should_ReturnEmpty_When_NoStatesGiven()
    {
        var handler = new ScriptedHandler();

        List<int> ids = [];
        await foreach (int id in Build(handler).EnumerateIdsInStatesAsync(
            "Test Case", [], DateTimeOffset.UtcNow, CancellationToken.None))
        {
            ids.Add(id);
        }

        Assert.Empty(ids);
        Assert.False(handler.HasQuery);
    }

    [Fact]
    public async Task GetTestCasesAsync_Should_RequestAndMapDescription()
    {
        // Titles are frequently a bare requirement id; description is the only matchable prose.
        var handler = new ScriptedHandler(WorkItemsResponse((101, "Test Case", "FR 101", "Verifies galaxy failover")));

        IReadOnlyList<TestCaseCandidate> result =
            await Build(handler).GetTestCasesAsync([101], CancellationToken.None);

        Assert.Contains("System.Description", handler.Uris[0] + handler.Bodies[0], StringComparison.Ordinal);
        Assert.Equal("Verifies galaxy failover", Assert.Single(result).Description);
    }

    [Fact]
    public async Task GetTestCasesAsync_Should_MapAutomatedTestName()
    {
        var handler = new ScriptedHandler(WorkItemsResponse((101, "Test Case", "FR 101", null)));

        TestCaseCandidate candidate = Assert.Single(
            await Build(handler).GetTestCasesAsync([101], CancellationToken.None));

        Assert.Equal("Suite.Test101", candidate.AutomatedTestName);
    }

    [Fact]
    public async Task GetChildTestCasesAsync_Should_WalkToConfiguredDepth()
    {
        // Feature 900 -> Story 950 -> Test Case 101. A one-level walk misses it entirely.
        var options = new ImpactMappingOptions();
        options.Ado.LinkWalkMaxDepth = 3;
        var handler = new ScriptedHandler(
            LinkResponse((900, 950)),
            LinkResponse((950, 101)),
            LinkResponse(),
            WorkItemsResponse((101, "Test Case", "FR 101", null)));

        IReadOnlyDictionary<int, IReadOnlyList<int>> result =
            await Build(handler, options).GetChildTestCasesAsync([900], CancellationToken.None);

        Assert.Equal([101], result[900]);
    }

    [Fact]
    public async Task GetChildTestCasesAsync_Should_StopAtDepthOne_When_Configured()
    {
        var options = new ImpactMappingOptions();
        options.Ado.LinkWalkMaxDepth = 1;
        var handler = new ScriptedHandler(
            LinkResponse((900, 950)),
            WorkItemsResponse((950, "User Story", "Story", null)));

        IReadOnlyDictionary<int, IReadOnlyList<int>> result =
            await Build(handler, options).GetChildTestCasesAsync([900], CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetChildTestCasesAsync_Should_AttributeGrandchildToOriginalRoot()
    {
        var options = new ImpactMappingOptions();
        options.Ado.LinkWalkMaxDepth = 3;
        var handler = new ScriptedHandler(
            LinkResponse((900, 950), (901, 960)),
            LinkResponse((950, 101), (960, 102)),
            LinkResponse(),
            WorkItemsResponse((101, "Test Case", "FR 101", null), (102, "Test Case", "FR 102", null)));

        IReadOnlyDictionary<int, IReadOnlyList<int>> result =
            await Build(handler, options).GetChildTestCasesAsync([900, 901], CancellationToken.None);

        Assert.Equal([101], result[900]);
        Assert.Equal([102], result[901]);
    }

    [Fact]
    public async Task GetChildTestCasesAsync_Should_ReturnEmpty_When_NoFeatureIds()
    {
        var handler = new ScriptedHandler();

        Assert.Empty(await Build(handler).GetChildTestCasesAsync([], CancellationToken.None));
        Assert.Empty(handler.Uris);
    }

    private static async IAsyncEnumerable<int> AsIds(AdoImpactWorkItemClient client)
    {
        await foreach (AdoWorkItemRef item in client.EnumerateChangedSinceAsync(
            "Test Case", DateTimeOffset.UtcNow.AddDays(-1), 50, CancellationToken.None))
        {
            yield return item.Id;
        }
    }

    private sealed class NoopAppLogger : IAppLogger
    {
#pragma warning disable CS0067 // event is part of the interface but unused in tests
        public event Action<AppLogEntry>? EntryAdded;
#pragma warning restore CS0067
        public void Log(LogLevel level, string category, string message, Exception? ex = null) { }
        public void Log(LogLevel level, string category, string message, string? correlationId, long elapsedMs = 0, Exception? ex = null) { }
        public void LogStructured(LogLevel level, string category, string message, string? agent = null, string? runId = null, string? pipeline = null, string? action = null, long elapsedMs = 0, Exception? ex = null) { }
        public void Info(string category, string message) { }
        public void Warn(string category, string message) { }
        public void Error(string category, string message, Exception? ex = null) { }
        public IReadOnlyList<AppLogEntry> GetRecentEntries(int count = 500) => [];
    }
}
