# Impacted Test Mapping — Copilot Build Guide

**Solution:** TestAgentSolution
**Target project:** `TestControllerGrpc.Core` (shared engine consumed by both the WPF host and the WebApi host)
**Runtime:** .NET 10, C# 13
**Purpose:** Given a code change, resolve the ADO Features and Test Cases that must be executed, with auditable provenance and measurable recall.

---

## How to use this file

1. Open a Copilot Chat session in the solution.
2. Paste **Section A (Context Primer)** once. Do not skip it. Every later prompt assumes Copilot has it.
3. Work through prompts **P01 → P30 in order**. Each prompt is a self-contained paste block delimited by triple backticks.
4. After each prompt: build, run the stated acceptance check, commit. Do not batch prompts.
5. Stop at each **CHECKPOINT** and verify before continuing. Checkpoints exist where a silent mistake would be expensive to unwind later.

Keep the already-generated files open in adjacent editor tabs. Copilot reads open editors as context and will match your existing record shapes rather than inventing new ones.

---

## Progress checklist

```
Phase 0  Foundations          [ ] P01 [ ] P02 [ ] P03
Phase 1  ADO access           [ ] P04 [ ] P05
Phase 2  Retrieval index      [ ] P06 [ ] P07 [ ] P08 [ ] P09      <-- CHECKPOINT 1
Phase 3  Query construction   [ ] P10 [ ] P11 [ ] P12
Phase 4  Retrieval & features [ ] P13 [ ] P14 [ ] P15 [ ] P16 [ ] P17
Phase 5  Rerank & calibrate   [ ] P18 [ ] P19                      <-- CHECKPOINT 2
Phase 6  Anchors & learning   [ ] P20 [ ] P21
Phase 7  Selection            [ ] P22 [ ] P23
Phase 8  Orchestration        [ ] P24 [ ] P25                      <-- CHECKPOINT 3
Phase 9  Test & eval harness  [ ] P26 [ ] P27 [ ] P28
Phase 10 Host integration     [ ] P29 [ ] P30                      <-- CHECKPOINT 4
```

---

# Section A — Context Primer

Paste this block once, at the start of the Copilot session.

```
CONTEXT PRIMER — read before every task in this session.

I am building an "impacted test mapping" engine inside an existing .NET 10 solution
called TestAgentSolution. All code goes into the class library project
TestControllerGrpc.Core, under the folder Core/Impact/, namespace root
TestControllerGrpc.Core.Impact.

ARCHITECTURAL RULE — two front doors, one engine.
This engine is consumed by two hosts: a WPF desktop application and an ASP.NET Core
WebApi. Both hosts register the same interfaces with their own concrete
implementations where hosting differs. Therefore:
- No host-specific type may appear anywhere under Core/Impact. No WPF Dispatcher,
  no SignalR IHubContext, no WinForms, no System.Windows.
- Notification and progress reporting are exposed as IAsyncEnumerable or IProgress<T>,
  never as an event tied to a UI thread.
- All configuration arrives via IOptionsMonitor<T>, never via static state.

WHAT THE ENGINE DOES.
Input: an impacted code area from churn analysis, plus the pull request / commit
payload that produced it.
Output: a ranked set of Azure DevOps Test Cases to execute, the Features they belong
to, coverage gaps, and a full provenance chain for every selection.

PIPELINE SHAPE — a cascade, not a linear pass.
  Tier 0  Anchors        cheap deterministic high-precision signals; may exit early
  Tier 1  Query build    turn a diff into text that looks like a test case
  Tier 2  Retrieval      BM25 + dense vectors over a persistent global index, RRF fused
  Tier 3  Rerank         graded LLM relevance 0-3, then score calibration
  Tier 4  Selection      MMR diversity + runtime-budget knapsack + recall safety net
  Tier 5  Learning       execution outcomes feed back into Tier 0

NON-NEGOTIABLE DESIGN RULES.
1. Recall bias. A missed regression costs far more than an extra test run. Every
   ambiguous path fails toward inclusion, never toward silent exclusion.
2. Provenance is a product feature, not debug output. Every selected test case carries
   an ordered chain: area -> query signal -> feature -> discovery path -> retrieval
   scores -> LLM grade -> selection reason. A QA lead must be able to audit any result
   backwards without reading logs.
3. Determinism. Same inputs produce the same output. All tie-breaks resolve on work
   item id ascending. All generated identifiers are stable hashes, never GUIDs or
   timestamps.
4. Degradation, not failure. The pipeline must produce useful output with no embedding
   provider and no LLM available, at reduced recall. Every optional dependency has a
   Null implementation.
5. Credentials never appear in source. Azure DevOps access goes through
   IAdoCredentialProvider exclusively. Never write a PAT into a literal, a config file
   that is committed, an exception message, or a log line.
6. No external packages unless I explicitly name them in the prompt. If you believe a
   package is required, say so and stop rather than adding it.

STYLE.
File-scoped namespaces. Nullable enabled. Sealed by default. Records for data, classes
for behaviour. XML doc comments on every public type explaining its role in the
pipeline. Constructor injection only. CancellationToken on every async method,
honoured at every await. ILogger<T> injected, structured logging, no string
concatenation in log messages.

Acknowledge this primer in one line, then wait for my first task.
```

---

# Section B — Architecture reference

## B.1 Pipeline

```
              Impacted Area + PR/Commit payload
                            |
                            v
                  ===== TIER 0: ANCHORS =====
      +---------------------+---------------------+
      |                     |                     |
      v                     v                     v
Linked Work Items    Historical Failure    Declared Map Edges
(PR -> WI -> TC)     Co-occurrence          (churn workbook,
                     (last N releases)       vobs.csv)
      +---------------------+---------------------+
                            |
                            v
                    Anchor Test Cases
                  (confidence = observed)
                            |
                  Coverage sufficient? --- yes ---> Tier 4
                            |
                            no
                            v
                 ===== TIER 1: QUERY BUILD =====
                            |
              Change Document Builder (Roslyn)
              paths + changed symbols + string literals
              + public API deltas + PR narrative
                            |
                            v
              LLM Change Summary + HyDE synthetic test text
                            |
                 +----------+----------+
                 |                     |
                 v                     v
          Keyword Groups        Dense Query Vector
                 |                     |
                 +----------+----------+
                            |
                 ===== TIER 2: RETRIEVAL =====
                            |
      +---------------------+---------------------+
      |                                           |
      v                                           v
Feature Index (global)                   Test Case Index (global)
BM25 + Dense                             BM25 + Dense
      |                                           |
      v                                           v
Top Features                              Top Test Cases
      |                                           |
      |                                           v
      |                              Parent Feature Extraction
      |                                           |
      +---------------------+---------------------+
                            |
                            v
                 RRF Merge + Corroboration Bonus
                            |
                            v
                 Fan-out Normalization (hub penalty)
                            |
                            v
                 Child Test Case Expansion
                            |
                            v
                 Candidate Reduction (BM25 + dense)
                            |
                 ===== TIER 3: RERANK =====
                            |
            Graded LLM Rerank (0-3) with diff hunks
            adaptive self-consistency on borderline cases
                            |
                            v
                 Score Calibration (isotonic / Platt)
                            |
                 ===== TIER 4: SELECTION =====
                            |
            MMR Diversity + Runtime Budget Knapsack
                            |
            Union with Recall Safety Net
                            |
                            v
      Final Test Cases + Coverage Gaps + Provenance
                            |
                 ===== TIER 5: LEARNING =====
                            |
      Execution outcomes -> edge weights -> back to Tier 0
```

## B.2 Two principles that decide most of the design

**Retrieval recall is the ceiling on the entire system.** A test case that never enters the candidate set cannot be recovered by a better reranker, a better selector, or a better model. This is why the persistent global index (Phase 2) is built before anything that consumes it, and why Azure DevOps `WIQL CONTAINS` is not the retrieval mechanism. A `CONTAINS` clause matching C# identifiers against English test titles sets a recall ceiling you can never climb back over.

**The query side matters more than the ranking side.** Test cases are written in functional English by engineers who never saw your class names. Searching that corpus with tokens scraped from file paths is a vocabulary mismatch, and it is where most recall is lost. Phase 3 exists to close that gap: extract the changed string literals and public API signatures from the diff, then have the model write what a test for this change would plausibly look like, and search with that.

---

# Phase 0 — Foundations

## P01 — Domain contracts

