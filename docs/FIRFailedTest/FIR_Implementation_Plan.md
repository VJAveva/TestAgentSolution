# FIR — Implementation Plan

> Path in repo: `docs/FIRFailedTest/FIR_Implementation_Plan.md`
> Inputs: `FIR_Requirement.md`, `FIR_Results_Mockup.html`, review of the codebase (2026-10-10).
> Status: plan approved; Phase 1 ready to start.
> **Two ground rules:** (1) **generic** — the engine knows nothing about any one product or test
> framework; WASSmokeTest is only the first rule pack. (2) **TRX-first** — older builds have only TRX
> files, so a TRX alone must produce a useful report; the `.log` and the Excel are optional extras.

## 1. Decisions

| # | Decision |
|---|---|
| D1 | **Input = TRX first.** Every report is built from the TRX (`ErrorInfo/Message`, `StackTrace`, `Output/StdOut`, `Output/DebugTrace`, duration, computer). A `.log` and test-data workbook are used only when present. |
| D2 | **Generic engine + stacked rule packs.** A built-in generic layer works on any TRX with no pack. Packs add knowledge per test framework and per product, and are selected automatically (§5). |
| D3 | **Rule packs ship in the deploy payload** under `TestControllerService\rules\*.fir.json` (versioned with the code, not in `preserveFiles`). A team adds a pack by dropping a file; no code change. |
| D4 | **Old builds:** reports are computed **on demand** when a test is opened and cached as `<Test>.fir.json`; an optional backfill command fills a whole build. |
| D5 | **Fixtures are real and sanitized.** WASSmokeTest fixtures exist (`FIR_Fixtures.zip`); add TRX fixtures from the older data, including at least one **non-WASSmokeTest** TRX to prove the generic layer. |
| D6 | **First ship:** Phases 1–4 (WPF). Phase 5 (React + email line) follows. |

## 2. Corrections adopted from the code review

1. **Core stays dependency-light.** Excel reading is an interface in Core (`ITestDataSheetReader`) with the ClosedXML implementation in `TestController.Reporting` (same pattern as `IRegressionImpactMatcher` / `TestController.Impact`).
2. **Redaction does not rely on the runtime registry.** `SecurityRedactor` (`TestControllerGrpc.Services`) is filled at run time; a report over an old TRX sees an empty registry. FIR carries its own patterns; the registry is an extra.
3. **No new `FailureAnalyzer`.** The façade is `FirAnalyzer`. Cross-build grouping extends `FailurePatternAnalyzer` (already behind `GET /api/results/analyze/{testName}`).
4. **Tests assert against real fixtures only.**

## 3. Reuse (do not rebuild)

| Need | Existing |
|---|---|
| TRX parse incl. StdOut + DebugTrace | `TrxModels.cs:45-59` (`TestResult`) |
| Build/test discovery + cache (`{path}|{lastWriteUtc}`, globs `*.trx`) | `TrxResultsParser.cs` — a `.fir.json` sibling is safe |
| Per-test `.log` / merged logs on a time window | `ExecutionLogCorrelator.cs`, `GET /api/results/builds/{b}/test/{t}/log` |
| Cross-build signatures / verdicts | `FailurePatternAnalyzer.cs` |
| Detail pane | `BuildResultsViewModel.cs:92-95` → `ResultsDashboardWindow.xaml:923-956` |

## 4. How a report is built (three layers)

```
TRX (always) ──┐
.log (optional)┼─► Generic layer ─► + Framework pack ─► + Product pack ─► + Test data (optional) ─► FirReport
               │   (built in)        (e.g. WASSmokeTest)  (e.g. AppServer)   (ITestDataSheetReader)
```

| Layer | Knows | Works with |
|---|---|---|
| **Generic (built in, no pack)** | TRX structure; assertion vs. unhandled exception vs. timeout; whether the failing frame is the test's own code, its `TestInitialize`/`TestCleanup`, or a library; first error-level line in the output; duration outliers; secrets by generic patterns (JWT, `password=`, connection strings) | **Any** MSTest / vstest TRX, old or new |
| **Framework pack** | How a keyword/data-driven framework writes its trace: step/row markers, keyword line format, pass/fail words, where the test-data file and sheet are named, reason-count line | All tests of that framework (WASSmokeTest today) |
| **Product pack** | Product messages that mean failure even when a step says Passed (e.g. `Deployed 0 of N`), product error lines, noise lines, keyword → category | All tests of that product |

