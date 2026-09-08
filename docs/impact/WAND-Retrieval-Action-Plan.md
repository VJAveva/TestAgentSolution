# WAND Dynamic Pruning — Action Plan

Status: **REVIEW COMPLETE — BLOCKED PENDING DECISIONS.** No code changed.

Per ground rule 1 every claim below was read from source. Per ground rule 3, six conflicts between the
requirements and the code are reported rather than resolved. Phase 1 must not start until C1–C4 are decided.

---

## 1. Verification log

Files read in full or in the relevant region:

| Concern | File | Verified fact |
|---|---|---|
| BM25 scorer | [Bm25Scorer.cs](../../TestController.Impact/Impact/Index/Bm25Scorer.cs) | `lengthNorm = k1 * (1 - b + b*(len/avgdl))`; `score += idf * (tf*(k1+1)) / (tf + lengthNorm)` |
| RRF fusion | [HybridRetriever.cs](../../TestController.Impact/Impact/Retrieval/HybridRetriever.cs) | Fuses lexical + dense **by rank**, not score |
| Fan-out damping | [FanOutNormalizer.cs](../../TestController.Impact/Impact/Ranking/FanOutNormalizer.cs) | `Normalize(IReadOnlyList<Scored<FeatureCandidate>>, IndexSnapshot, IList<string>)` |
| Search entry point | [IndexSnapshot.cs](../../TestController.Impact/Impact/Index/IndexSnapshot.cs) | `SearchBm25(IReadOnlyCollection<string> terms, int limit)` over in-memory `_documents` |
| Load path | [RetrievalIndexStore.cs](../../TestController.Impact/Impact/Index/RetrievalIndexStore.cs) | `GetSnapshotAsync(IndexKind, IReadOnlyCollection<string>?, CancellationToken)` |
| T2 caller | [ImpactTestMappingService.cs](../../TestController.Impact/Impact/ImpactTestMappingService.cs) | Loads both snapshots, then runs branches |
| Schema | [IndexEntities.cs](../../TestController.Impact/Impact/Index/IndexEntities.cs), [ImpactIndexDbContext.cs](../../TestController.Impact/Impact/Index/ImpactIndexDbContext.cs) | `DocumentTerm` PK = `(DocumentId, Term)`, index on `Term` |
| Provisioning | [ImpactIndexInitializer.cs](../../TestController.Impact/Impact/Index/ImpactIndexInitializer.cs) | `EnsureCreatedAsync` + WAL only |
| Build | [RetrievalIndexBuilder.cs](../../TestController.Impact/Impact/Index/RetrievalIndexBuilder.cs) | `BulkInsertTermsAsync` 300-row chunks; `RecomputeCorpusStatisticsAsync` full-corpus |
| Query construction | [ImpactContracts.cs](../../TestControllerGrpc.Core/Impact/ImpactContracts.cs) | `KeywordGroup(string GroupId, string Label, IReadOnlyList<string> Terms, double Weight)` |
| Options | [ImpactMappingOptions.cs](../../TestControllerGrpc.Core/Impact/ImpactMappingOptions.cs) | `Bm25K1=1.2`, `Bm25B=0.75`, `FanOutPenaltyFloor=0.35`, `FanOutPenaltyCeiling=1.30` |

Types required by the brief that **exist**: all of the above.
Types required that **do not exist**: a schema-version record, an integer document id, a postings cursor.
See C2 and C3.

---

## 2. Blocking conflicts

### C1 — R11's premise does not hold. Fan-out damping never touches the BM25 candidate set.

R11 states a document below the raw BM25 top-K can re-enter the final top-K after damping, and requires the
pivot test to compare against `threshold * (0.35 / 1.30)`.

What the code does:

- `FanOutNormalizer.Normalize` accepts `IReadOnlyList<Scored<FeatureCandidate>>`. It damps **Features only**.
  Test cases are never fan-out damped, so for the TestCase corpus — the 226k corpus this task targets — the
  widening factor has no basis at all.
- For features, damping runs in `MapCoreAsync` *after* `_featureRanker.RankGlobally` and `_merger.Merge`,
  i.e. on an already-selected set. It can reorder that set; it cannot reintroduce a document that
  `SearchBm25` never returned.

Consequence: the set that must be exact is `SearchBm25(terms, limit)` itself. Applying the R11 widening
would not break exactness — it only makes pruning weaker — but it would discard much of the benefit for a
reason that does not exist in this pipeline.

Also, the clamp bounds are **configurable** (`FanOutPenaltyFloor` / `FanOutPenaltyCeiling`), not constants.
Hardcoding `0.35 / 1.30` in a pivot test would silently break if either is retuned.