```
Create Core/Impact/ImpactContracts.cs, namespace TestControllerGrpc.Core.Impact.

Pure data only. No behaviour, no I/O. Sealed records with file-scoped namespace,
nullable enabled, XML docs on every public type.

ENUMS
  enum RiskTier { Unmapped, Medium, High, Critical }
  enum MappingConfidence { Assumed, Declared, Observed }
  enum SelectionTier { Smoke, Targeted, Full }
  enum IndexKind { Feature, TestCase }
  enum AnchorSource { LinkedWorkItem, HistoricalFailure, DeclaredMapping, PriorSelection }
  [Flags] enum FeatureDiscoveryPath { None = 0, DirectFeatureSearch = 1, TestCaseBackReference = 2, Anchor = 4 }

INPUT
  record ChurnMetrics(int LinesAdded, int LinesDeleted, int FilesTouched,
      int CommitCount, int DistinctAuthorCount, DateTimeOffset LastChangedUtc);

  record ImpactedArea(string AreaId, string DisplayName, string? Subsystem, string? Vob,
      IReadOnlyList<string> ChangedPaths, IReadOnlyList<string> DeclaredRegressionAreas,
      RiskTier RiskTier, ChurnMetrics Churn);

  record DiffHunk(int StartLine, int LineCount, string Text);
  record FileDiff(string Path, IReadOnlyList<DiffHunk> Hunks);
  record ChangePayload(int? PullRequestId, string? PrTitle, string? PrDescription,
      IReadOnlyList<string> CommitMessages, IReadOnlyList<FileDiff> Diffs,
      IReadOnlyList<int> LinkedWorkItemIds);

QUERY
  record ChangeDocument(string AreaId, IReadOnlyList<string> PathTokens,
      IReadOnlyList<string> ChangedSymbols, IReadOnlyList<string> ChangedLiterals,
      IReadOnlyList<string> PublicApiChanges, string? PrNarrative, string RawText,
      string Fingerprint);   // stable SHA256 of RawText, used as a cache key everywhere

  record HydeQuery(string ChangeSummary, IReadOnlyList<string> SyntheticTestTitles,
      string SyntheticTestBody, IReadOnlyList<string> ExpandedTerms);

  record KeywordGroup(string GroupId, string Label, IReadOnlyList<string> Terms, double Weight);

WORK ITEMS
  record AdoWorkItemRef(int Id, string WorkItemType, string Title, string? AreaPath,
      string? State, int Revision);

  record TestCaseCandidate(AdoWorkItemRef Item, string? StepsText, string? AutomationStatus,
      int? ParentFeatureId, IReadOnlyList<string> Tags);

  record FeatureCandidate(AdoWorkItemRef Item, string? Description,
      FeatureDiscoveryPath DiscoveryPath, IReadOnlyList<string> MatchedGroupIds,
      int ChildTestCaseCount);

SCORING
  record ScoreComponent(string Name, double Raw, double Weight, double Weighted);
  record Scored<T>(T Value, double Score, IReadOnlyList<ScoreComponent> Components) where T : notnull;
  record RelevanceJudgement(int Grade, double Confidence, string Reason,
      IReadOnlyList<string> CitedSignals);
  record AnchorEdge(int TestCaseId, int? FeatureId, AnchorSource Source, double Weight,
      string Justification);
  record AnchorResult(IReadOnlyList<AnchorEdge> Edges, double CoverageScore,
      bool SufficientForEarlyExit);

OUTPUT
  record ProvenanceLink(string Stage, string Detail, double? Score);
  record MappedTestCase(TestCaseCandidate TestCase, int FeatureId, double FinalScore,
      MappingConfidence Confidence, RelevanceJudgement? Judgement, AnchorSource? Anchor,
      IReadOnlyList<ProvenanceLink> Provenance);
  record CoverageGap(string RegressionArea, string Reason, RiskTier RiskTier);
  record SelectionDiagnostics(TimeSpan BudgetAllowed, TimeSpan BudgetUsed,
      int DroppedByBudget, int DroppedByGrade, int DroppedByDiversity,
      MappedTestCase? MarginalCandidate);
  record ImpactMappingResult(ImpactedArea Area, IReadOnlyList<KeywordGroup> Groups,
      IReadOnlyList<Scored<FeatureCandidate>> SelectedFeatures,
      IReadOnlyList<MappedTestCase> MappedTestCases, IReadOnlyList<CoverageGap> Gaps,
      AnchorResult Anchors, SelectionDiagnostics Diagnostics, SelectionTier Tier,
      bool EarlyExit, IReadOnlyList<string> Warnings, TimeSpan Elapsed, Guid RunId);

  record ImpactMappingProgress(string Stage, int StageIndex, int TotalStages,
      string Message, ImpactMappingResult? Result);
```

**Acceptance:** compiles with zero warnings under `TreatWarningsAsErrors`. No type references any host assembly.

---

## P02 — Options

```
Create Core/Impact/ImpactMappingOptions.cs.

A single class bound from configuration section "ImpactMapping". Group properties into
nested classes so appsettings stays readable: Keywords, Ado, Index, Retrieval, Anchors,
Rerank, Selection, Learning.

Keywords:   MaxKeywordGroups=6, MaxTermsPerGroup=8, SplitPascalCase=true,
            SplitSnakeAndKebab=true,
            StopWords=["src","test","tests","common","util","utils","helper","impl",
                       "base","core","new","old","temp","interface","abstract"]

Ado:        MaxFeaturesPerGroupQuery=100, MaxTestCasesPerGroupQuery=200, BatchSize=200,
            IncludedAreaPaths=[], ExcludedStates=["Removed","Closed"],
            MaxRetries=4, LinkWalkMaxDepth=3

Index:      DatabasePath="impact-index.db", MaxAge=30:00:00, UseWiqlPreFilter=false,
            EmbeddingModel="text-embedding-3-small", EmbeddingBatchSize=64,
            IncrementalPageSize=200, RebuildOnSchemaChange=true

Retrieval:  Bm25K1=1.2, Bm25B=0.75, RrfK=60, LexicalWeight=1.0, SemanticWeight=1.0,
            TopTestCasesPerGroup=25, TopFeaturesPerGroup=10, TopFeaturesPerBranch=20,
            MaxFeaturesAfterMerge=30, MaxCandidateTestCases=400,
            CorroborationBonus=0.15, FanOutPenaltyFloor=0.35,
            FanOutPenaltyCeiling=1.30, HubWarningMultiple=5.0

Anchors:    LookbackRuns=6, MinAnchorsForEarlyExit=5, EarlyExitCoverageThreshold=0.80,
            AllowEarlyExitForCriticalTier=false

Rerank:     EnableLlmRerank=true, EnableHyde=true, BatchSize=15, MaxParallelism=4,
            MaxDiffHunksToLlm=6, MaxHunkLines=40,
            SelfConsistencyConfidenceThreshold=0.70,
            DowngradeWhenNoCitedSignals=1.0, CacheTtl=7.00:00:00,
            HydeCacheTtl=30.00:00:00

Selection:  MmrLambda=0.70, MaxTestCasesPerFeature=12, MaxTestCasesTotal=150,
            MinFinalScore=0.15,
            TierBudgets={Smoke:00:15:00, Targeted:01:30:00, Full:10675199.02:48:05.4775807}

Learning:   ScoringMode="Linear" (Linear|Calibrated|Ranker), RankerModelPath=null,
            RecordOutcomes=true

Add:  IReadOnlyList<string> Validate();   // returns problems, never throws
Check: non-positive caps, RrfK<=0, both retrieval weights zero, empty TierBudgets,
       MmrLambda outside [0,1], FanOutPenaltyFloor > Ceiling.

Also emit the matching "ImpactMapping" JSON block with all defaults, ready to paste
into appsettings.json.
```

**Acceptance:** `Validate()` returns an empty list for defaults, and at least one problem for `RrfK = 0`.

---

## P03 — Shared tokenizer

```
Create Core/Impact/Text/Tokenizer.cs — a static class, the single source of truth for
tokenization across the whole engine.

public static class Tokenizer {
    public static IReadOnlyList<string> Tokenize(string? text, ImpactMappingOptions.KeywordOptions opts);
    public static IReadOnlyList<string> SplitIdentifier(string identifier);
    public static string NormalizeTerm(string term);
}

Rules:
- SplitIdentifier handles PascalCase, camelCase, snake_case, kebab-case, dot-separated
  and ALLCAPS runs. "GalaxyDeployEngine" -> [galaxy, deploy, engine].
  "HTTPRequestHandler" -> [http, request, handler] (do not split an acronym run
  mid-way; the boundary is the last uppercase before a lowercase).
- Always emit the original compound token alongside its parts. Compounds are the
  strongest lexical signal and dropping them loses precision.
- Lowercase, trim punctuation, drop tokens under 3 chars, drop pure numerics, drop
  stop words, drop GUIDs, drop anything matching a file-extension pattern.
- Deterministic order: preserve first-appearance order, deduplicate.

CRITICAL: index construction and query construction must both call this class. If
document tokenization and query tokenization ever drift apart, BM25 silently returns
garbage and no test will catch it. Do not create a second tokenizer anywhere.

Include a public const string Version = "1"; bump it on any rule change. The index
builder stores this version and forces a full rebuild when it changes.
```

