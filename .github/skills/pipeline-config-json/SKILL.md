---
name: pipeline-config-json
description: 'Create or update a layered <Release>-<UseCase>-pipeline-config.json (global / profiles / pipelines) from the template, validate it, keep cross-release constants in GlobalVariables.json, and repoint the WatchItem Initialize ParameterFile. Use for new release config, parameter file, pipeline-config, profile, build pin.'
argument-hint: 'Release (e.g. SP2026R2) and use case (e.g. WARM, Sanity)'
---

# Pipeline Config JSON

## Locations
- Template: `docs/Requirements/pipeline-config.sample.json`
- Schema: `docs/Requirements/pipeline-config.schema.json`
- Live: `C:\TestControllerService\Parameters\<Release>\<Release>-<UseCase>-pipeline-config.json` (e.g. `SP2026R2\SP2026R2-WARM-pipeline-config.json`)
- Constants: `C:\TestControllerService\Parameters\GlobalVariables.json` (sample: `docs/GlobalVariables/GlobalVariables.json`)
- Loader: `ParameterResolver.LoadJsonConfig`. Precedence is by **rank**, not file order: Global 10 < Profile 20 < PipelinePin 30 < GlobalVars 35 < TriggerFile 40 < RunOverride 50.

## Shape
```json
{
  "version": 1,
  "global":    { "_ReleaseName": "SP2026R2", "_BinariesFolder": "...", "_VCloudPassword": "__SET_VCLOUD_PASSWORD__" },
  "profiles":  { "Warm": { "_Agent1": "warmgr", "_AgentsGroup": "\"warmgr,warmpri\"" } },
  "pipelines": { "<exact WatchItem Tag>": {} }
}
```

## Procedure
1. Copy the template, set `version: 1`, and fill `global`, `profiles` and `pipelines`.
2. **Where each key goes**
   - Keys that are identical in every release (`_ControllerName`, `_EmailCheck`) go in **GlobalVariables.json**, not here.
   - `_ReleaseName` **must** stay in this file's `global`. The GlobalVariables release guard reads it, and if it is missing then `_BuildNumber`, `_DropLocation` and `_ReleaseName` from GlobalVariables are skipped.
   - `_BuildNumber` and `_DropLocation` normally come from GlobalVariables or the trigger. Pin one under `pipelines[tag]` only when the user wants that pipeline held on a specific build.
   - Agent hostnames go in `profiles`.
3. **Secrets**: use placeholders (`__SET_..._PASSWORD__`) in anything committed. Real values live only in the deployed file. Never print them when reading the live file.
4. **Validate**
   - **Schema**: `docs/Requirements/pipeline-config.schema.json`. Root has only `version` (const 1), `global` (required), `profiles` and `pipelines`. Every layer key matches `^_[A-Za-z0-9_]+$`, and every value is a string. `pipelines` keys are 1 to 36 characters.
     PS 5.1 has no `Test-Json`, so check those rules with `ConvertFrom-Json` plus `PSObject.Properties` loops. If Node is available, `npx --yes ajv-cli validate --spec=draft2020 -s docs/Requirements/pipeline-config.schema.json -d <file>` also works.
     Report each violation with its JSON path.
   - Every `pipelines` key exactly equals an existing WatchItem `Tag`. It's case-sensitive, so a mismatch is silently dead config.
   - Every `Profile=` used by an Initialize pointing here exists in `profiles`.
   - Every `[_Token]` used by the WatchItem (and its Ref'd templates) is defined by some layer.
   - No key is duplicated across `global` and GlobalVariables.json with a **different** value, unless that is intentional; flag any such key.
5. **Repoint the Initialize**
   - Set `ParameterFile` to the new full path and `Profile` to the right profile.
   - Check the path **exists**. A WatchItem once pointed at a non-existent `SP2026R2\pipeline-config.json` and failed at Initialize.
   - Never point an Initialize at JSON through code that reads the file with the CSV parser (`ParseParameterFile`): every line becomes a key and secrets end up in key names.
6. **Deploy**: back up the live file (`.bak-yyyyMMdd-HHmmss`), copy the new file, then edit the WatchList. Writing JSON first and repointing second avoids a window where the pipeline is pointed at a file that isn't there yet.

## Report
Path written, keys per layer (names only), validation results, every Initialize repointed, and any token still undefined.
