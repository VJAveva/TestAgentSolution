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
| `deploy-all.bat`        | Orchestrate full deployment (1 Controller + N Agents)    |

---

## Individual Deployment

### Deploy Controller

```bat
deploy\deploy-controller.bat <ControllerNode> [GrpcPort] [PublishDir]
```

**Parameters:**
- `ControllerNode` — Machine name or IP *(required)*
- `GrpcPort` — Controller gRPC port *(default: 5100)*
- `PublishDir` — Local publish output folder *(default: publish\controller)*

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
- `AgentNode` — Agent machine name or IP *(required)*
- `ControllerNode` — Controller machine name or IP *(required)*
- `AgentPort` — Agent gRPC port *(default: 5200)*
- `ControllerPort` — Controller gRPC port *(default: 5100)*
- `PublishDir` — Local publish output folder *(default: publish\agent)*

**Example:**
```bat
deploy\deploy-agent.bat AGENT01 CONTROLLER01 5200 5100
```

Deploys to `\\AGENT01\C$\TestAgentService\` and patches `appsettings.json` so the agent connects to `http://CONTROLLER01:5100`.

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
4. Print a summary with the full topology

---

## Remote Directories

| Service    | Default Remote Path                       |
|:-----------|:------------------------------------------|
| Controller | `\\<node>\C$\TestControllerService\`      |
| Agent      | `\\<node>\C$\TestAgentService\`           |

## Notes

- Both applications require an **interactive desktop session** (Controller is WPF, Agent is WinForms system tray). They cannot run as headless Windows Services without modification.
- For automated startup, create a **Scheduled Task** on each node that runs the `.exe` at logon in an interactive session.
- To switch to **self-contained** deployment (no runtime required on target), change `--self-contained false` to `--self-contained true` in the batch files.
