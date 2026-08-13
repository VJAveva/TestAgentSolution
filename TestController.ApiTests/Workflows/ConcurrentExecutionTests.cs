using TestController.ApiTests.Data;
using TestController.ApiTests.Infrastructure;

namespace TestController.ApiTests.Workflows;

/// <summary>
/// Concurrency / single-flight guard: a tag that is already running must reject a
/// second trigger with 409 Conflict. InMemory-only (needs a deterministic in-flight
/// window via the fake's execution delay).
/// </summary>
public sealed class ConcurrentExecutionTests : IClassFixture<ApiTestFixture>
{
    private readonly ApiTestFixture _fixture;
    private ApiClient Api => _fixture.Api;

    public ConcurrentExecutionTests(ApiTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Trigger_Should_RejectDuplicate_When_SameTagAlreadyRunning()
    {
        if (_fixture.IsLive)
            return;

        _fixture.ResetFake();
        _fixture.Fake!.ExecutionDelay = TimeSpan.FromSeconds(3);

        var tag = TestWatchItems.InMemoryEnabledTag;

        var first = await Api.TriggerAsync(tag);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var firstId = (await ApiClient.ReadAsync<TriggerResponse>(first))!.SessionId;

        var becameActive = await WorkflowHelpers.WaitUntilActiveAsync(Api, firstId);
        Assert.True(becameActive, "First session never became active");

        var second = await Api.TriggerAsync(tag);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        // Let the first session drain so cleanup is clean.
        await WorkflowHelpers.PollUntilTerminalAsync(Api, firstId, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Trigger_Should_RunAllConcurrently_When_DistinctWatchItems()
    {
        if (_fixture.IsLive)
            return;

        _fixture.ResetFake();
        _fixture.Fake!.ExecutionDelay = TimeSpan.FromSeconds(3);

        var tags = TestWatchItems.InMemoryDistinctTags;

        // Fire all triggers as close to simultaneously as possible.
        var responses = await Task.WhenAll(tags.Select(t => Api.TriggerAsync(t)));

        var sessionIds = new List<string>();
        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var body = await ApiClient.ReadAsync<TriggerResponse>(response);
            Assert.NotNull(body);
            sessionIds.Add(body!.SessionId);
        }

        // Distinct WatchItems produce distinct sessions (no cross-item serialization).
        Assert.Equal(tags.Length, sessionIds.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // Every session must be active at the same time — proves concurrent execution.
        foreach (var id in sessionIds)
            Assert.True(await WorkflowHelpers.WaitUntilActiveAsync(Api, id, TimeSpan.FromSeconds(5)),
                $"Session '{id}' never became active");

        var status = await Api.GetStatusAsync();
        Assert.True(status.ActiveCount >= tags.Length,
            $"Expected >= {tags.Length} concurrent sessions, saw {status.ActiveCount}");

        // Every session reaches a terminal state with a valid per-session id.
        var finals = await Task.WhenAll(sessionIds.Select(id =>
            WorkflowHelpers.PollUntilTerminalAsync(Api, id, TimeSpan.FromSeconds(15))));

        Assert.All(finals, s =>
        {
            Assert.NotNull(s);
            Assert.True(SessionStates.IsTerminal(s!.State),
                $"Session '{s.SessionId}' did not reach a terminal state (was '{s.State}')");
        });
    }

    [Fact]
    public async Task Status_Should_ReflectActiveExecution_When_Running()
    {
        if (_fixture.IsLive)
            return;

        _fixture.ResetFake();
        _fixture.Fake!.ExecutionDelay = TimeSpan.FromSeconds(3);

        var trigger = (await ApiClient.ReadAsync<TriggerResponse>(
            await Api.TriggerAsync(TestWatchItems.InMemoryEnabledTag)))!;
        await WorkflowHelpers.WaitUntilActiveAsync(Api, trigger.SessionId);

        var status = await Api.GetStatusAsync();
        Assert.True(status.IsExecuting);
        Assert.True(status.ActiveCount >= 1);

        await WorkflowHelpers.PollUntilTerminalAsync(Api, trigger.SessionId, TimeSpan.FromSeconds(10));
    }
}
