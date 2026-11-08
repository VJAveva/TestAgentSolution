# Install Plans — Feasibility Analysis

> **Question:** can one TestController pipeline install any release, any monthly patch, or any upgrade path,
> pick the matching installer, response files and framework binaries automatically, and run the same tests?
>
> **Short answer: yes, feasible.** Most building blocks already exist. Two small engine features are missing
> (choosing settings at run time, and repeating an install block per step). Everything can be proven on
> real files **before any code is written**, using the script in Appendix B.

Document status: feasibility only — no implementation started. Code freeze respected.
Revision 2: answers to Q1–Q10 applied (section 7).
Revision 3 (2026-10-04): section 3 replaced with **measured** values; the `InstallPlan` node and `_Step*`
tokens are withdrawn in favour of `Ref ForEachPlanStep` + baked literals (7.3); forward-compatibility guard
added (7.4). The engine verdict is unchanged; the **artefact** blockers are larger than revision 2 assumed.

---

## 1. Requirement

| # | Requirement |
|---|---|
| R1 | Pick a build/release; the pipeline uses that release's installer, response files and framework binaries |
| R2 | Optionally pick a patch on top; the patch installer and patch response file are used automatically |
| R3 | Monthly patches appear without editing pipelines |
| R4 | Upgrades: install a source release (+ patch), upgrade to a target release (+ patch) |
| R5 | Same tests for every release; framework binaries follow the **final** release |
| R6 | Three or more releases run one after another on the same pool without manual work |

### Use cases

| Use case | Plan | Install steps |
|---|---|---|
| SP2023 R2 SP1 + latest patch | `SP2023R2SP1/P04` | SP1 base → P04 (cumulative) |
| SP2023 R2 SP2 | `SP2023R2SP2` | SP2 base |
| SP2023 R2 SP2 P01 (monthly) | `SP2023R2SP2/P01` | SP2 base → P01 |
| SP2026 | `SP2026` | SP2026 base |
| SP2026 P01 (monthly) | `SP2026/P01` | SP2026 base → P01 |
| SP2026 R2 | `SP2026R2` | SP2026 R2 base |
| Upgrade SP1 base → SP2 P01 | `SP2023R2SP1>SP2023R2SP2/P01` | SP1 base → upgrade SP2 → P01 |
| Upgrade SP1 P04 → SP2 P01 | `SP2023R2SP1/P04>SP2023R2SP2/P01` | SP1 base → P04 → upgrade SP2 → P01 |
| Upgrade SP2 → SP2026 P01 | `SP2023R2SP2>SP2026/P01` | SP2 base → upgrade SP2026 → P01 |
| Upgrade SP1 P04 → SP2026 P01 | `SP2023R2SP1/P04>SP2026/P01` | SP1 base → P04 → upgrade SP2026 → P01 |
| Upgrade SP1 P04 → SP2026 R2 | `SP2023R2SP1/P04>SP2026R2` | SP1 base → P04 → upgrade SP2026 R2 |

Patches are cumulative, so a plan installs only the chosen patch (P04 already contains P01–P03).

---

## 2. Proposed model (one paragraph)

Every run is an **Install Plan**: an ordered list of steps (Base, Patch, Upgrade), then tests. Each step's build,
installer and response file come from **one catalog file** (`release-catalog.json`). Installation is **one shared
block** (GR first, restart, other nodes in parallel) repeated once per step. Tests use the framework binaries of the
**final** release. New monthly patches are found by folder name (`…-P{nn}`), so nothing is edited when one appears.

The repeat is expressed on the **existing** `Ref` node — `<Ref TemplateID="InstallStep" ForEachPlanStep="true"/>` —
and each repetition is produced by cloning the template and **substituting that step's values as literals**, on the
run copy only. No new node type, and no `_Step*` tokens exist at run time. Rationale and blast-radius measurement
in 7.3.

---

## 3. What we know about the real environment

> Everything in this section was **measured on 2026-10-04**, not inherited. Revision 2 carried several
> values that no longer hold; the rows that changed are marked **(was: …)**.

### Build share `\\DevTFSBldoaksp\REPL\Ado`

Directories actually present:

| Folder | Meaning | Catalog use |
|---|---|---|
| `SP-2023-R2-SP1` | SP1 release builds | base build (one pinned GA folder inside) |
| `SP2023R2SP1-P03`, `SP2023R2SP1-P04` | SP1 monthly patches | `patchRoot = …\SP2023R2SP1-P{nn}\` |
| `SP-2023-R2-SP2` | SP2 release builds | base build (pinned GA folder) |
| `SP` | **SP2026** builds | base build (pinned GA folder) |
| `SP2023R2-Meta-Patch` | not needed (Q6) | excluded |
| `SP-2023`, `SP-2023-R2` | older lines | not in scope |

**The only patch folders on the share are `SP2023R2SP1-P03` and `SP2023R2SP1-P04`.** There is no
`SP2023R2SP2-P*`, no `SP2026-P*`, and no SP2026 R2 root of any name. This is not the naming question Q7
assumed — those artefacts are not published yet. Consequence for section 1: **6 of the 11 use cases cannot
resolve today** (every row containing `/P01`, plus both SP2026 R2 rows).

Naming differs: releases use hyphens (`SP-2023-R2-SP1`), patches do not (`SP2023R2SP1-P04`). The catalog holds a
separate pattern per release, so this is not a problem.

### Controller JVGR22

| Item | State |
|---|---|
| `C:\TestSetup\FrameworkBinaries\SP2023R2SP2` | present, **1,685 files, 6.1 GB** |
| `C:\TestSetup\FrameworkBinaries\SP2026` | present, **1,584 files, 6.1 GB** (also for SP2026 R2) |
| `C:\TestSetup\Install\` | **`Common`, `SP2023R2SP2`, `SP2026`, `SP2026R2`** — there is **no `SP2023R2SP1`** |
| `C:\TestSetup\Common\SmokeTest` | shared tests in place; not yet in the repo |
| `C:\TestControllerService\Parameters\` | only `SP2023R2SP2\pipeline-config.json` and `SP2026\pipeline-config.json` |
| Free disk | **22.9 GB of 149.4 GB** *(was: ~10 GB)* |
| `C:\TestBinaries*` | **`TestBinaries26`, 0.0 GB** — already cleaned *(was: ~12 GB reclaimable)* |

**This contradicts revision 2's "Ready now" claim.** SP1 has patch *builds* on the share but **no installer or
response files on the controller**, so no SP1 plan can run. The genuinely runnable set today is
**`SP2023R2SP2` and `SP2026` fresh installs** — exactly what the two existing `pipeline-config.json` files
already cover.

---

## 4. Feasibility per requirement

| # | Requirement | Verdict | Reason |
|---|---|---|---|
| R1 | Release → installer, response, binaries | **Feasible now (config)** | Tokens + per-release parameter files already work. Automatic choice from the build needs Phase 1. |
| R2 | Patch on top | **Feasible now (config)** for a fixed list; automatic with Phase 1–2 | A patch step is a second install block with patch tokens. |
| R3 | Monthly patches without edits | **Needs Phase 1** | Folder discovery (`P{nn}`) is new; proven by the script in Appendix B. |
| R4 | Upgrades | **Feasible, needs Phase 2 for a single pipeline** | Upgrade = install with the upgrade response file; ordering already supported by sequential groups. |
| R5 | Same tests, final release binaries | **Feasible now** | Common SmokeTest + `Prepare-Agent.bat` 3rd argument already implemented. Suites per pool are templates (7.1). |
| R6 | Releases one after another | **Partly now, fully with Phase 4** | Agent locks prevent collisions today but a second run fails instead of waiting; a queue is new. |

### What already exists and is reused

| Existing capability | Used for |
|---|---|
| WatchList templates + `Ref` | One install block, written once |
| Parameter files + `Initialize Profile` + tokens | Per-release values |
| Sequential/parallel groups, GR-first pattern, `IsReboot` | Install order and restarts |
| Node-level skip (enforced, shows "Skipped") | Steps that do not apply |
| Agent locks and pipeline locks | No two runs on one pool |
| Fleet revert templates | Clean machines before each plan |
| Results per build, report card, email | Reporting per plan |

### What is missing (engine work)

| Gap | Size | Phase |
|---|---|---|
| Catalog + plan resolver (steps from release/patch/upgrade choice, patch discovery) | M | 1 |
| `Ref ForEachPlanStep` expansion with baked literals (7.3) | S | 2 |
| `Initialize Plan="auto"` — one string property, same shape as the existing `Profile` | S | 2 |
| `MinEngine` forward-compatibility guard (7.4) | S | 2 |
| Pre-flight checks + plan builder dialog | M | 3 |
| Trigger-file plans + pool queue | M | 4 |

Phase 2 dropped from **M** to **S** by withdrawing the new node type — see 7.3 for the measurement that
justifies it.

---

## 5. Two ways to deliver

| | Option A — configuration only (now) | Option B — Install Plans feature (after freeze) |
|---|---|---|
| How | One short WatchItem per plan and pool, shared templates, one parameter file per release | Two pipelines total; plans chosen at run time |
| Covers | R1, R2, R5 for a fixed list of plans | R1–R6 |
| Monthly patch | Edit the parameter file (patch number) | Automatic |
| Upgrade | One WatchItem per upgrade path | Any allowed path |
| Code change | None | Phases 1–4 |
| Effort | Small | 4 focused Copilot sessions + review |

**Recommendation:** use **Option A as the bridge** during the code freeze for the plans you need now, and build
**Option B** afterwards. The templates written for A carry straight into B.

---

## 6. Risks and how to handle them

| Risk | Impact | Mitigation |
|---|---|---|
| Patch assumed cumulative but is not | Missing fixes in tests | Decide Q1; catalog flag `patchesCumulative` installs P01…Pnn in order if false |
| Base build not pinned ("newest") | Different base every run | Pin the exact GA folder per release in the catalog |
| Upgrade installer behaves differently from fresh install | Upgrade fails | Separate `upgradeResponse` (and optional `upgradeInstaller`) per release; test each path once |
| 6 GB of binaries copied to each agent every run | Long prep time, agent disk | Measure on first run; option later: bake release binaries into snapshots |
| Long upgrade plans (4 installs × reboots) | 4–6 h per run | Run upgrades on a schedule; queue (Phase 4) runs them unattended |
| Controller disk | Failures mid-run | **Resolved** — measured 22.9 GB free on 2026-10-04; `TestBinaries26` is already empty |
| Test binaries incompatible with an upgraded product | False failures | Binaries always from the final release (R5) |
| SP2/SP2026 patch builds and the SP2026 R2 root **do not exist on the share** | 6 of 11 use cases cannot resolve | Build team must publish them; `-Check` shows FAIL until then (7.2) |
| **An older controller silently ignores unknown XML attributes** | After a rollback a 3-step plan runs **once**, with no error | `MinEngine` guard — refuse to load rather than under-run (7.4) |
| A new node type must be taught to every tree walker | One missed `case` = silent wrong behaviour | Withdrawn — reuse `Ref` instead (7.3) |
| Suites grow (WARM, PSR, more) | Pipelines get long | Suites as separate templates (section 7.1) |
| Base folder not yet decided | Unrepeatable results | Pin GA folders before the first unattended run (Q4) |

---

## 7. Answers and their impact

| # | Question | Answer | Impact on the design |
|---|---|---|---|
| Q1 | Patches cumulative? | **Yes** | Install only the chosen patch. `patchesCumulative: true`. |
| Q2 | Upgrade installer | **Same `Install-Build.bat`** with an upgrade response file | `upgradeInstaller` stays empty; every upgrade target needs `upgradeResponse`. |
| Q3 | Patch installer arguments | **Same order** as Install-Build.bat | One install block serves base, patch and upgrade steps. |
| Q4 | GA build folders | SP2026 and SP2026 R2 builds are in `SP`; **GA folders not decided** | **Blocker for pinning bases.** Pin when decided; until then plans cannot run unattended. |
| Q5 | SP1 P01/P02 needed? | **No, P04 is enough** | SP1 plans use P04 only. Share already has P03, P04. |
| Q6 | SP2023R2-Meta-Patch | **Not needed** | Excluded from the catalog. |
| Q7 | SP2026 R2 location, SP2026/R2 patch folder names | **Not confirmed** | **Blocker for SP2026 patches only.** SP2026 fresh plans unaffected. |
| Q8 | SP1 binaries | Use the **same binaries as SP2** for now | `binaries` of SP1 → `FrameworkBinaries\SP2023R2SP2`. *Confirm this reading.* |
| Q9 | Pools and suites | Sanity = install + smoke; **WARM = install + WARM tests + PSR**; **more suites coming** | Tests must be **pluggable suites** per pool (section 7.1). |
| Q10 | More upgrade paths | **SP1 → SP2026**, **SP1 → SP2026 R2** (plus SP1→SP2, SP2→SP2026) | `upgradesFrom` lists extended; SP2026 R2 now needs an `upgradeResponse`. |

### 7.1 Design change from Q9: test suites as building blocks

The test stage becomes a list of **suites**, each one template, so adding a suite never touches the install logic.

| Suite | Pool | Template |
|---|---|---|
| Smoke (Set1–Set3) | Sanity | `SuiteSmoke` |
| WARM test cases | WARM | `SuiteWarm` |
| PSR | WARM | `SuitePsr` |
| Future suites | any | `Suite<Name>` |

Each pool's pipeline lists its suites after the install plan:

```xml
<Ref TemplateID="InstallStepWarm" ForEachPlanStep="true" />
<Ref TemplateID="SuiteWarm" />
<Ref TemplateID="SuitePsr" />
```

Adding a suite = add one template + one `Ref`. Feasible today with the existing template mechanism (no new engine feature).

### 7.2 What is still blocking

Rewritten in revision 3 against measured state (section 3). The first two rows are larger than revision 2
assumed: the artefacts do not exist, so no amount of configuration reaches them.

| Blocker | Blocks | Owner |
|---|---|---|
| **No `SP2023R2SP2-P*` or `SP2026-P*` folder exists on the share** | `SP2023R2SP2/P01`, `SP2026/P01` and the 4 upgrade plans ending in a patch — **6 of 11 use cases** | Build team |
| **No SP2026 R2 root on the share** | `SP2026R2`, `SP2023R2SP1/P04>SP2026R2` | Build team |
| **No `C:\TestSetup\Install\SP2023R2SP1`** (installer + response files) | every SP1 plan, including SP1→SP2 upgrades | You |
| GA build folders not decided (Q4) | pinned bases for all releases — nothing is repeatable without this | Release decision |
| Patch and SP2026 R2 response files | patch, upgrade and SP2026 R2 steps | You |
| WARM test and PSR run commands | `SuiteWarm`, `SuitePsr` templates | You |

**Runnable today:** `SP2023R2SP2` and `SP2026` **fresh installs** only, once their GA folders are pinned.
Revision 2's "SP1 (+P04) and SP2 plans are ready now" was wrong — SP1 has no install files on the controller.

### 7.3 Design change: repeat via `Ref`, not a new node type

Revision 2 proposed a new `IActionNode` (`<InstallPlan StepTemplate="…"/>`) plus `_StepBuild` / `_StepInstaller`
/ `_StepResponse` tokens. **Both are withdrawn.**

**Why.** `RefConfig` was the last node type added. It is handled by **26 `switch` sites across 15 files** —
parser (parse + serialize), `PipelineExecutorBase` (×6), `NodeAddressing` (×2), `WatchListValidator` (×2),
`SnapshotExpander`, `TemplateUsage`, `PreflightRunner`, `AgentResolver`, `IActionPipelineExecutor`,
`WatchListController`, `TreeNodeViewModel` (×4), `WatchBuilderNodes`, `MainViewModel.TemplateCrud` — plus
`[JsonDerivedType]`, `DeepClone`, the `NodeKinds` icon/label maps and the TypeScript tree. Every one of those
fails **silently** when a node type is missed, and on 2026-10-04 exactly that happened: `AgentResolver` had no
`case RefConfig`, so a Ref-built pipeline reserved **no agents at all** — the Fleet showed nine idle machines
during a live install and a second pipeline could have dispatched on top of it. A new node type buys 26 more
chances at that bug.

**Instead:** one bool on the existing `RefConfig`.

```xml
<Ref TemplateID="InstallStep" ForEachPlanStep="true" />
```

| Property | Why it holds |
|---|---|
| All 26 sites still see a `RefConfig` | they keep working unchanged — no new silent-miss surface |
| `RefConfig.DeepClone()` is `MemberwiseClone` | the new property is copied automatically — this is the documented fix for the dropped `Skip` and `Profile` bugs |
| Already a `[JsonDerivedType]` | session snapshot and JSON round-trip unaffected |
| Parser cost | one line read, one line written, in the single shared `ParseChildren` / serializer |
| Agent locking | the Ref→template walk added on 2026-10-04 already resolves the agents — correct for free |

**Bake literals, do not invent per-node tokens.** Parameters are per-**run**, not per-**node**, so three steps
sharing one template would all resolve `[_StepBuild]` to the same value. Expansion therefore clones the
template's actions and substitutes that step's values **as literals** into the clone. Two consequences, both
good:

- no `_Step*` token exists at run time, so `ParameterResolver.FindUnresolvedTokens` — the "fail loudly" gate
  added on 2026-10-04 — needs **no special case**. Left as tokens, it would fail every plan pipeline in
  pre-flight;
- expansion happens on the **run copy only**, never the authored tree, which preserves both
  "saving never writes resolved tokens" and the positional node paths (`e0/c2/c1`) that `NodeAddressing` and
  retry depend on. `SnapshotExpander` already establishes this precedent and says so explicitly.

**Forced ordering:** resolve plan → expand → **pre-flight** → revert → run. Pre-flight then validates every
step's real installer and response path, which is most of Phase 3 for free.

Two items deferred to detailed design (after the two green runs and the snapshots): how retry addresses a
failed step inside an expanded plan, and whether a single step can be skipped independently.

### 7.4 Forward-compatibility guard

`WatchListXmlParser.ParseChildren` reads known attributes and **silently ignores the rest**. So a controller
rolled back to today's build would read `ForEachPlanStep="true"`, not understand it, and run a three-step
install **once** — wrong, silent, and indistinguishable from success until the test results look odd. The same
hazard applies to `Initialize Plan="auto"`.

The guard: a version stamp the parser checks **before** building any node.

```xml
<WatchList MinEngine="2">
```

| Rule | Behaviour |
|---|---|
| Attribute absent | treated as `1` — every existing WatchList loads exactly as today |
| `MinEngine` ≤ engine's supported version | load normally |
| `MinEngine` > supported | **refuse to load**, keep the previous config, surface one clear error naming the required version |

Refusing is the right failure: a pipeline that silently under-runs an install is worse than one that does not
start. The writer stamps `MinEngine` only when the file actually uses a newer feature, so files that avoid
plan features stay loadable by any build and no existing file changes.

---

## 8. How to prove feasibility without spending tokens

1. Save Appendix A as `C:\TestControllerService\Parameters\release-catalog.json`; fill every `<...>`.
2. Save Appendix B as `Test-InstallPlan.ps1` in the same folder.
3. Run on JVGR22:

```powershell
cd C:\TestControllerService\Parameters
.\Test-InstallPlan.ps1 -Check           # every path, patches found, binaries
.\Test-InstallPlan.ps1 -AllUseCases     # all plans, step by step, PASS/FAIL per file
.\Test-InstallPlan.ps1 -Plan "SP2023R2SP1/P04>SP2023R2SP2/P01"
```

**Feasibility is proven when:**

The gate is now **two-stage**, because section 3 showed that most releases have no artefacts to check.

Stage 1 — what can pass today (fill the catalog for **SP2023R2SP2 and SP2026 only**):

- [ ] `-Check` passes for those two releases: base build, installer, fresh response, binaries
- [ ] `-Plan "SP2023R2SP2"` and `-Plan "SP2026"` resolve to a single BASE step with all paths PASS
- [ ] A disallowed upgrade is refused (`-Plan "SP2026R2>SP2026/P01"`)
- [ ] SP1 shows **Patches found: P03, P04** — proves folder discovery works end to end

Stage 2 — blocked until the build team publishes (7.2); expected to FAIL until then, and that is the
correct reading, not a script fault:

- [ ] SP2 / SP2026 patch roots exist → the 4 `/P01` plans resolve
- [ ] SP2026 R2 root exists → both R2 plans resolve
- [ ] `C:\TestSetup\Install\SP2023R2SP1` exists → every SP1 plan resolves
- [ ] `-AllUseCases` shows the expected steps for all 11 rows in section 1

The script logic was tested against a simulated catalog: all plans resolved correctly, patch folders were
discovered, and a disallowed upgrade was refused. It has not yet run against the real share.

---

## 9. Decision

- [x] Questions Q1–Q10 answered (revision 2)
- [x] Environment measured, not inherited (revision 3, section 3)
- [x] Design agreed: `Ref ForEachPlanStep` + baked literals replace the `InstallPlan` node and `_Step*` tokens (7.3)
- [x] Forward-compatibility guard agreed: `MinEngine` (7.4)
- [ ] Build team confirms SP2/SP2026 patch publishing, the SP2026 R2 location and the GA folders (7.2)
- [ ] SP1 installer + response files placed on the controller (7.2)
- [ ] Catalog filled for SP2023R2SP2 + SP2026, stage-1 gate green (section 8)
- [ ] Option A bridge agreed for the freeze period
- [ ] Option B approved for after the freeze → continue with Appendix C, one phase per session

---

## Appendix A — `release-catalog.json` (known values filled)

```json
{
  "schemaVersion": 1,
  "releases": [
    {
      "id": "SP2023R2SP1",
      "display": "SP2023 R2 SP1",
      "baseBuild": "\\\\DevTFSBldoaksp\\REPL\\Ado\\SP-2023-R2-SP1\\<GA build folder>",
      "installer": "C:\\TestSetup\\Install\\SP2023R2SP1\\Base\\<Install-Build.bat>",
      "freshResponse": "C:\\TestSetup\\Install\\SP2023R2SP1\\Base\\<fresh response file>",
      "upgradeInstaller": "",
      "upgradeResponse": "",
      "patchRoot": "\\\\DevTFSBldoaksp\\REPL\\Ado\\SP2023R2SP1-P{nn}\\",
      "patchInstaller": "C:\\TestSetup\\Install\\SP2023R2SP1\\Patch\\<Install-Patch.bat>",
      "patchResponse": "C:\\TestSetup\\Install\\SP2023R2SP1\\Patch\\<patch response file>",
      "patchesCumulative": true,
      "binaries": "C:\\TestSetup\\FrameworkBinaries\\SP2023R2SP2",
      "upgradesFrom": []
    },
    {
      "id": "SP2023R2SP2",
      "display": "SP2023 R2 SP2",
      "baseBuild": "\\\\DevTFSBldoaksp\\REPL\\Ado\\SP-2023-R2-SP2\\<GA build folder>",
      "installer": "C:\\TestSetup\\Install\\SP2023R2SP2\\Base\\<Install-Build.bat>",
      "freshResponse": "C:\\TestSetup\\Install\\SP2023R2SP2\\Base\\<fresh response file>",
      "upgradeInstaller": "",
      "upgradeResponse": "C:\\TestSetup\\Install\\SP2023R2SP2\\Base\\<upgrade response file>",
      "patchRoot": "\\\\DevTFSBldoaksp\\REPL\\Ado\\SP2023R2SP2-P{nn}\\",
      "patchInstaller": "C:\\TestSetup\\Install\\SP2023R2SP2\\Patch\\<Install-Patch.bat>",
      "patchResponse": "C:\\TestSetup\\Install\\SP2023R2SP2\\Patch\\<patch response file>",
      "patchesCumulative": true,
      "binaries": "C:\\TestSetup\\FrameworkBinaries\\SP2023R2SP2",
      "upgradesFrom": ["SP2023R2SP1"]
    },
    {
      "id": "SP2026",
      "display": "SP2026",
      "baseBuild": "\\\\DevTFSBldoaksp\\REPL\\Ado\\SP\\<GA build folder>",
      "installer": "C:\\TestSetup\\Install\\SP2026\\Base\\<Install-Build.bat>",
      "freshResponse": "C:\\TestSetup\\Install\\SP2026\\Base\\<fresh response file>",
      "upgradeInstaller": "",
      "upgradeResponse": "C:\\TestSetup\\Install\\SP2026\\Base\\<upgrade response file>",
      "patchRoot": "<SP2026 patch folder pattern, e.g. \\\\DevTFSBldoaksp\\REPL\\Ado\\SP2026-P{nn}\\ - confirm Q7>",
      "patchInstaller": "C:\\TestSetup\\Install\\SP2026\\Patch\\<Install-Patch.bat>",
      "patchResponse": "C:\\TestSetup\\Install\\SP2026\\Patch\\<patch response file>",
      "patchesCumulative": true,
      "binaries": "C:\\TestSetup\\FrameworkBinaries\\SP2026",
      "upgradesFrom": ["SP2023R2SP2", "SP2023R2SP1"]
    },
    {
      "id": "SP2026R2",
      "display": "SP2026 R2",
      "baseBuild": "\\\\DevTFSBldoaksp\\REPL\\Ado\\SP\\<SP2026 R2 GA build folder - confirm Q4/Q7>",
      "installer": "C:\\TestSetup\\Install\\SP2026R2\\<Install-Build.bat>",
      "freshResponse": "C:\\TestSetup\\Install\\SP2026R2\\<fresh response file>",
      "upgradeInstaller": "",
      "upgradeResponse": "C:\\TestSetup\\Install\\SP2026R2\\<upgrade response file>",
      "patchRoot": "",
      "patchInstaller": "",
      "patchResponse": "",
      "patchesCumulative": true,
      "binaries": "C:\\TestSetup\\FrameworkBinaries\\SP2026",
      "upgradesFrom": ["SP2023R2SP1"]
    }
  ]
}
```

---

## Appendix B — `Test-InstallPlan.ps1` (read-only prototype)

Save with Windows line endings (CRLF). Runs on Windows PowerShell 5.1. Changes nothing.

```powershell
<#
.SYNOPSIS
  Prototype + readiness check for Install Plans (fresh, patch, upgrade).
  Reads release-catalog.json, checks every path, and shows the exact install
  steps a plan would run. Read-only: changes nothing. No Copilot needed.

