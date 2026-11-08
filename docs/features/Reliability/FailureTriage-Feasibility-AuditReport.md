# Failure Triage ("Why did this fail?") — Feasibility Audit Report

**Audit date:** 2026-09-09
**Repo state:** branch `Rbsl`, HEAD `4e71f17`
**Scope:** read-only source audit. No code was written or modified.
**Prompt:** [FailureTriage-Feasibility-AuditPrompt.md](FailureTriage-Feasibility-AuditPrompt.md)

Evidence format is `path/to/File.cs:lines`, all line numbers 1-based. Claims without a file
reference are not made. Where something was searched for and not found, the exact search terms
are recorded.

---

## Verdict in one line

**Feasible, but not as a reversed impact cascade.** The failure fingerprint and the blame window
already exist and run today; the commit-ranking step has to be built new, because the impact index
contains ADO work items rather than code.

---

## PART 1 — Capability inventory

### Group A — Failure signal (the input)

#### A1. Structured failure capture — `EXISTS`

Failures are parsed from `.trx` XML into structured fields, not left as raw stdout.

| What | Evidence |
|---|---|
| Error message | `TestControllerGrpc.Core/Services/TrxResultsParser.cs:308` — `var errorMessage = errorInfoEl?.Element(MessageName)?.Value;` |
| Stack trace | `TestControllerGrpc.Core/Services/TrxResultsParser.cs:309` — `var stackTrace = errorInfoEl?.Element(StackTraceName)?.Value;` |
| Parent element | `TestControllerGrpc.Core/Services/TrxResultsParser.cs:307` — `var errorInfoEl = outputEl?.Element(ErrorInfoName);` |
| Per-step errors | `TestControllerGrpc.Core/Services/TrxResultsParser.cs:284-285` (inner `<InnerResults>` results) |
| Parse entry point | `TestControllerGrpc.Core/Services/TrxResultsParser.cs:271` — `ParseUnitTestResult(XElement r, ...)` |

#### A2. Single test result DTO — `EXISTS`

`TestControllerGrpc.Core/Models/TrxModels.cs:42-55`

```
record TestResult
  TestName, ClassName, Outcome, Duration,
  ErrorMessage?, StackTrace?, StdOut?,
  TrxFileName, UseCaseName,
  ExecutionSteps (List<TestStep>), DebugTrace
```

#### A3. Results persisted — `MISSING`

`TestController.Persistence/OrchestratorDbContext.cs:15-21` declares exactly seven DbSets:
`Users`, `Sessions`, `PipelineAssignments`, `AuditEntries`, `NotificationMutes`,
`NotificationCooldowns`, `MaintenanceOperations`.

**There is no test-result table.** Results are re-parsed from the filesystem per request:
`TestController.WebApi/Endpoints/ResultsEndpoints.cs:52` — `parser.ParseBuildFolder(match.Path)`.

Execution-time command results live in an in-memory `ConcurrentBag`
(`TestControllerGrpc.Core/Models/WatchListConfig.cs:369`), exposed at
`WatchListConfig.cs:462`, and are lost on restart apart from a JSON session snapshot written for
dashboard crash recovery (`TestControllerGrpc.Core/Services/ExecutionSessionManager.cs:41-44`).

> This is the audit prompt's own stop condition. See *Caveats* below for why it does not block a
> read-only v1.

#### A4. Run identifier — `PARTIAL`

`TestControllerGrpc.Core/Models/WatchListConfig.cs:404` —
`public string SessionId { get; init; } = Guid.NewGuid().ToString("N")[..12];`

Ties all agents' action results to one pipeline trigger (`WatchListConfig.cs:462`, agents at
`:420`). **Downgraded to PARTIAL because** this is the *pipeline session* id; nothing in the model
links a `SessionId` to the `.trx` folder the run produced. That join is the crux of the spike in
Part 3.

#### A5. Build / product binaries under test — `PARTIAL`

Captured as strings only:

| What | Evidence |
|---|---|
| Resolved parameter bag | `TestControllerGrpc.Core/Models/WatchListConfig.cs:491` |
| Build number key name | `TestControllerGrpc.Core/Models/WatchListConfig.cs:51` — `BuildNumberField = "BuildNumber"` |
| Drop location key name | `TestControllerGrpc.Core/Models/WatchListConfig.cs:52` — `DropLocationField = "DropLocation"` |
| Install step executed | `TestControllerGrpc.Core/Maintenance/MachineRevertOperation.cs:188` |

No artifact/binary-version table exists. Which binaries actually landed on an agent is not recorded.

---

### Group B — Change data (the suspect list)

#### B1. Build → commits — `EXISTS` (C#)

`TestControllerGrpc.Core/Ado/BuildQueries.cs:44` and `:47` declare
`GetBuildChangesAsync(int buildId, ...)` and a project-scoped overload; implementations at
`:149` and `:156`.

The PowerShell tools named in the prompt are **NOT FOUND — searched for:** glob `**/GetBuildChanges*.ps1`,
`GetBuildChanges_OMI`, `function GetBuildChanges`. They appear only in markdown and in
`docs/AzureIntegration/Program.cs:403`, which is a legacy reference copy not built by any project.

#### B2. Output shape — `EXISTS`, full

`TestControllerGrpc.Core/Ado/Dto/AdoDtos.cs:46-56` — `AdoBuildChangeDto`:
`Id`, `Message`, `Type`, `Author` (`AdoIdentityDto`), `Timestamp`, `Location`, `DisplayUri`.

File paths are a separate call:
`TestControllerGrpc.Core/Ado/AdoRegressionDataProvider.cs:173` —
`await _git.GetCommitChangesAsync(repo.Id, change.Id, ct)` → `IReadOnlyList<(string Path, string ChangeType)>`.

So: commit id + author + timestamp + message + touched paths. Everything a suspect list needs.

#### B3. Consumed by C# — `EXISTS`

Live call sites (not a human-run script):

- `TestControllerGrpc.Core/Ado/AdoRegressionDataProvider.cs:166`
- `TestControllerGrpc.Core/Ado/ComponentChangeCollector.cs:312` and `:386`
- `TestControllerGrpc.Core/Ado/SpBuildImpactCollector.cs:171`

Output is translated to `SubsystemRow` via `TestControllerGrpc.Core/Ado/AdoChangeTranslator.cs:30`.

#### B4. Build → commit mapping stored — `PARTIAL`

60-second in-memory memo only, keyed `{mode}|{branch}|{from}|{to}`:
`TestControllerGrpc.Core/Ado/AdoRegressionDataProvider.cs:105-120`.
No persistence — after expiry it re-queries ADO.

---

### Group C — Impact engine reuse (the core question)

#### C1. Cascade behind an interface — `EXISTS`

`TestController.Impact/Impact/ImpactTestMappingService.cs:21-29`

```
interface IImpactTestMappingService
    Task<ImpactMappingResult> MapAsync(ImpactedArea, ChangePayload, SelectionTier, CancellationToken)
    IAsyncEnumerable<ImpactMappingProgress> MapWithProgressAsync(...)
```

Note the assembly split: this interface is *declared in `TestController.Impact`* under namespace
`TestControllerGrpc.Core.Impact`. The contract that genuinely lives in Core is
`TestControllerGrpc.Core/Impact/IRegressionImpactMatcher.cs:41`.

#### C2. Free-text entry point — `MISSING` at the public entry, `PARTIAL` one layer down

**This is the pivotal answer, so it is spelled out.**

Both `MapAsync` parameters are change-shaped and non-optional:

`TestControllerGrpc.Core/Impact/ImpactContracts.cs:42-45`
```
record ImpactedArea(
    string AreaId, string DisplayName, string? Subsystem, string? Vob,
    IReadOnlyList<string> ChangedPaths, IReadOnlyList<string> DeclaredRegressionAreas,
    RiskTier RiskTier, ChurnMetrics Churn)
```

`TestControllerGrpc.Core/Impact/ImpactContracts.cs:37-39`
```
record ChurnMetrics(
    int LinesAdded, int LinesDeleted, int FilesTouched,
    int CommitCount, int DistinctAuthorCount, DateTimeOffset LastChangedUtc)
```

