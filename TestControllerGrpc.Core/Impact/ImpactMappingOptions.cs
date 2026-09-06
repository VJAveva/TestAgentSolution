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
        Positive(Anchors.MinAnchorsForEarlyExit, "Anchors.MinAnchorsForEarlyExit");

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
        public int MaxFeaturesPerGroupQuery { get; set; } = 100;
        public int MaxTestCasesPerGroupQuery { get; set; } = 200;
        public int BatchSize { get; set; } = 200;
        public IReadOnlyList<string> IncludedAreaPaths { get; set; } = [];
        public IReadOnlyList<string> ExcludedStates { get; set; } = ["Removed", "Closed"];
        public int MaxRetries { get; set; } = 4;
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
        public bool UseWiqlPreFilter { get; set; }
        public string EmbeddingModel { get; set; } = "text-embedding-3-small";
        public int EmbeddingBatchSize { get; set; } = 64;
        public int IncrementalPageSize { get; set; } = 200;
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
        public int TopFeaturesPerBranch { get; set; } = 20;
        public int MaxFeaturesAfterMerge { get; set; } = 30;
        public int MaxCandidateTestCases { get; set; } = 400;
        public double CorroborationBonus { get; set; } = 0.15;
        public double FanOutPenaltyFloor { get; set; } = 0.35;
        public double FanOutPenaltyCeiling { get; set; } = 1.30;
        public double HubWarningMultiple { get; set; } = 5.0;
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
        public int MaxTestCasesPerFeature { get; set; } = 12;
        public int MaxTestCasesTotal { get; set; } = 150;
        public double MinFinalScore { get; set; } = 0.15;
        public Dictionary<SelectionTier, TimeSpan> TierBudgets { get; set; } = new()
        {
            [SelectionTier.Smoke] = TimeSpan.FromMinutes(15),
            [SelectionTier.Targeted] = TimeSpan.FromMinutes(90),
            [SelectionTier.Full] = TimeSpan.MaxValue,
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
