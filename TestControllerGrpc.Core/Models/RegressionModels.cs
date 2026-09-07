using System.Text.Json.Serialization;

namespace TestControllerGrpc.Models;

// =============================================================================
// Regression tab / CIRP domain model (docs/AzureIntegration/FEATURE-ARCHITECTURE.md §6,
// docs/AzureIntegration/RegressionTab-UI-Spec.md).
//
// STATUS: Phase 6 UI-first slice. Real Azure DevOps ingest (Phases 0-5 of the
// architecture doc) is a separate, gated track that needs Entra/PAT credentials
// and captured fixtures — see docs/AzureIntegration/Azure-DevOps-Integration-Guide.md.
// Until that lands, IRegressionDataProvider is backed by MockRegressionDataProvider
// so the grid (R9) and plan panel (R11-13) can be built and reviewed now, per the
// spec's own build order: "R9 first with static data".
//
// Every edge in the real model carries evidence + confidence (assumed/declared/
// observed) — that shape is preserved here even in the mock so the UI never has
// to change when real ingest replaces the provider.
// =============================================================================

public enum RegressionCategoryKind { Runtime, Config, Both, Unclassified }

public enum RegressionEvidenceKind { Assumed, Declared, Observed }

public enum RegressionScopeKind { Build, Weekly, Custom, Release }

public enum RegressionWorkItemKind { Ims, Bug, Story, Feature, Other }

/// <summary>How a change entered the build — used to distinguish human PRs from automated build-syncups.</summary>
public enum RegressionChangeKind { PullRequest, Automated, Commit }

/// <summary>A single linked Azure DevOps work item (Changes / work items column, R9 col 5).</summary>
/// <param name="WorkItemType">
/// Raw ADO type ("Task", "Bug", "User Story"...). Kept alongside <paramref name="Kind"/> because Kind folds
/// Task, Feature and every unmapped type into Other, so it cannot drive type-name exclusion policy.
/// </param>
public sealed record RegressionWorkItemRef(
    int Id,
    RegressionWorkItemKind Kind,
    string Title,
    string? Url,
    DateTimeOffset? CreatedUtc = null,
    string? WorkItemType = null);

/// <summary>One observed change (commit/PR) touching a subsystem.</summary>
public sealed record RegressionChangeRef(
    string ChangeId,
    string Summary,
    DateTimeOffset ObservedUtc,
    IReadOnlyList<string> FilePaths,
    IReadOnlyList<RegressionWorkItemRef> WorkItems,
    RegressionChangeKind Kind = RegressionChangeKind.Commit,
    string? Url = null);

/// <summary>An automated or manual suite chip (R9 columns 8/9). Unlinked manual entries render dashed in the UI.</summary>
public sealed record RegressionSuiteRef(
    string SuiteId,
    bool IsLinked,
    string? Url,
    RegressionEvidenceKind Evidence,
    string? Title = null);

/// <summary>One row of the Regression grid (R9) — a subsystem within the current scope.</summary>
public sealed record SubsystemRow(
    string Component,
    string Subsystem,
    RegressionCategoryKind Category,
    RegressionEvidenceKind CategoryConfidence,
    IReadOnlyList<string> FilesModified,
    int TotalFilesModified,
    IReadOnlyList<RegressionChangeRef> Changes,
    string RiskTier,
    IReadOnlyList<RegressionSuiteRef> AutomatedSuites,
    IReadOnlyList<RegressionSuiteRef> ManualSuites,
    double EstimatedMinutes,
    bool IsEstimate,
    IReadOnlyList<string>? RegressionAreas = null,
    IReadOnlyList<string>? UseCases = null,
    string? BuildNumber = null,
    DateTimeOffset? BuildFinishedUtc = null,
    string? BuildResult = null,
    string? LatestSuccessfulBuild = null,
    string? LatestSuccessfulBuildUrl = null,
    string? Repository = null,
    string? RepositoryUrl = null,
    string? DefaultBranch = null,
    IReadOnlyList<string>? SolutionNames = null);

/// <summary>Scope summary bar (R5).</summary>
public sealed record RegressionSummary(
    string ScopeLabel,
    string RangeText,
    int ChangeCount,
    int SubsystemCount,
    int FileCount,
    IReadOnlyList<int> WeeklyActivity);

/// <summary>Response for GET /api/impact/consolidated.</summary>
public sealed record ConsolidatedImpact(
    RegressionSummary Summary,
    IReadOnlyList<SubsystemRow> Rows);

/// <summary>One column of the plan panel (R11/R12): Runtime or Config.</summary>
public sealed record RegressionPlanColumn(
    int Subsystems,
    int AutomatedSuites,
    int ManualSuites,
    int Gaps,
    double EstimatedMinutes);

/// <summary>Response for GET /api/impact/scope — the recommended regression plan (R11-13).</summary>
public sealed record RegressionScope(
    RegressionPlanColumn Runtime,
    RegressionPlanColumn Config,
    IReadOnlyList<string> UnmappedSubsystems,
    int ParallelAgentCount);

/// <summary>Response for GET /api/impact/sync-status — status bar (R14).</summary>
public sealed record RegressionSyncStatus(
    string State,
    DateTimeOffset? LastSyncUtc,
    string MapVersion,
    IReadOnlyList<string> UnresolvedRepositories);

/// <summary>Body for POST /api/impact/suites — persists an inline suite-chip edit (R9 columns 8/9).</summary>
public sealed record RegressionSuiteEdit(
    [property: JsonPropertyName("subsystem")] string Subsystem,
    [property: JsonPropertyName("isManual")] bool IsManual,
    [property: JsonPropertyName("suiteId")] string SuiteId,
    [property: JsonPropertyName("remove")] bool Remove);
