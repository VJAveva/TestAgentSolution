# Requirement: Global Variables (single-release build for all pipelines)

> Status: requirement + scope, for review. No code yet.
> Platform: TestAgentSolution, controller JVGR22.

## 1. Purpose

Set one build (Build Number + Drop Location) in one place and have **every pipeline in a
single-release WatchList** use it, without editing each pipeline or dropping a trigger file per pipeline.

Triggered by the **"Apply Build to All Pipelines"** button in the Global Variables dialog.

## 2. When it applies (scope)

| WatchList | Use Global Variables? | Why |
|---|---|---|
| **Single release, many pipelines** (all pipelines target one release) | **Yes** | One build is valid for all of them. This is the feature. |
| **Multi-release** (e.g. the Smoke WatchList: 3 pipelines, 3 releases) | **No** | A build belongs to one release; you can't install an SP2026 build on an SP2023R2SP2 pipeline. Those pipelines get their build from the trigger file or their own config. |

So Global Variables is **opt-in by how you built the WatchList**, not forced on every run.

## 3. The guard that keeps it safe

The global build carries a **`_ReleaseName`**. Two rules make it impossible to push a build into the wrong release:

1. **Release match** — the global build is applied to a pipeline **only if the pipeline's release equals `_ReleaseName`**. A mismatch is ignored (that pipeline falls back to its own config).
2. **Trigger wins** — a trigger file's build always overrides the global build (see precedence below). So in the multi-release Smoke run, where you drop trigger files carrying the build, the trigger build wins and the global build is simply not used.

Together: single-release WatchList → all pipelines match → all use the global build. Multi-release WatchList → trigger builds win → global build stays out of the way.

## 4. The file

One file: `C:\TestControllerService\Parameters\GlobalVariables.json`

```json
{
  "Version": 1,
  "_ControllerName": "JVGR22",
  "_EmailCheck": "vinodkumar.javvadi@aveva.com",
  "_ReleaseName": "SP2023R2SP2",
  "_BuildNumber": "OAK_SP-2023-R2-SP2_20260909.4",
  "_DropLocation": "\\\\DevTFSBldoaksp\\REPL\\Ado\\SP-2023-R2-SP2\\OAK_SP-2023-R2-SP2_20260909.4"
}
```

- `_ControllerName`, `_EmailCheck` — constants, same for every release. (The live key is `_ControllerName`, not `_Controller`.)
- `_ReleaseName` + `_BuildNumber` + `_DropLocation` — the currently selected build.
- Single source of truth. No copies written into the per-release `pipeline-config.json` files (no fan-out, no clobbering).

## 5. What "Apply Build to All Pipelines" does

1. Read Drop Location (and derive Build Number + Release from it, as the dialog already shows).
2. Validate: drop location exists and is readable.
3. Write the five values above to `GlobalVariables.json`. **That is the whole action** — one file saved.
4. Show a confirmation naming the release and build that are now live.

It does **not** loop over or rewrite the release `pipeline-config.json` files.

## 6. Where it sits in resolution (precedence)

Higher wins:

```
Release pipeline-config Global   (release paths, installers, fallback build)
      -> Profile (Sanity / Warm / Single)
      -> Pipeline pin
      -> GlobalVariables.json     (only if _ReleaseName matches the pipeline's release)
      -> Trigger file             (overrides the global build for a one-off)
      -> Run override
```

## 7. Rules and constraints

- Global build is applied to a pipeline only on a release match; otherwise ignored.
- A trigger file build always wins over the global build.
- If `GlobalVariables.json` is missing or has no build, every pipeline uses its own config / trigger (today's behaviour — nothing breaks).
- The file is validated on save and on load; a bad file is reported, not silently used.
- Pre-flight shows the resolved Build Number and Drop Location per pipeline, so you can see whether the global build or a trigger build is in effect before running.

## 8. Out of scope (for now)

- Multiple saved global builds / history (one active build only).
- Any change to how multi-release WatchLists run.
- Applying different builds to different releases from one screen (that is the per-release config, not this feature).

## 9. Decision: Apply is always enabled (resolved)

Earlier draft proposed disabling Apply on a multi-release WatchList. That is dropped: the live WatchList
spans three releases, so a disable would turn the button off on the only WatchList in use.

**Apply stays always enabled.** Safety is the release-match guard (section 3), not a disabled button:
on a multi-release WatchList, only the matching-release pipelines take the global build, the rest keep
their own, and any trigger build still wins.

## 10. Implementation reality (from the code review)

- `GlobalVariables.json` is **not read on the execution path today** — only by the validator and the WPF
  token display. This feature wires it into the run paths for the first time, through the shared loader
  funnel (not per call site). That is the real regression surface.
- It is applied **after** Initialize (a pipeline's `_ReleaseName` is known only once its per-release config
  loads). Rank 35 — not load order — decides precedence.
- Pre-existing bug found separately: the "SP2026R2 - WARM GR Setup" pipeline's Initialize points at a config
  file that does not exist; fix its `ParameterFile` to the renamed per-use-case config.