**Decision needed:** drop the R11 widening (recommended, exactness is unaffected), or keep it and read both
bounds from `RetrievalOptions` at query time.

### C2 — R6/R7 assume integer document ids. The schema uses strings.

- `IndexedDocument.Id` is `string`, `HasMaxLength(64)`, values `"TC:5678"` / `"F:1234"`.
- `DocumentTerm.DocumentId` is `string`; PK is `(DocumentId, Term)`.

R6 requires "delta-encoded document ids (varint)" and R7 requires `SkipTo(long docId)`. Neither is possible
against string ids. `IndexedDocument.WorkItemId` is an `int` but is **not unique across kinds** — a Feature
and a Test Case may share a work item id, and they are distinguished only by the `F:` / `TC:` prefix.

Delivering R6/R7 therefore requires a dense integer surrogate key per document, which is a larger schema
change than R6 describes, and it must be assigned deterministically so Reader hosts agree with the writer.

**Decision needed:** add a surrogate `DocOrdinal INTEGER` to `IndexedDocuments` (unique per kind, assigned at
build), or keep string ids and abandon varint delta encoding in favour of a different packing.

### C3 — R4 has no mechanism to build on, and the existing production index will not self-upgrade.

- No schema version is stored anywhere. `IndexMetadata` holds `DocumentCount`, `AverageDocumentLength`,
  `TokenizerVersion`, `EmbeddingModel`, `BuiltUtc`, `IndexedThroughUtc` — no schema key.
- `Index.RebuildOnSchemaChange` is declared at `ImpactMappingOptions.cs:110` and is **never read** anywhere
  in the solution. It is dead configuration.
- `ImpactIndexInitializer.EnsureCreatedAsync` calls `EnsureCreatedAsync`, which creates tables only when the
  database is absent. **It never ALTERs an existing schema.**

Consequence: adding columns per R1/R2 will not appear in the 947 MB index currently on jvgr22. Reads would
fail against the missing columns until the file is deleted by hand.

**Decision needed:** confirm Phase 1 also implements a `SchemaVersion` metadata key plus a
version-mismatch → drop-and-recreate path, and confirm a forced full rebuild on the next production deploy
is acceptable (the index is regenerable, and the rebuild is currently a multi-hour ADO crawl).

### C4 — The 18.5 s cold cost is snapshot **load**, not scoring. WAND alone will not remove it.

`GetSnapshotAsync` materialises the full `IndexSnapshot` — a `List<SnapshotDocument>` with per-document
`TermFrequencies` dictionaries — *before* any scoring happens. `SearchBm25` then iterates that in-memory
list. WAND prunes scoring, but under the current architecture the documents have already been loaded and
allocated by the time scoring starts.

To realise the objective, retrieval must move from load-then-score to cursor-over-postings. That changes
`IndexSnapshot` and every consumer of it:

- `HybridRetriever.RetrieveAsync` (takes an `IndexSnapshot`)
- `FanOutNormalizer.Normalize` (uses `snapshot.MedianChildCount`)
- `IndexSnapshot.SearchDense` / `VectorFor` (MMR diversity in `BudgetedDiversitySelector`)
- `IndexSnapshot.MatchAllTerms` (used by `ComponentMapDeclaredMappingSource`)
- `ImpactTestMappingService.MapCoreAsync`, which passes `testCaseSnapshot` into `_selector.Select`

**Decision needed:** confirm the scope includes splitting `IndexSnapshot` into a lazy lexical cursor source
plus the corpus-statistics/vector surface the other consumers need. Without that, Phases 2–4 will show
reduced *documents scored* but little or no reduction in cold load time, and the acceptance criterion
"cold snapshot load time reduced" cannot be met.

### C5 — R2 reuses a name that already means something else.

`Bm25Scorer` already has a local `lengthNorm` that **includes** `k1`:
`k1 * (1 - b + b*(len/avgdl))`. R2 asks to persist `(1 - b + b*(len/avgdl))`, which **excludes** `k1`.

Both are valid; storing without `k1` is the better choice since `k1` is a query-time option. The risk is a
double-`k1` bug if the persisted column is dropped into the existing expression unchanged. If R2 proceeds,
the column will be named `LengthNormBase` and the distinction documented at the property.

### C6 — Persisting `Idf` creates a second source of truth.

`IndexSnapshot.InverseDocumentFrequency(term)` derives IDF at query time from `DocumentCount` and the
`CorpusStatistics` document frequency. Adding an `Idf` column duplicates that. If the two ever disagree,
scores change silently.

