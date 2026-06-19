using TestController.ApiTests.Data;
using TestController.ApiTests.Infrastructure;

namespace TestController.ApiTests.Workflows;

/// <summary>
/// Happy-path functional flow: trigger a pipeline, poll the session to completion,
/// and assert the terminal outcome. Runs in both InMemory and Live modes.
/// </summary>
public sealed class TriggerExecuteResultsTests : IClassFixture<ApiTestFixture>
{
    private readonly ApiTestFixture _fixture;
    private ApiClient Api => _fixture.Api;

    public TriggerExecuteResultsTests(ApiTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Trigger_Should_CompleteSession_When_TagIsValid()
    {
        _fixture.ResetFake();

        var tag = await TestWatchItems.DiscoverEnabledTagAsync(Api);

        var response = await Api.TriggerAsync(tag);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var trigger = await ApiClient.ReadAsync<TriggerResponse>(response);
        Assert.NotNull(trigger);
        Assert.False(string.IsNullOrWhiteSpace(trigger!.SessionId));

        var final = await WorkflowHelpers.PollUntilTerminalAsync(Api, trigger.SessionId);

        Assert.NotNull(final);
        Assert.True(SessionStates.IsTerminal(final!.State), $"Session did not terminate; state={final.State}");
        Assert.Equal(tag, final.WatchItemTag);

        // InMemory uses the fake (deterministic success) — assert the strong outcome.
        if (!_fixture.IsLive)
        {
            Assert.Equal(SessionStates.Completed, final.State);
            Assert.True(final.TotalActions > 0, "Expected at least one recorded action");
            Assert.True(final.SucceededCount > 0, "Expected at least one succeeded action");
        }
    }

    [Fact]
    public async Task Trigger_Should_Return404_When_TagIsUnknown()
    {
        _fixture.ResetFake();

        var response = await Api.TriggerAsync("__no_such_tag__");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetSession_Should_Return404_When_SessionIsUnknown()
    {
        var session = await Api.GetSessionAsync("deadbeefdead");
        Assert.Null(session);
    }
}
