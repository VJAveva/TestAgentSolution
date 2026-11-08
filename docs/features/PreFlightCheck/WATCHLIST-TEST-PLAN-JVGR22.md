# Test Plan — WatchList, Token Resolution, Template Context and Pre-flight (JVGR22)

**Covers:** release-driven WatchList (6 pipelines, shared templates), per-release `pipeline-config.json`,
Profile fix, unresolved-token checks, no-resolved-values-on-save, template pipeline context, node-selection fix,
pre-flight check, stop-on-failure stages, password redaction.

**How to use:** work top to bottom. Tick each box, write the result in the last column. Stop at the first FAIL in
sections 3–6 and report it before continuing. Sections 1–5 do **not** touch any test machine.

---

## 0. Before you start (10 min)

- [ ] Back up on JVGR22 into `C:\TestBackup\<date>\`:
  `C:\TestControllerService\WatchList.xml`, `C:\TestControllerService\Parameters\`, both `appsettings.json` (controller + web)
- [ ] Trigger folders exist: `C:\Triggers\SP2023R2SP2`, `SP2026`, `SP2026R2`
- [ ] `C:\TestSetup\Install\SP2023R2SP2\Base\` contains `Install-Build.bat` **and** `ResponseFiles\`
- [ ] `C:\TestSetup\FrameworkBinaries\SP2023R2SP2\` contains `WASSmokeTest.dll` and `DEPENDENCIES\`
- [ ] `C:\TestSetup\Common\SmokeTest\` contains Run-WASTests.bat, TestCaseList.json, ParseTestList.ps1, Copy-DependentBinaries.bat
- [ ] Web `appsettings.json` lists all 10 agents (incl. WARMGR, WARMPRI, WARMBAK, WARMHIST); app pool stopped/started
- [ ] Controller started; Fleet shows 10 agents online

**Rollback (any time):** close controller → restore the backup files → start controller.

---

## 1. Editor — static checks (no runs)

| # | Do | Expected | Result |
|---|---|---|---|
| 1.1 | Open WatchList in the editor | Validator: **0 errors** (warnings listed and understood) | |
| 1.2 | Expand all 6 pipelines | Each has: Initialize first → stage group (Revert, Prep, Install, Smoke/none) → Sending Email | |
| 1.3 | SP2023R2SP2 Sanity → PrepSanity → any action | Resolved Parameters: `\\jvgr22\c$\...`, `SP2023R2SP2`, real agent names | |
| 1.4 | SP2026 Sanity → same action | Resolved shows **SP2026** values, not SP2023R2SP2 | |
| 1.5 | WARM pipeline → any install action | `[_Agent1]` resolves to **warmgr** (Warm profile), not jvgr1 | |
| 1.6 | Sanity pipeline → same template action | `[_Agent1]` resolves to **jvgr1** | |
| 1.7 | Sending Email (each release) | To = your address; Title shows the right release | |
| 1.8 | Click around, **Save**, compare file with backup (`fc` or WinMerge) | **No difference**: tokens still `[_Agent1]`, never `jvgr1` | |

## 2. Node selection (Part 1)

| # | Do | Expected | Result |
|---|---|---|---|
| 2.1 | Templates → PrepSanity: click every node top to bottom | Node Properties follows **every** click | |
| 2.2 | Repeat, clicking a Test Plans node in between | Still correct; right-click menu matches the selected node | |
| 2.3 | Repeat in PrepWarm and InstallSanity_Single | Same | |

## 3. Template context (Part 2)

| # | Do | Expected | Result |
|---|---|---|---|
| 3.1 | Templates → PrepSanity, no context | Tokens shown **unresolved**; header has no chip | |
| 3.2 | Right-click a child node → Execute → pick SP2023R2SP2 Sanity → **Cancel at pre-flight** | Header chip `Context: SP2023R2SP2 - Sanity` | |
| 3.3 | Click every PrepSanity node | All show resolved Parameters **and** Command for SP2023R2SP2 | |
| 3.4 | Change context → SP2026 Sanity | All nodes update to SP2026 values | |
| 3.5 | Set a different context on PrepWarm | PrepSanity keeps its own context | |
| 3.6 | Context offered list | Only pipelines that Ref that template | |
| 3.7 | Reload WatchList | Contexts cleared | |

## 4. Pre-flight "Check only" (Part 3) — no run starts

Positive:

| # | Do | Expected | Result |
|---|---|---|---|
| 4.1 | Check only: SP2023R2SP2 Sanity | All PASS (or only understood WARN); secrets masked | |
| 4.2 | Check only: SP2023R2SP2 WARM | All PASS | |
| 4.3 | Check only: SP2026 / SP2026R2 | FAIL only for items known to be open (response files, build path) | |
| 4.4 | Report shows token table | Each token with value + source (Global/Profile/Pipeline/Trigger) | |

Negative — make one change, Check only, then **undo** it:

| # | Break | Expected FAIL | Result |
|---|---|---|---|
| 4.5 | Rename `C:\Triggers\SP2023R2SP2` | Trigger folder missing | |
| 4.6 | Rename `...\Base\Install-Build.bat` | Installer missing (controller source) | |
| 4.7 | Remove `_EmailCheck` from the SP2023R2SP2 json | Unresolved token `[_EmailCheck]` | |
| 4.8 | Rename the SP2023R2SP2 `pipeline-config.json` | Parameter file missing | |
| 4.9 | Change `Profile="Sanity"` to `Profile="Sanityx"` (copy of WatchList) | Profile not found | |
| 4.10 | Rename `FrameworkBinaries\SP2023R2SP2\WASSmokeTest.dll` | Binaries incomplete | |
| 4.11 | Start a run on one pipeline, Check only the same pipeline from the other client | Locked by <user> | |

- [ ] Every negative case **undone** and 4.1 passes again

## 5. Trigger-file behaviour (no machine touched when it fails)

| # | Do | Expected | Result |
|---|---|---|---|
| 5.1 | Break 4.6 again, drop `WarmInstall.txt` into `C:\Triggers\SP2023R2SP2\` | Run **not started**; email with the pre-flight report | |
| 5.2 | Undo the break | — | |

## 6. Small real runs (harmless steps first)

| # | Do | Expected | Result |
|---|---|---|---|
| 6.1 | Node run: "Settle 4 minutes" inside a pipeline | Pre-flight passes; runs on Controller; green | |
| 6.2 | Node run: "Copy Prepare-Agent.bat on warmgr" inside WARM pipeline | Copies; log shows real paths | |
| 6.3 | Template run (context SP2023R2SP2 WARM): same copy node | Same result as 6.2 | |
| 6.4 | Check the controller log for these runs | **No password** visible (`popcorn` absent) | |

## 7. Full runs

| # | Run | Watch for | Result |
|---|---|---|---|
| 7.1 | **SP2023R2SP2 – WARM 4 Nodes Install Only** | Reverts start and succeed; prep copies; GR first then others; email arrives | |
| 7.2 | Force a failure: before a WARM run, rename `Install-Build.bat` *after* pre-flight is impossible — instead cancel during Prep | Later stages **not run**; email still sent | |
| 7.3 | **SP2023R2SP2 – Sanity 5 Nodes Smoke E2E** | Revert → prep → install → Set1–3 in parallel → email; results in Results viewer | |
| 7.4 | After 7.3, Results / Report Card | Build appears with correct release name | |

## 8. Regression (existing features still work)

| # | Check | Result |
|---|---|---|
| 8.1 | Skip a node → Execute → blocked with `[Skip]` message; Include again works | |
| 8.2 | Apply to agents dialog opens and counts correctly (Cancel) | |
| 8.3 | Live status glyphs visible left of node names during 7.1 | |
| 8.4 | Web client: pipelines listed, trigger, live progress, all 10 agents | |
| 8.5 | WPF and web cannot run the same pipeline at the same time | |
| 8.6 | Light / Dark / High Contrast: editor and previews readable | |

---

## 9. Exit criteria

- [ ] Sections 1–6: all PASS
- [ ] 7.1 and 7.3 green end to end
- [ ] 8.x: no regressions
- [ ] Backup kept until two more successful days of runs

## 10. Reporting a failure to Copilot (short)

```
Test <number> failed on JVGR22.
Did: <what you clicked/changed>
Expected: <from the plan>
Got: <what happened> + log lines / screenshot
Investigate root cause and fix; red-prove; tell me what to re-test.
```

## 11. Optional — let Copilot run the read-only parts

```
Read-only verification on JVGR22, no runs: for all 6 pipelines run the pre-flight "Check only" (API or code path),
validate the WatchList, and confirm that saving the WatchList without edits leaves the file byte-identical.
Report a table: pipeline -> PASS/WARN/FAIL counts -> open items. Do not change any file.
```