`TestControllerGrpc.Core/Impact/ImpactContracts.cs:54-57`
```
record ChangePayload(
    int? PullRequestId, string? PrTitle, string? PrDescription,
    IReadOnlyList<string> CommitMessages, IReadOnlyList<FileDiff> Diffs,
    IReadOnlyList<int> LinkedWorkItemIds)
```

You cannot hand this a failure string. To call it from a failure you would have to fabricate a
changeset, which defeats the point.

**However**, the retrieval primitives below it do accept arbitrary terms:

- `TestController.Impact/Impact/Index/IndexSnapshot.cs:105` —
  `SearchBm25(IReadOnlyCollection<string> terms, int limit)`
- `TestController.Impact/Impact/Retrieval/HybridRetriever.cs:16-17` —
  `RetrieveAsync(IndexSnapshot snapshot, KeywordGroup group, float[]? denseQuery, int topK, ct)`
- `TestControllerGrpc.Core/Impact/ImpactContracts.cs:74` —
  `record KeywordGroup(string GroupId, string Label, IReadOnlyList<string> Terms, double Weight)`
  — constructible from any text.

So a free-text query has a seam at the retrieval layer, not at the service layer.

#### C3. `impact-index.db` schema — `EXISTS`, with a decisive caveat

`TestController.Impact/Impact/Index/IndexEntities.cs`

| Table | Columns | Lines |
|---|---|---|
| `IndexedDocument` | `Id`, `Kind`, `WorkItemId`, `Title`, `Text`, `Fingerprint`, `Revision`, `Length`, `ChildCount`, `UpdatedUtc` | 8-40 |
| `DocumentTerm` | `DocumentId`, `Term`, `TermFrequency` | 43-53 |
| `CorpusStatistic` | `Term`, `DocumentFrequency` | 57-64 |
| `DocumentVector` | `DocumentId`, `Vector`, `Dimension`, `Model` | 67-79 |
| `IndexMetadata` | `Key`, `Value` | 83-90 |

**The corpus is ADO Features and Test Cases.** `Kind` is `IndexKind` (Feature \| TestCase) and
`WorkItemId` is an ADO work item id (`IndexEntities.cs:14,17`). There are **no code units, files,
symbols or commits** in this index. It cannot score a stack trace against source code, because the
source code is not in it.

#### C4. Reranker separately callable — `EXISTS`, but change-shaped

`TestController.Impact/Impact/Rerank/RelevanceReranker.cs:13-18`
```
interface IRelevanceReranker
    Task<IReadOnlyDictionary<int, RelevanceJudgement>> RerankAsync(
        ChangeDocument change, HydeQuery hyde, ChangePayload payload,
        IReadOnlyList<Scored<TestCaseCandidate>> candidates, CancellationToken ct)
```

Stable contract, two implementations: `PassThroughReranker` (`:25`) and `LlmRelevanceReranker` (`:51`).
It grades **test cases**, keyed by test case id — not commits — and it demands a `ChangeDocument`
plus a `ChangePayload` as context.

#### C5. `impact-outcomes.db` schema — `MISSING` for a triage verdict

`TestController.Impact/Impact/Learning/OutcomeDbContext.cs`

| Table | Columns | Lines |
|---|---|---|
| `MappingRun` | `Id`, `AreaId`, `PullRequestId`, `Tier`, `CreatedUtc`, `ModelVersion`, `ScoringMode`, `SelectedCount`, `BudgetUsedSeconds`, `EarlyExit` | 6-18 |
| `MappingSelection` | `Id`, `RunId`, `TestCaseId`, `FeatureId`, `FinalScore`, `Grade`, `AnchorSource`, `Rank`, `WasExecuted` | 21-32 |
| `ExecutionOutcome` | `Id`, `RunId`, `TestCaseId`, `Result`, `DurationSeconds`, `FailureSignature` | 35-43 |
| `EscapeRecord` | `Id`, `AreaId`, `TestCaseId`, `DetectedUtc`, `Source`, `Notes` | 46-54 |

Every row is keyed on `TestCaseId`. **There is no commit id column, no culprit column, no verdict
column.** Confirmed/Wrong feedback does not fit without a schema change.

