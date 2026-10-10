# Copilot Prompt — First Information Report (FIR)

> Requirement: `FIR_Requirement.md`. Mockup: `FIR_Results_Mockup.html`.
> Read the real code first; reuse the existing TRX reader of the Results window, `SecurityRedactor`, and the
> Detail pane. Show the design before coding. Build Part A first.

---

## Part A — Core: FailureAnalyzer (no UI)

```
Add FailureAnalyzer to TestControllerGrpc.Core. Input: one test's TRX (Output/StdOut + DebugTrace +
ErrorInfo) and, if present, its .log. Output: FirReport. Pure, no UI, no writes.

1. Parse:
   - outcome, duration, reason counts from "Validation Failure Count: v, Logger ERRORS Count: e, Logger WARNINGS Count: w"
   - sheet from "(ReadValuesFromExcel) ExcelPath : ... sheet name: "X""
   - rows: split on "(ReadValuesFromExcel) *** Row No: N ***"
   - keyword lines: optional "[LineNo:L-File.cs]" + "[yyyy/MM/dd HH:mm:ss.fff] Level: (Keyword) message"
   - product lines: "Error: Failed to (deploy|undeploy) <obj> : <reason>" and
     "[SUCCESS|WARNING] (Un)Deploy Completed: (Un)Deployed a object(s) out of a total b"
   - harness noise: vstest "Test Run deployment issue ..." / "Parameter name: startIndex" -> count and drop.

2. Classify:
   - a row FAILS if it has a keyword Failed, a product Error, or a < b in a deploy count;
   - ROOT = first failing row; MASKED if its keyword said Passed (trust the counts, never the [SUCCESS] banner);
   - later failing rows = CASCADE; a later success for the same object = RECOVERED;
   - row duration >= SlowThreshold (default 180 s) = SLOW;
   - categories: Product error, Masked pass, Validation mismatch (Validate*/Compare*/WaitFor*),
     Runtime write failed (Modify*/ObjectViewer*/UserSet*), Keyword exception (exception/stack trace in
     keyword output), Logger (from reason counts; logger-only when v == 0), Slow, Harness noise.
   - group repeated product reasons with counts.

3. All patterns come from a RULE PACK file per product (rules\<Product>.fir.json), not code.

4. Redaction: mask GetUserToken lines, any "eyJ..." JWT, and SecurityRedactor-registered values in every
   string the report holds. Set SecretsFound = n so the UI can warn.

5. Shorten generated object names to their prefix (e.g. "REP1_...") and keep the full name for hover/copy.

6. Correlate with the test data: open the Excel path from the "ReadValuesFromExcel ... ExcelPath" line, sheet =
   test name; log "Row No: N" = sheet row N (cells "Component | keyword | object | params"). For every chain entry
   store ExcelStep {Row, Component, Keyword, Object, Params}. Map Excel keyword -> log keyword via the rule pack
   (deploy/redeploy -> DeployOperation; getandvalidateovvalue -> OVGetValue + ValidateOVValue; modifyovvalue ->
   ModifyOVvalue + ObjectViewerValidation). Cascade link = "same object/host as root" (High) or "later in time"
   (Medium). Include PASSED rows on the same object between root and cascade, and a passed row on another host
   that proves the environment works. If evidence has a gap, emit OpenPoint text instead of guessing.
   Read-only access to the Excel; if the file is missing, fall back to log-only (no failure).

FirReport: Verdict (one sentence), Root {Row, Keyword, Sheet, SourceFileLine, Time, RowDuration, Reasons},
Chain[ {Tag, Rows, Text} ], Categories[ {Name, Count} ], Hint (labelled), NoiseLines, SecretsFound,
Signature (normalized root reason with object names/timestamps removed - for later cross-build matching).

Tests - use the real logs as fixtures (Logs.zip from OAK_main_20261005.3/Set1):
- S1_04_SmokeTestDeploy: root Row 6, DeployOperation, AutoGalaxyConfiguration.cs:8622; reasons include
  "Platform startup failed" x2 and "Platform communication error" x12; 32 failed keyword lines; Row 32 MASKED
  (Passed but Deployed 0 of 17); Row 6 SLOW (~270 s); 64 noise lines; SecretsFound 2.
- S1_13_TPTest3: root Row 23 MASKED (Passed but Deployed 0 of 1, "Unable to communicate with the target
  engine"); cascade Rows 29-32; Row 30 SLOW (~942 s); categories Validation mismatch 3, Runtime write 2;
  SecretsFound 4. With WASSet1.xlsx: Row 23 = Appserver | redeploy | DDESL_002...; rows 29-32 are objectviewer
  steps on the same DDESL_002 (High); Row 28 shown as PASSED context; OpenPoint present.
- S1_04 with WASSet1.xlsx: Row 6 = Appserver | deploy | SmokeSet1 | true | true | false; Rows 24/25 = deploy
  ViewEngine_002/_003 hosted by REP1/REP2 (High); Row 23 ViewEngine_001 on GRP1 shown as PASSED context;
  Rows 33-34 deploy REP1/REP2 RECOVERED (16 of 16).
- Any of the 22 passed logs: no FIR, 64 noise lines.
- No output string contains the masked secrets.
Show FirReport and the rule-pack schema before coding.
```

## Part B — Save and show it

```
1. After each test result is collected, run FailureAnalyzer for failed tests and save <Test>.fir.json next to
   the TRX. Recompute on demand if missing (older runs).
2. WPF Results window, Detail pane: render the FIR at the TOP (verdict, 4 facts, ordered chain with tags,
   category chips, hint, secret warning); keep Error message / Stack trace / STDOUT / Debug trace below,
   collapsed, with secrets masked. Match FIR_Results_Mockup.html.
3. Actions: "Jump to root row" (scroll the debug trace to Row N), "Copy as bug text" (verdict + facts + chain,
   plain text, masked), "Compare with last pass" (open the same test from the previous passing build).
4. Web results page: same render from the same .fir.json.
5. Result email: add the one-sentence verdict under each failed test.
Tests: Detail pane shows the FIR for both fixture tests; copy text contains no secret; old runs without
.fir.json get one computed.
```

## Part C — Set / build view and known-vs-new (phase 2)

```
Group failed tests in a set/build by FirReport.Signature: "17 of 20 failures in Set2 share <signature>".
Compare each signature with previous builds of the same set: tag "Known since <build>" or "New".
```

## Notes for you

- Part A is testable today against the logs you sent; the expected numbers above came from running a
  prototype over them.
- Three fixes belong in the test framework, not the controller: DeployOperation must fail on "0 of N",
  GetErrors/GetWarnings must print messages, GetUserToken must stop logging the password and token.
