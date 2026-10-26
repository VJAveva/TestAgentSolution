---
name: preflight-triage
description: 'Explain each pre-flight failure for a pipeline (unresolved token, missing or access-denied path, agent offline/locked/maintenance, pipeline locked, low disk) and give the exact fix. Use for preflight failed, pre-flight report, cannot run pipeline, Unresolved parameter, Not found in, Registered but offline.'
argument-hint: 'Pipeline tag, or paste the pre-flight report/email'
---

# Pre-flight Triage

Pre-flight is read-only: `PreflightRunner` (`TestControllerGrpc.Core/Preflight/PreflightRunner.cs`) runs the groups Settings, Controller files, Install sources, Agents, Structure, Disk.
Each check has `Group / Name / Status (Pass|Warn|Fail) / Detail / FixHint`. `CanRun` is false when any check is Fail.

## Get the report
- Pasted report or e-mail: use it as given.
- Otherwise: `POST http://JVGR22:5200/api/preflight/{tag}` with `-UseDefaultCredentials -UseBasicParsing`. URL-encode the tag; a tag containing `+` 404s at IIS. On :81 a `text/html` 404 means IIS rejected the request, not "no such pipeline".
- Triage **Fail** checks first, in group order. A Settings failure (parameters didn't load) causes many later "unresolved"/"not found" failures, so fix it first and re-run before chasing the rest.

## Failure, meaning, how to verify, fix
| Detail (starts with) | Meaning | Verify (read-only) | Fix |
|---|---|---|---|
| `No pipeline supplies this run's settings` / `has no Initialize node` / `names no parameter file` | WatchItem has no usable Initialize | Read the WatchItem in the **deployed** WatchList | Add `<Initialize ParameterFile=".json" Profile="...">` (skill `author-watchlist-pipeline`) |
| `Unresolved parameter(s) [...] - the Initialize parameter source ... did not load` | Parameter file missing or invalid JSON, or read by the wrong parser | `Test-Path` the ParameterFile; `ConvertFrom-Json` it; check the path is the **exact** file name | Fix the path or the JSON (skill `pipeline-config-json`). Known case: `SP2026R2-WARM` once pointed at a non-existent `pipeline-config.json` |
| `No 'pipelines' entry is keyed '{tag}'` (Warn) | No per-pipeline layer, or it's keyed with a different tag | Compare the keys with the Tag exactly (case-sensitive) | Add `"<Tag>": {}`. Harmless if you don't need a pin |
| `Unresolved: [_X], ...` | Token not defined in any layer | Search global / profile / pipelines / GlobalVariables.json for the exact name. Look for typos like `[_Controller]` vs `[_ControllerName]` | Define it in the right layer. Build tokens (`_BuildNumber`, `_DropLocation`) normally arrive by trigger file or GlobalVariables. GlobalVariables applies them **only if** `_ReleaseName` matches |
| `Watch folder ... Not found in '{folder}'` | Trigger folder missing | `Test-Path \\JVGR22\C$\<folder>` | Create the folder (controller-side actions also use it as their working directory) |
| `[_DropLocation]/[_BinariesFolder]/... Not found in '{path}'` | Path missing, **or** denied/unreachable (pre-flight reports every non-Exists result as "Not found") | 1) Compare the file/folder **CreationTime** with the failure time. FrameworkBinaries is deleted and recopied during the day, so it may have appeared minutes later. 2) Check the share as the **controller's identity**, not yours | If it now exists: just re-run. If missing: fix the token value or copy the payload. If denied: grant read on the share to the identity named |
| `WASSmokeTest.dll ... denied access` (Warn) | Identity can't read the binaries folder | as above | Grant read on the share. Not a missing file |
| `Script '...' not found - restore ...` | A controller-side action's Command file is absent | `Test-Path` on the controller | Restore the script, or correct the action's `Command` |
| `Agents will look for '{v}', but '{expected}' does not exist on the controller to copy from` | Install source missing on the controller | `Test-Path` expected | Copy the release payload, or fix `_BinariesFolder` / `_BinariesSource` |
| `Not registered with the controller` | Agent never connected since controller start | Fleet tab / `GET /api/agents/fleet` | Skill `diagnose-agent-offline` |
| `Registered but offline - no live connection` | Listener lost or host down | Fleet + port 5200 | Skill `diagnose-agent-offline`. Don't restart anything blind |
| `Held out for maintenance: {state}` | Node is Draining / Quarantined / Reverting / Updating | Fleet card state | Quarantine is sticky: if the node is healthy, the operator uses **Return to rotation**. Draining from reboot-required clears after a reboot |
| `Locked by {holder}` / `Pipeline ... Already running - locked by {owner}` | Agent or pipeline lock held by another run | `GET /api/execution/locks`, `/api/execution/sessions` | Wait, or have the holder cancel. Force-release is admin-only; ask first |
| `Busy running another command` | Dispatcher has an active execution on that agent | Sessions list | Wait for it to finish |
| `A reboot is pending` (Warn) | Servicing keys say reboot | Fleet update tile | Not blocking. Reboot when convenient |
| Disk `{free} GB free, below the {min} GB minimum` (Warn) | Low disk on controller or agent | `(Get-PSDrive C).Free` | Check `Logs\crash_*.dmp` first (about 600 MB each), then `%TEMP%` |
| Structure group | WatchListValidator finding (Ref to unknown template, empty group, ...) | Read the message | Fix the WatchList |

## Output
For each Fail (and any Warn the user should care about): **check name, what it means, the evidence you verified, and the exact fix** (command or edit).
Then: "re-run pre-flight after: <ordered fixes>". Make no changes unless asked.