Cross-host merge treats `MappingSelections` and `ExecutionOutcomes` as AUTOINCREMENT-keyed and
dedupes on `(RunId, TestCaseId)` — `TestController.Impact/Impact/Index/ImpactPersistenceService.cs:155`.
A new triage table would need the same treatment.

#### C6. LLM reranker call — `EXISTS` in code, inert in configuration

`TestControllerGrpc.Core/Ado/Reporting/Llm/AzureOpenAiClient.cs`

| Aspect | Evidence |
|---|---|
| Endpoint | `:45-46` — `{Endpoint}/openai/deployments/{model}/chat/completions?api-version=...` |
| Auth | `:38` env var named by `LlmOptions.ApiKeyEnvVarName`; `:67` header `api-key` |
| Timeout | `:27-28` from `RequestTimeoutSeconds`, default **60 s** (`LlmOptions.cs:52`) |
| Retry | **None** — returns `null` on any non-success (`:70-72`) |

Live configuration in **both** hosts (read 2026-09-09):

| Setting | `TestControllerGrpc` | `TestController.WebApi` |
|---|---|---|
| `Ado.Enabled` | True | True |
| `Ado.AuthMode` | **Interactive** | **Pat** |
| `Ado.Llm.Enabled` | False | False |
| `Ado.Llm.Endpoint` | *(empty)* | *(empty)* |
| `ImpactMapping.Rerank.LlmModel` | `''` | `''` |
| `ImpactMapping.Index.EmbeddingEndpoint` | `''` | `''` |

Consequences, from the DI wiring:
- `TestController.Impact/Impact/ImpactServiceCollectionExtensions.cs:111` registers
  `PassThroughReranker`, which grades **every** candidate `2 / 0.5 / "rerank disabled"`
  (`RelevanceReranker.cs:25-38`).
- `ImpactServiceCollectionExtensions.cs:98` registers `NullEmbeddingProvider` — the dense retrieval
  leg is inert.

**Unattended reachability at 3am:** the LLM path is API-key based, so yes. The WPF host's ADO auth
is `Interactive`, which needs a prior browser sign-in and a persisted token cache — that half is not
reliably unattended. The WebApi host uses `Pat`.

---

### Group D — Host wiring (where it would live)

#### D1. DI registration blocks — `EXISTS`

| Host | Evidence |
|---|---|
| WPF | `TestControllerGrpc/App.xaml.cs:95-220` (`AddRbacFeature` :98, `AddControllerLockServices` :101, `AddFleetMaintenanceServices` :104); impact engine at `:268-269` |
| WebApi | `TestController.WebApi/Program.cs:148` (`AddRbacFeature(..., isPrimaryHost: false)`), `:162` (`AddControllerApi()`) |
| Shared | `TestController.Api/ControllerApiExtensions.cs:26-54` — already registers `FailurePatternAnalyzer` |
| DbContext | `TestController.Api/RbacFeatureExtensions.cs:63` — `AddDbContextFactory<OrchestratorDbContext>` |

#### D2. REST + SignalR pair — `EXISTS`

End-to-end example:

1. Controller injects the hub — `TestController.Api/Controllers/ExecutionController.cs:37`
   (`IHubContext<ControllerHub> _hub`)
2. Endpoint — `TestController.Api/Controllers/ExecutionController.cs:81` (`GET /api/execution/sessions`)
3. Hub — `TestController.Api/Hubs/ControllerHub.cs:13`
4. Broadcast helper — `TestController.Api/Services/SignalRNotifier.cs:96-116`
5. Web client subscription — `TestController.WebClient/src/hooks/useSignalR.ts:160-319`

#### D3. WPF detail surface — `EXISTS`

`TestControllerGrpc/Views/Dialogs/FailureAnalysisDialog.xaml`

| Element | Line | Binding |
|---|---|---|
| Verdict / confidence block | 45-62 | `PatternLabel`, `Verdict`, `ConfidenceText` |
| Build history strip | 70 | `ItemsSource="{Binding History}"` |
| Signature grid | 96 | `ItemsSource="{Binding FailureSignatures}"` |

ViewModel: `TestControllerGrpc/ViewModels/FailureAnalysisVM.cs:21-35`. A triage card can be added
here with no new window plumbing.