With no pack, the generic layer still gives: verdict ("Assertion failed in TestCleanup — the test failed on a summary check, not on a step"), the failing frame, first error line, duration, and masked secrets. Packs make it precise.

**Note from the fixtures:** WASSmokeTest failures are thrown from `TestCleanup()` by a summary assertion. The generic layer must recognise "failure raised in cleanup by a summary check" and look **earlier in the trace** for the first failing step instead of reporting the cleanup frame as the cause.

## 5. Rule packs: shape and selection

Each pack declares **what it applies to**; all matching packs are stacked (framework, then product).

```json
{
  "id": "WASSmokeTest", "kind": "framework", "version": 1,
  "appliesTo": { "testAssembly": ["wassmoketest.dll"], "className": ["^WASSmokeTest\\."] },
  "rowMarker": "\\(ReadValuesFromExcel\\) \\*+ Row No: (?<row>\\d+)",
  "testDataLine": "\\(ReadValuesFromExcel\\) ExcelPath : \"(?<path>[^\"]+)\" and sheet name: \"(?<sheet>[^\"]+)\"",
  "stepLine": "^\\s*(?:\\[LineNo:(?<line>\\d+)-(?<file>[^\\]]+)\\])?\\[(?<ts>[^\\]]+)\\] (?<level>Passed|Failed|Info|Error|Warning): \\((?<kw>[^)]+)\\) ?(?<msg>.*)$",
  "failLevels": ["Failed", "Error"],
  "reasonLine": "Validation Failure Count: (?<v>\\d+), Logger ERRORS Count: (?<e>\\d+), Logger WARNINGS Count: (?<w>\\d+)",
  "summaryAssertFrames": ["AssertTestPassed", "TestCleanup"],
  "secretLines": ["\\(GetUserToken\\)"],
  "slowStepSeconds": 180,
  "testDataKeywordMap": {
    "deploy": ["DeployOperation"], "redeploy": ["DeployOperation"], "undeploy": ["UndeployOperation"],
    "modifyovvalue": ["ModifyOVvalue", "ObjectViewerValidation"],
    "getandvalidateovvalue": ["OVGetValue", "ValidateOVValue"],
    "waitforattributequality": ["WaitforAttributeQuality"], "ovcomparetimestamp": ["OVCompareTimestamp"]
  }
}
```

```json
{
  "id": "AppServer", "kind": "product", "version": 1,
  "appliesTo": { "outputContains": ["Deploy Completed:", "galaxy"] },
  "productError": ["^Error: Failed to (?:deploy|undeploy) (?<obj>\\S+?)\\s*:\\s*(?<reason>.+)$"],
  "productCount": "^\\[(?:SUCCESS|WARNING|ERROR)\\] (?:Un)?Deploy Completed: (?:Un)?Deployed (?<done>\\d+) object\\(s\\) out of a total (?<total>\\d+)",
  "noise": [
    "^Warning: Test Run deployment issue: The assembly or module",
    "^Test Run deployment issue: an error occurred while getting satellite assemblies",
    "^Parameter name: startIndex"
  ],
  "categories": {
    "Product error": ["DeployOperation", "UndeployOperation"],
    "Validation mismatch": ["Validate*", "OVCompare*", "WaitforAttribute*"],
    "Runtime write failed": ["Modify*", "ObjectViewer*", "UserSet*"]
  },
  "objectShortName": "^(?<short>[A-Za-z]+\\d*(?:_\\d{3})?)"
}
```

Selection: match `appliesTo` against the TRX `TestMethod` (`codeBase`, `className`) and the output text. No match → generic layer only. The report records which packs were used (`RulePacks: ["generic", "WASSmokeTest@1", "AppServer@1"]`).

## 6. Architecture