.EXAMPLES
  # 1. Check the whole catalog (all paths, patches found, binaries)
  .\Test-InstallPlan.ps1 -CatalogPath C:\TestControllerService\Parameters\release-catalog.json -Check

  # 2. Show one plan
  .\Test-InstallPlan.ps1 -Plan "SP2023R2SP2/P01"
  .\Test-InstallPlan.ps1 -Plan "SP2023R2SP1/P04>SP2023R2SP2/P01"     # upgrade: from > to

  # 3. Resolve all agreed use cases
  .\Test-InstallPlan.ps1 -AllUseCases

  Exit code: 0 = all OK, 1 = something missing or invalid, 2 = cannot read catalog.
#>
[CmdletBinding()]
param(
    [string]$CatalogPath = (Join-Path $PSScriptRoot 'release-catalog.json'),
    [switch]$Check,
    [string]$Plan,
    [switch]$AllUseCases
)

$ErrorActionPreference = 'Stop'
$script:Problems = 0

$UseCases = @(
    'SP2023R2SP1/P04',
    'SP2023R2SP2', 'SP2023R2SP2/P01',
    'SP2026', 'SP2026/P01',
    'SP2026R2',
    'SP2023R2SP1>SP2023R2SP2/P01',
    'SP2023R2SP1/P04>SP2023R2SP2/P01',
    'SP2023R2SP2>SP2026/P01',
    'SP2023R2SP1/P04>SP2026/P01',
    'SP2023R2SP1/P04>SP2026R2'
)

