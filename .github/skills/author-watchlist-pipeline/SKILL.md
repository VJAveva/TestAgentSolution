---
name: author-watchlist-pipeline
description: 'Author a new WatchItem (pipeline) in WatchList.xml with Initialize, Ref templates and a trigger file. Checks tag safety (36 chars max, IIS-safe), trigger Path/Filter collisions, SecretEnv credentials, and stop-on-failure stage groups. Use for new pipeline, add WatchItem, add trigger, add stage, edit WatchList.'
argument-hint: 'Release, use case, agents/profile, stages'
---

# Author a WatchList Pipeline

## First decide which file
- Local dev: `C:\TestControllerService\WatchList.xml`. Production: `\\JVGR22\C$\TestControllerService\WatchList.xml`. The two **differ**, so say which one you are reading.
- Production is edited often. Re-read it right before editing, back it up as `WatchList.xml.bak-yyyyMMdd-HHmmss`, and keep encoding and BOM byte-identical. The controller's file watcher hot-reloads it, so no restart is needed.
- Model and parser: `TestControllerGrpc.Core/Models/WatchListConfig.cs` and `Services/WatchListXmlParser.cs`. The parser drops attributes it does not know about, so use only the attributes listed below.

## Shape
```xml
<WatchItem Tag="SP2026-Sanity-Smoke" Path="C:\Triggers\SP2026\" Filter="SanitySmoke.txt"
           BuildNumberField="BuildNumber" DropLocationField="DropLocation" BuildBasePath="\\server\repl\Ado\SP\">
  <Event Type="Renamed" ExecutionType="Sequential">
    <Initialize Tag="LoadParams" ParameterFile="C:\TestControllerService\Parameters\SP2026\SP2026-Sanity-pipeline-config.json" Profile="Sanity" />
    <ActionGroup Tag="Revert" ExecutionType="Parallel" FailAndContinue="false"> ... </ActionGroup>
    <ActionGroup Tag="Install" ExecutionType="Sequential" FailAndContinue="false">
      <Ref TemplateID="InstallSanity" />
    </ActionGroup>
  </Event>
</WatchItem>
```

## Checks (all must pass before saving)
1. **Tag**
   - 1 to 36 characters, using only `[A-Za-z0-9._-]` (`PipelineTag.IsValid` in `PipelineBuilder/PipelineBuilderService.cs`).
   - **No `+`, spaces or `%`.** IIS rejects them in URL paths, so every trigger and retry endpoint would 404.
   - Must be unique among WatchItems.
   - The Tag is an **identity key**: it equals the RBAC `PipelineId` and the `pipelines[tag]` key in the config. Renaming an existing Tag removes every engineer assignment for it, so don't do that without the user's say-so.
2. **Trigger collision**
   - Normalise every other WatchItem's `Path` (full path, case-insensitive, trailing `\`).
   - On the same folder, the new `Filter` collides if either filter matches the other (`-like` both ways) or both are wildcards that overlap. Treat `*.*` as a collision with everything.
   - A collision means one file drop fires two pipelines, so stop and report it. `WatchListValidator` does **not** check this.
   - The watch folder **must exist** on the controller. `C:\Triggers\SP2023R2SP2\` was missing once and every controller-side action died with "directory name is invalid". Create it, or report that it is missing.
3. **Initialize**
   - `ParameterFile` must exist and be layered `.json` (see the `pipeline-config-json` skill).
   - `Profile` must exist in its `profiles`.
   - Add a `pipelines["<Tag>"]` entry (empty `{}` = inherit) so the validator doesn't warn.
4. **Templates**
   - Prefer `<Ref TemplateID>` to existing templates. Count how many WatchItems already `Ref` a template before editing it: it is **shared**, so a change hits every pipeline that uses it.
   - New template IDs must be unique.
   - Agent names inside templates should be tokens (`[_Agent1]`) resolved by the profile, not literal hostnames.
5. **Stages stop on failure**
   - Each stage is an `ActionGroup` with `FailAndContinue="false"` (the default; write it explicitly).
   - Use `FailAndContinue="true"` only for best-effort steps the user names (cleanup, mail).
6. **Credentials**
   - Pass secrets only through `SecretEnv="RCLOUD_USER=[_RcloudUser];RCLOUD_PASSWORD=[_RcloudPassword]"`, which is applied as process environment variables (`Services/SecretEnvironment.cs`).
   - **Never** put `[_...Password]` or similar tokens in `Command` or `Parameters`, and never use the `Password` attribute. Anything on a command line shows in the process table and in logs.
   - The script must read the secret from the environment variable.
   - Deploy controller code that understands `SecretEnv` **before** saving a WatchList that uses it. An old controller silently drops the attribute when it saves.
7. **Actions**
   - `RunRemoteCommand` needs `AgentName`.
   - Controller-local steps are `RunCommand` with `AgentName=""`.
   - Call scripts as `cmd /c x.bat` or `powershell -File x.ps1`, never `powershell x.bat`.
   - `Timeout` is in seconds and must be under 48 h.
8. **Tokens**: every `[_Token]` used must be defined by some layer (GlobalVariables, global, profile, pipelines[tag]). Check against the config. Don't assume `[_Controller]` exists; the real key is `[_ControllerName]`.

## Trigger file
- Create a sample trigger `<Path>\<Filter>` with CSV lines `BuildNumber,<value>` and `DropLocation,<value>`. The keys must match `BuildNumberField` and `DropLocationField`.
- `Event Type="Renamed"` (the default) fires on a rename **into** the filter name. Tell the user how to drop it: write a temp name, then rename.

## Finish
- Show the XML diff.
- List the checks with pass/fail, plus any manual steps (create the folder, deploy code first, RBAC assignments for the new Tag).
- Do not trigger a run unless asked.