**Acceptance:** unit-test `SplitIdentifier` against `GalaxyDeployEngine`, `HTTPRequestHandler`, `deploy_engine_v2`, `Runtime.Node.Manager`.

---

# Phase 1 — Azure DevOps access

## P04 — Credentials and HTTP plumbing

```
Create Core/Impact/Ado/AdoCredentialProvider.cs and AdoHttpClientExtensions.cs.

interface IAdoCredentialProvider {
    ValueTask<AuthenticationHeaderValue> GetAuthHeaderAsync(CancellationToken ct);
    string OrganizationUrl { get; }
    string ProjectName { get; }
}

Implementations:
  EnvironmentAdoCredentialProvider  — reads ADO_PAT from the environment or from
      IConfiguration key "Ado:Pat" that is expected to come from user-secrets or an
      environment variable, never from a committed file.
  AzureIdentityAdoCredentialProvider — uses DefaultAzureCredential with the Azure
      DevOps scope, preferred where available.

SECURITY REQUIREMENTS, apply without exception:
- The token value must never be written to a log, an exception message, a WIQL echo,
  a cache key, or a serialized object. Add a DebuggerDisplay that shows only the
  organization URL.
- If the configured token is missing, throw AdoConfigurationException with remediation
  text that names the environment variable but never prints any value.
- Add a unit test asserting that ToString() on the provider contains no token.

AddAdoHttpClient(this IServiceCollection, IConfiguration):
- Typed HttpClient named "AdoApi", BaseAddress from OrganizationUrl.
- DelegatingHandler that attaches the auth header per request from the provider.
- Polly-free manual retry handler: retry on 408, 429, 502, 503, 504 and
  HttpRequestException, exponential backoff 1s/2s/4s/8s with jitter, honouring the
  Retry-After header when present, max Ado.MaxRetries attempts.
- Timeout 100s. No package references beyond Microsoft.Extensions.Http.
```

**Acceptance:** the token-redaction unit test passes. Revoke and rotate any PAT that currently exists in a checked-in script before this code runs anywhere.

---

## P05 — ADO work item client

```
Create Core/Impact/Ado/AdoWorkItemClient.cs implementing:

interface IAdoWorkItemClient {
    Task<IReadOnlyList<int>> QueryIdsAsync(string wiql, int maxResults, CancellationToken ct);
    Task<IReadOnlyList<AdoWorkItemRef>> HydrateAsync(IReadOnlyCollection<int> ids,
        IReadOnlyCollection<string> fields, CancellationToken ct);
    Task<IReadOnlyList<TestCaseCandidate>> GetTestCasesAsync(IReadOnlyCollection<int> ids,
        CancellationToken ct);
    Task<IReadOnlyList<FeatureCandidate>> GetFeaturesAsync(IReadOnlyCollection<int> ids,
        CancellationToken ct);
    Task<IReadOnlyDictionary<int,int>> ResolveParentFeaturesAsync(
        IReadOnlyCollection<int> testCaseIds, CancellationToken ct);
    Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> GetChildTestCasesAsync(
        IReadOnlyCollection<int> featureIds, CancellationToken ct);
    IAsyncEnumerable<AdoWorkItemRef> EnumerateChangedSinceAsync(string workItemType,
        DateTimeOffset since, int pageSize, CancellationToken ct);
}

Implementation notes:
- HydrateAsync uses POST _apis/wit/workitemsbatch in chunks of Ado.BatchSize. Always
  request System.Rev so the index and all caches can key on revision.
- Steps flattening: Microsoft.VSTS.TCM.Steps is XML containing HTML-encoded fragments.
  Parse steps/step/parameterizedString, HTML-decode, strip tags, join action and
  expected result with " -> ", join steps with newline. On malformed XML set StepsText
  to null and record a warning. Never throw from parsing.
- ResolveParentFeaturesAsync and GetChildTestCasesAsync use WIQL WorkItemLinks queries
  over BOTH System.LinkTypes.Hierarchy and Microsoft.VSTS.Common.TestedBy, walking at
  most Ado.LinkWalkMaxDepth levels. Batch these — never one request per work item.
- EnumerateChangedSinceAsync drives incremental indexing: WIQL ordered by
  System.ChangedDate ascending, paged, yielding as it goes.
- WIQL safety: escape single quotes by doubling; reject any term containing a control
  character or a newline; never interpolate a raw user string into a query without
  passing it through the escaper.
- Wrap failures in AdoQueryException carrying the WIQL text. Confirm the exception
  message cannot contain credentials.
```

**Acceptance:** against a real project, `GetTestCasesAsync` on 5 known ids returns flattened `StepsText` with no HTML tags.

---

# Phase 2 — Persistent retrieval index

This phase is the one that determines the quality ceiling of everything downstream. Build it before any retrieval code.

## P06 — Index storage

```
Create Core/Impact/Index/ImpactIndexDbContext.cs and entity types, EF Core + SQLite.
Reuse the solution's existing EF Core and SQLite package references; add none.

Entities:
  IndexedDocument(Id PK, Kind, WorkItemId, Revision, Title, BodyText, AreaPath, Tags,
      ParentId, ChildCount, TokenCount, UpdatedUtc)
      unique index on (Kind, WorkItemId)
  DocumentTerm(DocumentId FK, Term, Frequency)
      composite PK (DocumentId, Term); index on (Term) — this is the inverted index
  CorpusStatistic(Kind, Term, DocumentFrequency, PK (Kind, Term))
  DocumentVector(DocumentId FK PK, Model, Dimensions, Vector BLOB)
  IndexMetadata(Kind PK, LastIndexedUtc, DocumentCount, AverageTokenCount,
      TokenizerVersion, EmbeddingModel)

Configuration:
- SQLite in WAL journal mode so the WPF host can read while the WebApi host writes.
- Vector BLOB is contiguous float32, little-endian. Provide extension methods
  ToBlob(float[]) and ToVector(byte[]) using MemoryMarshal, no per-element loops.
- Migrations enabled. On startup, if IndexMetadata.TokenizerVersion differs from
  Tokenizer.Version, or EmbeddingModel differs from options, mark the index for full
  rebuild rather than serving stale statistics.

Provide a DbContextFactory so background indexing and request-path reads use separate
contexts. Never share a DbContext across threads.
```

## P07 — BM25 over the persistent index

```
Create Core/Impact/Index/Bm25Scorer.cs.

public sealed class Bm25Scorer {
    Bm25Scorer(double k1, double b, double averageDocumentLength, long corpusSize);
    double Score(int termFrequency, int documentLength, long documentFrequency);
}

And Core/Impact/Index/IndexSnapshot.cs:

public sealed class IndexSnapshot {
    IndexKind Kind { get; }
    long DocumentCount { get; }
    double AverageTokenCount { get; }
    int MedianChildCount { get; }
    double Idf(string term);
    IReadOnlyList<(int WorkItemId, double Score)> SearchBm25(IEnumerable<string> terms, int topK);
    IReadOnlyList<(int WorkItemId, double Score)> SearchDense(float[] query, int topK);
    IndexedDocument? GetDocument(int workItemId);
}

CRITICAL — inverse document frequency must be computed over the ENTIRE corpus, read
from CorpusStatistic, never over a pre-filtered candidate subset. IDF derived from a
result set whose documents all already contain the query terms is near-uniform and
turns BM25 into a term counter. This is the single most important correctness
requirement in the retrieval layer.

Standard Okapi BM25:
    idf = ln(1 + (N - df + 0.5) / (df + 0.5))
    score = idf * (tf * (k1 + 1)) / (tf + k1 * (1 - b + b * dl / avgdl))
Sum over query terms. Unseen terms contribute zero and must not divide by zero.

SearchBm25 walks the inverted index: for each query term, load the DocumentTerm rows
for that term only, accumulate per-document scores in a dictionary, then take topK
with a bounded min-heap. Do not materialize the whole corpus.

SearchDense brute-forces cosine over the DocumentVector table, loaded once into a
pooled float array and cached with the snapshot. For a corpus under roughly 100k
documents this is fast enough on a single core. Do not introduce a vector database.

IndexSnapshot is immutable and cached per (Kind, LastIndexedUtc). Loading it is
expensive; serving from it must be cheap.
```