function Write-Result([bool]$Ok, [string]$Text) {
    if ($Ok) { Write-Host ("  [PASS] " + $Text) -ForegroundColor Green }
    else     { Write-Host ("  [FAIL] " + $Text) -ForegroundColor Red; $script:Problems++ }
}

function Test-Filled([string]$Value) {
    return (-not [string]::IsNullOrWhiteSpace($Value)) -and ($Value -notmatch '<.*>')
}

function Test-PathItem([string]$Label, [string]$PathValue) {
    if (-not (Test-Filled $PathValue)) { Write-Result $false ("{0}: not filled in" -f $Label); return $false }
    $ok = Test-Path -LiteralPath $PathValue
    Write-Result $ok ("{0}: {1}" -f $Label, $PathValue)
    return $ok
}

function Get-Release($Catalog, [string]$Id) {
    $r = $Catalog.releases | Where-Object { $_.id -eq $Id }
    if (-not $r) { throw ("Unknown release '{0}'. Known: {1}" -f $Id, (($Catalog.releases | ForEach-Object { $_.id }) -join ', ')) }
    return $r
}

function Get-PatchFolder($Release, [int]$Number) {
    if (-not (Test-Filled $Release.patchRoot)) { return $null }
    $nn = '{0:D2}' -f $Number
    return $Release.patchRoot.Replace('{nn}', $nn)
}

