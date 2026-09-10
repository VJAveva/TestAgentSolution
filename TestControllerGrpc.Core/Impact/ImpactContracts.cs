namespace TestControllerGrpc.Core.Impact;

// =============================================================================
// Impacted Test Mapping — domain contracts (P01).
// Pure data only: sealed records + enums, no behaviour, no I/O, no host types.
// Consumed identically by the WPF host and the WebApi host ("two front doors, one engine").
// =============================================================================

/// <summary>How exposed an impacted area is to regression, driving selection aggressiveness.</summary>
public enum RiskTier { Unmapped, Medium, High, Critical }

/// <summary>Strength of the evidence tying a test case to a change: assumed &lt; declared &lt; observed.</summary>
public enum MappingConfidence { Assumed, Declared, Observed }

/// <summary>Requested breadth of the run, which sets the runtime budget.</summary>
public enum SelectionTier { Smoke, Targeted, Full }

/// <summary>Which persistent retrieval index a document belongs to.</summary>
public enum IndexKind { Feature, TestCase }

/// <summary>Origin of a deterministic anchor edge (Tier 0).</summary>
public enum AnchorSource { LinkedWorkItem, HistoricalFailure, DeclaredMapping, PriorSelection }

/// <summary>Discovery path(s) by which a feature entered the candidate set (OR-able).</summary>
[Flags]
public enum FeatureDiscoveryPath
{
    None = 0,
    DirectFeatureSearch = 1,
    TestCaseBackReference = 2,
    Anchor = 4,
}

// ── Input ────────────────────────────────────────────────────────────────────

/// <summary>Churn signals for an impacted area, used to weight risk and recency.</summary>
public sealed record ChurnMetrics(
    int LinesAdded, int LinesDeleted, int FilesTouched,
    int CommitCount, int DistinctAuthorCount, DateTimeOffset LastChangedUtc);

/// <summary>An impacted code area resolved from churn analysis — the engine's primary input.</summary>
public sealed record ImpactedArea(
    string AreaId, string DisplayName, string? Subsystem, string? Vob,
    IReadOnlyList<string> ChangedPaths, IReadOnlyList<string> DeclaredRegressionAreas,
    RiskTier RiskTier, ChurnMetrics Churn);

/// <summary>A contiguous changed region within a file diff.</summary>
public sealed record DiffHunk(int StartLine, int LineCount, string Text);

/// <summary>All changed hunks for a single file.</summary>
public sealed record FileDiff(string Path, IReadOnlyList<DiffHunk> Hunks);

/// <summary>The pull request / commit payload that produced the impacted area.</summary>
public sealed record ChangePayload(
    int? PullRequestId, string? PrTitle, string? PrDescription,
    IReadOnlyList<string> CommitMessages, IReadOnlyList<FileDiff> Diffs,
    IReadOnlyList<int> LinkedWorkItemIds);

// ── Query ────────────────────────────────────────────────────────────────────

/// <summary>Structured extraction of a change, ready to turn into retrieval queries.</summary>
public sealed record ChangeDocument(
    string AreaId, IReadOnlyList<string> PathTokens, IReadOnlyList<string> ChangedSymbols,
    IReadOnlyList<string> ChangedLiterals, IReadOnlyList<string> PublicApiChanges,
    string? PrNarrative, string RawText,
    string Fingerprint); // stable SHA256 of RawText, used as a cache key everywhere

/// <summary>HyDE synthetic test text that closes the code-to-test vocabulary gap.</summary>
public sealed record HydeQuery(
    string ChangeSummary, IReadOnlyList<string> SyntheticTestTitles,
    string SyntheticTestBody, IReadOnlyList<string> ExpandedTerms);

/// <summary>A weighted, labelled group of search terms derived from one facet of a change.</summary>
public sealed record KeywordGroup(string GroupId, string Label, IReadOnlyList<string> Terms, double Weight);

// ── Work items ───────────────────────────────────────────────────────────────

/// <summary>Minimal Azure DevOps work item projection, keyed by revision for caching.</summary>
public sealed record AdoWorkItemRef(
    int Id, string WorkItemType, string Title, string? AreaPath, string? State, int Revision);