## P08 — Embedding provider

```
Create Core/Impact/Index/IEmbeddingProvider.cs plus three implementations.

interface IEmbeddingProvider {
    bool IsAvailable { get; }
    string ModelId { get; }
    int Dimensions { get; }
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct);
}

  NullEmbeddingProvider    — IsAvailable false, returns empty. The pipeline must run
                             correctly with this in place, at reduced recall. This is
                             the Phase-0 default and the permanent fallback.
  AzureOpenAiEmbeddingProvider — typed HttpClient, batches of Index.EmbeddingBatchSize,
                             retry on 429 honouring Retry-After, L2-normalize vectors
                             on return so cosine reduces to a dot product.
  CachingEmbeddingProvider — decorator over any provider, IMemoryCache keyed by
                             SHA256(modelId + text). Register as the outermost
                             registration so every caller benefits.

Register conditionally: use Azure when Index:Embeddings:Endpoint is non-empty,
otherwise Null. Log once at startup which provider is active and at what dimensions.
```

## P09 — Index builder

```
Create Core/Impact/Index/RetrievalIndexBuilder.cs and IndexMaintenanceService.cs.

interface IRetrievalIndexBuilder {
    Task<IndexBuildReport> BuildIncrementalAsync(IndexKind kind, CancellationToken ct);
    Task<IndexBuildReport> RebuildAsync(IndexKind kind, CancellationToken ct);
}
record IndexBuildReport(IndexKind Kind, int Scanned, int Updated, int Embedded,
    TimeSpan Elapsed, IReadOnlyList<string> Warnings);

BuildIncrementalAsync:
1. Read IndexMetadata.LastIndexedUtc. If TokenizerVersion or EmbeddingModel changed,
   escalate to RebuildAsync.
2. EnumerateChangedSinceAsync for the matching work item type, pages of
   Index.IncrementalPageSize.
3. For each work item, compare System.Rev against the stored Revision. Skip unchanged
   documents entirely — this is what keeps the nightly run to dozens of documents
   rather than thousands, and re-embedding unchanged text is the dominant avoidable
   cost in this system.
4. BodyText composition:
     Test Case -> Title + " " + Tags + " " + StepsText
     Feature   -> Title + " " + Description
   Truncate BodyText at 8000 chars.
5. Tokenize with Tokenizer, upsert DocumentTerm rows, delete stale ones.
6. Populate ChildTestCaseCount on Feature documents from GetChildTestCasesAsync.
   OPT: this drives the fan-out penalty later and is cheap to compute here.
7. Recompute CorpusStatistic document frequencies for affected terms, and
   AverageTokenCount, after the batch.
8. Embed only documents whose Revision changed, batched, when the provider is available.
9. Write IndexMetadata last, inside the same transaction, so a crashed build does not
   advance the watermark.

IndexMaintenanceService : BackgroundService
- Registered in the WebApi host ONLY. The WPF host reads the same SQLite file and never
  builds; two writers on one file is the failure mode to avoid.
- Runs nightly at 02:00 local and once at startup if the index is older than
  Index.MaxAge.
- Exposes a manual trigger through an injectable IIndexTriggerSignal so an API endpoint
  can force a rebuild without a process restart.
```

### CHECKPOINT 1

Do not proceed until all of the following hold.

- `BuildIncrementalAsync(IndexKind.TestCase)` completes against the real project and reports a plausible document count.
- A second immediate run reports `Updated = 0`. If it re-processes everything, revision comparison is broken and every nightly run will re-embed the whole corpus.
- `IndexSnapshot.Idf("deployment")` and `Idf("galaxy")` return different values. Equal values mean IDF is not reading `CorpusStatistic`.
- `SearchBm25(["runtime","node"], 10)` returns test cases you recognize as relevant, sourced from the whole corpus rather than from a WIQL result set.

---

# Phase 3 — Query construction

## P10 — Change document builder

```
Create Core/Impact/Query/ChangeDocumentBuilder.cs.

interface IChangeDocumentBuilder {
    ChangeDocument Build(ImpactedArea area, ChangePayload payload);
}

Add package reference Microsoft.CodeAnalysis.CSharp (Roslyn) — this is the one package
I am explicitly authorising.

Extraction, in descending order of value:

1. ChangedLiterals — string literals inside changed hunks that are longer than 4 chars
   and contain a space or read as human text. Exclude paths, GUIDs, format specifiers,
   log templates, and anything matching ^[A-Za-z]+\.[A-Za-z.]+$.
   These are the highest-value tokens in the entire pipeline: QA writes test titles in
   the same words the product shows the user. Treat them as the primary signal.

2. PublicApiChanges — declaration lines with public or protected accessibility that
   appear in an addition hunk, a removal hunk, or both. Normalize to
   "MethodName(paramType1, paramType2) -> returnType". A changed public signature is a
   near-certain behavioural change.

3. ChangedSymbols — for .cs files, parse hunk text with
   SyntaxFactory.ParseCompilationUnit in script mode, which tolerates incomplete
   fragments. Collect MethodDeclaration, ClassDeclaration, PropertyDeclaration and
   InvocationExpression identifiers whose span intersects a changed line. On parse
   failure fall back to a regex over C# declaration keywords. Never throw.

4. PrNarrative — PR title and description with markdown, HTML and issue-link noise
   stripped.

5. PathTokens — Tokenizer over file names and the two closest parent folders.

RawText composition, capped at 12000 chars, in priority order:
   ChangedLiterals (repeated 3x) + PublicApiChanges (repeated 3x) + ChangedSymbols
   + PrNarrative + PathTokens
Repetition is deliberate: it raises term frequency for the highest-value signals so
BM25 favours them without needing a custom field-weighted scorer.

Fingerprint = lowercase hex SHA256 of RawText. Every downstream cache keys on it.

No I/O in this class. Fully unit testable from fixture diffs.
```

## P11 — Keyword extractor

```
Create Core/Impact/Query/KeywordExtractor.cs.

interface IKeywordExtractor {
    IReadOnlyList<KeywordGroup> Extract(ImpactedArea area, ChangeDocument doc, HydeQuery? hyde);
}

Group construction, not one group per token:
  Group 1 "identity"  — area DisplayName, Vob, Subsystem tokens. Weight 1.0.
  Group 2 "literals"  — ChangedLiterals tokens. Weight 0.95. This group usually
                        outperforms every other group; do not down-weight it.
  Group 3 "api"       — PublicApiChanges tokens. Weight 0.85.
  Group 4..n "paths"  — one group per top-level folder cluster in ChangedPaths,
                        Weight = 0.3 + 0.7 * (files under cluster / total files).
  Group n+1 "expanded"— hyde.ExpandedTerms when HyDE ran. Weight 0.6.

Cap at Keywords.MaxKeywordGroups by merging the lowest-weight groups into the nearest
higher-weight one. Never drop the identity or literals groups.

Within a group, order terms by specificity: compound tokens first, then by descending
length. Cap at Keywords.MaxTermsPerGroup.

GroupId must be a deterministic stable hash of the sorted term list, so the same area
always yields the same ids across runs. Caching and provenance both depend on this.

Pure function. Uses Tokenizer only. No I/O.
```

## P12 — HyDE query generator

```
Create Core/Impact/Query/HydeQueryGenerator.cs.

interface IHydeQueryGenerator {
    Task<HydeQuery> GenerateAsync(ChangeDocument doc, CancellationToken ct);
}

Purpose: close the vocabulary gap. Rather than searching a corpus of English test cases
with C# identifiers, have the model write what a test case for this change would
plausibly look like, then embed and search with THAT. Searching a test-case corpus with
text shaped like a test case is a fundamentally better match than searching it with
class names, and this stage moves recall more than any ranking weight downstream.

Single LLM call, temperature 0.

System prompt: "You translate code changes into the language a QA engineer uses when
writing test cases for an industrial automation platform. You describe only behaviour
that the given change plausibly affects. You never invent product features that are not
implied by the change. You never mention class names, file names or code identifiers in
the synthetic test text — write in functional user-facing language."

User content, in this order with clear headers: changed user-visible strings, changed
public API signatures, changed symbols, PR narrative, changed paths.

Require JSON only, no prose, no markdown fences:
{ "changeSummary": "2-3 sentences",
  "syntheticTestTitles": ["5 to 8 plausible test case titles"],
  "syntheticTestBody": "one paragraph of plausible test steps",
  "expandedTerms": ["functional synonyms of the code terms"] }

Strip any fences before parsing. On parse failure, retry once with a stricter reminder,
then fall back.

Fallback when the LLM is unavailable or parsing fails twice: synthesize
SyntheticTestTitles as "Verify {literal}" over ChangedLiterals, ChangeSummary from the
PR narrative, ExpandedTerms empty. Log a warning. The pipeline must still run.

Cache on doc.Fingerprint with Rerank.HydeCacheTtl. Re-analysing the same PR must not
re-pay for this call.
```

