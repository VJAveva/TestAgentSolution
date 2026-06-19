using TestController.ApiTests.Data;
using TestController.ApiTests.Infrastructure;
using TestControllerGrpc.Services;

namespace TestController.ApiTests.Workflows;

/// <summary>
/// Failure handling: when a command fails, the session must reach a terminal state
/// that is NOT "Completed". InMemory-only — failure injection requires the fake
/// dispatcher (driving a real Live agent to fail on demand is out of scope).
/// </summary>
public sealed class FailureRecoveryTests : IClassFixture<ApiTestFixture>
{
    private readonly ApiTestFixture _fixture;
    private ApiClient Api => _fixture.Api;

    public FailureRecoveryTests(ApiTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Trigger_Should_ReportFailure_When_CommandFails()
    {
        if (_fixture.IsLive)
            return;

        _fixture.ResetFake();
        _fixture.Fake!.OnExecute = _ => new ActionResult(false, 1, "injected failure");

        var response = await Api.TriggerAsync(TestWatchItems.InMemoryEnabledTag);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var trigger = (await ApiClient.ReadAsync<TriggerResponse>(response))!;

        var final = await WorkflowHelpers.PollUntilTerminalAsync(Api, trigger.SessionId);

        Assert.NotNull(final);
        Assert.True(SessionStates.IsTerminal(final!.State), $"state={final.State}");
        Assert.NotEqual(SessionStates.Completed, final.State);
    }

    [Fact]
    public async Task Trigger_Should_Succeed_When_CommandSucceedsAfterPriorFailure()
    {
        if (_fixture.IsLive)
            return;

        _fixture.ResetFake();

        // First run fails.
        _fixture.Fake!.OnExecute = _ => new ActionResult(false, 1, "injected failure");
        var failId = (await ApiClient.ReadAsync<TriggerResponse>(
            await Api.TriggerAsync(TestWatchItems.InMemoryEnabledTag)))!.SessionId;
        await WorkflowHelpers.PollUntilTerminalAsync(Api, failId);

        // Recovery run succeeds.
        _fixture.Fake.OnExecute = _ => new ActionResult(true, 0, string.Empty);
        var okId = (await ApiClient.ReadAsync<TriggerResponse>(
            await Api.TriggerAsync(TestWatchItems.InMemoryEnabledTag)))!.SessionId;
        var final = await WorkflowHelpers.PollUntilTerminalAsync(Api, okId);

        Assert.NotNull(final);
        Assert.Equal(SessionStates.Completed, final!.State);
    }
}
