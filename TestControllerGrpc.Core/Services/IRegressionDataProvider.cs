using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Serves the Regression tab (CIRP — see docs/AzureIntegration/FEATURE-ARCHITECTURE.md).
/// Both hosts (WPF Controller, WebApi) consume this directly or via TestController.Api's
/// /api/impact endpoints — same shape either way, per the platform's "one engine" keystone.
/// </summary>
public interface IRegressionDataProvider
{
    /// <summary>R1/R2/R5/R9 — the subsystem rollup for a scope window; pass null from/to for the latest build (current vs previous).</summary>
    Task<ConsolidatedImpact> GetConsolidatedAsync(DateOnly? from, DateOnly? to, string? branch, CancellationToken ct);

    /// <summary>R11-13 — the recommended regression plan; pass null from/to for the latest build (current vs previous).</summary>
    Task<RegressionScope> GetScopeAsync(DateOnly? from, DateOnly? to, RegressionCategoryKind? category, string? branch, CancellationToken ct);

    /// <summary>R14 — sync freshness / unresolved-repository status bar.</summary>
    Task<RegressionSyncStatus> GetSyncStatusAsync(CancellationToken ct);

    /// <summary>R7/R9 col 8-9 — persist an inline Automation/Manual suite chip edit.</summary>
    Task ApplySuiteEditAsync(RegressionSuiteEdit edit, CancellationToken ct);
}