#### D4. WebClient detail surface — `EXISTS`

- `TestController.WebClient/src/components/results/FailureAnalysisDialog.tsx:29` — fetches
  `/api/results/analyze/{testName}`
- Launched from `TestController.WebClient/src/components/results/BuildDetail.tsx:26`
- Server side: `TestController.Api/Controllers/ResultsController.cs:304`

---

### Group E — Constraints and risks

#### E1. SQLite / SMB locking — `PARTIAL`

| Store | Handling | Evidence |
|---|---|---|
| `orchestrator.db` | `PRAGMA journal_mode = WAL`, `PRAGMA busy_timeout = 5000` | `TestController.Persistence/OrchestratorDbContext.cs:53,56` |
| impact stores | `VACUUM INTO` snapshot copy for network persistence | `TestController.Impact/Impact/Index/ImpactPersistenceService.cs:155` |

**NOT FOUND — searched for:** `Polly`, retry-on-`SQLITE_BUSY`, `SqliteErrorCode == 5`, in the impact
stores. There is no busy-retry policy on `impact-index.db` / `impact-outcomes.db`.

#### E2. Credential handling — `EXISTS`, no plaintext in source

| Provider | Secret source | Evidence |
|---|---|---|
| PAT | env var `AdoOptions.SecretEnvVarName ?? "ADO_PAT"` | `TestControllerGrpc.Core/Ado/PatTokenProvider.cs:34,51-56` |
| Service principal | cert thumbprint from Windows cert store; secret fallback via env var | `TestControllerGrpc.Core/Ado/ServicePrincipalTokenProvider.cs:65-76`, `:50` |
| Interactive (WPF) | persisted Entra token cache | `TestControllerGrpc.Core/Ado/InteractiveTokenProvider.cs` |
| LLM | env var `LlmOptions.ApiKeyEnvVarName` (default `AZURE_OPENAI_API_KEY`) | `AzureOpenAiClient.cs:38`, `LlmOptions.cs:29` |

Selection by `AuthMode`: `TestControllerGrpc.Core/Ado/AdoServiceCollectionExtensions.cs:103-106`.
No secret values are reproduced in this report. The plaintext PAT repeatedly flagged in the docs
lives in `.ps1` files that are not in this repository (see B1).

#### E3. Run output size — `EXISTS`

| Limit | Value | Evidence |
|---|---|---|
| TRX streaming threshold | 25 MB | `TestControllerGrpc.Core/Services/TrxResultsParser.cs:152` |
| TRX file cache | 4000 files | `TestControllerGrpc.Core/Services/TrxResultsParser.cs:37` |
| SignalR pending output | 5000 lines, 500-line chunks | `TestController.Api/Services/SignalRNotifier.cs:58,61` |
| Session log backfill buffer | 500 entries | `TestControllerGrpc.Core/Models/WatchListConfig.cs:436` |
| Session history retained | 50 sessions | `TestControllerGrpc.Core/Services/ExecutionSessionManager.cs:18` |

Fingerprint extraction cost is bounded: the parser streams anything ≥ 25 MB rather than buffering.

---

## Unprompted finding — this changes the answer

The audit prompt did not ask about it, but **the failure fingerprint and the blame window already
exist and are wired into both hosts**.

`TestControllerGrpc.Core/Services/FailurePatternAnalyzer.cs`

| Capability | Evidence |
|---|---|
| Message normalisation (GUIDs, datetimes, numbers, paths → placeholders) | `:374-392` `NormalizeErrorMessage` |
| Exception type extraction | `:83` `ErrorType = ExtractExceptionType(r.ErrorMessage)` |
| Top stack frame | `:85` `TopStackFrame = ExtractTopStackFrame(r.StackTrace)` |
| Failed step name / index | `:79-80` |
| Signature record | `:457-469` `FailureSignature(BuildName, BuildDate, FailedStepIndex, FailedStepName, ErrorType, NormalizedMessage, TopStackFrame, Agent, Duration)` |
| **Blame window** | `:470-486` `FailureAnalysisReport.LastPassBuild`, `.FirstFailBuild` |
| Cross-build signature match | `:88-93` `allSignaturesMatch` |
| Pattern classification | `:98-118` (`SystemicRegression`, `FlakyTest`, `ChronicFailure`, …) |