**Proposed constraint:** the persisted `Idf` is used **only** to compute and validate
`MaxTermContribution`, never in `Bm25Scorer`. Scoring continues to derive IDF exactly as today, preserving
I1 by construction.

---

## 3. Phase plans (execute only after C1–C4 are decided)

### Phase 1 — build-time precomputation

Scope: `IndexEntities.cs`, `ImpactIndexDbContext.cs`, `ImpactIndexInitializer.cs`, `RetrievalIndexBuilder.cs`.

1. `CorpusStatistic` gains `Idf REAL` and `MaxTermContribution REAL` (R1). It is already keyed by `Term` and
   is the natural home; the brief's "term table" maps to `CorpusStatistics`, not `DocumentTerms`.
2. `IndexedDocument` gains `LengthNormBase REAL` (R2, named per C5).
3. Recompute lands in `RecomputeCorpusStatisticsAsync`, which **already** deletes all `CorpusStatistics` and
   regroups every `DocumentTerms` row on every build, full or incremental. Because that full-corpus pass
   already exists, R3's stricter option — recompute `MaxTermContribution` for **all** terms on every build —
   costs one additional aggregate over a table already being scanned, and removes the stale-bound risk
   entirely. **This is the option to implement**; the "changed postings only + 0.5 % avgdl drift" variant
   buys nothing here and adds a correctness cliff.
4. Add `SchemaVersion` to `IndexMetadata` and a mismatch → drop-and-recreate path (R4, and see C3).
5. No change to what is indexed, to SHA-256 fingerprint skipping, or to adaptive WIQL windowing (R5).

Report: schema diff, recompute location, measured full-rebuild delta. **Then stop.**

### Phase 2 — packed postings

Blocked on C2. Once the id representation is settled: one row per term holding a BLOB (R6), a forward-only
cursor exposing `DocId`/`Freq`/`Next()`/`SkipTo(...)` (R7), and replacement of the 300-row chunking in
`BulkInsertTermsAsync` with a single prepared statement rebound per row inside one transaction (R8).

Report: bytes-on-disk delta, plus proof that a cursor walk over a known term reproduces the old
`(docId, freq)` sequence exactly. **Then stop.**

### Phase 3 — WAND behind a flag, shadow mode

`Retrieval.UseWandPruning`, default `false` (R9). WAND over the cursors per R10. Threshold handling per the
C1 decision. Facet weight assertion (R12) belongs in `KeywordExtractor` where `KeywordGroup.Weight` is set —
`Weight` is a plain `double` today with no sign constraint. Shadow mode runs both paths, returns the old
result, and logs divergence at Error (R13).

Report: divergence count over ≥50 queries spanning all 35 components, plus documents-scored per path.
**Then stop.**

### Phase 4 — cutover

Only after Phase 3 reports zero divergence. Flip the default (R14), keep the old path reachable for one
release, add `PRAGMA mmap_size = 2147483648` on read connections only (R15).

Report: cold and warm snapshot timings, before and after, on the TestCase corpus.

---

## 4. Invariants — how each is held

| Invariant | Mechanism |
|---|---|
| I1 exact top-K | WAND prunes only documents whose upper bound cannot reach the K-th score. Phase 3 shadow mode proves it empirically before cutover. |
| I2 determinism | Existing tie-break is ascending work-item id in `IndexSnapshot.TopByScore`; the WAND heap must apply the same comparator. |
| I3 loud absence | The health gate at the top of `GetSnapshotAsync` is untouched; `ImpactIndexUnavailableException` still throws before any cursor is opened. |
| I4 single writer | All new columns are written only by `RetrievalIndexBuilder`, which runs on the ReaderWriter host. Readers only read. The `SchemaVersion` check makes a reader fail loudly against an old index rather than mis-scoring. |
| I5 outcomes untouched | No work item in this plan references `impact-outcomes.db`, `OutcomeStore`, or `OutcomeDbContext`. |

---

## 5. Non-goals

Untouched, as specified: `Ado.IncludedAreaPaths` / `Ado.ExcludedStates` corpus filtering; LLM reranker
batching or distillation; knapsack DP and MMR similarity caching in `BudgetedDiversitySelector`; HyDE
gating; the health-check caching path; any UI, WPF or WebClient change.

---

## 6. Open decisions

1. **C1** — drop the R11 threshold widening, or keep it reading bounds from options?
2. **C2** — add a surrogate integer document ordinal, or keep string ids and repack differently?
3. **C3** — confirm Phase 1 owns schema versioning, and that a forced production rebuild is acceptable.
4. **C4** — confirm the lazy-cursor refactor of `IndexSnapshot` is in scope; without it the cold-load
   acceptance criterion is unreachable.

No implementation will begin until these are answered.
