using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace TestController.WebApi.Tests.Execution;

/// <summary>
/// End-to-end proof that PER-NODE execution status actually reaches a web client over SignalR.
/// </summary>
/// <remarks>
/// Every layer of this path was individually correct on inspection while the UI still showed nothing,
/// so this drives a REAL hub client against the real host and records what genuinely arrives. It is the
/// only test in the suite that can tell "the server never sent it" apart from "the browser never showed it".
/// </remarks>
public sealed class NodeStatusSignalRTests : IClassFixture<TestWebAppFactory>, IAsyncLifetime
{
    private readonly TestWebAppFactory _factory;
    private readonly HttpClient _client;
    private HubConnection _hub = null!;

    private readonly List<JsonElement> _actionProgress = [];
    private readonly List<JsonElement> _executionCompleted = [];

    public NodeStatusSignalRTests(TestWebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        _hub = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/controller"), o =>
            {
                // TestServer has no real socket; long polling is the transport it can serve.
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        _hub.On<JsonElement>("ActionProgress", e => { lock (_actionProgress) _actionProgress.Add(e); });
        _hub.On<JsonElement>("ExecutionCompleted", e => { lock (_executionCompleted) _executionCompleted.Add(e); });

        await _hub.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_hub is not null) await _hub.DisposeAsync();
    }

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private IReadOnlyList<JsonElement> ProgressSnapshot()
    {
        lock (_actionProgress) return [.. _actionProgress];
    }

    private string Dump()
    {
        var progress = ProgressSnapshot();
        if (progress.Count == 0) return "(no ActionProgress messages were received at all)";
        return string.Join("\n", progress.Select(p =>
            $"  actionTag={Str(p, "actionTag") ?? "-"} groupTag={Str(p, "groupTag") ?? "-"} " +
            $"status={Str(p, "status") ?? "-"} sessionId={Str(p, "sessionId") ?? "-"}"));
    }

    /// <summary>Starts the seeded pipeline, tolerating a lock held by a concurrently running test class.</summary>
    private async Task<string> TriggerAndWaitAsync()
    {
        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            response = await _client.PostAsJsonAsync("/api/execution/trigger/TestBuild", new { });
            if (response.StatusCode != HttpStatusCode.Conflict) break;
            await Task.Delay(250);
        }

        Assert.True(response!.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK,
            $"Could not start the pipeline: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = json.GetProperty("sessionId").GetString()!;

        // The run is fire-and-forget; wait for the terminal broadcast rather than a fixed sleep.
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            lock (_executionCompleted)
            {
                if (_executionCompleted.Any(e => Str(e, "sessionId") == sessionId)) break;
            }
            await Task.Delay(100);
        }

        // Let any trailing per-node message land after the completion broadcast.
        await Task.Delay(500);
        return sessionId;
    }

    [Fact]
    public async Task PerNodeStatus_Should_ReachAWebClient_When_PipelineRuns()
    {
        await TriggerAndWaitAsync();

        var progress = ProgressSnapshot();
        Assert.True(progress.Count > 0,
            "No per-node ActionProgress reached the hub client during a full pipeline run.\n" + Dump());
    }

    [Fact]
    public async Task PerNodeStatus_Should_ReportRunning_When_ActionStarts()
    {
        await TriggerAndWaitAsync();

        Assert.True(
            ProgressSnapshot().Any(p => Str(p, "status") == "Running"),
            "No node ever reported 'Running', so the tree can never show a node as in-progress.\n" + Dump());
    }

    /// <summary>
    /// The headline requirement: a run must never leave a node pulsing forever. Every node that started
    /// has to be followed by a terminal status.
    /// </summary>
    [Fact]
    public async Task PerNodeStatus_Should_EndInATerminalState_When_RunFinishes()
    {
        await TriggerAndWaitAsync();

        var progress = ProgressSnapshot();
        var started = progress
            .Where(p => Str(p, "actionTag") is not null)
            .Select(p => Str(p, "actionTag")!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(started.Count > 0, "No per-node messages carried an actionTag.\n" + Dump());

        string[] terminal = ["Success", "Failed", "Skipped", "Cancelled"];
        foreach (var tag in started)
        {
            var last = progress
                .Where(p => string.Equals(Str(p, "actionTag"), tag, StringComparison.OrdinalIgnoreCase))
                .Select(p => Str(p, "status"))
                .LastOrDefault();

            Assert.True(terminal.Contains(last),
                $"Node '{tag}' finished reporting '{last}', which is not a terminal state — "
                + "the UI would pulse forever and never show pass or fail.\n" + Dump());
        }
    }

    /// <summary>
    /// The dashboard routes per-node messages by sessionId, so a message without one is invisible there
    /// no matter how correct the rest of the payload is.
    /// </summary>
    [Fact]
    public async Task PerNodeStatus_Should_CarrySessionId_When_Broadcast()
    {
        var sessionId = await TriggerAndWaitAsync();

        var progress = ProgressSnapshot();
        Assert.True(progress.Count > 0, "No per-node messages arrived.\n" + Dump());

        var orphaned = progress.Where(p => Str(p, "sessionId") is null).ToList();
        Assert.True(orphaned.Count == 0,
            $"{orphaned.Count} of {progress.Count} per-node message(s) carried no sessionId, so the "
            + "Execution dashboard and Monitor cannot route them to a session card.\n" + Dump());

        Assert.Contains(progress, p => Str(p, "sessionId") == sessionId);
    }

    [Fact]
    public async Task NodeRun_Should_ReportPerNodeStatus_When_SingleNodeIsRun()
    {
        var nodes = await (await _client.GetAsync("/api/execution/pipelines/TestBuild/nodes"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var revision = nodes.GetProperty("treeRevision").GetString();

        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            response = await _client.PostAsJsonAsync(
                "/api/execution/pipelines/TestBuild/nodes/run",
                new { nodePath = "e0/c0", scope = "OnlyThisNode", treeRevision = revision });
            if (response.StatusCode != HttpStatusCode.Conflict) break;
            await Task.Delay(250);
        }

        Assert.Equal(HttpStatusCode.Accepted, response!.StatusCode);
        var sessionId = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("sessionId").GetString()!;

        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            lock (_executionCompleted)
            {
                if (_executionCompleted.Any(e => Str(e, "sessionId") == sessionId)) break;
            }
            await Task.Delay(100);
        }
        await Task.Delay(500);

        var mine = ProgressSnapshot().Where(p => Str(p, "sessionId") == sessionId).ToList();
        Assert.True(mine.Count > 0,
            "A single-node run produced no per-node status for its own session.\n" + Dump());
    }
}
