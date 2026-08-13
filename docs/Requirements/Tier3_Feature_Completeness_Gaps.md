# Tier 3 — Feature Completeness: Gap Implementation Plan

| Field | Value |
|---|---|
| **Scope** | Close the *remaining* gaps in the five Tier 3 features |
| **Date** | 2026-08-06 |
| **Source assessment** | Code-level audit of specs vs. actual implementation |
| **Key finding** | 4 of 5 features are already substantially built; net-new greenfield work is the WatchItem Builder only |

> **How to read this doc.** Each feature section states what already exists (with file
> paths), the precise remaining gap, the files to touch, concrete tasks, acceptance
> criteria, and risk. Cite sections by number in follow-up sessions (e.g. "implement §3.2").
> Do **not** rebuild what is marked ✅ done.

---

## 0. Status Summary

| # | Feature | Backend | API | WPF | React | Remaining gap | Effort |
|---|---------|:------:|:---:|:---:|:-----:|---------------|:------:|
| 1 | API Test Framework | ✅ | ✅ | — | — | Verify green; expand assertions | XS |
| 2 | Build Report Card | ✅ | ✅ | ✅ | ✅ | PSR parsing, email, PDF export | M* |
| 3 | Enhanced Results Pipeline | ⚙️ | ✅ | ✅ | ✅ | Parallelize 2 parse loops | S |
| 4 | Concurrent WatchItem Execution | ⚙️ | ✅ | ⚙️ | — | Multi-item concurrency + UI marshaling + load test | M |
| 5 | WatchItem Builder | ❌ | n/a | ❌ | — | Greenfield (per spec) | L |

`*` §2 email/PDF are low effort; PSR is **blocked** on a file-format decision (see §2.4).

**Recommended sequence:** §1 (verify) → §2.2/§2.3 (email/PDF) → §3 (parallelism) → §4 (concurrency) → §5 (builder). §2.4 (PSR) runs whenever the format decision lands.

---

## 1. API Test Framework — Verify & Expand

**Status:** ✅ Implemented in [TestController.ApiTests](../../TestController.ApiTests). Two-mode
(InMemory/Live) harness, `FakeAgentDispatcher` seam, and workflow suites all exist.

**Existing files (do not recreate):**
- Infra: [ApiTestWebFactory.cs](../../TestController.ApiTests/Infrastructure/ApiTestWebFactory.cs), [ApiTestFixture.cs](../../TestController.ApiTests/Infrastructure/ApiTestFixture.cs), [FakeAgentDispatcher.cs](../../TestController.ApiTests/Infrastructure/FakeAgentDispatcher.cs), [TestConfig.cs](../../TestController.ApiTests/Infrastructure/TestConfig.cs)
- Workflows: [TriggerExecuteResultsTests.cs](../../TestController.ApiTests/Workflows/TriggerExecuteResultsTests.cs), [ConcurrentExecutionTests.cs](../../TestController.ApiTests/Workflows/ConcurrentExecutionTests.cs), [CancellationTests.cs](../../TestController.ApiTests/Workflows/CancellationTests.cs), [FailureRecoveryTests.cs](../../TestController.ApiTests/Workflows/FailureRecoveryTests.cs)

### 1.1 Tasks
1. Run the suite in InMemory mode: `dotnet test TestController.ApiTests\TestController.ApiTests.csproj`. Record pass/fail.
2. For each failing test, fix the *test* (not production) unless it exposes a real regression — then file the regression separately.
3. Add regression guards for bugs already hit this program (one `[Fact]` per historical defect in `docs/Issues/`): stdin-hang (Tier 1 B1), compressed-flag gRPC error, blank-page secured mode. Name `Method_Should_Expected_When_State`.

### 1.2 Acceptance
- `dotnet test` on `TestController.ApiTests` is green in InMemory mode.
- At least 3 new regression tests map 1:1 to documented past defects.

**Risk:** Low. **Effort:** XS.

---

## 2. Build Report Card — Close 3 Gaps

**Status:** ✅ End-to-end scaffold exists. Parallel aggregation, controller, WPF, and React all present.

**Existing files (do not recreate):**
- Core: [BuildReportAggregator.cs](../../TestControllerGrpc.Core/Services/BuildReportAggregator.cs) (already parallel), [BuildReportCardModels.cs](../../TestControllerGrpc.Core/Models/BuildReportCardModels.cs), [BuildSummaryStore.cs](../../TestControllerGrpc.Core/Services/BuildSummaryStore.cs)
- API: [BuildReportCardController.cs](../../TestController.Api/Controllers/BuildReportCardController.cs) (`/api/reportcard`, `/api/reportcard/builds`)
- WPF: [BuildReportCardWindow.xaml](../../TestControllerGrpc/Views/Results/BuildReportCardWindow.xaml), [BuildReportCardViewModel.cs](../../TestControllerGrpc/ViewModels/Results/BuildReportCardViewModel.cs)
- React: [ReportCardView.tsx](../../TestController.WebClient/src/components/reportcard/ReportCardView.tsx), [reportCardStore.ts](../../TestController.WebClient/src/stores/reportCardStore.ts), [useReportCard.ts](../../TestController.WebClient/src/hooks/useReportCard.ts)

