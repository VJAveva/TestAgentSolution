# Requirement: First Information Report (FIR) for test failures

> Status: requirement, built from the real logs of OAK_main_20261005.3 / Set1 (24 tests, 2 failed).
> Mockup: `FIR_Results_Mockup.html`. Prompt: `FIR_Copilot_Prompt.md`.

## 1. Purpose

When a test fails, show **one short report at the top of the Detail pane** that says:
- **what really caused it** (the first real failure, not the first red line),
- **where** — sheet, Excel row, keyword, source file:line, time,
- **what followed from it** (cascade), grouped into categories,
- with the 64 lines of harness noise and any secrets hidden.

The raw Error message, Stack trace, STDOUT and Debug trace stay below it, collapsed.

## 2. What the real logs showed (why this matters)

| Finding | Evidence |
|---|---|
| **The first red line is often not the cause.** | S1_13_TPTest3: first `Failed:` is Row 29, but the cause is Row 23 — `DeployOperation` logged **Passed** while the product said *Deployed 0 of 1, Unable to communicate with the target engine*. |
| **One cause produces many failures.** | S1_04_SmokeTestDeploy: 32 failures, all from *Platform startup failed* on 2 platforms at Row 6. |
| **The product banner can lie.** | `[SUCCESS] Deploy Completed: Deployed 0 object(s) out of a total 17` — trust the counts, not the banner. |
| **Waiting on a cascade wastes time.** | Row 30 of TPTest3 waited 15 m 42 s for a quality that could never arrive. |
| **Every log has the same 64 lines of noise.** | vstest "assembly not found" / "satellite assemblies" deployment warnings. |
| **Logger counts are printed, the logger messages are not.** | "1 Errors, 14 Warnings on jvgr1" — the actual messages are not in the log. |
| **Credentials are written to the logs.** | `GetUserToken` prints the account name + password and the access token (2 lines in S1_04, 4 in S1_13). |

## 3. What the FIR shows

1. **Verdict** — one sentence: root cause, where, how many failures follow from it.
2. **Facts** — Where (row + keyword + sheet), Source (file:line), When (+ row duration), Impact (N failures → 1 cause).
3. **What happened, in order** — rows tagged ROOT / MASKED / CASCADE / SLOW / RECOVERED, each with a one-line reason. Click a row → jump to it in the log.
4. **Categories** — counts per category (see §4).
5. **Hint** — pattern-based, clearly labelled as a hint, never stated as fact.
6. **Actions** — Jump to root row · Copy as bug text · Compare with last pass.
7. **Secret warning** — if the log contained credentials (hidden in all views).

## 4. Categories

| Category | Detected by |
|---|---|
| **Product error** | Product output lines: `Error: Failed to deploy X : <reason>`, deploy/undeploy count `a of b` with a < b |
| **Masked pass** | Keyword logged `Passed` but the same row has a product error or `a of b` with a < b |
| **Validation mismatch** | `Failed:` from Validate*/Compare*/WaitFor* keywords (expected vs actual, quality, timestamp) |
| **Runtime write failed** | `Failed:` from Modify*/ObjectViewer*/UserSet* keywords |
| **Keyword exception** | Exception text or stack trace inside the keyword output (a bug in the test code, not the product) |
| **Logger errors / warnings** | The `Logger ERRORS / WARNINGS Count` in the reason; **logger-only failure** when Validation count is 0 (e.g. S2_10: 0 validation, 11 warnings) |
| **Slow / timeout** | Row duration above a threshold (default 3 min) |
| **Harness noise** | Known vstest deployment warnings — counted, hidden |

## 5. Rules (how the root is chosen)

1. Split the debug trace into **rows** (`ReadValuesFromExcel … Row No: N`).
2. A row is **failing** if it has a keyword `Failed:`, a product `Error:` line, or a count `a of b` with a < b.
3. The **root** is the **first failing row**, even when its keyword said Passed (that row is tagged MASKED).
4. Later failing rows are **CASCADE**; a later successful retry of the same object is **RECOVERED**.
5. Repeated product reasons are grouped with counts (`Platform communication error ×12`).
6. Long generated object names are shortened to their prefix (`REP1_…`, `DDESL_002_…`) with the full name on hover/copy.
7. All patterns live in a **rule pack** file per product, not in code, so other product teams can add their own.

### 5a. Correlation with the test data (now phase 1 — confirmed on WASSet1.xlsx)

- **Join key:** the log's `Row No: N` is **row N of the sheet named after the test** (`S1_13_TPTest3`) in the Excel path printed by `ReadValuesFromExcel`. Each row is `Component | keyword | object | params…`.
- Every FIR line shows **both**: the Excel step (`Appserver · redeploy · DDESL_002`) and the log evidence for that row.
- **Keyword names differ** between Excel and log — keep a mapping in the rule pack, e.g. `deploy`/`redeploy` → `DeployOperation`; `getandvalidateovvalue` → `OVGetValue` + `ValidateOVValue`; `modifyovvalue` → `ModifyOVvalue` + `ObjectViewerValidation`.
- **Cascade link strength:** *same object or same host platform as the root* = high confidence; *only later in time* = medium. Show the reason under each line ("same platform as the root").
- **Show passing rows that matter:** a passed step on the same object between root and cascade (TPTest3 row 28), or on another host that proves the environment is fine (S1_04 row 23 on GRP1), is shown as PASSED context.
- **Open point:** when the evidence has a gap (row 28 passed, row 29 failed, nothing in between), say so in a separate box — never fill it with a guess.
- Region labels in the sheets are sometimes copy-pasted (`Connect To Galaxy` above Object Viewer steps), so the FIR shows the keyword, not the region name.

## 6. Security (must)

- Mask credentials in every FIR view, the Detail pane, emails and exports: the `GetUserToken` lines, any `eyJ…` token, and values already registered with `SecurityRedactor`.
- Separately (test framework, not the controller): stop `GetUserToken` logging the password and token, and **rotate that account's password**. The same text is probably also in the `.trx` files (the Detail pane STDOUT/Debug trace comes from them) and in `Logs.zip` attachments.

## 7. Where it runs

- **FailureAnalyzer** in Core: input = TRX (stdout/debug trace) + `.log`; output = `FirReport`.
- Computed **once after each test** and saved next to the TRX as `<Test>.fir.json`, so WPF, Web and the result email all read the same report.
- WPF Detail pane and Web results page render it at the top.

## 8. Later phases

| Phase | Adds |
|---|---|
| 2 | **Set/build view:** group failed tests by root-cause signature ("17 of 20 Set2 failures share *Platform communication error*") |
| 2 | **Known vs new:** compare the signature with previous builds ("seen in 20260830.1" / "new in this build") |
| 3 | **Logger messages:** show the actual logger errors/warnings (needs the framework to print them, or the controller to read the agent's logger for the test's time window) |

## 9. Framework fixes this exposes (WASSmokeTest)

1. `DeployOperation` / `UndeployOperation` should **fail** when the product reports `Deployed 0 of N` (today it passes — S1_13).
2. `GetErrors` / `GetWarnings` should **print the messages**, not only the counts.
3. `GetUserToken` must **not** log the password or the token.
