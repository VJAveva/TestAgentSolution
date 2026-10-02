# Deployment Scripts

Batch files for deploying the **TestControllerGrpc** (Controller) and **TestAgentGrpc** (Agent) services to their respective nodes.

## Prerequisites

- .NET 9.0 SDK (or later) installed on the build machine
- Network access to target nodes via admin shares (`\\<node>\C$`)
- The target machines must have the .NET Desktop Runtime installed (unless you switch to self-contained publish)

## Scripts

| Script                  | Purpose                                                  |
|:------------------------|:---------------------------------------------------------|
| `deploy-controller.bat` | Build and deploy the Controller to a single node         |
| `deploy-agent.bat`      | Build and deploy an Agent to a single node               |
| `deploy-webapi.bat`     | Build React frontend + WebApi and deploy to IIS          |
| `deploy-all.bat`        | Orchestrate full deployment (1 Controller + N Agents + WebApi) |
| `Invoke-Deploy.ps1`     | **Deploy or rollback** one component with backup + smoke-test + auto-rollback |
| `Validate-DeployManifest.ps1` | Validate artifact checksums and proto compatibility |
| `Invoke-PreDeployCheck.ps1`   | Pre-deployment safety check (active sessions, locks)  |
| `Invoke-SmokeTest.ps1`        | Post-deployment smoke test (fleet, SignalR, capabilities) |
| `Invoke-LogCleanup.ps1`       | Log/audit retention cleanup (configurable retention days) |

---

## Individual Deployment

### Deploy Controller

```bat
deploy\deploy-controller.bat <ControllerNode> [GrpcPort] [PublishDir]
```

**Parameters:**
- `ControllerNode` - Machine name or IP *(required)*
- `GrpcPort` - Controller gRPC port *(default: 5100)*
- `PublishDir` - Local publish output folder *(default: publish\controller)*

**Example:**
```bat
deploy\deploy-controller.bat CONTROLLER01 5100
```

Deploys to `\\CONTROLLER01\C$\TestControllerService\`.

---

### Deploy Agent

```bat
deploy\deploy-agent.bat <AgentNode> <ControllerNode> [AgentPort] [ControllerPort] [PublishDir]
```

**Parameters:**
- `AgentNode` - Agent machine name or IP *(required)*
- `ControllerNode` - Controller machine name or IP *(required)*
- `AgentPort` - Agent gRPC port *(default: 5200)*
- `ControllerPort` - Controller gRPC port *(default: 5100)*
- `PublishDir` - Local publish output folder *(default: publish\agent)*

**Example:**
```bat
deploy\deploy-agent.bat AGENT01 CONTROLLER01 5200 5100
```

Deploys to `\\AGENT01\C$\TestAgentService\` and patches `appsettings.json` so the agent connects to `http://CONTROLLER01:5100`.

---

### Deploy WebApi (IIS)

```bat
deploy\deploy-webapi.bat <TargetNode> [IISSiteName] [PublishDir]
```

**Parameters:**
- `TargetNode` — Machine name or IP where IIS is running *(required)*
- `IISSiteName` — IIS site name *(default: TestControllerWeb)*
- `PublishDir` — Local publish output folder *(default: publish\webapi)*

**Example:**
```bat
deploy\deploy-webapi.bat WEBSERVER01 TestControllerWeb
```

Builds the React frontend, publishes the .NET WebApi, and deploys to `\\WEBSERVER01\C$\inetpub\TestControllerWeb\`.

**IIS Prerequisites:**
1. Install the [ASP.NET Core Hosting Bundle](https://dotnet.microsoft.com/download/dotnet) on the target server
2. Create an IIS site pointing to `C:\inetpub\TestControllerWeb`
3. Set the Application Pool to **No Managed Code**
4. Enable **WebSockets** in IIS (required for SignalR)
5. Edit `appsettings.Production.json` on the server for environment-specific config

---

## Full Deployment

Edit the **CONFIGURATION** section at the top of `deploy-all.bat` to define your topology:

```bat
set "CONTROLLER_NODE=CONTROLLER01"
set "CONTROLLER_PORT=5100"