### 2.1 The three gaps (confirmed in code)
| Gap | Evidence | Blocked? |
|-----|----------|:--------:|
| Consolidated email | No `SendReportCard`/`ReportCardEmail` type exists | No |
| PDF export | No `ExportPdf`/`PdfExport` type exists | No |
| Real PSR results | `BuildPsrCards()` returns hardcoded `PsrOutcome.Pending` dummies ([BuildReportAggregator.cs:511-521](../../TestControllerGrpc.Core/Services/BuildReportAggregator.cs#L511-L521)) | **Yes** |

### 2.2 Consolidated email (replaces per-use-case flood)
**Files:** new `TestControllerGrpc.Core/Services/BuildReportCardEmailComposer.cs`; reuse the existing HTML renderer ([BuildReportHtmlGenerator.cs](../../TestControllerGrpc.Core/Services/BuildReportHtmlGenerator.cs)) and the existing `SendMail` action path used by the pipeline.
**Tasks:**
1. Compose one email body from a `BuildReportCard` (grade, verdict, per-CI/agent summary, top failures) via the existing HTML generator — no new template engine.
2. Add a WPF command "Email Report Card" on [BuildReportCardViewModel.cs](../../TestControllerGrpc/ViewModels/Results/BuildReportCardViewModel.cs) (`[RelayCommand]`) that resolves recipients from `CiOwnerResolver` and sends via the existing mail sender.
3. Log via `IAppLogger.Log(LogLevel.Information, "BuildReportCard", ...)`. Do **not** await mail send inside any request path — fire from the command handler.
**Acceptance:** One email per build (not per use-case) with the full card; recipients resolved from CI owners.

### 2.3 PDF export
**Files:** new `TestControllerGrpc.Core/Services/BuildReportCardPdfExporter.cs`; WPF command on the VM.
**Tasks:**
1. Render the existing report HTML to PDF. Prefer an already-referenced dependency; if none, use a single well-maintained library (evaluate before adding — check `Directory.Packages.props`/existing `PackageReference`s first).
2. Add `[RelayCommand] ExportPdf` on the VM with a `SaveFileDialog`.
**Acceptance:** "Export PDF" produces a file visually matching the card; no new heavyweight runtime dependency added without confirmation.

### 2.4 Real PSR results — **BLOCKED (needs decision)**
**Blocker:** The PSR source format ("PSR Excel format") is undecided; `BuildPsrCards()` is stubbed pending it.
**When unblocked:** replace [BuildReportAggregator.BuildPsrCards()](../../TestControllerGrpc.Core/Services/BuildReportAggregator.cs#L511) with a real parser (`TestControllerGrpc.Core/Services/PsrResultParser.cs`) that reads the agreed format into `PsrResult` records. Keep the dummy fallback when no PSR file is present.
**Acceptance:** PSR cards reflect real run data; graceful fallback when absent.

**Risk:** Low (email/PDF), N/A until decision (PSR). **Effort:** M (mostly PSR, which is gated).

---

## 3. Enhanced Results Pipeline — Parallelize Parsing

**Status:** ⚙️ Caching + streaming already exist and are thread-safe; only two parse loops are sequential.

**Existing (do not recreate):**
- File cache: `ConcurrentDictionary<string,TrxTestRun>` with `{path}|{lastWriteUtc}` key + LRU eviction in [TrxResultsParser.cs](../../TestControllerGrpc.Core/Services/TrxResultsParser.cs).
- Build cache: [CachedBuildResultsProvider.cs](../../TestControllerGrpc.Core/Services/CachedBuildResultsProvider.cs).
- Hybrid buffered/streaming parse (25 MB threshold) already present.

### 3.1 Sequential sites to parallelize (confirmed)
| Site | Location | Change |
|------|----------|--------|
| Per-use-case TRX parse | [TrxResultsParser.cs:72](../../TestControllerGrpc.Core/Services/TrxResultsParser.cs#L72) `trxFiles.Select(ParseFile).ToList()` | `Parallel.ForEach` / `.AsParallel()` into a thread-safe collector |
| Directory parse | [TrxResultsParser.cs:381](../../TestControllerGrpc.Core/Services/TrxResultsParser.cs#L381) `.Select(ParseFile)` | Same |

### 3.2 Tasks
1. Replace the two `.Select(ParseFile)` sites with a bounded-parallel parse (`ParallelOptions.MaxDegreeOfParallelism` capped — reuse the same throttling rationale as [PipelineExecutorBase.cs:321](../../TestControllerGrpc.Core/Services/PipelineExecutorBase.cs#L321)). Collect into `ConcurrentBag`/thread-local then order deterministically after.
2. Preserve output ordering where the UI depends on it (re-sort by feature/use-case name after the parallel gather, matching current `OrderBy`).
3. Confirm the per-file cache remains the concurrency boundary (it already is `ConcurrentDictionary`); do not add a second lock.
4. Add a unit test: parsing a folder of N TRX files yields identical `BuildNode` output to the sequential version (determinism guard).

### 3.3 Acceptance
- Same aggregated result as before (byte-for-byte on counts/ordering), measurably faster on multi-file folders.
- No new shared mutable state outside the existing caches.

**Risk:** Low–Medium (only hazard is result-merge ordering; caches already safe). **Effort:** S.

---

## 4. Concurrent WatchItem Execution

**Status:** ⚙️ Infra ready. Sessions are `ConcurrentDictionary`-backed; the executor already runs
action groups in parallel with a `SemaphoreSlim` throttle ([PipelineExecutorBase.cs:318-329](../../TestControllerGrpc.Core/Services/PipelineExecutorBase.cs#L318-L329)). The remaining work is allowing *multiple WatchItems* to run at once and making the WPF UI safe under that.

### 4.1 Tasks
1. **Confirm the serialization point.** Trace `TriggerWatchItem` in [ExecutionController.cs](../../TestController.Api/Controllers/ExecutionController.cs) and `MainViewModel.Execution.cs` `TriggerEvent`; identify any implicit single-run gate (a shared `_isRunning`, a single executor await, or a UI `CanExecute`). Remove/relax only the *cross-item* gate — keep the **per-tag lock** (concurrent runs of the *same* WatchItem must still block).
2. **UI-thread marshaling (WPF).** Convert the flagged synchronous `Dispatcher.Invoke` sites to `InvokeAsync` and ensure every `ObservableCollection` mutation from a background progress/result stream is marshaled. Known sites (per `docs/Requirements/GAP-ANALYSIS.md`): `MainViewModel.Execution.cs:60,137,331` and `MonitorVM.cs:443,465`. Consider `BindingOperations.EnableCollectionSynchronization` for hot collections.
3. **Load test.** Add a test that triggers ≥5 distinct WatchItems concurrently and asserts all reach a terminal state with correct per-session counts (extend [ConcurrentExecutionTests.cs](../../TestController.ApiTests/Workflows/ConcurrentExecutionTests.cs)).

### 4.2 Acceptance
- ≥5 distinct WatchItems run concurrently to correct terminal states.
- Same-tag concurrency still blocks on the per-tag lock.
- No WPF `ObservableCollection` mutation off the UI thread; no sync `Dispatcher.Invoke` on the execution hot path.

**Risk:** Medium (UI thread-safety is the real hazard; executor/session layers are done). **Effort:** M.

---

## 5. WatchItem Builder — Greenfield

**Status:** ❌ Not implemented (spec only: [WatchItemBuilder_Implementation_Spec.md](Requirements/WatchItemBuilder_Implementation_Spec.md)). This is the one true feature build.

**Foundation that de-risks it:**
- Round-trip parser/serializer exists: [WatchListXmlParser.cs](../../TestControllerGrpc.Core/Services/WatchListXmlParser.cs) (the schema authority — form fields and validation rules must mirror it).
- Domain model `WatchListConfig` + action types are settled.

### 5.1 Tasks (follow the spec phases; summary only)
1. Form-based node editor (WatchItem → Event → ActionGroup → Action tree) with auto-completion sourced from the real action types/attributes in `WatchListXmlParser`.
2. Live XML pane bound to the model via the existing serializer (no bespoke XML string-building).
3. Rule-based real-time validation (missing close tags impossible by construction; guard `PollInterval > Timeout`, empty `ActionGroup`, plaintext password, unknown action type).
4. Silent auto-upgrade of legacy XML on open (round-trip through parser → serializer).
5. CommunityToolkit.Mvvm VMs (`[ObservableProperty]`/`[RelayCommand]`); no manual `INotifyPropertyChanged`.

### 5.2 Acceptance
- A user builds a valid WatchItem with auto-complete + inline validation and cannot save XML that fails to load.
- Opening a legacy file upgrades it silently and round-trips losslessly.

**Risk:** Low architectural (model settled) / Medium UI scope. **Effort:** L (spec: 6–8 days).

---

## 6. Cross-Cutting Conventions (apply to all sections)
- `IAppLogger` (`_logger.Log(level, category, message, ...)`), **not** Serilog.
- Singleton DI unless a framework demands otherwise.
- App-generated `Guid.NewGuid()` for any ids; lowercase hyphenated TEXT if persisted.
- WPF: CommunityToolkit.Mvvm source-generators. React: Zustand store per domain + `apiFetch<T>()`.
- Tests: `Method_Should_Expected_When_State`.
- Authoritative compile check in this workspace is `dotnet build <project>` (the language server is unreliable here); the WPF host output-copy fails on file locks while the controller runs — use `-t:CoreCompile` to validate host code.

## 7. Open Decisions (need user input)
1. **PSR file format** (§2.4) — the only hard blocker. Provide a sample + field mapping.
2. **PDF library** (§2.3) — approve adding a dependency, or target HTML-only export.
3. **Execution start order** — confirm the sequence in §0 or reprioritize.
