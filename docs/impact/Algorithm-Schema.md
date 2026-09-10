# Impact Analysis — Algorithm Schema

The complete pipeline in one place: what flows between stages, every formula, and every tunable.
Companion to `ImpactDb-Deep-Dive.md` (operations) and `Regression-Selection-Algorithm.md` (R1–R3 design).

---

## 1. End-to-end flow

```mermaid
flowchart TD
    A[SubsystemRow<br/>component + changes] --> R1
    R1[R1 RegressionRiskScorer] -->|ImpactedArea<br/>RiskTier, ChurnMetrics| T0
    P[ChangePayload<br/>PR, commits, diffs, linked WIs] --> T0

    T0[T0 AnchorEdgeProvider] -->|AnchorResult| EX{Sufficient<br/>for early exit?}
    EX -->|yes| T4
    EX -->|no| T1

    T1[T1 ChangeDocumentBuilder<br/>+ KeywordExtractor + HyDE] -->|KeywordGroup[]| T2
    T2[T2 Retrieval] --> T2A[Branch A<br/>Feature search]
    T2 --> T2B[Branch B<br/>TestCase search + backref]
    T2A --> M[FeatureMerger + FanOutNormalizer]
    T2B --> M
    M -->|Scored FeatureCandidate| EXP
    T2B -->|Scored TestCaseCandidate| EXP
    EXP[ExpandAndReduceCandidates] -->|candidate set| T3
    T3[T3 RelevanceReranker<br/>grade 0-3] --> CAL[ScoreCalibrator]
    CAL --> T4
    T4[T4 BudgetedDiversitySelector] --> T5[T5 CoverageGapDetector]
    T5 --> OUT[ImpactMappingResult]
    OUT --> R3[R3 RunPlanWriter] & OS[(impact-outcomes.db)]
    OS -.->|failure rates, historical anchors| T0
```

---

## 2. Stage reference

### R1 — Risk scoring (`RegressionRiskScorer`)

$$R = 0.30\hat{C} + 0.25\hat{B} + 0.20F + 0.15\hat{T} + 0.10U$$

Full derivation in `Regression-Selection-Algorithm.md` §2. Bands to Critical ≥ 0.70, High ≥ 0.45, else Medium.

### T0 — Anchors (`AnchorEdgeProvider`)

Three parallel sources, deduped by `(TestCaseId, Source)` keeping the strongest:

| Source | Weight | Origin |
|---|---|---|
| `LinkedWorkItem` | 1.0 | `GetChildTestCasesAsync` over `payload.LinkedWorkItemIds`, walked to `Ado.LinkWalkMaxDepth` |
| `HistoricalFailure` | 0.5–1.0 | `OutcomeStore.GetHistoricalAnchorsAsync(areaId, Anchors.LookbackRuns)` |
| `DeclaredMapping` | 0.9 | component map regression areas |

Coverage:

$$\text{coverage} = \begin{cases}
1.0 & \text{no declared areas and any edge exists} \\
1.0 & \text{any LinkedWorkItem edge exists} \\
|\text{declared} \cap \text{covered}| \;/\; |\text{declared}| & \text{otherwise}
\end{cases}$$

Early exit iff `coverage ≥ EarlyExitCoverageThreshold` **and** `edges ≥ MinAnchorsForEarlyExit`, and never when
`RiskTier == Critical` unless `AllowEarlyExitForCriticalTier`.

### T1 — Query construction

`ChangeDocumentBuilder` (Roslyn) extracts changed symbols, literals, public-API changes and path tokens.
`KeywordExtractor` groups them into weighted facets. `HydeQueryGenerator` optionally adds synthetic test
prose to close the code-to-test vocabulary gap. Output: `KeywordGroup[]`, each with a `Weight`.

### T2 — Retrieval

**What a document is.** `IndexTextComposer` is the single definition, used by both production indexing and
the fixtures:

| Kind | Composed text |
|---|---|
| Test Case | Title + **Description** + flattened Steps + Tags |
| Feature | Title + Description |

> **Requirement-id titles.** Where test case titles are bare ids ("FR 12345") they carry no matchable
> vocabulary, and `System.Description` is the only functional prose on the item. It was previously fetched for
> Features and **not** for Test Cases, so such a corpus had almost nothing to match on. Measured on the
> fixture's `--fr-titles` corpus: precision@10 **10 % → 60 %**, recall@25 **20 % → 100 %**, APFD
> **0.017 → 0.585**. Indexing it requires a full rebuild, forced by `CurrentSchemaVersion = 2`.

BM25 per term, over the **term-scoped** document set (only documents containing query terms are loaded):