set "AGENT_NODES=AGENT01 AGENT02 AGENT03"
set "AGENT_PORT=5200"
```

Then run:

```bat
deploy\deploy-all.bat
```

This will:
1. Publish and deploy the Controller to `CONTROLLER01`
2. Publish and deploy the Agent to each node in `AGENT_NODES`
3. Patch each agent's `appsettings.json` to point at the Controller
4. Build and deploy the WebApi + React frontend to IIS on `WEBSERVER01`
5. Print a summary with the full topology

---

## Remote Directories

| Service    | Default Remote Path                       |
|:-----------|:------------------------------------------|
| Controller | `\\<node>\C$\TestControllerService\`      |
| Agent      | `\\<node>\C$\TestAgentService\`           |
| WebApi     | `\\<node>\C$\inetpub\TestControllerWeb\`  |

## Notes

- Both applications require an **interactive desktop session** (Controller is WPF, Agent is WinForms system tray). They cannot run as headless Windows Services without modification.
- For automated startup, create a **Scheduled Task** on each node that runs the `.exe` at logon in an interactive session.
- To switch to **self-contained** deployment (no runtime required on target), change `--self-contained false` to `--self-contained true` in the batch files.

## Backlog

### Invoke-Patch should classify "changed" by hash, not by file size

`Invoke-Patch.ps1` already compares SHA256 to decide *whether* a file differs, but then uses **file size** to
decide whether the difference is meaningful:

```powershell
# Invoke-Patch.ps1 ~L188
$reason = 'changed'
if ($dstItem.Length -eq $file.Length) { $reason = 'rebuilt' }   # "MVID churn"
```

The premise in the comment — *"Identical size means recompiled, not behaviour changed"* — is false. Plenty of
real changes preserve byte length, and it has now misreported **three** times:

| Date | File | Script said | Actually carried |
|---|---|---|---|
| 2026-10-01 | `TestControllerGrpc.dll` | "same size, MVID churn" | 114 lines of changed XAML + 5 lines of C# |
| 2026-10-02 | `TestControllerGrpc.dll` | "No real content changes detected" | the R1-01 status-glyph fix (`Grid.Column="5"`→`"1"` is the same byte count) |
| 2026-10-02 | `TestController.Api.dll` | "same size, MVID churn" | `MaxConcurrentUpdates` + `IMachineUpdateOperation` (verified staged-True / deployed-False by marker scan) |

**Severity: reporting only.** Both buckets are copied, so no deploy has ever shipped the wrong bits. The risk is
a human reading "No real content changes detected" and concluding a fix did not need to ship, or skipping
verification.

**Suggested fix:** drop the size heuristic. Report every hash difference as `changed`, and if an "unchanged
source" signal is still wanted, get it honestly — compare the assembly MVID, or diff a metadata/BAML string
scan — rather than inferring it from length.

**Until then:** never let the size verdict decide whether to ship. Verify with a marker scan for a string the
change must introduce, or chain hashes `tested build -> staged -> deployed`.

### Repair the remaining user-visible U+FFFD corruption

A repo-wide sweep on 2026-10-02 found the Unicode replacement character `U+FFFD` in **46 files**. It is never
typed deliberately — it is what a character decays into when a file is read as one encoding and written back
as another — so every occurrence is corruption from a past round-trip.

The e-mail styling work cleared 6 files; `SourceEncodingTests` now pins the rest at a baseline of **393
matches** so the count can only go down, and holds those 6 at a hard zero.

Classified by where the damage sits (C# files only):

| Where | Count | Ships to a user? |
|---|---|---|
| Comment lines (section-divider rules) | 149 | No - cosmetic, safe to defer |
| **Code lines (string literals)** | **95** | **Yes** |

The code-line hits are the ones worth fixing. Known clusters:

| File | Hits | Example (the corrupt byte shown as `<?>`) |
|---|---|---|
| `TestAgentGrpc/Services/ReportGenerator.cs` | 6 | `<title>Execution Report <?> {agent}</title>` |
| `TestAgentGrpc/UI/ConnectionDetailForm.cs` | 6 | placeholder dashes, `"Running<?>"` |
| `TestController.Api/Services/LockRecoveryService.cs` | 6 | `"{Agent}: BUSY <?> lock validated"` |
| `TestAgentDisplay/ViewModels/AuditTimelineViewModel.cs` | 6 | `summary += $" <?> {e.Command}"` |
| `TestController.Api/Controllers/ExecutionController.cs` | 2 | `"Cannot start '{tag}' <?> {n} agent(s) are locked"` |

**Severity: cosmetic but customer-facing.** These render as a black-diamond question mark in report titles,
WPF labels and log lines.

**Suggested fix:** repair in per-project batches, lowering `ReplacementCharBaseline` in the same commit. Write
the replacement as an escape (`\u2014`, `\u2026`, `\u2022`) rather than a literal, so it cannot decay again.
Prefer plain ASCII `-` in comments.

