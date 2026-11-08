# Impact Analysis — Algorithm As Built

> **Scope.** This document describes what the code in `TestController.Impact/` **actually does**, as
> surveyed on 2026-09-23. It is deliberately separate from the design documents in this folder, which
> describe what the algorithm is *meant* to do. Where the two disagree, §7 records the difference.
>
> Companion documents:
> - [`Algorithm-Schema.md`](Algorithm-Schema.md) — index schema and document composition rationale
> - [`Regression-Selection-Algorithm.md`](Regression-Selection-Algorithm.md) — design intent and phase plan
> - [`ImpactDb-Deep-Dive.md`](ImpactDb-Deep-Dive.md) — the two-database split
> - [`WAND-Retrieval-Action-Plan.md`](WAND-Retrieval-Action-Plan.md) — planned retrieval optimisation (not built)

---

## 1. Overview

Impact analysis answers one question: **given a build that changed these components, which tests should
we run?**

It is a six-stage cascade. The cheap, deterministic stage runs first and can short-circuit the whole
pipeline; progressively more expensive stages run only when the cheap one is not confident enough.

```
R1  Risk score        → Critical / High / Medium
T0  Anchors           → deterministic edges      ← can EXIT EARLY here
T1  Query build       → symbols, literals, keywords
T2  Hybrid retrieval  → BM25 + dense, fused by RRF
T3  Rerank            → LLM grades each candidate 0–3
T4  Selection         → MMR diversity + knapsack under a time budget
T5  Record            → persist to the learning database
```

Orchestrated by `ImpactTestMappingService.MapCoreAsync()` in
[`ImpactTestMappingService.cs`](../../TestController.Impact/Impact/ImpactTestMappingService.cs).
Entry points are `IImpactTestMappingService.MapAsync()` and
[`RegressionImpactMatcher`](../../TestController.Impact/Impact/RegressionImpactMatcher.cs), which adapts
build-churn rows (`SubsystemRow`) into the engine's input.

---

## 2. Stage by stage

### R1 — Risk scoring

[`RegressionRiskScorer.Score()`](../../TestController.Impact/Impact/Risk/RegressionRiskScorer.cs)

A weighted blend of five normalised signals produces a score in $[0,1]$ and a tier:

$$R = 0.30\,\hat{C} + 0.25\,\hat{B} + 0.20\,F + 0.15\,\hat{T} + 0.10\,U$$

| Term | Meaning | Normalisation |
|---|---|---|
| $\hat{C}$ | churn | $\min\left(1, \dfrac{\log(1+f) + \log(1+k)}{\log(41) + \log(11)}\right)$ over files $f$, changes $k$ |
| $\hat{B}$ | defect density | $\min\left(1, \dfrac{b + 2m}{4}\right)$ over bugs $b$, incidents $m$ |
| $F$ | build failure | $1$ if `BuildResult != "succeeded"`, else $0$ |
| $\hat{T}$ | recency | $2^{-d/7}$ where $d$ = days since last change |
| $U$ | coverage uncertainty | $1$ if no declared areas **or** `Category == Unclassified` |

Banding: **Critical** at $R \ge 0.70$, **High** at $0.45 \le R < 0.70$, **Medium** below. The scorer never
emits `Unmapped` — Medium is the floor.

### T0 — Anchors

[`AnchorEdgeProvider.GetAnchorsAsync()`](../../TestController.Impact/Impact/Anchors/AnchorEdgeProvider.cs)

An **anchor** is a high-precision `(testCaseId, source, weight, rationale)` edge that links a changed
component directly to a test **without consulting the index at all**. This is what makes the pipeline
affordable to run per-PR rather than nightly.

| Source | Weight | Derivation |
|---|---|---|
| `LinkedWorkItem` | 1.0 | Change's linked Bug/IMS/Story → test case, walking `System.LinkTypes.Hierarchy-Forward` and `Microsoft.VSTS.Common.TestedBy-Forward` up to `Ado.LinkWalkMaxDepth` (3) levels |
| `DeclaredMapping` | 0.9 | `IDeclaredMappingSource` — regression areas from the component map / vobs.csv |
| `HistoricalFailure` | 0.5–1.0 | `OutcomeStore.GetHistoricalAnchorsAsync()` — tests that caught regressions in this area before |

**Coverage** is computed as: 1.0 if any linked-work-item edge exists; else (declared areas hit) / (total
declared areas); else 1.0 if any edge at all exists when no areas are declared.

**Early exit** requires coverage $\ge 0.80$ **and** $\ge 5$ edges. It is **vetoed for the Critical tier**
unless `Anchors.AllowEarlyExitForCriticalTier` is set (default `false`).