function Get-NewestBuild([string]$Folder) {
    # Newest build subfolder inside a patch folder; the folder itself if it has none.
    if (-not (Test-Path -LiteralPath $Folder)) { return $null }
    $sub = Get-ChildItem -LiteralPath $Folder -Directory -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($sub) { return $sub.FullName }
    return $Folder
}

function Find-Patches($Release) {
    $found = @()
    if (-not (Test-Filled $Release.patchRoot)) { return $found }
    for ($i = 1; $i -le 24; $i++) {
        $f = Get-PatchFolder $Release $i
        if (Test-Path -LiteralPath $f) { $found += ('P{0:D2}' -f $i) }
    }
    return $found
}

function New-Step([string]$Kind, [string]$Label, [string]$Build, [string]$Installer, [string]$Response) {
    return [pscustomobject]@{ Kind = $Kind; Label = $Label; Build = $Build; Installer = $Installer; Response = $Response }
}

function Add-PatchSteps($Release, [string]$PatchId, [System.Collections.ArrayList]$Steps) {
    if (-not $PatchId) { return }
    $n = [int]($PatchId.TrimStart('P', 'p'))
    $numbers = @($n)
    if (-not $Release.patchesCumulative) { $numbers = 1..$n }
    foreach ($k in $numbers) {
        $folder = Get-PatchFolder $Release $k
        if (-not $folder) { throw ("Release {0} has no patchRoot, so {1} is not possible" -f $Release.id, $PatchId) }
        $build = Get-NewestBuild $folder
        if (-not $build) { $build = $folder + '  (NOT FOUND)' }
        [void]$Steps.Add((New-Step 'PATCH' ("{0} - P{1:D2}" -f $Release.display, $k) $build $Release.patchInstaller $Release.patchResponse))
    }
}