$$\text{score}(d) = \sum_{t \in q} \text{idf}(t) \cdot \frac{\text{tf}(t,d)\,(k_1+1)}{\text{tf}(t,d) + k_1\left(1-b+b\frac{|d|}{\text{avgdl}}\right)}$$

with $k_1 = 1.2$, $b = 0.75$.

Lexical and dense results fuse by **Reciprocal Rank Fusion** — rank-based, so BM25 magnitude is discarded:

$$\text{RRF}(d) = \sum_{\text{branches}} \frac{w_{\text{branch}}}{K + \text{rank}(d)}, \quad K = 60$$

> **Consequence worth knowing.** RRF rewards appearing in *many* facets over matching *strongly* in one, and
> with $K=60$ over short result lists it compresses scores into a narrow band (rank 1 vs rank 25 differ by
> ~28 %). Retrieval discriminates less than its 0.45 weight implies.

Features are damped by fan-out so a hub feature with hundreds of children cannot dominate:

$$\text{damp} = \text{clamp}\!\left(\frac{\log(1+\text{median})}{\log(1+\text{childCount})},\; 0.35,\; 1.30\right)$$

### T2b — Candidate assembly (`ExpandAndReduceCandidatesAsync`)

Three origins, and **they must share a scale** or origin decides rank instead of relevance:

| Origin | Score |
|---|---|
| Direct retrieval | RRF score |
| Feature expansion | $\max_{\text{retrieval}} \times \dfrac{\text{parentScore}}{\max_{\text{feature}}} \times \text{ExpandedChildScoreFactor}$ |
| Orphan (no parent) | $\max_{\text{retrieval}} \times \text{OrphanScoreFactor}$ |

> **This was the defect.** Expansion candidates previously got a flat `0.1` and orphans `0.05`, which placed
> every test found via the back-reference branch below every direct lexical hit however generic. Measured on
> the fixture: precision@10 **0 %**, APFD **0.429**. After scoring on the retrieval scale: precision@10
> **100 %**, APFD **0.810**, at identical cost. Pinned by
> `MapAsync_Should_RankExpandedTestCases_AboveGenericLexicalHits`.

Truncated to `MaxCandidateTestCases` (400) by score, tie-broken on ascending work-item id.

### T3 — Rerank

`LlmRelevanceReranker` grades 0–3 with cited signals; cached 7 days; fails open to grade 2. A judgement with
no cited signals is downgraded by `DowngradeWhenNoCitedSignals`. Absent judgement defaults to grade 2 at
confidence 0.5. `PassThroughReranker` is used when no LLM is configured.

The prompt carries id, title, parent feature, **description** and steps (each truncated to 400 chars). Without
description, a requirement-id-titled candidate reaches the grader as little more than "FR 12345".

### T4 — Selection (`BudgetedDiversitySelector`)

Per-candidate score, all five weights now configurable under `Selection`:

$$s = w_r \hat{r} + w_f \hat{f} + w_g \cdot \frac{g}{3}c + w_h h + [\text{automated}] \cdot w_a$$

Defaults $w_r=0.45,\; w_f=0.25,\; w_g=0.20,\; w_h=0.10,\; w_a=0.05$, where $\hat{r}$ and $\hat{f}$ are
normalised against the run maximum, $g$ is grade, $c$ confidence, $h$ historical failure rate.

> Grade 3 vs grade 2 is worth only $w_g(1 - \tfrac{2}{3})c = 0.06$ at default weights. The reranker is the
> highest-precision signal available and it is nearly outvoted by retrieval noise. Raising $w_g$ is the first
> lever to try if the head of the ranking looks wrong.

Then, in order:

1. Drop grade 0.
2. **MMR diversity** — greedy $\arg\max\;\lambda s - (1-\lambda)\max_{j \in S}\cos(v_i, v_j)$, $\lambda = 0.70$,
   subject to `MaxTestCasesPerFeature` and `MaxTestCasesTotal`.
   > `MaxSimilarity` returns 0 when a document has no dense vector. With no embedding endpoint configured
   > (the default) the whole MMR term vanishes and selection is pure score order — **diversity is a silent
   > no-op in lexical-only deployments.**
3. **Budget knapsack** — greedy by $s / \text{cost}$, cost = recorded duration or 60 s default.
4. **Recall safety net**, bypassing the budget: all `LinkedWorkItem` anchors, top 3 `HistoricalFailure`,
   every grade-3 with confidence ≥ 0.8, and one test per selected feature.
5. **Risk floor** — promote highest-scoring dropped candidates to `MinimumSelections[risk]` (R2, flagged off).
6. **Score floor** — drop below `MinFinalScore`, never touching anything from step 4 or 5.

Confidence: `Observed` when anchored, `Declared` when the parent feature was discovered by anchor or
back-reference, else `Assumed`.