### T1 — Query construction

`ChangeDocumentBuilder` extracts symbols, string literals and public-API changes from the change set.
[`KeywordExtractor`](../../TestController.Impact/Impact/Query/KeywordExtractor.cs) groups them into
weighted facets:

| Facet | Weight | Content |
|---|---|---|
| identity | 1.00 | area, subsystem, vob |
| literals | 0.95 | string constants |
| api | 0.85 | public members |
| narrative | 0.75 | PR / commit text |
| expanded | 0.60 | HyDE synthetic prose |
| paths | 0.30–1.00 | folder names |

[`HydeQueryGenerator`](../../TestController.Impact/Impact/Query/HydeQueryGenerator.cs) optionally
generates synthetic "what a test for this would read like" prose to bridge vocabulary gaps. It requires an
LLM and is skipped silently when none is configured.

### T2 — Hybrid retrieval

[`HybridRetriever`](../../TestController.Impact/Impact/Retrieval/HybridRetriever.cs)

Retrieval is **lexical + dense fused by Reciprocal Rank Fusion**. It is *not* call-graph based, *not*
file-path scoped and *not* co-change based.

$$\text{RRF}(d) = \left(\frac{w_{\text{lex}}}{60 + r_{\text{lex}}(d)} + \frac{w_{\text{dense}}}{60 + r_{\text{dense}}(d)}\right) \cdot w_{\text{group}}$$

- **Lexical leg** — Okapi BM25 ($k_1 = 1.2$, $b = 0.75$) via `IndexSnapshot.SearchBm25()`
- **Dense leg** — cosine similarity over `text-embedding-3-small` embeddings. **Off by default**
  (`Index.EmbeddingEndpoint` is empty).
- $K = 60$ is the RRF discount constant.

**Document composition** is centralised in
[`IndexTextComposer`](../../TestController.Impact/Impact/IndexTextComposer.cs). Test cases index
Title + **Description** + flattened Steps + Tags. Descriptions matter because titles are frequently bare
requirement IDs such as `"FR 12345"`, which carry no retrievable signal.

**Fan-out damping** prevents hub features from dominating:

$$\text{damp} = \operatorname{clamp}\left(\frac{\log(1+\text{median})}{\log(1+\text{childCount})},\ 0.35,\ 1.30\right)$$

**Candidate assembly** (`ExpandAndReduceCandidatesAsync`) normalises three origins onto a common scale:

| Origin | Score |
|---|---|
| Direct lexical hit | RRF score |
| Found via parent feature | $\text{max}_{\text{retrieval}} \times \dfrac{\text{parent score}}{\text{max}_{\text{feature}}} \times 0.85$ |
| Orphan (no parent) | $\text{max}_{\text{retrieval}} \times 0.30$ |

Truncated to `Retrieval.MaxCandidateTestCases` (400), ranked by score then work-item ID.

### T3 — Rerank

[`RelevanceReranker.cs`](../../TestController.Impact/Impact/Rerank/RelevanceReranker.cs)

- **`LlmRelevanceReranker`** grades each candidate **0–3** and must cite the code signals it relied on.
  Results cache for 7 days keyed on `SHA256(fingerprint + id + revision)`. It **fails open** — a parse
  error yields grade 2 / confidence 0.5. Borderline judgements (grade 1–2, low confidence) get up to two
  extra self-consistency passes. Judgements citing **no** signals are downgraded one full grade.
- **`PassThroughReranker`** grades everything 2 / confidence 0.5 with reason `"rerank disabled"`. This is
  what runs when no LLM model is configured.

Ranking (T2) uses corpus statistics; rerank (T3) uses semantic judgement against the actual change.

### T4 — Selection

[`BudgetedDiversitySelector.Select()`](../../TestController.Impact/Impact/Selection/BudgetedDiversitySelector.cs)

Final per-candidate score:

$$s = 0.45\,\hat{r} + 0.25\,\hat{f} + 0.20 \cdot \frac{g}{3} \cdot c + 0.10\,\phi + 0.05\,[\text{automated}]$$

where $\hat{r}$ = normalised retrieval score, $\hat{f}$ = parent feature score, $g$ = rerank grade,
$c$ = grade confidence, $\phi$ = historical failure rate.

Six steps, in order:

1. **Drop grade 0** — noise.
2. **MMR diversity** — greedy $\arg\max\ \lambda \cdot s(c) - (1-\lambda)\max_{j \in S}\cos(v_i, v_j)$,
   $\lambda = 0.70$. Degrades to pure score ranking when dense vectors are unavailable.