function Resolve-Plan($Catalog, [string]$PlanText) {
    # Grammar: "<release>[/Pnn]"  or  "<from>[/Pnn]><to>[/Pnn]"
    $steps = New-Object System.Collections.ArrayList
    $parts = $PlanText.Split('>')
    if ($parts.Count -gt 2) { throw "Use at most one '>' (from>to)." }

    $toText = $parts[$parts.Count - 1]
    $toRel = Get-Release $Catalog ($toText.Split('/')[0])
    $toPatch = $null; if ($toText.Contains('/')) { $toPatch = $toText.Split('/')[1] }

    if ($parts.Count -eq 2) {
        $fromText = $parts[0]
        $fromRel = Get-Release $Catalog ($fromText.Split('/')[0])
        $fromPatch = $null; if ($fromText.Contains('/')) { $fromPatch = $fromText.Split('/')[1] }
        if (@($toRel.upgradesFrom) -notcontains $fromRel.id) {
            throw ("Upgrade {0} -> {1} is not allowed: add '{0}' to upgradesFrom of {1} if it is supported." -f $fromRel.id, $toRel.id)
        }
        [void]$steps.Add((New-Step 'BASE' ($fromRel.display + ' - base') $fromRel.baseBuild $fromRel.installer $fromRel.freshResponse))
        Add-PatchSteps $fromRel $fromPatch $steps
        $upInstaller = $toRel.installer
        if (Test-Filled $toRel.upgradeInstaller) { $upInstaller = $toRel.upgradeInstaller }
        [void]$steps.Add((New-Step 'UPGRADE' ('Upgrade to ' + $toRel.display) $toRel.baseBuild $upInstaller $toRel.upgradeResponse))
        Add-PatchSteps $toRel $toPatch $steps
    }
    else {
        [void]$steps.Add((New-Step 'BASE' ($toRel.display + ' - base') $toRel.baseBuild $toRel.installer $toRel.freshResponse))
        Add-PatchSteps $toRel $toPatch $steps
    }
    return [pscustomobject]@{ Plan = $PlanText; Steps = $steps; Binaries = $toRel.binaries; Final = $toRel.display }
}