Registered in both hosts via `TestController.Api/ControllerApiExtensions.cs:26-54`.
Unit-tested: `TestControllerGrpc.Tests/Services/FailurePatternAnalyzerTests.cs:28-57`.

`LastPassBuild` → `FirstFailBuild` is precisely the build range whose commits are the suspects.

---

## PART 2 — Summary table

| ID | Capability | Verdict | Evidence (file:lines) | Est. effort to close gap |
|----|-----------|---------|----------------------|--------------------------|
| A1 | Structured failure capture | EXISTS | `TrxResultsParser.cs:307-309` | NONE |
| A2 | Single test result DTO | EXISTS | `TrxModels.cs:42-55` | NONE |
| A3 | Run results persisted | **MISSING** | `OrchestratorDbContext.cs:15-21` (no table) | M |
| A4 | Run identifier | PARTIAL | `WatchListConfig.cs:404` (no `.trx` link) | S |
| A5 | Build under test recorded | PARTIAL | `WatchListConfig.cs:491,51-52` | S |
| B1 | Build → commits fetch | EXISTS | `BuildQueries.cs:44,149` | NONE |
| B2 | Output shape (id/author/time/paths) | EXISTS | `AdoDtos.cs:46-56`; `AdoRegressionDataProvider.cs:173` | NONE |
| B3 | Consumed by C# | EXISTS | `AdoRegressionDataProvider.cs:166` | NONE |
| B4 | Build → commit mapping stored | PARTIAL | `AdoRegressionDataProvider.cs:105-120` (60 s memo) | S |
| C1 | Cascade behind an interface | EXISTS | `ImpactTestMappingService.cs:21-29` | NONE |
| C2 | **Arbitrary free-text entry** | **MISSING (public) / PARTIAL (inner)** | `ImpactContracts.cs:42-57`; `IndexSnapshot.cs:105` | M |
| C3 | Index schema scores text vs code units | EXISTS — **work items, not code** | `IndexEntities.cs:8-90` | L |
| C4 | Reranker separately callable | EXISTS (change-shaped input) | `RelevanceReranker.cs:13-18` | S |
| C5 | Outcome schema general enough | **MISSING** | `OutcomeDbContext.cs:6-54` | M |
| C6 | LLM reranker reachable unattended | PARTIAL — disabled in config | `AzureOpenAiClient.cs:31-72`; both `appsettings.json` | M |
| D1 | DI blocks for both hosts | EXISTS | `App.xaml.cs:95-220`; `Program.cs:148,162` | NONE |
| D2 | REST + SignalR pattern | EXISTS | `ExecutionController.cs:37,81`; `SignalRNotifier.cs:96-116` | NONE |
| D3 | WPF detail surface | EXISTS | `FailureAnalysisDialog.xaml:96` | NONE |
| D4 | WebClient detail surface | EXISTS | `FailureAnalysisDialog.tsx:29` | NONE |
| E1 | SQLite/SMB lock handling | PARTIAL | `OrchestratorDbContext.cs:53,56` | S |
| E2 | Credential handling reusable | EXISTS | `PatTokenProvider.cs:34,51-56` | NONE |
| E3 | Run output size bounded | EXISTS | `TrxResultsParser.cs:152`; `SignalRNotifier.cs:58,61` | NONE |
| — | Failure fingerprint + blame window | EXISTS *(unprompted)* | `FailurePatternAnalyzer.cs:374-392,457-486` | NONE |

---

## PART 3 — Go / no-go

> **For a single failed test, can I today obtain (a) a stable text fingerprint of the failure and
> (b) the list of code changes in that build — using existing code, without building new
> infrastructure?**

**Yes, both halves exist today.** (a) The fingerprint is already built: `FailurePatternAnalyzer.NormalizeErrorMessage`
plus `FailureSignature` gives error type, normalized message and top stack frame, registered and
running in both hosts. (b) `IBuildQueries.GetBuildChangesAsync` returns commits with id, author,
timestamp and message, and `GetCommitChangesAsync` adds file paths. Better still,
`FailureAnalysisReport.LastPassBuild`/`FirstFailBuild` already hands you the exact build range to blame.