---

# Phase 4 — Retrieval and feature resolution

## P13 — Hybrid retriever

```
Create Core/Impact/Retrieval/HybridRetriever.cs.

interface IHybridRetriever {
    Task<IReadOnlyList<Scored<int>>> RetrieveAsync(IndexSnapshot snapshot,
        KeywordGroup group, float[]? denseQuery, int topK, CancellationToken ct);
}

Returns scored work item ids; hydration happens at the caller so retrieval stays
independent of ADO.

Algorithm:
1. Lexical leg: snapshot.SearchBm25(group.Terms, topK * 4).
2. Dense leg: when denseQuery is non-null, snapshot.SearchDense(denseQuery, topK * 4).
   denseQuery is the embedding of hyde.SyntheticTestTitles joined with
   SyntheticTestBody — not the keyword bag.
3. Fuse with Reciprocal Rank Fusion:
       score(d) = LexicalWeight / (RrfK + rankLex(d))
                + SemanticWeight / (RrfK + rankDense(d))
   A document absent from a ranking contributes nothing from that leg.

Use RRF rather than normalizing and adding. BM25 scores and cosine similarities live on
incompatible scales and always will; rank fusion is scale-free, which is why RrfK is the
only knob here. Leave it at 60 until the eval harness says otherwise.

Emit ScoreComponents named "bm25", "cosine", "rrf" with raw sub-scores preserved so
provenance can show the arithmetic.

Multiply the final score by group.Weight. Deterministic tie-break on work item id.
Log one information line per call: group id, candidates considered, whether the dense
leg was available, top score.
```

## P14 — Feature ranking

```
Create Core/Impact/Ranking/FeatureRanker.cs.

record TestCaseEvidence(int FeatureId, int TestCaseId, string TestCaseTitle, double RetrievalScore);

interface IFeatureRanker {
    IReadOnlyList<Scored<FeatureCandidate>> RankWithinGroup(KeywordGroup group,
        IReadOnlyList<FeatureCandidate> candidates, IReadOnlyList<TestCaseEvidence> evidence,
        ImpactedArea area, IndexSnapshot snapshot);
    IReadOnlyList<Scored<FeatureCandidate>> RankGlobally(
        IReadOnlyList<IReadOnlyList<Scored<FeatureCandidate>>> perGroup, int topN);
}

RankWithinGroup components — emit every one into Components:
  retrieval     0.30  normalized score from the index retrieval leg
  titleMatch    0.20  fraction of group terms present in the title
  exactCompound 0.15  1.0 when the group's most specific term appears verbatim
  areaPathMatch 0.10  1.0 when the feature AreaPath shares >= 2 segments with the
                      impacted area's subsystem path
  tcEvidence    0.20  log-damped normalized sum of evidence RetrievalScore for this
                      feature: log(1+sum)/log(1+maxSum)
  stateActive   0.05  0.0 for closed or removed states, 1.0 otherwise
Multiply by group.Weight. Take Retrieval.TopFeaturesPerGroup.

RankGlobally fuses per-group lists with RRF weighted by group.Weight, unions
MatchedGroupIds, ORs DiscoveryPath flags for repeated feature ids, returns topN.
Deterministic tie-break on work item id ascending.
```

## P15 — Parent feature resolution

```
Create Core/Impact/Features/ParentFeatureResolver.cs.

interface IParentFeatureResolver {
    Task<ParentResolution> ResolveAsync(KeywordGroup group,
        IReadOnlyList<Scored<TestCaseCandidate>> rankedTestCases, CancellationToken ct);
}
record ParentResolution(IReadOnlyList<FeatureCandidate> Features,
    IReadOnlyList<TestCaseEvidence> Evidence, IReadOnlyList<TestCaseCandidate> Orphans);

1. Collect distinct non-null ParentFeatureId from the ranked test cases; for nulls,
   call ResolveParentFeaturesAsync in one batch before giving up.
2. Hydrate those features with IAdoWorkItemClient.GetFeaturesAsync in one batched call.
3. Deduplicate by id. Set DiscoveryPath = TestCaseBackReference,
   MatchedGroupIds = [group.GroupId].
4. Emit one TestCaseEvidence per ranked test case that resolved to a parent, carrying
   the retrieval score. This evidence feeds the tcEvidence component in P14 and is the
   entire reason this branch exists: it finds features whose titles never match the
   change but whose test cases do.
5. Orphans are returned, not discarded. They are selectable later under synthetic
   feature id -1. Record a warning naming the count.
```

## P16 — Feature merge

```
Create Core/Impact/Features/FeatureMerger.cs.

interface IFeatureMerger {
    IReadOnlyList<Scored<FeatureCandidate>> Merge(
        IReadOnlyList<Scored<FeatureCandidate>> directBranch,
        IReadOnlyList<Scored<FeatureCandidate>> backReferenceBranch,
        int maxFeatures);
}

- Key on work item id.
- Where a feature appears in both branches, fuse with RRF over the two branch rankings
  rather than adding raw scores; the branches use different scales.
- Apply Retrieval.CorroborationBonus (+15%) to any feature with both DiscoveryPath
  flags set. Independent agreement between a direct feature match and a test-case
  back-reference is the strongest signal this pipeline produces.
- OR the flags, union MatchedGroupIds, sum ChildTestCaseCount consistently.
- Components named "directBranch", "backReferenceBranch", "corroboration".
- Sort descending, take maxFeatures, tie-break on id.
```

## P17 — Fan-out normalization

```
Create Core/Impact/Ranking/FanOutNormalizer.cs.

interface IFanOutNormalizer {
    IReadOnlyList<Scored<FeatureCandidate>> Normalize(
        IReadOnlyList<Scored<FeatureCandidate>> features, IndexSnapshot snapshot,
        IList<string> warnings);
}

Problem: a feature such as "Common Framework" with 800 linked test cases matches almost
every query and then floods candidate expansion with noise. A feature with 6 linked
test cases that matches the query is a far more specific signal.

Multiplicative penalty in the spirit of IDF:
    penalty(f) = log(1 + snapshot.MedianChildCount) / log(1 + f.ChildTestCaseCount)
    clamp to [Retrieval.FanOutPenaltyFloor, Retrieval.FanOutPenaltyCeiling]

Features below the median get a mild boost; hubs get damped. Never zero anything out —
a hub feature with an extremely strong text match may still deserve selection.

When ChildTestCaseCount exceeds MedianChildCount * Retrieval.HubWarningMultiple, add a
warning naming the feature and its child count so the Regression tab can render it as
low-specificity rather than silently expanding 800 candidates.

Emit the penalty as a ScoreComponent named "fanOut".
```

---

# Phase 5 — Rerank and calibrate

## P18 — Graded relevance reranker

```
Create Core/Impact/Rerank/RelevanceReranker.cs with two implementations.

interface IRelevanceReranker {
    Task<IReadOnlyDictionary<int, RelevanceJudgement>> RerankAsync(ChangeDocument change,
        HydeQuery hyde, ChangePayload payload,
        IReadOnlyList<Scored<TestCaseCandidate>> candidates, CancellationToken ct);
}

  PassThroughReranker — Grade 2, Confidence 0.5, reason "rerank disabled". Used when
      Rerank.EnableLlmRerank is false and throughout early phases.
  LlmRelevanceReranker — the real one.

Graded, not binary. Include/Exclude discards ordering information the model already
has; a 0-3 grade feeds the budget knapsack in P22 and turns the threshold into a
tuning knob rather than a prompt change.

Rubric, stated verbatim in the system prompt:
  3 - the test case executes the changed code path directly
  2 - the test case exercises a feature whose behaviour this change can alter
  1 - the test case shares a subsystem but is unlikely to be affected
  0 - unrelated

Context per batch, in this order:
  1. hyde.ChangeSummary — the model's own functional summary, not the raw diff, which
     is too noisy for it to weigh reliably
  2. up to Rerank.MaxDiffHunksToLlm hunks, chosen as those containing the most
     ChangedLiterals and PublicApiChanges, each capped at Rerank.MaxHunkLines lines
  3. the candidate list: id, title, parent feature title, first 400 chars of steps

Response JSON only:
  [{"id":int,"grade":0-3,"confidence":0.0-1.0,"reason":"<=25 words",
    "citedSignals":["the changed literal or symbol that drove this grade"]}]

CitedSignals is mandatory. A judgement the model cannot tie to a specific changed
literal or symbol is downgraded by Rerank.DowngradeWhenNoCitedSignals at the caller.
This suppresses the model's tendency to rate everything in a familiar subsystem as
relevant.

Adaptive self-consistency: single pass at temperature 0. Re-run ONLY candidates
returning grade 1 or 2 with confidence below
Rerank.SelfConsistencyConfidenceThreshold, twice more at temperature 0.3, take the
median grade. Confident 0s and 3s are not worth extra tokens; borderline cases are
where the extra calls buy something.

Batching: Rerank.BatchSize per call, at most Rerank.MaxParallelism concurrent batches
via SemaphoreSlim.

Fail open, always. Parse failure after one retry, or a candidate omitted from the
response, yields Grade 2 / Confidence 0.5 / reason "rerank unavailable" plus a warning.
Never drop a test case because the model did not mention it.

Cache on SHA256(change.Fingerprint + testCaseId + testCaseRevision), TTL
Rerank.CacheTtl.
```