```
TestControllerGrpc.Core/Fir/          pure; no I/O except loading packs
  FirRulePack, FirRulePackLoader      loads rules\*.fir.json, validates, orders framework -> product
  FirPackSelector                     appliesTo matching
  FirTrxInput                         adapts the existing TrxModels.TestResult (+ optional .log text)
  FirGenericAnalyzer                  layer 1: assertion/exception/timeout/frame/first-error/duration
  FirStepParser                       layer 2: rows/steps via framework pack
  FirClassifier                       ROOT / MASKED / CASCADE / RECOVERED / SLOW / PASSED-context, categories
  FirRedactor                         generic + pack patterns; SecurityRedactor registry as an extra
  FirReport (records), FirAnalyzer    facade: Analyze(FirTrxInput, packs, sheetRows?) -> FirReport?
  ITestDataSheetReader

TestController.Reporting/
  ClosedXmlTestDataSheetReader        read-only; missing file/sheet -> null (no failure)

Both hosts' DI (ResultsController is not proxied)
  FirService                          on-demand compute + cache <Test>.fir.json; best-effort write
  FirBackfill (command)               compute for a whole build folder
  GET /api/results/builds/{build}/test/{testName}/fir
```

## 7. Phases

### Phase 0 — Fixtures
- Done: WASSmokeTest `.log` fixtures + 3-sheet workbook (`FIR_Fixtures.zip`).
- **Needed:** sanitized TRX files from older builds:
  1. the TRX for `S1_04_SmokeTestDeploy` and `S1_13_TPTest3` (same run as the logs) if still available, otherwise any 2 failed WASSmokeTest TRX;
  2. one passed WASSmokeTest TRX;
  3. **one or two failed TRX from a different test project / product** (proves the generic layer).
  Sanitize before committing (GetUserToken lines, JWTs, passwords, connection strings).

### Phase 1 — TRX input + generic layer (no packs, no Excel, no UI)
Scope: `FirTrxInput`, `FirGenericAnalyzer`, `FirRedactor` (generic patterns), `FirReport`, `FirAnalyzer`.
Exit:
- Any failed TRX fixture (WASSmokeTest **and** the other product) yields a report: failure type (assertion / exception / timeout), failing frame classified as test code / init / cleanup / library, first error-level output line, duration.
- WASSmokeTest TRX: recognised as "summary assertion in TestCleanup" → verdict points to the trace, not the cleanup frame.
- Passed TRX → no report. Empty/odd output → report with what is known; never throws.
- No output string contains a secret value.

### Phase 2 — Rule packs (framework + product) on top
Scope: `FirRulePackLoader`, `FirPackSelector`, `FirStepParser`, `FirClassifier`, packs `WASSmokeTest.fir.json` + `AppServer.fir.json`.
Exit (WASSmokeTest fixtures — same numbers from TRX DebugTrace as from `.log`):

