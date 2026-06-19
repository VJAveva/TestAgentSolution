using TestController.ApiTests.Infrastructure;

namespace TestController.ApiTests.Contracts;

/// <summary>
/// Read-only contract checks for the supporting endpoints the workflow tests rely
/// on: liveness, the agents registry, and the execution status/sessions envelopes.
/// These guard the JSON shapes the test client deserializes. Run in both modes.
/// </summary>
public sealed class HealthAndRegistryTests : IClassFixture<ApiTestFixture>
{
    private readonly ApiTestFixture _fixture;
    private ApiClient Api => _fixture.Api;

    public HealthAndRegistryTests(ApiTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Health_Should_ReturnSuccess_When_LiveProbeQueried()
    {
        var response = await Api.GetHealthAsync();
        Assert.True(response.IsSuccessStatusCode, $"/healthz/live returned {(int)response.StatusCode}");
    }

    [Fact]
    public async Task Agents_Should_ReturnArray_When_Queried()
    {
        var agents = await Api.GetAgentsAsync();
        Assert.NotNull(agents);

        if (!_fixture.IsLive)
            Assert.Contains(agents, a => string.Equals(a.Name, "Agent1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Status_Should_ReturnEnvelope_When_Queried()
    {
        var status = await Api.GetStatusAsync();
        Assert.True(status.ActiveCount >= 0);
    }

    [Fact]
    public async Task Sessions_Should_ReturnEnvelope_When_Queried()
    {
        var sessions = await Api.GetSessionsAsync();
        Assert.NotNull(sessions.Sessions);
        Assert.True(sessions.ActiveCount >= 0);
    }
}