## P19 — Score calibration

```
Create Core/Impact/Learning/ScoreCalibrator.cs and FeatureVectorExtractor.cs.

Problem: hand-tuned weights produce scores that are not probabilities, so a threshold
like MinFinalScore = 0.15 means nothing in particular and cannot be reasoned about.

FeatureVectorExtractor emits, per (change, test case) pair, a float vector of signals
already computed upstream:
  bm25Score, denseScore, rrfScore, featureScore, fanOutPenalty,
  llmGrade, llmConfidence, citedSignalCount,
  anchorWeight, anchorSourceOneHot[4],
  historicalFailureRate, daysSinceLastRun, automationStatusFlag,
  sameSubsystemFlag, areaPathOverlapDepth, testCaseAgeDays, parentChildCount

ScoreCalibrator:
  interface IScoreCalibrator { double Calibrate(double rawScore, float[] features); }
  LinearScoreCalibrator    — identity pass-through. Default, Learning.ScoringMode="Linear".
  IsotonicScoreCalibrator  — fitted on labelled pairs from the outcome store; falls
        back to Platt scaling below 200 labels, which is more stable on small samples.
  RankerScoreCalibrator    — loads a persisted Microsoft.ML LightGbmRanking model from
        Learning.RankerModelPath, used when ScoringMode="Ranker".

Training never runs in the request path. Provide a separate CLI verb in the eval
console (P28). Persist and version models; keep linear fusion selectable as a fallback.

Report calibration quality — Brier score and reliability-curve buckets — so a drifting
model is visible rather than silently degrading.
```

### CHECKPOINT 2

- Rerank on 20 candidates returns grades spanning at least three distinct values. All-2s means the rubric or context is not reaching the model.
- Deliberately corrupt the model response and confirm every candidate survives at Grade 2 with a warning. Fail-open is the property that keeps this system safe to run.
- Re-running the same change issues zero LLM calls. If it does not, the fingerprint or revision cache key is wrong.

---

# Phase 6 — Anchors and the learning loop

## P20 — Outcome store

```
Create Core/Impact/Learning/OutcomeStore.cs, EF Core against the same SQLite database.

Entities:
  MappingRun(Id PK Guid, AreaId, PullRequestId, Tier, CreatedUtc, ModelVersion,
      ScoringMode, SelectedCount, BudgetUsedSeconds, EarlyExit)
  MappingSelection(RunId FK, TestCaseId, FeatureId, FinalScore, Grade, AnchorSource?,
      Rank, WasExecuted)
  ExecutionOutcome(RunId FK, TestCaseId, Result, DurationSeconds, FailureSignature?)
  EscapeRecord(Id PK, AreaId, TestCaseId, DetectedUtc, Source, Notes)

interface IOutcomeStore {
    Task RecordRunAsync(ImpactMappingResult result, CancellationToken ct);
    Task RecordExecutionAsync(Guid runId, IReadOnlyList<ExecutionOutcome> outcomes, CancellationToken ct);
    Task RecordEscapeAsync(string areaId, int testCaseId, string source, string? notes, CancellationToken ct);
    Task<IReadOnlyList<AnchorEdge>> GetHistoricalAnchorsAsync(string areaId, int lookbackRuns, CancellationToken ct);
    Task<IReadOnlyDictionary<int,double>> GetFailureRatesAsync(IReadOnlyCollection<int> ids, CancellationToken ct);
    Task<IReadOnlyDictionary<int,TimeSpan>> GetDurationsAsync(IReadOnlyCollection<int> ids, CancellationToken ct);
    Task<IReadOnlyList<LabelledPair>> GetTrainingPairsAsync(CancellationToken ct);
}

Wire RecordExecutionAsync into the existing ExecutionSessionManager completion path so
every orchestrated run feeds the loop with no extra operator action.

Escapes are the most valuable records here and the hardest to capture: a test case that
would have caught a field bug but was not selected. Provide a manual entry path in the
Regression tab. Each escape promotes an edge toward Observed confidence and directly
strengthens Tier 0 for the next change in that area.

This store is what turns a static heuristic into something that improves every release,
and what makes the churn workbook's area-to-test edges permanent instead of re-derived
and discarded each cycle.
```

## P21 — Anchor edge provider

```
Create Core/Impact/Anchors/AnchorEdgeProvider.cs.

interface IAnchorEdgeProvider {
    Task<AnchorResult> GetAnchorsAsync(ImpactedArea area, ChangePayload payload,
        CancellationToken ct);
}

Three sources, queried in parallel:

1. LinkedWorkItem — from payload.LinkedWorkItemIds, walk the ADO link graph down
   through children and across through TestedBy to Test Cases. Weight 1.0.
   Highest precision signal available, one query, and v1 designs routinely ignore it.

2. HistoricalFailure — from IOutcomeStore.GetHistoricalAnchorsAsync over the last
   Anchors.LookbackRuns releases: test cases that failed in a run where this AreaId was
   selected. Weight = 0.5 + 0.5 * (failures / runs), capped at 1.0.
   A test that has caught a regression in this area before is the best available
   predictor that it will again.

3. DeclaredMapping — evidenced edges from the impact map, sourced from the vobs.csv
   regression-area columns and the churn workbook. Weight 0.9 for Observed confidence,
   0.7 for Declared, 0.4 for Assumed.

CoverageScore = weighted fraction of area.DeclaredRegressionAreas that have at least
one anchor edge.

SufficientForEarlyExit is true when ALL hold:
   CoverageScore >= Anchors.EarlyExitCoverageThreshold
   Edges.Count >= Anchors.MinAnchorsForEarlyExit
   area.RiskTier != Critical, unless Anchors.AllowEarlyExitForCriticalTier

Early exit is what makes this pipeline affordable to run on every pull request rather
than nightly.
```

---

# Phase 7 — Selection

## P22 — Budgeted diversity selector

```
Create Core/Impact/Selection/BudgetedDiversitySelector.cs.

interface IBudgetedSelector {
    IReadOnlyList<MappedTestCase> Select(ImpactedArea area,
        IReadOnlyList<Scored<FeatureCandidate>> features,
        IReadOnlyList<Scored<TestCaseCandidate>> candidates,
        IReadOnlyDictionary<int, RelevanceJudgement> judgements,
        AnchorResult anchors,
        IReadOnlyDictionary<int, double> failureRates,
        IReadOnlyDictionary<int, TimeSpan> durations,
        IndexSnapshot snapshot, SelectionTier tier, TimeSpan budget,
        out SelectionDiagnostics diagnostics);
}

Step 1 — filter. Drop grade 0. Downgrade judgements with empty CitedSignals by
Rerank.DowngradeWhenNoCitedSignals before filtering.

Step 2 — score.
  finalScore = 0.45 * calibrated(retrievalScore)
             + 0.25 * normalized(parentFeatureScore)
             + 0.20 * (grade / 3.0) * confidence
             + 0.10 * historicalFailureRate
  plus 0.05 when AutomationStatus == "Automated" — automated cases are cheaper to run,
  so they should win near-ties.

Step 3 — MMR diversity. Iteratively pick the candidate maximizing
    Selection.MmrLambda * relevance(c)
  - (1 - Selection.MmrLambda) * maxCosineSimilarity(c, alreadySelected)
using the dense vectors already in the index. Twelve near-identical deployment tests
catch one bug between them; eight diverse ones catch more. This step changes what the
output is actually worth more than any scoring tweak.

Step 4 — budget knapsack.
    value(c) = finalScore * max(historicalFailureRate, 0.05)
    cost(c)  = durations[c] ?? corpus median duration
Solve greedily by value/cost ratio. Greedy is within a constant factor of optimal and
is trivially explainable to a QA lead, which matters more here than optimality.

Step 5 — recall safety net, applied AFTER the knapsack and never subject to it:
  - every AnchorSource.LinkedWorkItem edge, unconditionally
  - the top 3 HistoricalFailure anchors for this area
  - any candidate with Grade 3 and Confidence >= 0.8
  - the single highest-scoring test case of every selected feature, so a feature with
    zero coverage in the output is a real gap rather than a truncation artefact
This safety net is what makes early exit and aggressive budgets safe to enable.

Step 6 — assign MappingConfidence:
  Observed  — reached via an anchor edge, or via an explicit ADO link from a feature
              that matched both discovery paths
  Declared  — reached via an explicit ADO parent/child link, single branch
  Assumed   — orphan, or reached only through text similarity

Step 7 — build the full Provenance list per selection, one ProvenanceLink per stage in
pipeline order: area -> keyword group -> retrieval scores -> feature -> discovery path
-> fan-out -> grade -> selection reason.

Populate SelectionDiagnostics including the marginal candidate that just missed the cut.
```