/// <summary>A candidate Test Case with flattened steps and its parent feature.</summary>
/// <param name="Description">
/// System.Description, HTML stripped. Often the only functional prose on a test case: titles are commonly
/// short requirement ids ("FR 12345") that carry no matchable vocabulary.
/// </param>
/// <param name="AutomatedTestName">
/// Microsoft.VSTS.TCM.AutomatedTestName — the fully-qualified test method. This is the only join key
/// from an ADO test case to something runnable; null for manual cases.
/// </param>
public sealed record TestCaseCandidate(
    AdoWorkItemRef Item, string? StepsText, string? AutomationStatus,
    int? ParentFeatureId, IReadOnlyList<string> Tags,
    string? AutomatedTestName = null, string? AutomatedTestStorage = null,
    string? Description = null);

/// <summary>A candidate Feature and how it was discovered.</summary>
public sealed record FeatureCandidate(
    AdoWorkItemRef Item, string? Description, FeatureDiscoveryPath DiscoveryPath,
    IReadOnlyList<string> MatchedGroupIds, int ChildTestCaseCount);

// ── Scoring ──────────────────────────────────────────────────────────────────

/// <summary>One named contribution to a score, preserved so provenance can show the arithmetic.</summary>
public sealed record ScoreComponent(string Name, double Raw, double Weight, double Weighted);

/// <summary>A value with its aggregate score and the components that produced it.</summary>
public sealed record Scored<T>(T Value, double Score, IReadOnlyList<ScoreComponent> Components) where T : notnull;

/// <summary>A graded LLM relevance judgement (0-3) with the signals it cited.</summary>
public sealed record RelevanceJudgement(
    int Grade, double Confidence, string Reason, IReadOnlyList<string> CitedSignals);

/// <summary>A deterministic Tier-0 edge linking a change area to a test case.</summary>
public sealed record AnchorEdge(
    int TestCaseId, int? FeatureId, AnchorSource Source, double Weight, string Justification);

/// <summary>The full anchor result plus whether it is strong enough to exit the cascade early.</summary>
public sealed record AnchorResult(
    IReadOnlyList<AnchorEdge> Edges, double CoverageScore, bool SufficientForEarlyExit);

// ── Output ───────────────────────────────────────────────────────────────────

/// <summary>One auditable step in a selection's provenance chain.</summary>
public sealed record ProvenanceLink(string Stage, string Detail, double? Score);

/// <summary>A selected test case with its final score, confidence and full provenance.</summary>
public sealed record MappedTestCase(
    TestCaseCandidate TestCase, int FeatureId, double FinalScore, MappingConfidence Confidence,
    RelevanceJudgement? Judgement, AnchorSource? Anchor, IReadOnlyList<ProvenanceLink> Provenance);

/// <summary>A regression area or feature that changed but has no adequate test coverage selected.</summary>
public sealed record CoverageGap(string RegressionArea, string Reason, RiskTier RiskTier);

/// <summary>Why the selector kept or dropped candidates, including the marginal miss.</summary>
public sealed record SelectionDiagnostics(
    TimeSpan BudgetAllowed, TimeSpan BudgetUsed,
    int DroppedByBudget, int DroppedByGrade, int DroppedByDiversity,
    MappedTestCase? MarginalCandidate);

/// <summary>The complete, auditable result of one impact-mapping run.</summary>
public sealed record ImpactMappingResult(
    ImpactedArea Area, IReadOnlyList<KeywordGroup> Groups,
    IReadOnlyList<Scored<FeatureCandidate>> SelectedFeatures,
    IReadOnlyList<MappedTestCase> MappedTestCases, IReadOnlyList<CoverageGap> Gaps,
    AnchorResult Anchors, SelectionDiagnostics Diagnostics, SelectionTier Tier,
    bool EarlyExit, IReadOnlyList<string> Warnings, TimeSpan Elapsed, Guid RunId);

/// <summary>A progress tick emitted per completed tier; only the terminal tick carries a Result.</summary>
public sealed record ImpactMappingProgress(
    string Stage, int StageIndex, int TotalStages, string Message, ImpactMappingResult? Result);
