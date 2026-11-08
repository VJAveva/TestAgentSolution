# Team onboarding: run your own TestController

Each product team runs its own controller (one WPF controller + optional IIS web tier) and its own agents.
Features the team does not use are switched off in one file, `Features.json`.

## 1. Install the controller and agents

On the controller machine (elevated PowerShell):

```powershell
.\Utilites\TestAgent_SetupScripts\Prepare-ControllerNode.ps1 -InstallDir C:\TestControllerService -LogonUser <domain\account>
```

On each agent machine:

```powershell
.\Utilites\TestAgent_SetupScripts\Prepare-AgentNode-Full.ps1 -Controller <controller> -LogonUser <domain\account>
```

Use `-ControllerAddress` in full: `-Controller` on its own is ambiguous (it also matches `-ControllerPort`) and
PowerShell refuses it.

Then deploy the build with `deploy\Invoke-Patch.ps1` (controller) and `deploy\Invoke-FleetDeployment.ps1` (agents).
Start `TestControllerGrpc.exe` on the controller by hand; it is a desktop app, not a service.

## 2. Turn off the features you do not use

1. Copy `deploy\Features.sample.json` to `C:\TestControllerService\Config\Features.json`.
2. Set `TeamName` and switch off what you do not use (`false`). A missing file or a missing key means **on**.
   Replace the placeholder `CodeChurn` repository with yours, or delete the whole `CodeChurn` section to keep
   scanning every component in the component map (see section 3).
3. Pipelines, Agents, Library, Pre-flight and Admin are always on and cannot be listed.

| Key | What it covers | Depends on |
|---|---|---|
| `Results` | Results dashboard over MSTest TRX files | |
| `FirReport` | First Information Report for failed tests | `Results` |
| `CodeChurn` | Code Churn (Azure DevOps changes per component/branch) | |
| `ImpactMapping` | Impacted test cases per changed component | `CodeChurn` |
| `PipelineBuilder` | "New Pipeline…" wizard | |
| `GlobalVariables` | Global Variables editor | |
| `Reports` | Build Report Card and the consolidated run email | |

- Changes apply without a restart: the WPF ribbon and menus update, the web client picks them up on its next
  refresh or window focus, and the API returns `404 Feature disabled` for a turned-off feature.
- Background work of a disabled feature (impact index, churn diagnostics, run email) is skipped **at startup**;
  restart the controller after turning one of those back on.
- Pre-flight warns, naming the step and the feature, when a pipeline step calls a turned-off feature (for example
  a `RunCommand` that calls `/api/impact/...` or `Rebuild-ImpactIndex.ps1`).
- Administrators can also switch features in **ADMIN > Features** (WPF, Secured mode) or the **Features** tab
  (web, Secured mode). Saving makes a timestamped `Features.json.bak-yyyyMMdd-HHmmss` and writes an audit entry.
- The IIS web tier reads its own copy unless you point it at the controller's file. In the web tier's
  `appsettings.Production.json` set `"Features": { "ConfigPath": "C:\\TestControllerService\\Config\\Features.json" }`
  and give the app-pool identity read access to that folder.

## 3. Code Churn: your repositories and branches

Repositories and branches are team configuration in the same `Features.json` (one file per controller, one
admin screen, one backup and audit trail):

```json
"CodeChurn": {
  "Repositories": [
    { "Name": "<RepoName>", "Url": "https://dev.azure.com/<org>/<project>/_git/<RepoName>",
      "Branches": ["main", "release/<version>"], "IncludePaths": ["src/"], "ExcludePaths": ["src/tests/"],
      "CredentialRef": "ADO_PAT" }
  ]
}
```

| Field | Meaning |
|---|---|
| `Name` | Azure DevOps repository name. Only components whose `repository` in the component map matches are scanned |
| `Url` | Optional. Must be in the organization/project Code Churn reads (`Ado:Organization` / `Ado:OmiProject`) |
| `Branches` | The branches offered in the Code Churn branch picker for this repository |
| `IncludePaths` / `ExcludePaths` | Folder prefixes (`src/`) or globs (`*.cs`). Exclude wins. A change whose files are all out of scope is dropped |
| `CredentialRef` | **Name** of an environment variable on the controller. Never the token: a value that looks like a PAT is rejected |

- **No `CodeChurn` section**: Code Churn scans every component in the component map, as before.
- **`"Repositories": []`**: the Code Churn screen says **"No repositories configured"**.
- Administrators edit the list in **ADMIN > Features** (WPF) or the **Features** tab (web).
- The Code Churn screen has a repository picker and shows, per repository, anything that stops it working:
  repository not found in Azure DevOps, a branch that does not exist, a `Url` outside the organization/project,
  a credential variable that is not set, or Azure DevOps unreachable. The same lines go to the controller log.
- Azure DevOps calls use the controller's ADO credential (`Ado:AuthMode` / `Ado:SecretEnvVarName`).
  `CredentialRef` is checked so a missing secret is reported; per-repository tokens are not used yet.

The Azure DevOps connection itself stays in the `Ado` section of `appsettings.json`:

| Setting | Meaning |
|---|---|
| `Organization`, `Project`, `OmiProject` | Your Azure DevOps organization and projects |
| `Branches`, `BranchIncludePatterns`, `DefaultBranch` | Branch list when `Features.json` has no repositories |
| `AuthMode`, `SecretEnvVarName` | How to authenticate; **the name of** the environment variable that holds the PAT, never the PAT itself |
| `ComponentMapPath` | Your component map (components, repositories, build definitions, use cases). Copy `docs\impact\component-usecase-map.v1.json` as a template |
| `ComponentCategoriesPath` | Runtime/Config categories per component (template: `docs\impact\component-categories.xml`) |
| `RepositoryPrefix` | Prefix used when a hint is a short component name (default `AppServer.`; set `""` to disable) |

Credentials are only ever referenced by environment-variable name; set the variable at **Machine** scope and run
`iisreset` for the web tier.

## 4. Parameters

- Folder layout: `C:\TestControllerService\Parameters\<Release>\<Release>-<UseCase>-pipeline-config.json`.
- Each Initialize node points at one config file and a `Profile`; values resolve Global < Profile < pipeline override.
- Shared values for every pipeline of a release (build number, drop location) go in `GlobalVariables.json`.

## 5. Teams not using C# / MSTest

- Turn `Results` off (which also turns `FirReport` off): the results screens only understand MSTest TRX.
- Run your own test runner from a pipeline with a `RunRemoteCommand` action on the agent; the controller only
  needs the exit code.

## 6. What NOT to copy from another team's controller

- Any `pipeline-config.json` that contains real user names or passwords.
- Their `Features.json`, `WatchList.xml`, `appsettings.json` or `orchestrator.db`.
- Their logs, TRX files and crash dumps. If you must share one, sanitize it first (secrets, tokens, passwords).
