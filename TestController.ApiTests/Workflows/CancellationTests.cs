using TestController.ApiTests.Data;
using TestController.ApiTests.Infrastructure;

namespace TestController.ApiTests.Workflows;

/// <summary>
/// Cancellation flow. InMemory-only: the fake injects an execution delay so the
/// session is observably "Running" when we cancel. Note there is NO "Cancelled"
/// session state — cancellation simply drives the session to a terminal,
/// non-Running state.
/// </summary>
public sealed class CancellationTests : IClassFixture<ApiTestFixture>
{
    private readonly ApiTestFixture _fixture;
    private ApiClient Api => _fixture.Api;

    public CancellationTests(ApiTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Cancel_Should_EndSession_When_SessionIsRunning()
    {
        if (_fixture.IsLive)
            return; // Requires the fake's execution delay to observe a running session deterministically.

        _fixture.ResetFake();
        _fixture.Fake!.ExecutionDelay = TimeSpan.FromSeconds(3);

        var response = await Api.TriggerAsync(TestWatchItems.InMemoryEnabledTag);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var trigger = (await ApiClient.ReadAsync<TriggerResponse>(response))!;

        var becameActive = await WorkflowHelpers.WaitUntilActiveAsync(Api, trigger.SessionId);
        Assert.True(becameActive, "Session never appeared in the active list");

        // Test user is Admin, so ownership passes and cancel succeeds while Running.
        var cancel = await Api.CancelSessionAsync(trigger.SessionId, userId: "TestUser");
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

        var final = await WorkflowHelpers.PollUntilTerminalAsync(
            Api, trigger.SessionId, TimeSpan.FromSeconds(10));

        Assert.True(
            final is null || final.State != SessionStates.Running,
            $"Session was still Running after cancellation; state={final?.State}");
    }

    [Fact]
    public async Task Cancel_Should_Return404_When_SessionIsUnknown()
    {
        if (_fixture.IsLive)
            return;

        _fixture.ResetFake();
        var cancel = await Api.CancelSessionAsync("deadbeefdead", userId: "TestUser");
        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
    }
}
