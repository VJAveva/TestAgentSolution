using TestControllerGrpc.Ado.Dto;

namespace TestControllerGrpc.Ado;

public interface ITestPlanQueries
{
    /// <summary>All test plans in the project.</summary>
    Task<IReadOnlyList<AdoTestPlanRefDto>> GetPlansAsync(CancellationToken ct);

    /// <summary>All suites under a plan (flat, not hierarchical).</summary>
    Task<IReadOnlyList<AdoTestSuiteDto>> GetSuitesAsync(int planId, CancellationToken ct);
}

/// <summary>
/// Minimal Test Plan/Suite queries. Real suite-id \u2192 subsystem resolution is a known gap
/// (FEATURE-ARCHITECTURE.md \u00a711 gap scoreboard) \u2014 this only fetches raw plan/suite data;
/// matching a suite to a subsystem still needs the naming-convention rule (suite id = test case id
/// minus trailing case number, e.g. "FR1359724.WP.IO.Scaling.TCS001" -> "FR1359724.WP.IO.Scaling").
/// </summary>
public sealed class TestPlanQueries : ITestPlanQueries
{
    private readonly AdoClient _client;

    public TestPlanQueries(AdoClient client) => _client = client;

    public async Task<IReadOnlyList<AdoTestPlanRefDto>> GetPlansAsync(CancellationToken ct)
    {
        var path = _client.ProjectApiPath("testplan/plans?api-version=7.1");
        var result = await _client.GetAsync<AdoListResponse<AdoTestPlanRefDto>>(path, ct);
        return result.Value;
    }

    public async Task<IReadOnlyList<AdoTestSuiteDto>> GetSuitesAsync(int planId, CancellationToken ct)
    {
        var path = _client.ProjectApiPath($"testplan/plans/{planId}/suites?api-version=7.1");
        var result = await _client.GetAsync<AdoListResponse<AdoTestSuiteDto>>(path, ct);
        return result.Value;
    }
}
