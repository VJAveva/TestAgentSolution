namespace TestControllerGrpc.Core.Impact;

/// <summary>
/// Strongly-typed configuration for the impact-mapping engine (P02), bound from the "ImpactMapping"
/// section. Grouped into nested option classes so appsettings stays readable. <see cref="Validate"/>
/// reports problems and never throws.
/// </summary>
public sealed class ImpactMappingOptions
{
    public const string SectionName = "ImpactMapping";

    /// <summary>
    /// Directory holding the impact databases. <c>null</c> means "use the ProgramData default"
    /// (<c>%ProgramData%\TestAgentSolution\ImpactIndex</c>). Overridden by the <c>IMPACT_INDEX_ROOT</c>
    /// environment variable. Resolved by <see cref="IImpactIndexPathProvider"/>.
    /// </summary>
    public string? IndexRoot { get; set; }

    /// <summary>How old the index may be before the health check reports Stale.</summary>
    public TimeSpan IndexStaleAfter { get; set; } = TimeSpan.FromDays(14);

    public KeywordOptions Keywords { get; set; } = new();
    public AdoOptions Ado { get; set; } = new();
    public IndexOptions Index { get; set; } = new();
    public RetrievalOptions Retrieval { get; set; } = new();
    public AnchorOptions Anchors { get; set; } = new();
    public RerankOptions Rerank { get; set; } = new();
    public SelectionOptions Selection { get; set; } = new();
    public RiskOptions Risk { get; set; } = new();
    public LearningOptions Learning { get; set; } = new();

    /// <summary>Returns a list of configuration problems (empty when valid). Never throws.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        void Positive(int value, string name)
        {
            if (value <= 0) problems.Add($"{name} must be greater than 0.");
        }

        Positive(Keywords.MaxKeywordGroups, "Keywords.MaxKeywordGroups");
        Positive(Keywords.MaxTermsPerGroup, "Keywords.MaxTermsPerGroup");
        Positive(Ado.BatchSize, "Ado.BatchSize");
        Positive(Ado.MaxRetries, "Ado.MaxRetries");
        Positive(Ado.LinkWalkMaxDepth, "Ado.LinkWalkMaxDepth");
        Positive(Index.EmbeddingBatchSize, "Index.EmbeddingBatchSize");
        Positive(Index.IncrementalPageSize, "Index.IncrementalPageSize");
        Positive(Retrieval.TopTestCasesPerGroup, "Retrieval.TopTestCasesPerGroup");
        Positive(Retrieval.TopFeaturesPerGroup, "Retrieval.TopFeaturesPerGroup");
        Positive(Retrieval.MaxCandidateTestCases, "Retrieval.MaxCandidateTestCases");
        Positive(Selection.MaxTestCasesTotal, "Selection.MaxTestCasesTotal");
        Positive(Selection.MaxTestCasesPerFeature, "Selection.MaxTestCasesPerFeature");
        Positive(Selection.MaxInlineFilterChars, "Selection.MaxInlineFilterChars");
        Positive(Anchors.MinAnchorsForEarlyExit, "Anchors.MinAnchorsForEarlyExit");

        if (Selection.EmitRunManifest && string.IsNullOrWhiteSpace(Selection.RunManifestFolder))
            problems.Add("Selection.RunManifestFolder is required when Selection.EmitRunManifest is true.");

        if (Retrieval.RrfK <= 0)
            problems.Add("Retrieval.RrfK must be greater than 0.");
        if (Retrieval.LexicalWeight == 0 && Retrieval.SemanticWeight == 0)
            problems.Add("At least one of Retrieval.LexicalWeight / Retrieval.SemanticWeight must be non-zero.");
        if (Retrieval.FanOutPenaltyFloor > Retrieval.FanOutPenaltyCeiling)
            problems.Add("Retrieval.FanOutPenaltyFloor must be <= Retrieval.FanOutPenaltyCeiling.");
        if (Selection.MmrLambda is < 0 or > 1)
            problems.Add("Selection.MmrLambda must be within [0, 1].");
        if (Selection.TierBudgets is null || Selection.TierBudgets.Count == 0)
            problems.Add("Selection.TierBudgets must not be empty.");

        Positive(Risk.ChurnReferenceFiles, "Risk.ChurnReferenceFiles");
        Positive(Risk.ChurnReferenceChanges, "Risk.ChurnReferenceChanges");
        if (Risk.DefectSaturation <= 0)
            problems.Add("Risk.DefectSaturation must be greater than 0.");
        if (Risk.RecencyHalfLifeDays <= 0)
            problems.Add("Risk.RecencyHalfLifeDays must be greater than 0.");
        if (Risk.HighThreshold > Risk.CriticalThreshold)
            problems.Add("Risk.HighThreshold must be <= Risk.CriticalThreshold.");

        double weightSum = Risk.ChurnWeight + Risk.DefectWeight + Risk.BuildFailureWeight
            + Risk.RecencyWeight + Risk.UncertaintyWeight;
        if (Math.Abs(weightSum - 1.0) > 0.001)
            problems.Add($"Risk weights must sum to 1.0 (found {weightSum:0.###}).");