### T5 — Gaps + R3

`CoverageGapDetector` flags declared areas with no selection, selected features with no tests, all-`Assumed`
selections, and High/Critical areas with fewer than 3 tests. `RunPlanWriter` emits the trigger manifest
(flagged off) — see `Regression-Selection-Algorithm.md` §4.

---

## 3. Tunables

| Option | Default | Effect |
|---|---|---|
| `Ado.ExcludedStates` | `["Removed","Closed"]` | WIQL `NOT IN` predicate at index time, **and** purges matching documents on the next build |
| `Ado.LinkWalkMaxDepth` | 3 | hierarchy levels the anchor walk descends |
| `Ado.IncludedAreaPaths` | `[]` | **declared but still ignored** — the corpus is not area-scoped |
| `Index.RebuildOnSchemaChange` | true | writers drop and recreate an index stamped with an older `SchemaVersion` |
| `Retrieval.Bm25K1` / `Bm25B` | 1.2 / 0.75 | term saturation / length normalisation |
| `Retrieval.RrfK` | 60 | **lower ⇒ more separation between ranks** |
| `Retrieval.ExpandedChildScoreFactor` | 0.85 | how strongly expansion candidates compete |
| `Retrieval.OrphanScoreFactor` | 0.30 | parentless test cases |
| `Retrieval.MaxCandidateTestCases` | 400 | candidate-set cap |
| `Retrieval.FanOutPenaltyFloor/Ceiling` | 0.35 / 1.30 | hub-feature damping |
| `Selection.RetrievalWeight` | 0.45 | $w_r$ |
| `Selection.FeatureWeight` | 0.25 | $w_f$ — credits a test for its *parent's* match |
| `Selection.GradeWeight` | 0.20 | $w_g$ — **raise first if the head ranks badly** |
| `Selection.FailureWeight` | 0.10 | $w_h$ |
| `Selection.AutomationBonus` | 0.05 | $w_a$ |
| `Selection.MmrLambda` | 0.70 | ↓ = more diversity; **no-op without embeddings** |
| `Selection.MinFinalScore` | 0.15 | score floor |
| `Selection.MaxTestCasesPerFeature` | 12 | per-feature cap |
| `Selection.EmitRunManifest` | false | R3 — when true a mapping run can trigger a pipeline |
| `Selection.RunManifestFolder` | — | watched folder the manifest is written to; required when emitting |
| `Selection.MaxInlineFilterChars` | 6000 | beyond this only `[_TestListFile]` is emitted, not `[_TestFilter]` |
| `Anchors.EarlyExitCoverageThreshold` | 0.80 | how readily T1–T3 are skipped |
| `Risk.EnableRiskWeighting` | false | R2 budget + floor |

---

## 4. Measuring a change

```
dotnet run --project TestController.ImpactEval -c Release -- replay --dump 20
dotnet run --project TestController.ImpactEval -c Release -- replay --w-grade 0.35 --w-retrieval 0.55
```

Flags: `--dump N`, `--fr-titles`, `--drop-descriptions`, `--min-score`, `--mmr-lambda`, `--w-retrieval`,
`--w-feature`, `--w-grade`, `--child-factor`, `--bm25-b`, `--max-per-feature`, `--max-total`, `--out <csv>`.

`--fr-titles` rebuilds the corpus with bare requirement-id titles, boilerplate steps and the prose moved to
the description — use it to check any retrieval change against that corpus shape, which behaves very
differently from the default one.

`--dump` prints rank, id, score, grade, feature and a ground-truth flag. **Always read the dump, not just the
aggregate** — the defect above showed as precision@10 = 0 while safe recall stayed at 100 %.

| Metric | Target | Meaning |
|---|---|---|
| Safe recall | ≥ 0.98 | every failing test was selected — the only hard gate |
| precision@10 | high | is the *head* right |
| APFD | ≥ 0.80 | are fault-revealing tests ranked early |
| cost | ≤ 0.35 | selected runtime ÷ full suite |

### Harness limitations

- One synthetic case (`report.Cases = 1`) over the built-in fixture. It catches ranking inversions, not
  real-corpus behaviour.
- Ground truth is the children of features 10, 11, 12, 20, 21; "failed" is the first two of those.
- `--bm25-b` does not reach the in-memory fixture store, which fixes $k_1$ and $b$ at construction.
- The fixture's `FakeRelevanceReranker` grades from the title, so under `--fr-titles` every candidate grades
  1. The production reranker receives the description and does not share this blind spot.
- **`compare` and `train` are stubs** — they print a description and exit. There is no paired A/B and no
  calibrator fitting. Sweep `replay` manually until they are built.