function Show-Plan($Resolved) {
    Write-Host ""
    Write-Host ("PLAN: {0}   (final release: {1})" -f $Resolved.Plan, $Resolved.Final) -ForegroundColor Cyan
    $i = 0; $n = $Resolved.Steps.Count
    foreach ($s in $Resolved.Steps) {
        $i++
        Write-Host ("  Step {0}/{1}  [{2}]  {3}" -f $i, $n, $s.Kind, $s.Label) -ForegroundColor White
        [void](Test-PathItem '    Build    ' ($s.Build -replace '  \(NOT FOUND\)$', ''))
        [void](Test-PathItem '    Installer' $s.Installer)
        [void](Test-PathItem '    Response ' $s.Response)
    }
    Write-Host "  Tests" -ForegroundColor White
    Test-Binaries $Resolved.Binaries
}

function Test-Binaries([string]$Folder) {
    if (Test-PathItem '    Binaries ' $Folder) {
        Write-Result (Test-Path -LiteralPath (Join-Path $Folder 'WASSmokeTest.dll')) '    WASSmokeTest.dll present'
        Write-Result (Test-Path -LiteralPath (Join-Path $Folder 'DEPENDENCIES')) '    DEPENDENCIES folder present'
    }
}

function Invoke-CatalogCheck($Catalog) {
    $ids = $Catalog.releases | ForEach-Object { $_.id }
    foreach ($r in $Catalog.releases) {
        Write-Host ""
        Write-Host ("RELEASE {0}" -f $r.id) -ForegroundColor Cyan
        [void](Test-PathItem 'Base build      ' $r.baseBuild)
        [void](Test-PathItem 'Installer       ' $r.installer)
        [void](Test-PathItem 'Fresh response  ' $r.freshResponse)
        if (@($r.upgradesFrom).Count -gt 0) {
            [void](Test-PathItem 'Upgrade response' $r.upgradeResponse)
            if (Test-Filled $r.upgradeInstaller) { [void](Test-PathItem 'Upgrade installer' $r.upgradeInstaller) }
            foreach ($u in $r.upgradesFrom) { Write-Result ($ids -contains $u) ("upgradesFrom '{0}' exists in catalog" -f $u) }
        }
        if (Test-Filled $r.patchRoot) {
            [void](Test-PathItem 'Patch installer ' $r.patchInstaller)
            [void](Test-PathItem 'Patch response  ' $r.patchResponse)
            $p = Find-Patches $r
            Write-Result ($p.Count -gt 0) ("Patches found: {0}" -f ($(if ($p.Count) { $p -join ', ' } else { 'none' })))
        }
        else { Write-Host "  [INFO] No patches defined for this release" -ForegroundColor DarkGray }
        Test-Binaries $r.binaries
    }
}