| Fixture | Expected |
|---|---|
| S1_04_SmokeTestDeploy | reason v=32 e=1 w=14; ROOT row 6 `DeployOperation` `AutoGalaxyConfiguration.cs:8622`; failing rows 6, 24, 25, 32; MASKED row 32 (0/17); SLOW row 6 (~270 s); "Platform startup failed." ×2, "Communication error: Platform communication error" ×12 (product wording kept; shortening is display-only); 32 failed steps; SecretsFound 2 with the pack (generic layer alone finds 1 — the JWT; the `grpcuser:` line needs the pack's `secretLines`) |
| S1_13_TPTest3 | reason v=5 e=0 w=1; ROOT row 23 MASKED (0/1, "Unable to communicate with the target engine."); CASCADE 29–32; SLOW rows 29 (~203 s, ModifyOVvalue timeout) and 30 (~942 s); Validation mismatch 3, Runtime write failed 2; SecretsFound 4 |
| S1_21_GalaxyStatus | Passed; no report |
| All three `.log` fixtures | NoiseLines 63 (the "delegated to legacy TestPlatform runner" line is not noise) |
| Other-product TRX | packs not selected; generic report unchanged from Phase 1 |

SecretsFound counts distinct lines (not values), so sanitized fixtures still count; a line hit by two rules counts once.
Not blocked on TRX: the parser takes plain text, so this exit table runs today against the `.log` fixtures in
`FIR_Fixtures.zip` (unzip into `TestControllerGrpc.Tests/`). Re-run the same table on TRX DebugTrace when the
sanitized TRX files land. Pack loader + selector are tested on small synthesized pack files (format logic only).
Red-proof: remove the masked-pass rule → S1_13 root moves to row 29 and the test fails. Add a dummy third pack file → it is selected only when its `appliesTo` matches (proves "drop a file, no code").
(Noise counts come from the `.log` only — TRX RunInfos are counted separately if present.)

### Phase 3 — Test-data correlation (optional input)
`ITestDataSheetReader` + `ClosedXmlTestDataSheetReader`. Join and link rules from `FIR_Requirement.md` §5a, driven by the framework pack (`testDataLine`, `rowMarker`, `testDataKeywordMap`). Missing workbook → same report without Excel steps.
Exit: S1_13 row 23 = `Appserver | redeploy | DDESL_002…`; rows 29–32 same object (High); row 28 PASSED context; OpenPoint present. S1_04 row 6 = `Appserver | deploy | SmokeSet1 | true | true | false`; rows 24/25 High; row 23 PASSED context; rows 33–34 RECOVERED.

### Phase 4a — Finding the test-data workbook on the controller (decided)

The logged path is the agent's run folder (`C:\TestResults\…\Out\TestData\…`), which does not exist on the
controller. Resolution order:

1. **Beside the TRX** — `<results folder>\TestData\<workbook>` or `<results folder>\<workbook>`. This is the
   exact copy used by the run.
2. **Configured roots** — controller setting `Fir:TestDataRoots` (e.g. the release's
   `TestSetup\FrameworkBinaries\<Release>\` folder), searched by workbook file name.
3. **Never** reach out to agents over the network (VMs are reverted, may be offline).

**Staleness guard (required for step 2):** before using a workbook, check it against the log: every logged
`Row No: N` must land on a non-empty, non-structural row, and where the log shows a keyword, the sheet's keyword
must map to it via `testDataKeywordMap`. Any mismatch → discard the workbook, report log-only, and say
"test data found but it has changed since this run". A wrong row attribution is worse than none.

The summary's Sources line says which copy was used: `test data (copy from run)` or
`test data (controller copy, matched to log)`.

**Run-side change (makes step 1 the normal case):** when results are copied back to the controller, also copy
the workbook(s) the tests used into `<results folder>\TestData\`. Old builds fall back to step 2 + guard.

Cache key for `<Test>.fir.json`: TRX last-write + `.log` last-write + workbook last-write + rule-pack versions.
Each file contributes `lastWriteUtc:length`, so a restore that hands back an older file with a fresh timestamp
is still caught. Missing files stamp as `-`.

**The workbook stamp is re-resolved, not remembered.** Both when the key is written and when it is checked,
`FirService` re-runs `FirWorkbookResolver.Resolve` from the *logged* path (`DataWorkbook`) against the current
results folder and roots. It deliberately does **not** stamp the report's stored `ResolvedWorkbook`:

- A report built with no workbook stores `ResolvedWorkbook = null`, and a null stamp never changes — so the
  "test data: not found" verdict would outlive the arrival of the workbook. This matters because the run-side
  copy above is exactly an event that makes a previously-missing workbook appear.
- `ResolvedWorkbook` is also null when a workbook *was* found but failed to read, so stamping it on one side
  and a live resolution on the other would disagree permanently and the cache would never hit.

Cost is one filename-matched directory enumeration per cache check, and none at all for a test whose log has
no `ExcelPath` line (the resolver exits before touching the disk).

**Still not detected:** a change in the *rule packs' workbook-resolution inputs* — specifically a newly added
`Fir:TestDataRoots` entry is picked up (it feeds the live resolve), but an edit to a workbook that is shadowed
by a higher-priority copy is not, because resolution short-circuits on the first match. Use `refresh: true` to
force a recompute.

### Phase 4 — Persist, serve, WPF
- `FirService`: compute on demand when a test is opened; cache `<Test>.fir.json` beside the TRX (best-effort; results root is often a share; a failed write never fails anything). Recompute when the TRX or any pack version changes.
- `FirBackfill` command for a whole build (old data).
- `GET …/fir` in both hosts.
- WPF Detail pane per mockup; existing four TextBoxes stay below, collapsed. `DynamicResource` only, `AutomationProperties.Name` on every interactive control, ratchets respected.

### Phase 5 — React parity + email verdict line
### Phase 6 — Set/build grouping via `FailurePatternAnalyzer` + `FirReport.Signature`

## 7a. The summary (what the user reads first)

Every report starts with a **plain-text summary** built from all available sources: TRX + log + test data.
Fixed shape, so it reads the same for every product:

```
<Test> failed (<duration>, <computer>).
Cause: <data-sheet row N `Component | keyword | object`> — <what the product/log says>; <masked note if the step logged Passed>.
Effect: <rows / steps that failed because of it, grouped>; <time lost if a step was SLOW>.
Sources: TRX (<used>), log (<used | not found>), test data (<workbook › sheet | not found>), packs (<ids>).
```

Example (S1_13_TPTest3, all three sources):

```
S1_13_TPTest3 failed (21 m 39 s, jvgr1).
Cause: test-data row 23 `Appserver | redeploy | DDESL_002` — the redeploy did not happen ("Unable to communicate
with the target engine", deployed 0 of 1), although the step logged Passed.
Effect: rows 29–32 (Object Viewer on DDESL_002) failed — ScanStateCmd write, quality Bad, value and timestamp
mismatch; about 19 min spent waiting at rows 29–30.
Sources: TRX (outcome, error, trace), log (deploy output), test data (WASSet1.xlsx › S1_13_TPTest3), packs (WASSmokeTest, AppServer).
```

When a source is missing the summary says so and degrades: no test data → steps named by keyword only; no log →
evidence from TRX DebugTrace only; no matching pack → generic summary (failure type, failing frame, first error line).
The summary is the text used for "Copy as bug text" and the email verdict line (first two sentences).

## 8. FirReport (shape)

```
FirReport {
  Test, Outcome, Duration, Computer, RulePacks[],
  FailureType: Assertion|Exception|Timeout|SummaryCheck|Unknown,
  FailingFrame { Kind: TestCode|Init|Cleanup|Library, Method, FileLine },
  Reason? {Validation, LoggerErrors, LoggerWarnings},          // from framework pack
  Verdict,
  Root? { Row, Keyword, DataSheet, SourceFileLine, Time, StepDurationSec, Masked, Reasons[{Text,Count}] },
  Steps[ { Tag, Row, DataStep?, Evidence, Link: High|Medium|null, Why } ],
  Categories[ {Name, Count} ], FirstErrorLine?,
  Hint?, OpenPoint?, NoiseLines, SecretsFound, Signature
}
```

## 9. Separate actions (not FIR code)

1. **Rotate** the account whose password `GetUserToken` logs (raw logs, zips, and likely every TRX — including the old ones FIR will now read; redaction hides it in views, it does not remove it from disk).
2. WASSmokeTest: stop logging the password/token; `deploy`/`redeploy` must fail on `Deployed 0 of N`; `GetErrors`/`GetWarnings` must print messages.
3. Git-history credential clean-up.

---

## Phase 1 prompt (paste to start)

```
Implement Phase 1 of docs/FIRFailedTest/FIR_Implementation_Plan.md: TRX input + generic layer only.
No rule packs, no Excel, no UI. The engine must contain NO WASSmokeTest/AppServer knowledge.
Create in TestControllerGrpc.Core/Fir/: FirTrxInput (adapts TrxModels.TestResult + optional .log text),
FirGenericAnalyzer (failure type assertion/exception/timeout/summary-check; failing frame = test code /
TestInitialize / TestCleanup / library; first error-level output line; duration), FirRedactor (generic
patterns: JWT, password=/pwd=, connection-string secrets; SecurityRedactor registry only as an extra),
FirReport records, FirAnalyzer facade.
A failure thrown from TestCleanup by a summary assertion must be reported as SummaryCheck and point to the
output, not the cleanup frame.
Tests on sanitized TRX fixtures in TestControllerGrpc.Tests/TestData/Fir/ (WASSmokeTest + one other
product): report produced for each failed TRX; passed TRX -> null; malformed/empty output never throws;
no output string contains a secret. Show FirTrxInput and FirReport before the analyzer.
```