What you cannot do for free is *rank* those commits with the impact engine. Its public entry demands
`ImpactedArea` + `ChangePayload`, and its index contains ADO work items — not code. Ranking commits
by fingerprint similarity needs its own scorer, not a reversed cascade.

### The three biggest unknowns

1. **Build-folder → ADO build id.** `LastPassBuild` is a *folder name* from `ResultsRootPath`
   (`FailurePatternAnalyzer.cs:28-31` via `DiscoverBuilds`). Nothing in code maps that string to an
   ADO build id, which is what `GetBuildChangesAsync` needs. Without this join the two halves never meet.
2. **Fingerprint stability in production.** `NormalizeErrorMessage` is five regexes
   (`FailurePatternAnalyzer.cs:378-389`). Whether it collapses the *same* real failure across builds —
   and separates different ones — is unmeasured against production `.trx` data.
3. **Unattended ADO auth on the controller VM.** WPF host is `AuthMode=Interactive`; the PAT on the
   WebApi host has expired before. Overnight triage depends on whichever host runs it having a valid,
   non-interactive credential.

### The single cheapest spike (≤ half a day)

Take one genuinely failing test from `ResultsRootPath`, call `FailurePatternAnalyzer.AnalyzeTest`,
and print `LastPassBuild`, `FirstFailBuild` and `NormalizedMessage`. Then try to resolve those two
folder names to ADO build ids.

That single step resolves unknowns 1 and 2 together, and unknown 1 is the one that quietly decides
whether the feature is buildable at all.

### Assumptions the code contradicts

- **"Reuse the impact engine in reverse" does not work as stated.** The reverse direction needs
  code↔failure similarity. `impact-index.db` holds Feature and Test Case work items
  (`IndexEntities.cs:8-40`); there is nothing to match a stack trace against. You would be indexing a
  new corpus, not reversing an existing one.
- **"Confirmed/Wrong feedback lands in the existing outcome store."** It cannot. Every table is keyed
  on `TestCaseId` with no commit or verdict column (`OutcomeDbContext.cs:6-54`).
- **"The reranker will score culprits."** Its signature takes `ChangeDocument` + `ChangePayload` and
  grades test cases (`RelevanceReranker.cs:13-18`). Today it resolves to `PassThroughReranker`, which
  returns `2 / 0.5` for everything (`:25-38`). Any confidence score shown to a user would be
  fabricated until an Azure OpenAI deployment is configured.
- **A3 is `MISSING`**, which is the audit prompt's own stop condition. See the caveat below.

### Caveat on the A3 stop rule

The prompt says: *"If A1 or A3 comes back MISSING, stop and fix the failure-capture path first."*

A1 is `EXISTS`, so the failure itself is **not** an unparsed stdout line — it is structured and
normalised. A3 is `MISSING` only in the sense that nothing is written to a database; the `.trx`
files themselves are durable on disk and `FailurePatternAnalyzer` already reads five builds back
from them (`FailurePatternAnalyzer.cs:26-31`).

So a **read-only v1 can ship without persistence.** What persistence buys is the ability to measure
triage accuracy over time and to store Confirmed/Wrong feedback — i.e. it blocks the *learning loop*,
not the *first version*. Treat it as Phase 2, not a hard stop.

---

## Recommended shape, if this proceeds

Not a design — just what the evidence implies about sequencing.

| Phase | Work | Rests on |
|---|---|---|
| 0 | Spike: build-folder → ADO build id | unknown 1 |
| 1 | Blame window → commit suspects; deterministic scorer (path/symbol overlap vs `TopStackFrame`, recency, author) | B1-B3, `FailurePatternAnalyzer` |
| 2 | New triage tables (`TriageRun`, `TriageCandidate`, `TriageVerdict`) keyed on commit id | C5 gap |
| 3 | Card in the two existing dialogs | D3, D4 |
| 4 | Optional LLM re-ranking of the top N suspects | C6, needs a configured deployment |

Note that Phase 1 requires **no** LLM, **no** embeddings and **no** impact index — which is fortunate,
since all three are currently switched off in production configuration.
