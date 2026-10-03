# Install Plans — Feasibility Analysis

> **Question:** can one TestController pipeline install any release, any monthly patch, or any upgrade path,
> pick the matching installer, response files and framework binaries automatically, and run the same tests?
>
> **Short answer: yes, feasible.** Most building blocks already exist. Two small engine features are missing
> (choosing settings at run time, and repeating an install block per step). Everything can be proven on
> real files **before any code is written**, using the script in Appendix B.

Document status: feasibility only — no implementation started. Code freeze respected.
Revision 2: answers to Q1–Q10 applied (section 7).

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

---

## 3. What we know about the real environment

### Build share `\\DevTFSBldoaksp\REPL\Ado`

| Folder | Meaning | Catalog use |
|---|---|---|
| `SP-2023-R2-SP1` | SP1 release builds | base build (one pinned GA folder inside) |
| `SP2023R2SP1-P03`, `SP2023R2SP1-P04` | SP1 monthly patches | `patchRoot = …\SP2023R2SP1-P{nn}\` |
| `SP-2023-R2-SP2` | SP2 release builds | base build (pinned GA folder) |
| `SP` | **SP2026** builds | base build (pinned GA folder) |
| `SP2023R2-Meta-Patch` | unknown | open question Q6 |
| `SP-2023`, `SP-2023-R2` | older lines | not in scope |

Naming differs: releases use hyphens (`SP-2023-R2-SP1`), patches do not (`SP2023R2SP1-P04`). The catalog holds a
separate pattern per release, so this is not a problem.

### Controller JVGR22

| Item | State |
|---|---|
| `C:\TestSetup\FrameworkBinaries\SP2023R2SP2` | present, 1,685 files, 6.2 GB, validated |
| `C:\TestSetup\FrameworkBinaries\SP2026` | present, 1,584 files, 6.3 GB, validated (also for SP2026 R2) |
| `C:\TestSetup\Common\SmokeTest` | shared tests in place; not yet in the repo |
| Install files per release | partly in place; patch and SP2026 R2 response files still to be supplied |
| Free disk | **~10 GB** (old `C:\TestBinaries*` copies still present, ~12 GB reclaimable) |

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
| `Initialize Plan="auto"` + `InstallPlan` node that repeats the install block per step | M | 2 |
| Pre-flight checks + plan builder dialog | M | 3 |
| Trigger-file plans + pool queue | M | 4 |

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
| Controller disk at ~10 GB | Failures mid-run | Delete old `C:\TestBinaries*` after first green run (frees ~12 GB) |
| Test binaries incompatible with an upgraded product | False failures | Binaries always from the final release (R5) |
| SP2026 R2 location and patch folder names unconfirmed | SP2026 patch / R2 plans cannot be resolved | Fill when the build team confirms (Q7); `-Check` will show them as FAIL until then |
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
<InstallPlan StepTemplate="InstallStepWarm" />
<Ref TemplateID="SuiteWarm" />
<Ref TemplateID="SuitePsr" />
```

Adding a suite = add one template + one `Ref`. Feasible today with the existing template mechanism (no new engine feature).

### 7.2 What is still blocking

| Blocker | Blocks | Owner |
|---|---|---|
| GA build folders not decided (Q4) | Pinned bases for all releases | Release decision |
| SP2026 R2 location and SP2026 / R2 patch folder names (Q7) | SP2026 patch plans, SP2026 R2 plans | Build team |
| Patch and SP2026 R2 response files | Patch, upgrade and SP2026 R2 steps | You |
| WARM test and PSR run commands | `SuiteWarm`, `SuitePsr` templates | You |

**Ready now:** SP2023 R2 SP1 (+P04) and SP2023 R2 SP2 plans, including SP1 → SP2 upgrades — once their GA folders are pinned.

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

- [ ] `-Check` ends with **RESULT: ALL CHECKS PASSED**
- [ ] SP1 shows **Patches found: P03, P04**
- [ ] SP2026 / SP2026 R2 entries show FAIL only for the items still open in 7.2
- [ ] `-AllUseCases` shows the expected steps for every row in section 1
- [ ] A disallowed upgrade is refused (`-Plan "SP2026R2>SP2026/P01"`)

The script logic was tested against a simulated catalog: all plans resolved correctly, patch folders were
discovered, and a disallowed upgrade was refused. It has not yet run against the real share.

---

## 9. Decision

- [x] Questions Q1–Q10 answered (revision 2)
- [ ] Blockers in 7.2 resolved (GA folders, SP2026 R2 / patch names, response files, suite commands)
- [ ] Catalog filled, `-Check` green on real paths
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

**Phase 2 — InstallPlan node**
```
New WatchList node <InstallPlan StepTemplate="..."/> repeating the template per plan step with _StepBuild,
_StepInstaller, _StepResponse, _StepLabel; tags "Step n/N - <label>". <Initialize Plan="auto" Profile="..."/>
loads the plan (trigger keys Plan/From/To or dialog) and sets _ReleaseName and _BinariesFolder from the final
release. Expansion saved in the session snapshot; a failed step stops later steps; validator knows the node.
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
