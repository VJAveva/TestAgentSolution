---
name: triage-failed-run
description: 'Triage a failed test build: pull the FIR (First Information Report) for every failed test, group failures by root cause, separate new vs recurring, and draft bug text. Use for failed run, triage build, why did tests fail, FIR report, draft bug, failure analysis.'
argument-hint: 'Build number (folder name under ResultsRootPath)'
---

# Triage a Failed Run

## Sources
- Controller API (also served by the web tier on :81): `http://JVGR22:5200`, called with `-UseDefaultCredentials -UseBasicParsing`.
  - `GET /api/results/builds`: list builds.
  - `GET /api/results/builds/{build}/detail`: every test with its outcome.
  - `GET /api/results/builds/{build}/test/{testName}/fir[?refresh=true]`: the FIR report. Cached as `<ResultsRootPath>\<build>\<Test>.fir.json` (default root `C:\TestResults`).
  - `GET /api/results/analyze/{testName}`: cross-build failure signature, to tell new failures from recurring ones.
- Offline fallback: read the `*.fir.json` files directly from the build folder.

## FIR fields used
`FailureType` (Unknown / Assertion / Exception / Timeout / SummaryCheck), `Verdict`, `Summary`, `Root` (the first failing row: step, object, reason, product error), `Rows` (each tagged ROOT / CASCADE / PASSED / RECOVERED / masked), `Categories`, `RulePacks`, `DataWarning`, `FirstErrorLine`, `SecretsFound`.

## Procedure
1. List the failed tests (outcome Failed / Timeout / Aborted) from `/detail`. If there are none, say so and stop.
2. Fetch the FIR for each one. Use `refresh=true` only if a cached report predates a rule-pack change or the user asks.
   - A 404 means the host doesn't have the feature, or the TRX is missing. Report it; don't guess.
   - If `DataWarning` is set, the workbook correlation was rejected. Root and cascade are still valid, but say the data link is unverified.
3. **Group by root cause.** Key = `FailureType` + root **reason**, normalised (strip numbers, GUIDs, timestamps, paths and host names) + root **operation/object class**.
   - **Report the ROOT, never a cascade.** A `SummaryCheck` failure raised in cleanup points at the summary assert; the real cause is the `Root` row.
   - Masked roots (failing row with no failing step, product error only) still define the group.
   - Merge groups whose normalised reason matches across tests, e.g. `Communication error: Platform communication error` x12 is **one** group.
4. **New vs recurring**: for one representative test per group, call `/analyze/{test}`. Mark the group *recurring (since build X)* or *new in this build*.
5. **Separate environment from product.** Flag groups that are infrastructure, not product bugs: agent lost or rebooted mid-run (see skill `diagnose-agent-offline`; ManageEngine reboots), missing binaries, pre-flight-class problems, timeouts during a revert.
   **Don't draft product bugs for these**; list them as environment issues with the owner.
6. **Draft bug text** for each product group (new groups first):
   ```
   Title: [<Area>] <normalised reason> during <operation> (<N> tests, build <build>)
   Build / environment: <build>, agents <list>, release <_ReleaseName>
   Affected tests: <test list> (<new | recurring since X>)
   Root step: row <n> <keyword> <object> -> <reason>
   Product error: <first product error line>
   Cascade: <count> later failures caused by the root (rows ...)
   Evidence: FIR <path>, TRX <path>, first error line <...>
   Repro: <from test data sheet: keyword + params of the root row>
   Expected / Actual: <from the step>
   ```
   Keep quoted log excerpts short, and **run them through the `sanitize-logs` rules**. Never quote tokens or passwords. A FIR with `SecretsFound > 0` means the raw log carries secrets.
7. Do not file bugs in ADO and do not edit FIR caches. Drafts only.

## Report
1. Headline: failed count, group count, how many groups are new, how many are environment.
2. Group table: group, type, tests, new/recurring, root reason, product vs environment.
3. Bug drafts (product groups).
4. Environment issues with a suggested owner.
5. Gaps: tests with no FIR, data warnings, unknown failure types.