3. **Value/cost knapsack** — greedy, under the tier time budget. Unknown test duration defaults to 60 s.
4. **Recall safety net** — bypasses the budget for linked-work-item anchors, top historical failures,
   grade-3 selections, and one test per selected feature.
5. **Risk floor** — minimum selections per tier, also budget-exempt. Only active when
   `Risk.EnableRiskWeighting` is on.
6. **Score floor** — drop anything below `Selection.MinFinalScore` (0.15).

Tier budgets: **Smoke** 15 min, **Targeted** 90 min, **Full** unlimited.

`CoverageGapDetector` runs alongside and surfaces areas the selection did not cover.

### T5 — Recording

`OutcomeStore.RecordRunAsync()` persists the run and its selections. `RunPlanWriter` optionally emits a
JSON trigger file into a watched folder (off by default).

---

## 3. Index

Two SQLite databases, split deliberately:

| Database | Nature | Contents |
|---|---|---|
| `impact-index.db` | **rebuildable cache** | BM25 postings, dense vectors, corpus statistics |
| `impact-outcomes.db` | **durable** | run history, selections, learning signals |

Built by
[`RetrievalIndexBuilder.BuildAsync()`](../../TestController.Impact/Impact/Index/RetrievalIndexBuilder.cs):

- Corpus is **only** `Feature` and `Test Case` work items. Bugs / IMS / Stories enter the pipeline as
  anchors, not index documents.
- Enumeration uses adaptive WIQL date windows (180 days, halving on overflow) to dodge the `VS402337`
  20 000-match limit.