## P23 — Coverage gap detection

```
Create Core/Impact/Selection/CoverageGapDetector.cs.

interface ICoverageGapDetector {
    IReadOnlyList<CoverageGap> Detect(ImpactedArea area,
        IReadOnlyList<MappedTestCase> selected,
        IReadOnlyList<Scored<FeatureCandidate>> features);
}

Emit a CoverageGap for each of:
- a declared regression area of the impacted area with zero selected test cases
- a selected feature with zero selected test cases
- an impacted area whose entire selection is MappingConfidence.Assumed, meaning nothing
  is backed by a real link — reason "no evidenced mapping"
- an area with RiskTier Critical or High and fewer than 3 selected test cases

Carry the area's RiskTier onto each gap so the UI can sort by it.

This is a first-class output, not a warning. An area that changed and has no test mapped
is the single most actionable thing this pipeline produces, and it is exactly the
category the churn workbook showed 24 instances of. Surface it at the top of the
Regression tab, never in a log file.
```

---

# Phase 8 — Orchestration

## P24 — Cascade orchestrator

```
Create Core/Impact/ImpactTestMappingService.cs.

interface IImpactTestMappingService {
    Task<ImpactMappingResult> MapAsync(ImpactedArea area, ChangePayload payload,
        SelectionTier tier, CancellationToken ct);
    IAsyncEnumerable<ImpactMappingProgress> MapWithProgressAsync(ImpactedArea area,
        ChangePayload payload, SelectionTier tier, CancellationToken ct);
}

Cascade sequence:

T0  anchors = await AnchorEdgeProvider.GetAnchorsAsync(area, payload, ct)
    if (anchors.SufficientForEarlyExit && tier != SelectionTier.Full)
        -> jump directly to T4 with anchor edges only, set EarlyExit = true

T1  changeDoc = ChangeDocumentBuilder.Build(area, payload)
    hyde      = Rerank.EnableHyde ? await HydeQueryGenerator.GenerateAsync(changeDoc, ct)
                                  : fallback HydeQuery
    groups    = KeywordExtractor.Extract(area, changeDoc, hyde)
    denseQuery= await embeddings.EmbedAsync([hyde.SyntheticTestTitles + Body])

T2  snapshots = await GetSnapshotAsync(Feature) and (TestCase), in parallel
    Branch A: per group, retrieve features from the Feature index, rank within group
    Branch B: per group, retrieve test cases from the Test Case index, resolve parent
              features, rank within group with test case evidence
    Both branches run concurrently; within a branch, groups run concurrently bounded by
    a SemaphoreSlim of 4 so the ADO API is not hammered during hydration.
    Then: rank globally per branch -> merge -> fan-out normalize -> expand child test
    cases -> candidate reduction via HybridRetriever down to
    Retrieval.MaxCandidateTestCases

T3  judgements = await RelevanceReranker.RerankAsync(...)
    calibrate via IScoreCalibrator

T4  failureRates and durations from IOutcomeStore, in parallel
    selected = BudgetedSelector.Select(..., anchors, tier, budget, out diagnostics)
    gaps     = CoverageGapDetector.Detect(...)

T5  if Learning.RecordOutcomes: await OutcomeStore.RecordRunAsync(result, ct)

Resilience: a failure in one keyword group degrades to a warning in Warnings, not a
failed run. A total failure of both branches with no anchors is a hard failure and
throws. Every optional dependency being unavailable produces a degraded but valid result.

Timing: Stopwatch the whole run into Elapsed; log per-tier durations at Debug.

MapWithProgressAsync yields one ImpactMappingProgress per completed tier so the WPF
Regression tab and the WebApi SignalR hub can both drive a progress strip from the same
engine, with no host type crossing into Core. Only the terminal yield carries a non-null
Result.
```

## P25 — Dependency injection

```
Create Core/Impact/ImpactServiceCollectionExtensions.cs.

public static IServiceCollection AddImpactMapping(this IServiceCollection services,
    IConfiguration config, ImpactHostRole role)

enum ImpactHostRole { Reader, ReaderWriter }

Registrations:
  Configure<ImpactMappingOptions>(config.GetSection("ImpactMapping"))
  AddAdoHttpClient(config)
  IAdoCredentialProvider     -> Azure identity when configured, else environment
  IAdoWorkItemClient         -> scoped
  ImpactIndexDbContext       -> AddDbContextFactory, SQLite, WAL
  IRetrievalIndexStore       -> singleton
  IEmbeddingProvider         -> CachingEmbeddingProvider wrapping Azure when
                                Index:Embeddings:Endpoint is set, else Null
  IRelevanceReranker         -> Llm when Rerank.EnableLlmRerank, else PassThrough
  IHydeQueryGenerator        -> real when EnableHyde and an LLM is configured, else a
                                fallback generator
  IScoreCalibrator           -> selected by Learning.ScoringMode
  IOutcomeStore              -> scoped
  Stateless components (Tokenizer users, rankers, merger, normalizer, selector,
      gap detector, change document builder, keyword extractor) -> singleton
  IImpactTestMappingService  -> scoped
  IndexMaintenanceService    -> AddHostedService ONLY when role == ReaderWriter

Both hosts call AddImpactMapping from their own composition root: the WebApi host with
ReaderWriter, the WPF host with Reader. Two writers against one SQLite file is the
failure mode this parameter exists to prevent.

Assert at registration time that no type under Core/Impact references a host assembly;
add an architecture test in P27 that enforces it.
```

### CHECKPOINT 3

- End-to-end run on fixtures produces a non-empty `MappedTestCases` and a populated `Provenance` chain on every entry.
- Run with `EnableLlmRerank=false`, `EnableHyde=false` and `NullEmbeddingProvider`. It must still return sensible results. If it throws or empties, a degradation path is missing.
- An area with strong anchors sets `EarlyExit = true` and completes in under two seconds.

---

# Phase 9 — Tests and evaluation

## P26 — Fixtures and fakes

```
Create Core/Impact/Testing/ with fake providers and embedded JSON fixtures.

FakeAdoWorkItemClient, FakeEmbeddingProvider (deterministic hash-based vectors),
FakeRelevanceReranker (rule-based grades), FixtureImpactedAreaFactory (builds
ImpactedArea from a churn-workbook-shaped CSV with columns AreaId, DisplayName,
Subsystem, Vob, ChangedPaths semicolon-separated, DeclaredRegressionAreas, RiskTier,
LinesAdded, LinesDeleted, FilesTouched, CommitCount, DistinctAuthorCount,
LastChangedUtc).

Generate fixtures containing, deliberately:
- 40 features, 300 test cases, a realistic link graph
- 3 features whose TITLES do not contain the area keyword but whose TEST CASES do —
  the back-reference branch must find these, and this is the fixture that proves the
  branch earns its cost
- 5 orphan test cases with no parent feature
- 2 features matched by both branches, to prove the corroboration bonus fires
- 1 hub feature with 200 children, to prove fan-out damping fires
- 1 cluster of 10 near-identical test cases, to prove MMR does not select all 10
```

## P27 — Unit tests

