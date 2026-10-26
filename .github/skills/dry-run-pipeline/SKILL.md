---
name: dry-run-pipeline
description: 'Build a safe dry-run copy of a WatchList pipeline for demos or for testing controller changes: no VM revert, no install, no reboot, no real credentials, no mail to the team. Keeps the tree shape, agents, locks and progress so the run looks real. Use for dry run, demo pipeline, safe test run, rehearse pipeline.'
argument-hint: 'Source WatchItem tag; mode echo (default) or skip'
---

# Dry-run Pipeline

The executor has no built-in dry-run mode; the dashboard "Demo" toggle only fakes UI sessions. This skill makes a **separate WatchList file** where every dangerous action is neutralised.

## Never
- Never edit the production WatchList (`\\JVGR22\C$\TestControllerService\WatchList.xml`) or reuse a production Tag or trigger folder. The Tag is an RBAC/lock identity, and a shared folder would fire the real pipeline.
- Never carry real `SecretEnv`, `Password` or credential tokens into the dry-run file.

## Procedure
1. **Source**: copy the WatchItem **and every Template it Refs** (directly or nested) from the chosen WatchList into `WatchList.dryrun.xml`, next to the local dev WatchList (default `C:\TestControllerService\`).
   Inline the Refs, or give the copied templates a `DRY-` ID prefix so they can never collide with live template IDs.
2. **Identity**:
   - Tag = `<original>-DRY`, cut to 36 characters with only `[A-Za-z0-9._-]` (follow `PipelineTag.Sanitize`).
   - `Path="C:\Triggers\DryRun\"`, a unique `Filter`. Create the folder.
3. **Neutralise each action.** Classify every `<Action>`:
   | Dangerous if | Examples |
   |---|---|
   | revert / power | `RevertRcloudMachine`, `Revert-*`, `Vm-Ops`, `shutdown`, `IsReboot="true"` |
   | install | `Install-Build`, `Monitor-SilentInstall`, `setup.exe`, `msiexec` |
   | destructive copy | `Prepare-Agent`, `robocopy /MIR`, `del`, `rd /s` |
   | mail | `Type="SendMail"` |
   | everything else | ping, waits, smoke tests: decide with the user (default: neutralise too) |

   - **echo mode (default)**: keep `Type`, `AgentName`, `Tag`, `Timeout`. Set `Command="cmd"` and `Parameters="/c echo [DRY] <original command file name> &amp; timeout /t 3 /nobreak &gt;nul"`. Remove `SecretEnv`, `Password`, `UserName`, `IsReboot` and `CompletionCheckCommand`. Real agents get dispatched, locks taken, progress painted, and nothing changes on the node.
   - **skip mode**: set `Skip="true" SkipReason="dry run"` on the dangerous ActionGroups or Actions. Skipped nodes return success and show as Skipped, without dispatching. Use this for a tree/UI demo where you also want the dispatch path not to run.
   - `SendMail`: set `To` to the user's own address (ask), or skip it.
4. **Parameters**: point Initialize at the same config + Profile (read-only use). Add `"<Tag>-DRY": {}` under `pipelines` only in a **copy** of the config if the validator warning matters. Never edit the live config for a dry run.
5. **Validate**:
   - XML parses.
   - Grep the dry-run file: zero matches for `Revert|Install-Build|setup\.exe|msiexec|shutdown|IsReboot="true"|SecretEnv|Password=`, outside `[DRY]` echo text and comments.
   - Every Tag/ID is unique, and the Tag is at most 36 characters.
   - Run pre-flight against it (skill `preflight-triage`).
6. **Run it** (only when the user asks):
   - Point the **local** controller at the file: back up `TestControllerGrpc\bin\<cfg>\net10.0-windows\appsettings.json`, set `VocabularyFile` to the dry-run path, and restore the backup afterwards. Startup logs `Loaded from startup config: <path>` to confirm.
   - Trigger by writing `C:\Triggers\DryRun\<Filter>.tmp` with `BuildNumber,<value>` / `DropLocation,<value>` lines, then renaming it to `<Filter>` (Event `Type="Renamed"`).
   - Agents in the dry run must be free. Check Fleet first so a demo doesn't take locks a real run needs.

## Report
Output file path, per-action table (original command, then dry action/skip), the validation grep result (0 hits), how to trigger it, and how to restore `VocabularyFile`.
