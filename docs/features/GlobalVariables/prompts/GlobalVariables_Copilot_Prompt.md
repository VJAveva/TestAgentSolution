# Copilot Prompt — Global Variables (single-release build for all pipelines)

> Mockup: `GlobalVariables_Dialog_Mockup.html`. Requirement: `GlobalVariables_Requirement.md`.
> Read the real code first; reuse ParameterResolver, the Initialize flow, the WatchList model and the
> existing Global Variables dialog. Show the design before coding. No fan-out: one file is the source of truth.
>
> **Reality check (confirmed against live code/data):**
> - `GlobalVariables.json` is **not read at run time today**. `WatchListConfig.GlobalVariablesFile` reaches a
>   `PipelineExecutionContext` only in `WatchListValidator.cs` (~280-281, rank Global) and the WPF token
>   display (`MainViewModel.Helpers.cs:224`). The executor, all three `ExecutionController` trigger paths,
>   `PreflightRunner` and the web node-run never load it, and the attribute is empty in both deployed
>   WatchLists. So Part A is wiring a new parameter source into the execution paths for the FIRST time.
> - The real controller key is **`_ControllerName`** (`[_ControllerName]` x30 in the deployed WatchList;
>   `[_Controller]` x0). `_ReleaseName` is real (Global layer of all four configs, referenced 14x).
> - `_ControllerName` / `_EmailCheck` are identical across all four configs, so hoisting them to the global
>   file changes no value today.

---

## Part A — Wire GlobalVariables.json into the execution paths (the real work)

```
Add GlobalVariables.json as a parameter source that is actually LOADED on every run. It is read nowhere
on the execution path today, so this is new wiring, not a one-line rank add. Do it in the SHARED funnel
(TryLoadInitializeSource / LoadForWatchItem), NOT as N copy-pasted call sites.

1. File: C:\TestControllerService\Parameters\GlobalVariables.json
   Shape: { "Version":1, "_ControllerName", "_EmailCheck", "_ReleaseName", "_BuildNumber", "_DropLocation" }.
   All keys optional; missing file = no-op. Validate JSON; a bad file is reported, not silently used.

2. New precedence rank GlobalVars = 35 (the enum spacing already leaves room; no renumbering):
   Global(10) -> ParameterFile(20) -> PipelinePin(30) -> GlobalVars(35) -> TriggerFile(40) -> RunOverride(50).

3. Apply through the one shared funnel so EVERY path picks it up: the executor, all three ExecutionController
   trigger paths, PreflightRunner, and the web node-run. Confirm no path loads parameters outside that funnel.

4. Load AFTER Initialize (a pipeline's _ReleaseName lives inside its per-release config, so it is known only
   after the Initialize source loads). Order does not affect precedence - rank 35 decides that - it only
   means GlobalVars cannot be the first thing loaded.

5. Release-match guard: _BuildNumber / _DropLocation / _ReleaseName apply ONLY when GlobalVariables._ReleaseName
   equals the pipeline's resolved _ReleaseName. Every other key (_ControllerName, _EmailCheck) applies
   unconditionally. If either side has no _ReleaseName, skip the three build keys (can't prove a match -> don't
   touch the build). A trigger-file build always wins (Trigger 40 > GlobalVars 35).

Tests: single-release pipeline picks up the global build; a different-release pipeline ignores it; a trigger
build overrides it; missing/invalid file leaves resolution unchanged; the SAME funnel feeds executor,
all trigger paths, PreflightRunner and web node-run (one source, not N). Show the funnel change + rank table first.
```

## Part B — Dialog: workflow help + move Apply to the bottom

```
Update the Global Variables dialog to match GlobalVariables_Dialog_Mockup.html.

1. Add a "How this works" info panel at the top (3 numbered steps + the trigger-override note + the
   release-match safety note). Static text, accent-tinted panel.
2. MOVE "Apply Build to All Pipelines" OUT of the body into a bottom action bar:
   [Apply Build to All Pipelines]   (short note)        [Save Variables File] [Close]
   Apply is the primary (accent) button on the left; Save/Close on the right.
3. Bind the Variables File to GlobalVariables.json; show its path. Mark _ReleaseName / _BuildNumber /
   _DropLocation as "from build" (filled by Apply, read-only); _ControllerName / _EmailCheck stay editable.
4. Build Number + Release are derived from the Drop Location (as today), shown read-only.
```

## Part C — Apply Build to All Pipelines (the action)

```
Wire "Apply Build to All Pipelines":

1. Validate: Drop Location exists and is readable; derive _BuildNumber and _ReleaseName from it. Block with a
   clear message if invalid.
2. Write _ControllerName, _EmailCheck, _ReleaseName, _BuildNumber, _DropLocation to GlobalVariables.json
   (atomic: temp file + move; keep a .bak). Do NOT touch any per-release pipeline-config.json.
3. Confirm, naming the release and build now live, e.g.
   "SP2023R2SP2 build OAK_SP-2023-R2-SP2_20260909.4 is now the global build. Pipelines of other releases are
    unaffected."
4. Apply is ALWAYS enabled (no multi-release disable). Safety is the Part A release-match guard: on a
   WatchList that spans releases, only the matching-release pipelines take the global build; the rest keep
   their own, and any trigger build still wins. (This replaces the earlier "disable on multi-release" idea,
   which would have disabled Apply on the only WatchList in use.)

Tests: Apply saves exactly one file with the five keys; no pipeline-config.json is modified; invalid drop
location is blocked; on the live 3-release Smoke WatchList, setting an SP2026 global build affects only the
SP2026 pipeline and leaves the SP2023R2SP2 / SP2026R2 pipelines untouched; after Apply, pre-flight shows the
resolved Build Number / Drop Location per pipeline. Show the file-write before coding.
```

---

## Also fix (pre-existing bug, unrelated but found during review)

```
WatchItem "SP2026R2 - WARM GR Setup" Initialize points at
  C:\TestControllerService\Parameters\SP2026R2\pipeline-config.json   (does not exist)
Real file is the renamed per-use-case config, e.g.
  C:\TestControllerService\Parameters\SP2026R2\SP2026R2-WARM-pipeline-config.json
Fix the ParameterFile path (confirm the exact folder/name on disk). Today that pipeline fails at Initialize
before any action dispatches.
```

## Notes for you

- Build Part A first - it is the regression surface. Once the funnel loads the file, B and C are UI.
- The live 3-release Smoke WatchList is safe: its trigger files carry the build (rank 40 > 35), so a global
  build is ignored there unless a pipeline's release matches AND no trigger build is supplied.
- `GlobalVariables.json` sample (with `_ControllerName`) is in the same folder.