```
Create tests in TestControllerGrpc.Core.Tests/Impact/ using xUnit and FluentAssertions.

TokenizerTests            PascalCase, acronym runs, snake_case, stop words, stability
Bm25ScorerTests           unseen term contributes zero; k1 saturation means 10
                          occurrences do not score 10x one; global IDF differentiates
                          a common term from a rare one
IndexBuilderTests         second incremental run reports Updated = 0; tokenizer version
                          bump forces full rebuild
ChangeDocumentBuilderTests  literals extracted and excluded correctly; malformed C#
                          hunk falls back without throwing; Fingerprint stable
HydeGeneratorTests        LLM unavailable produces the templated fallback, not an
                          exception
HybridRetrieverTests      with NullEmbeddingProvider results equal pure BM25 order;
                          RRF ranks a 2nd/2nd document above a 1st/50th document
FeatureMergerTests        corroboration bonus applies; flags OR-ed; ids unioned
FanOutNormalizerTests     hub damped to the floor; below-median boosted; nothing zeroed
RerankerTests             malformed JSON fails open to Grade 2; omitted candidate fails
                          open; cached second call issues no HTTP request; empty
                          CitedSignals downgrades
SelectorTests             MMR does not select all 10 near-duplicates; budget respected;
                          every selected feature keeps at least one test; anchors
                          survive the knapsack
CoverageGapTests          all-Assumed selection produces a gap; critical area with 2
                          tests produces a gap
OrchestratorTests         end-to-end on fixtures finds the 3 title-mismatched features;
                          branch failure degrades to a warning; cancellation propagates
                          within 500ms
ArchitectureTests         no type under Core/Impact references PresentationFramework,
                          System.Windows, WindowsBase, Microsoft.AspNetCore.SignalR,
                          or System.Windows.Forms
```

## P28 — Evaluation harness

```
Create a console project TestController.ImpactEval.

Verbs:
  eval replay --from <release> --to <release> --config <path> [--out <csv>]
  eval compare --config-a <path> --config-b <path> --out <csv>
  eval train --model <isotonic|ranker> --out <path>

Replay:
1. Load historical impacted areas and the test sets a human actually selected, from the
   churn workbooks and the outcome store. This is the ground truth, and it turns a
   discarded artefact into a permanent evaluation asset.
2. Run the pipeline over each historical area with the index and LLM pinned to a frozen
   snapshot so runs are reproducible.
3. Report per configuration:
     Recall@k for k in {10, 25, 50, 100}
     SAFE RECALL — fraction of areas where every test that actually failed was
       selected. This is the metric that matters. Target >= 0.98. A configuration with
       better precision and worse safe recall is a worse configuration, full stop.
     Precision@k
     APFD — average percentage of faults detected, measuring ordering quality
     Cost — selected runtime as a fraction of full-suite runtime
     Early-exit rate, and safe recall WITHIN early-exit runs specifically
4. Compare produces a paired per-area delta table, not just averages, so a regression
   confined to one subsystem is not hidden by a good mean.
5. Emit console table plus CSV in the same shape the team's existing workbooks use.

Do not tune any weight in this system without running this harness. Hand-tuning ranking
weights against intuition is how retrieval systems quietly get worse while feeling
better.
```

---

# Phase 10 — Host integration

## P29 — WebApi surface

```
In TestController.WebApi, add ImpactMappingController and SignalR wiring.

Endpoints:
  POST /api/impact/map            body: area + payload + tier -> ImpactMappingResult
  POST /api/impact/map/stream     Server-Sent Events over MapWithProgressAsync
  GET  /api/impact/runs/{id}      historical run with full provenance
  POST /api/impact/runs/{id}/outcomes   record execution results
  POST /api/impact/escapes        record an escape
  POST /api/impact/index/rebuild  trigger reindex, requires elevated policy
  GET  /api/impact/index/status   last indexed time, doc counts, model, staleness

SignalR: broadcast ImpactMappingProgress on hub method "ImpactProgress", group-scoped
per run id.

Startup: services.AddImpactMapping(Configuration, ImpactHostRole.ReaderWriter);

Adapt Core progress to SignalR in the controller layer. No SignalR type reaches Core.
```

## P30 — WPF Regression tab wiring

```
In TestControllerGrpc (WPF), wire the Regression tab to the same engine in-process.

Startup: services.AddImpactMapping(Configuration, ImpactHostRole.Reader);

ViewModel consumes MapWithProgressAsync directly, marshalling to the UI thread at the
view model boundary only.

Views required, in priority order:
  1. Coverage Gaps panel, pinned at the top. Sorted by RiskTier descending. This is the
     most actionable output and must not be buried below the test list.
  2. Selected Test Cases grid: id, title, feature, grade, confidence badge, final score,
     estimated duration, anchor source icon.
  3. Provenance strip: on selecting a row, render the chain horizontally —
     impacted area -> keyword group -> retrieval score -> feature -> discovery path ->
     LLM grade -> selection reason. A QA lead must be able to audit any single result
     backwards without opening a log. This is the element that decides whether the
     feature gets adopted or ignored.
  4. Diagnostics footer: budget used vs allowed, counts dropped by budget / grade /
     diversity, and the marginal candidate that just missed the cut.
  5. Early-exit banner when EarlyExit is true, naming which anchors triggered it.

Bind SelectionTier to a scope selector, consistent with the existing regression tab
scope control.
```

### CHECKPOINT 4

- WPF and WebApi both produce identical results for the same input. Any divergence means host-specific state leaked into Core.
- The provenance strip renders a complete chain for an anchor-sourced selection and for a text-similarity selection, and the two look visibly different.
- Only the WebApi host writes to the index database. Confirm by inspecting the SQLite WAL while both hosts run.

---

# Section C — Configuration reference

```json
{
  "ImpactMapping": {
    "Keywords": { "MaxKeywordGroups": 6, "MaxTermsPerGroup": 8, "SplitPascalCase": true, "SplitSnakeAndKebab": true },
    "Ado": { "MaxFeaturesPerGroupQuery": 100, "MaxTestCasesPerGroupQuery": 200, "BatchSize": 200, "ExcludedStates": ["Removed","Closed"], "MaxRetries": 4, "LinkWalkMaxDepth": 3 },
    "Index": { "DatabasePath": "impact-index.db", "MaxAge": "30:00:00", "UseWiqlPreFilter": false, "EmbeddingModel": "text-embedding-3-small", "EmbeddingBatchSize": 64, "IncrementalPageSize": 200 },
    "Retrieval": { "Bm25K1": 1.2, "Bm25B": 0.75, "RrfK": 60, "LexicalWeight": 1.0, "SemanticWeight": 1.0, "TopTestCasesPerGroup": 25, "TopFeaturesPerGroup": 10, "TopFeaturesPerBranch": 20, "MaxFeaturesAfterMerge": 30, "MaxCandidateTestCases": 400, "CorroborationBonus": 0.15, "FanOutPenaltyFloor": 0.35, "FanOutPenaltyCeiling": 1.30, "HubWarningMultiple": 5.0 },
    "Anchors": { "LookbackRuns": 6, "MinAnchorsForEarlyExit": 5, "EarlyExitCoverageThreshold": 0.80, "AllowEarlyExitForCriticalTier": false },
    "Rerank": { "EnableLlmRerank": true, "EnableHyde": true, "BatchSize": 15, "MaxParallelism": 4, "MaxDiffHunksToLlm": 6, "MaxHunkLines": 40, "SelfConsistencyConfidenceThreshold": 0.70, "DowngradeWhenNoCitedSignals": 1.0, "CacheTtl": "7.00:00:00", "HydeCacheTtl": "30.00:00:00" },
    "Selection": { "MmrLambda": 0.70, "MaxTestCasesPerFeature": 12, "MaxTestCasesTotal": 150, "MinFinalScore": 0.15, "TierBudgets": { "Smoke": "00:15:00", "Targeted": "01:30:00", "Full": "23:59:59" } },
    "Learning": { "ScoringMode": "Linear", "RankerModelPath": null, "RecordOutcomes": true }
  }
}
```

---

# Section D — Keeping Copilot on track

**Paste one prompt at a time.** Each is sized to produce a single file Copilot can hold in context. Batching them produces plausible code that does not compose.

**Re-anchor after drift.** If output starts referencing types that do not exist, paste: *"Re-read the CONTEXT PRIMER. Use only the types defined in ImpactContracts.cs, which is open in my editor. Do not invent new record shapes."*

**Reject invented packages.** Copilot will suggest Lucene.NET for the BM25 work and a vector database for the dense search. Both are refusals: *"No external packages. Implement it directly against the SQLite schema in P06."* The only authorised additions are `Microsoft.CodeAnalysis.CSharp` (P10) and `Microsoft.ML` (P19, ranker mode only).

**Run the test before moving on.** Debugging Phase 7 with Phase 2 unverified is the slow path, and retrieval bugs are invisible downstream: they look like a ranking problem right up until you check the index.

**When a result looks wrong, check retrieval first.** Ranking can only reorder what retrieval returned. If an expected test case is missing from the final output, confirm it was in the candidate set before touching a single weight.
