using TestControllerGrpc.Models;

namespace TestControllerGrpc.Core.Impact;

/// <summary>
/// One Test Case matched to an impacted component, projected from the impact-mapping engine's
/// <see cref="MappedTestCase"/> into a display-ready shape shared by the Code Churn workbook
/// ("Impacted Test Cases" sheet) and the regression grid's row-expand (React + WPF).
/// </summary>
/// <param name="ImpactedArea">The impacted component this test case is recommended for.</param>
/// <param name="TestCaseId">Azure DevOps Test Case work-item id.</param>
/// <param name="TestCaseTitle">Test Case title.</param>
/// <param name="Description">Flattened test steps (trimmed) — the "what it verifies" blurb.</param>
/// <param name="TestCaseUrl">Deep link to the Test Case in Azure DevOps (null when the org is unknown).</param>
/// <param name="ParentFeatureId">Resolved parent Feature id; 0 when none ("does not exist").</param>
/// <param name="MatchType">"full" for a grade-3 judgement, otherwise "partial".</param>
/// <param name="ConfidencePercent">Ranking confidence as a whole percent (0-100).</param>
/// <param name="MatchReason">Natural-language reason the engine matched this test case.</param>
public sealed record ImpactedTestCaseMatch(
    string ImpactedArea,
    int TestCaseId,
    string TestCaseTitle,
    string? Description,
    string? TestCaseUrl,
    int ParentFeatureId,
    string MatchType,
    int ConfidencePercent,
    string MatchReason);

/// <summary>
/// Maps an impacted component (<see cref="SubsystemRow"/>) to the Test Cases the impact-mapping engine
/// recommends for it. Resilient by contract: a component that yields nothing — empty retrieval index,
/// no linked work items, or an engine failure — returns an empty list rather than throwing, so a single
/// bad component never fails a report export or a grid row-expand.
/// </summary>
public interface IRegressionImpactMatcher
{
    /// <summary>Matches one component's changes to recommended Test Cases.</summary>
    Task<IReadOnlyList<ImpactedTestCaseMatch>> MatchAsync(SubsystemRow row, CancellationToken ct);

    /// <summary>
    /// Matches many components with bounded concurrency; one component's failure never fails the batch.
    /// Results are ordered by component, then by descending confidence within each component.
    /// </summary>
    Task<IReadOnlyList<ImpactedTestCaseMatch>> MatchManyAsync(IReadOnlyList<SubsystemRow> rows, CancellationToken ct);
}