- Incremental builds resume from an `IndexedThroughUtc` watermark once older than `Index.MaxAge` (30 h).
- Unchanged documents are skipped via a SHA-256 fingerprint on raw text.
- Corpus statistics are recomputed **corpus-wide** each build so IDF stays global.
- Inserts are chunked at 300 rows (SQLite's 999-variable limit).

Snapshot loads are **term-scoped** — only documents containing at least one query term are read. This is a
cost reduction rather than an approximation, since BM25 cannot score a document sharing no term. Loads run
inside a read transaction so postings and statistics come from a single generation.

`IndexMaintenanceService` rebuilds every 15 minutes, and **only on the `ReaderWriter` host**. The WebApi
host registers as `ReaderWriter`; the WPF host registers as `Reader` and never writes. Operational
scripts: `deploy/Rebuild-ImpactIndex.ps1`, `deploy/Prepare-ImpactIndexStorage.ps1`.

---

## 4. Learning

[`OutcomeStore`](../../TestController.Impact/Impact/Learning/OutcomeStore.cs) persists:

| Record | Written by | Status |
|---|---|---|
| `MappingRun` + `MappingSelection` | `RecordRunAsync()` | **active** |
| `ExecutionOutcome` | `RecordExecutionAsync()` | **never called** — see §7 |
| `EscapeRecord` | `RecordEscapeAsync()` | tests that should have been selected but were not |

Three signals feed back into ranking: historical anchors (T0), per-test failure rates ($\phi$ in the
selection score), and mean durations (knapsack cost).

`Learning.ScoringMode` selects the calibrator — `Linear` (identity, default) or `Ranker` (a fitted
isotonic / Platt model). `IsotonicScoreCalibrator.Fit()` needs ≥ 200 labelled pairs and falls back to
Platt scaling below that.

---

## 5. Evaluation

[`TestController.ImpactEval`](../../TestController.ImpactEval/Program.cs) is a standalone CLI:

```
TestController.ImpactEval replay  <corpus>          # score historical selections against outcomes
TestController.ImpactEval compare <before> <after>  # paired delta analysis
```

Metrics in [`EvalMetrics.cs`](../../TestController.Impact/Impact/Eval/EvalMetrics.cs): `RecallAtK`,
`PrecisionAtK`, `Apfd`, `IsSafeRecall`.

Documented exit criteria: **safe recall ≥ 0.98** (hard gate), selection cost ≤ 0.35 at Targeted tier,
APFD ≥ 0.80, escape rate non-increasing.

---

## 6. Configuration

Bound to `ImpactMappingOptions` under the `ImpactMapping` section.

| Key | Default | Effect |
|---|---|---|
| `Retrieval.Bm25K1` / `.Bm25B` | 1.2 / 0.75 | BM25 saturation and length normalisation |
| `Retrieval.RrfK` | 60 | RRF discount |
| `Retrieval.MaxCandidateTestCases` | 400 | pre-selection cap |
| `Retrieval.ExpandedChildScoreFactor` | 0.85 | score for tests found via parent |
| `Retrieval.OrphanScoreFactor` | 0.30 | score for parentless tests |
| `Index.EmbeddingEndpoint` | `""` | **empty disables dense retrieval** |
| `Index.MaxAge` | 30 h | incremental refresh trigger |
| `Anchors.MinAnchorsForEarlyExit` | 5 | early-exit edge count |
| `Anchors.EarlyExitCoverageThreshold` | 0.80 | early-exit coverage |
| `Anchors.AllowEarlyExitForCriticalTier` | `false` | Critical-tier veto override |
| `Anchors.LookbackRuns` | 6 | historical anchor window |
| `Rerank.EnableLlmRerank` | `true` | but see `LlmModel` |
| `Rerank.LlmModel` | `""` | **empty disables rerank regardless of the flag** |
| `Rerank.CacheTtl` | 7 d | judgement cache lifetime |
| `Selection.MmrLambda` | 0.70 | relevance vs diversity |
| `Selection.RetrievalWeight` | 0.45 | score fusion |
| `Selection.FeatureWeight` | 0.25 | score fusion |
| `Selection.GradeWeight` | 0.20 | score fusion |
| `Selection.FailureWeight` | 0.10 | score fusion |
| `Selection.AutomationBonus` | 0.05 | score fusion |
| `Selection.MinFinalScore` | 0.15 | drop floor |
| `Selection.MaxTestCasesTotal` | 150 | hard cap |
| `Selection.MaxTestCasesPerFeature` | 12 | diversity constraint |
| `Selection.EmitRunManifest` | `false` | R3 trigger file |
| `Risk.EnableRiskWeighting` | `false` | R2 budget multipliers and floors |
| `Risk.CriticalThreshold` / `.HighThreshold` | 0.70 / 0.45 | tier banding |
| `Learning.ScoringMode` | `Linear` | calibrator selection |
| `Learning.RecordOutcomes` | `true` | persist to learning DB |

---

## 7. What is not running

Several stages are implemented but **gated off by default**. This is intentional, but it materially
changes what executes.

| Capability | State | Reason |
|---|---|---|
| Dense retrieval | **off** | `Index.EmbeddingEndpoint` empty |
| LLM rerank | **off** | `Rerank.LlmModel` empty → `PassThroughReranker` |
| HyDE expansion | **off** | requires the same LLM |
| Risk weighting (R2) | **off** | `Risk.EnableRiskWeighting = false`, pending validation |
| Run manifest (R3) | **off** | `Selection.EmitRunManifest = false` |
| Learning loop | **incomplete** | `RecordExecutionAsync()` is never called (Phase 5 not started) |
| WAND pruning | **not built** | designed only — see `WAND-Retrieval-Action-Plan.md` |

So the pipeline that actually executes is:

```
anchors → BM25 only → pass-through rerank → MMR + knapsack
```

### Consequence for scoring

This follows from the defaults rather than being stated anywhere in the code, and is worth verifying with
`ImpactEval replay` before acting on it.

With rerank disabled every candidate receives grade 2 and confidence 0.5, so the grade term collapses to a
**constant** for all candidates:

$$0.20 \cdot \frac{2}{3} \cdot 0.5 \approx 0.067$$

And because the learning loop never closes, `GetFailureRatesAsync()` returns an empty map, so $\phi = 0$
for every test. Effective discrimination therefore reduces to:

$$s \approx 0.45\,\hat{r} + 0.25\,\hat{f} + 0.05\,[\text{automated}] + \text{const}$$

**30% of the designed scoring weight currently carries no signal.** Ranking is, in practice, BM25
retrieval plus parent-feature score.

Related: even with the LLM enabled, the difference between grade 3 and grade 2 is worth only $\approx
0.067$ — small enough that the reranker is nearly outvoted by retrieval noise. `Selection.GradeWeight` is
the first lever to reach for if rerank is turned on.

### Other gaps

- `ChurnMetrics.LinesAdded` / `.LinesDeleted` / `.DistinctAuthorCount` are **always zero**. Author
  dispersion is explicitly deferred — `RegressionChangeRef` carries no author field. The weight is zero
  too, so nothing is silently folded elsewhere.
- `Learning.ScoringMode` accepts **`Linear`** (default) or **`Ranker`**. Leaving it unset resolves to
  `Linear`; an explicitly set unsupported value now **throws at startup** rather than degrading silently.
- `ComponentMapDeclaredMappingSource` falls back to `NullDeclaredMappingSource` when no component map is
  registered — no error, simply no declared edges.
