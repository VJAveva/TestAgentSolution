# .NET 10.0 Upgrade Plan

## Execution Steps

Execute steps below sequentially one by one in the order they are listed.

1. Validate that a .NET 10.0 SDK required for this upgrade is installed on the machine and if not, help to get it installed.
2. Ensure that the SDK version specified in global.json files is compatible with the .NET 10.0 upgrade.
3. Upgrade TestControllerGrpc\TestControllerGrpc.csproj
4. Upgrade TestAgentDisplay\TestAgentDisplay.csproj
5. Upgrade TestAgentGrpc\TestAgentGrpc.csproj

## Settings

This section contains settings and data used by execution steps.

### Excluded projects

No projects are excluded from the upgrade.

### Aggregate NuGet packages modifications across all projects

NuGet packages used across all selected projects or their dependencies that need version update in projects that reference them.

| Package Name | Current Version | New Version | Description                      |
|:-------------|:---------------:|:-----------:|:---------------------------------|
| AvalonEdit   | 6.3.0.90        | 6.2.0.78    | Incompatible with .NET 10.0      |

### Project upgrade details

This section contains details about each project upgrade and modifications that need to be done in the project.

#### TestControllerGrpc\TestControllerGrpc.csproj modifications

Project properties changes:
  - Target framework should be changed from `net9.0-windows` to `net10.0-windows`

NuGet packages changes:
  - AvalonEdit should be updated from `6.3.0.90` to `6.2.0.78` (*incompatible with .NET 10.0*)

#### TestAgentDisplay\TestAgentDisplay.csproj modifications

Project properties changes:
  - Target framework should be changed from `net9.0-windows` to `net10.0-windows`

#### TestAgentGrpc\TestAgentGrpc.csproj modifications

Project properties changes:
  - Target framework should be changed from `net9.0-windows` to `net10.0-windows`