# ---------------- main ----------------
try {
    $catalog = Get-Content -LiteralPath $CatalogPath -Raw | ConvertFrom-Json
}
catch {
    Write-Host ("Cannot read catalog '{0}': {1}" -f $CatalogPath, $_.Exception.Message) -ForegroundColor Red
    exit 2
}

if (-not ($Check -or $Plan -or $AllUseCases)) { $Check = $true }

if ($Check) { Invoke-CatalogCheck $catalog }

$plans = @()
if ($Plan) { $plans += $Plan }
if ($AllUseCases) { $plans += $UseCases }
foreach ($p in $plans) {
    try { Show-Plan (Resolve-Plan $catalog $p) }
    catch { Write-Host ""; Write-Result $false ("PLAN {0}: {1}" -f $p, $_.Exception.Message) }
}

Write-Host ""
if ($script:Problems -eq 0) { Write-Host "RESULT: ALL CHECKS PASSED" -ForegroundColor Green; exit 0 }
Write-Host ("RESULT: {0} problem(s) - fix the [FAIL] lines above" -f $script:Problems) -ForegroundColor Yellow
exit 1
```

---

## Appendix C — Build prompts (after the freeze, one per Copilot session)

**Token rules:** one phase per session; attach this file and the filled catalog only; ask for the design first;
reply with short numbered approve / change / drop lines.

**Phase 1 — Catalog + plan resolver**
```
Implement Install Plans phase 1 per this document. Port Test-InstallPlan.ps1 (Resolve-Plan, Add-PatchSteps,
Get-NewestBuild, upgrade check) to a C# PlanResolver in Core reading Parameters\release-catalog.json.
Output: ordered steps {Kind Base|Patch|Upgrade, Label, Build, Installer, Response} + final binaries.
Invalid plan -> clear error. Tests: all plans in section 1 give the same steps as the script. Design first.
```

**Phase 2 — plan expansion**
```
Add bool RefConfig.ForEachPlanStep (parse + serialize in WatchListXmlParser only; DeepClone is MemberwiseClone
so it copies itself). On the RUN CLONE, expand such a Ref once per plan step: clone the template's actions and
substitute that step's Build/Installer/Response as LITERALS, tag "Step n/N - <label>". No new node type and no
_Step* tokens - every existing switch over IActionNode keeps working unchanged (see 7.3).
Also: string InitializeConfig.Plan ("auto") loading the plan from trigger keys Plan/From/To or the dialog, and
setting _ReleaseName and _BinariesFolder from the FINAL release; mirror the expansion in SnapshotExpander so the
dashboard shows every step; add the MinEngine guard from 7.4.
REGRESSION BAR: a Ref WITHOUT ForEachPlanStep must produce a byte-identical node tree, and a current WatchList
must parse->serialize byte-identically. Red-proof by flipping the flag on a fixture.
```

**Phase 3 — Pre-flight + plan builder**
```
Pre-flight before revert = the checks of Test-InstallPlan.ps1 -Plan; any FAIL stops before touching machines.
WPF plan builder: Fresh/Upgrade tabs, release + discovered patch chips, resolved steps with pass/fail per file,
Save plan (Parameters\plans.json), Add to queue, Run.
```

**Phase 4 — Trigger-file plans + pool queue**
```
Trigger file may carry Plan=Fresh|Upgrade, From=<id>/<Pnn>, To=<id>/<Pnn>. One run per pool, FIFO queue,
visible in WPF and web, cancellable, persisted across controller restart.
```