        return problems;
    }

    /// <summary>Tokenization and keyword-group shaping.</summary>
    public sealed class KeywordOptions
    {
        public int MaxKeywordGroups { get; set; } = 6;
        public int MaxTermsPerGroup { get; set; } = 8;
        public bool SplitPascalCase { get; set; } = true;
        public bool SplitSnakeAndKebab { get; set; } = true;
        public IReadOnlyList<string> StopWords { get; set; } =
        [
            "src", "test", "tests", "common", "util", "utils", "helper", "impl",
            "base", "core", "new", "old", "temp", "interface", "abstract",
        ];
    }

    /// <summary>Azure DevOps query and hydration limits.</summary>
    public sealed class AdoOptions
    {
        public int BatchSize { get; set; } = 200;
        public IReadOnlyList<string> IncludedAreaPaths { get; set; } = [];

        /// <summary>States excluded from the corpus. Applied as a WIQL NOT IN predicate at index time, and
        /// purged from the existing index on the next build.</summary>
        public IReadOnlyList<string> ExcludedStates { get; set; } = ["Removed", "Closed"];
        public int MaxRetries { get; set; } = 4;

        /// <summary>How many hierarchy levels the linked-work-item anchor walk descends.</summary>
        public int LinkWalkMaxDepth { get; set; } = 3;
    }

    /// <summary>Persistent index storage + embedding parameters.</summary>
    public sealed class IndexOptions
    {
        /// <summary>
        /// Legacy relative path. Left for back-compat only — a relative value resolves against the process
        /// working directory, which is how a 1.29 GB database ended up inside the source tree. Hosts now take
        /// the absolute path from <see cref="IImpactIndexPathProvider"/>; set <c>ImpactMapping:IndexRoot</c>
        /// to relocate.
        /// </summary>
        public string DatabasePath { get; set; } = "impact-index.db";
        public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(30);
        public string EmbeddingModel { get; set; } = "text-embedding-3-small";
        public int EmbeddingBatchSize { get; set; } = 64;
        public int IncrementalPageSize { get; set; } = 200;

        /// <summary>Drop and recreate the index when its stamped schema version is not the current one.</summary>
        public bool RebuildOnSchemaChange { get; set; } = true;

        /// <summary>Azure OpenAI resource endpoint for embeddings. Empty → semantic retrieval is disabled
        /// (the null embedding provider is used and the engine runs lexical-only).</summary>
        public string EmbeddingEndpoint { get; set; } = "";

        /// <summary>Azure OpenAI REST api-version used for the embeddings call.</summary>
        public string EmbeddingApiVersion { get; set; } = "2024-10-21";

        /// <summary>Env var holding the Azure OpenAI API key (never stored in config). Default: AZURE_OPENAI_API_KEY.</summary>
        public string EmbeddingApiKeyEnvVar { get; set; } = "AZURE_OPENAI_API_KEY";
    }

    /// <summary>Hybrid retrieval + fusion tuning.</summary>
    public sealed class RetrievalOptions
    {
        public double Bm25K1 { get; set; } = 1.2;
        public double Bm25B { get; set; } = 0.75;
        public int RrfK { get; set; } = 60;
        public double LexicalWeight { get; set; } = 1.0;
        public double SemanticWeight { get; set; } = 1.0;
        public int TopTestCasesPerGroup { get; set; } = 25;
        public int TopFeaturesPerGroup { get; set; } = 10;
        public int MaxFeaturesAfterMerge { get; set; } = 30;
        public int MaxCandidateTestCases { get; set; } = 400;
        public double CorroborationBonus { get; set; } = 0.15;
        public double FanOutPenaltyFloor { get; set; } = 0.35;
        public double FanOutPenaltyCeiling { get; set; } = 1.30;
        public double HubWarningMultiple { get; set; } = 5.0;

        /// <summary>Share of the retrieval scale a test inherits from its parent feature when it was found by
        /// feature expansion rather than by a direct lexical hit.</summary>
        public double ExpandedChildScoreFactor { get; set; } = 0.85;

        /// <summary>Share of the retrieval scale given to a test case with no resolvable parent feature.</summary>
        public double OrphanScoreFactor { get; set; } = 0.30;
    }

    /// <summary>Tier-0 anchor and early-exit thresholds.</summary>
    public sealed class AnchorOptions
    {
        public int LookbackRuns { get; set; } = 6;
        public int MinAnchorsForEarlyExit { get; set; } = 5;
        public double EarlyExitCoverageThreshold { get; set; } = 0.80;
        public bool AllowEarlyExitForCriticalTier { get; set; }
    }

    /// <summary>Graded LLM rerank + HyDE parameters.</summary>
    public sealed class RerankOptions
    {
        public bool EnableLlmRerank { get; set; } = true;
        public bool EnableHyde { get; set; } = true;

        /// <summary>Azure OpenAI chat deployment used for HyDE (P12) and graded rerank (P18). Empty → both
        /// stages fall back to their deterministic offline paths (degrade, never fail).</summary>
        public string LlmModel { get; set; } = "";

        public int BatchSize { get; set; } = 15;
        public int MaxParallelism { get; set; } = 4;
        public int MaxDiffHunksToLlm { get; set; } = 6;
        public int MaxHunkLines { get; set; } = 40;
        public double SelfConsistencyConfidenceThreshold { get; set; } = 0.70;
        public double DowngradeWhenNoCitedSignals { get; set; } = 1.0;
        public TimeSpan CacheTtl { get; set; } = TimeSpan.FromDays(7);
        public TimeSpan HydeCacheTtl { get; set; } = TimeSpan.FromDays(30);
    }

    /// <summary>Selection diversity, budget and threshold knobs.</summary>
    public sealed class SelectionOptions
    {
        public double MmrLambda { get; set; } = 0.70;

        // T4 candidate score = Retrieval*r + Feature*f + Grade*g + Failure*h + Automation bonus.
        // Feature weight is the dangerous one: it credits a test for its PARENT's title matching, which
        // outranks a test whose own text matches when the parent is title-mismatched.
        public double RetrievalWeight { get; set; } = 0.45;
        public double FeatureWeight { get; set; } = 0.25;
        public double GradeWeight { get; set; } = 0.20;
        public double FailureWeight { get; set; } = 0.10;
        public double AutomationBonus { get; set; } = 0.05;
        public int MaxTestCasesPerFeature { get; set; } = 12;
        public int MaxTestCasesTotal { get; set; } = 150;
        public double MinFinalScore { get; set; } = 0.15;

        /// <summary>When false the engine never writes a run manifest, so it can never trigger a pipeline (R3).</summary>
        public bool EmitRunManifest { get; set; }

        /// <summary>Folder the run manifest is written to. Must be a folder a WatchItem watches.</summary>
        public string? RunManifestFolder { get; set; }

        /// <summary>Longest inline [_TestFilter] to emit; beyond this only [_TestListFile] is usable.</summary>
        public int MaxInlineFilterChars { get; set; } = 6000;
        public Dictionary<SelectionTier, TimeSpan> TierBudgets { get; set; } = new()
        {
            [SelectionTier.Smoke] = TimeSpan.FromMinutes(15),
            [SelectionTier.Targeted] = TimeSpan.FromMinutes(90),
            [SelectionTier.Full] = TimeSpan.MaxValue,
        };
    }

    /// <summary>
    /// Regression risk scoring (R1) and the risk-aware selection tuning it drives (R2).
    /// See docs/impact/Regression-Selection-Algorithm.md §2-3.
    /// </summary>
    public sealed class RiskOptions
    {
        /// <summary>When false the scored tier is computed and logged but never consumed by selection (phase 1).</summary>
        public bool EnableRiskWeighting { get; set; }

        public double ChurnWeight { get; set; } = 0.30;
        public double DefectWeight { get; set; } = 0.25;
        public double BuildFailureWeight { get; set; } = 0.20;
        public double RecencyWeight { get; set; } = 0.15;
        public double UncertaintyWeight { get; set; } = 0.10;

        /// <summary>Files-touched value that saturates the churn term.</summary>
        public int ChurnReferenceFiles { get; set; } = 40;

        /// <summary>Change-count value that saturates the churn term.</summary>
        public int ChurnReferenceChanges { get; set; } = 10;

        /// <summary>Weighted defect count at which the defect term reaches 1.0.</summary>
        public double DefectSaturation { get; set; } = 4;

        /// <summary>Half-life of the recency decay, in days.</summary>
        public double RecencyHalfLifeDays { get; set; } = 7;

        public double CriticalThreshold { get; set; } = 0.70;
        public double HighThreshold { get; set; } = 0.45;

        /// <summary>Budget scaling per tier. Never applied to SelectionTier.Full (TimeSpan.MaxValue).</summary>
        public Dictionary<RiskTier, double> BudgetMultipliers { get; set; } = new()
        {
            [RiskTier.Critical] = 1.50,
            [RiskTier.High] = 1.25,
            [RiskTier.Medium] = 1.00,
            [RiskTier.Unmapped] = 1.00,
        };

        /// <summary>Floor on selected test cases per tier, enforced after the budget knapsack.</summary>
        public Dictionary<RiskTier, int> MinimumSelections { get; set; } = new()
        {
            [RiskTier.Critical] = 8,
            [RiskTier.High] = 5,
            [RiskTier.Medium] = 3,
            [RiskTier.Unmapped] = 3,
        };
    }

    /// <summary>Score calibration + outcome-learning configuration.</summary>
    public sealed class LearningOptions
    {
        public string ScoringMode { get; set; } = "Linear"; // Linear | Calibrated | Ranker
        public string? RankerModelPath { get; set; }
        public bool RecordOutcomes { get; set; } = true;

        /// <summary>Durable learning database, kept SEPARATE from the rebuildable index so a forced index
        /// rebuild never wipes outcome history. Default: impact-outcomes.db.</summary>
        public string OutcomeDatabasePath { get; set; } = "impact-outcomes.db";
    }
}
