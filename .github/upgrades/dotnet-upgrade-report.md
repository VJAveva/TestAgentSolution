# .NET 10.0 Upgrade Report

## Project target framework modifications

| Project name                             | Old Target Framework | New Target Framework | Commits                          |
|:-----------------------------------------|:--------------------:|:--------------------:|:---------------------------------|
| TestControllerGrpc\TestControllerGrpc.csproj | net9.0-windows       | net10.0-windows      | ea6a3d81, c1edd90f, 3d0b5673     |
| TestAgentDisplay\TestAgentDisplay.csproj     | net9.0-windows       | net10.0-windows      | faf6c70d                         |
| TestAgentGrpc\TestAgentGrpc.csproj           | net9.0-windows       | net10.0-windows      | 842a10fd, ae7d91a2               |

## NuGet Packages

| Package Name                                    | Old Version | New Version | Commit Id |
|:------------------------------------------------|:-----------:|:-----------:|:----------|
| AvalonEdit                                      | 6.3.0.90    | 6.2.0.78    | c1edd90f  |
| Microsoft.Extensions.Configuration.Json          | 10.0.3      | (removed)   | 3d0b5673  |
| Microsoft.Extensions.DependencyInjection         | 10.0.3      | (removed)   | 3d0b5673  |
| Microsoft.Extensions.Hosting                     | 10.0.3      | (removed)   | 3d0b5673  |
| Microsoft.Extensions.Logging                     | 10.0.3      | (removed)   | 3d0b5673  |
| Microsoft.Extensions.Logging.Console             | 10.0.3      | (removed)   | 3d0b5673  |
| System.Diagnostics.PerformanceCounter            | 10.0.3      | (removed)   | 842a10fd  |

## All commits

| Commit ID | Description                                                          |
|:----------|:---------------------------------------------------------------------|
| 9e9b1744  | Commit upgrade plan                                                  |
| ea6a3d81  | Update TestControllerGrpc.csproj to target .NET 10.0                 |
| c1edd90f  | Downgrade AvalonEdit in TestControllerGrpc.csproj                    |
| 3d0b5673  | Remove unused Microsoft.Extensions packages from csproj              |
| faf6c70d  | Update TestAgentDisplay.csproj to target .NET 10.0                   |
| 842a10fd  | Remove PerformanceCounter package from TestAgentGrpc.csproj          |
| ae7d91a2  | Update TestAgentGrpc.csproj to target .NET 10.0                      |

## Project feature upgrades

### TestControllerGrpc\TestControllerGrpc.csproj

- Target framework upgraded from `net9.0-windows` to `net10.0-windows`.
- AvalonEdit package downgraded from `6.3.0.90` to `6.2.0.78` due to .NET 10.0 incompatibility.
- Removed unnecessary Microsoft.Extensions.* package references (DependencyInjection, Hosting, Configuration.Json, Logging, Logging.Console) as they are provided by the FrameworkReference to Microsoft.AspNetCore.App.

### TestAgentDisplay\TestAgentDisplay.csproj

- Target framework upgraded from `net9.0-windows` to `net10.0-windows`.

### TestAgentGrpc\TestAgentGrpc.csproj

- Target framework upgraded from `net9.0-windows` to `net10.0-windows`.
- Removed unnecessary System.Diagnostics.PerformanceCounter package reference.
