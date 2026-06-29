# Hosting the TestAgentGrpc Service

`TestAgentGrpc` is the Windows agent that runs commands dispatched by the Controller.
It hosts a **gRPC HTTP/2 server** (default port **5200**) and connects back to the
Controller (default `http://<controller>:5100`) for registration, heartbeat, and
event push. It also shows a system-tray icon.

> Two ways to run it: as a **Windows service** (recommended for unattended nodes)
> or as an **interactive tray app** (handy for a quick manual test).

---

## 1. Prerequisites (on the agent machine)

- Windows 10/11 or Windows Server.
- **.NET 10 runtimes** (the app is published framework-dependent):
  - ASP.NET Core Runtime 10 — for the Kestrel/gRPC server.
  - .NET Desktop Runtime 10 — for the WinForms tray UI.
  - Download: <https://dotnet.microsoft.com/download/dotnet/10.0>
- Network line-of-sight to the Controller on its gRPC port (default `5100`).

---

## 2. Publish the binaries (on your build machine)

From the solution root:

```powershell
dotnet publish .\TestAgentGrpc\TestAgentGrpc.csproj -c Release -r win-x64 --self-contained false -o .\publish\agent
```

Copy the contents of `publish\agent` to the agent machine, e.g. `C:\TestAgentSolution\Agent`.

---

## 3. Configure `appsettings.json`

Edit `appsettings.json` next to `TestAgentGrpc.exe` and set the four values that
matter per node (see [AgentSettings.cs](AgentSettings.cs)):

```jsonc
{
  "AgentSettings": {
    "AgentName": "JVGR1",                          // unique name used in the Controller WatchList
    "GrpcPort": 5200,                              // port this agent listens on
    "ControllerAddress": "http://CONTROLLER01:5100", // where the Controller lives
    "AgentEndpoint": "http://JVGR1:5200"           // how the Controller reaches this agent
  }
}
```

> gRPC over plaintext **requires HTTP/2**. If you create `appsettings.json` by hand,
> ensure Kestrel forces it: `"Kestrel": { "EndpointDefaults": { "Protocols": "Http2" } }`.
> The `Setup-AgentNode.ps1` script writes this for you.

---

## 4. Open the firewall + reserve the URL (run elevated)

```powershell
# Inbound: Controller/Dashboard -> this agent
New-NetFirewallRule -DisplayName "TestAgent gRPC Inbound (TCP 5200)" -Direction Inbound -Protocol TCP -LocalPort 5200 -Action Allow

# Outbound: this agent -> Controller
New-NetFirewallRule -DisplayName "TestAgent to Controller Outbound (TCP 5100)" -Direction Outbound -Protocol TCP -RemotePort 5100 -Action Allow

# HTTP.sys URL reservation for the gRPC port
netsh http add urlacl url=http://+:5200/ user=Everyone
```

---

## 5. Run it

### Option A — interactive (quick test)

Double-click `TestAgentGrpc.exe` (or run it from a console). A tray icon appears and
the agent registers with the Controller. This requires an interactive desktop session.

### Option B — Windows service (recommended)

```powershell
# Create the service (LocalSystem; use New-Service -Credential for a domain account)
New-Service -Name TestAgentGrpc -DisplayName "TestAgent gRPC Service" `
  -BinaryPathName '"C:\TestAgentSolution\Agent\TestAgentGrpc.exe"' -StartupType Automatic

# Start on a ready network, auto-restart on failure, treat non-zero exit as failure
sc.exe config      TestAgentGrpc start= delayed-auto
sc.exe failure     TestAgentGrpc reset= 60 actions= restart/5000/restart/10000/restart/30000
sc.exe failureflag TestAgentGrpc 1

Start-Service TestAgentGrpc
```

> The `failureflag` is important: the agent's watchdog calls `Environment.Exit(3)`
> when stuck, and this flag lets the Service Control Manager restart it.

#### Running as a domain service account

Use a domain account when remote commands need network access (shares, other nodes).
The agent **executes every `RunRemoteCommand` as its own service identity** — not as
the Controller or the WPF operator. Three things must be in place on the agent node:

```powershell
$acct = "DOMAIN\svc_testagent"

# 1. "Log on as a service" right (REQUIRED — missing right = SCM error 1069/1385)
#    GUI: secpol.msc -> Local Policies -> User Rights Assignment -> Log on as a service
#    (In a domain this may be governed by GPO; add the account there if a GPO resets it.)

# 2. NTFS access to the install + Logs folder
icacls "C:\TestAgentService" /grant "${acct}:(OI)(CI)RX" /T
icacls "C:\TestAgentService\Logs" /grant "${acct}:(OI)(CI)M" /T

# 3. Create the service under that account (prompts securely — do not pass plaintext)
$cred = Get-Credential $acct
New-Service -Name TestAgentGrpc -DisplayName "TestAgent gRPC Service" `
  -BinaryPathName '"C:\TestAgentService\TestAgentGrpc.exe"' -StartupType Automatic -Credential $cred
Start-Service TestAgentGrpc
```

> `Setup-AgentNode.ps1 -InstallAsService -ServiceUser "DOMAIN\svc_testagent"` does the
> service creation with a secure `-ServiceCredential` prompt. It does **not** grant the
> logon-as-service right or folder ACLs — do those first.

### Execution identity model (why remote commands fail)

There are two command kinds, run by two different identities:

| Command | Runs on | Under whose identity |
|---------|---------|----------------------|
| `RunRemoteCommand` (AgentName set) | the **agent** machine | the **TestAgent service account** |
| `RunCommand` (local) | the **Controller** machine | the **Controller app** identity |

Key consequences:

- Elevating or changing the **WPF Controller** identity does **not** affect
  `RunRemoteCommand` — the Controller only dispatches over gRPC; the agent executes.
- For a remote command to reach `\\other\share`, the **agent's service account** must
  have rights there.
- **Avoid `c$` / admin shares** (`\\node\c$\...`). They are reachable only by a *local
  administrator* of the target, so a normal domain service account gets
  `Invalid drive specification`. Use a regular shared folder
  (`\\node\TestControllerService\...`) with share + NTFS read granted to the service
  account (or its machine account) instead.

> **No system tray when run as a service.** Windows services run in the isolated
> **Session 0**, which has no access to the interactive desktop, so the WinForms
> tray icon is created but never visible. The gRPC server, registration, heartbeat,
> and command execution all work normally — only the tray UI is hidden. See
> [Monitoring a service-hosted agent](#monitoring-a-service-hosted-agent) for how to
> watch live activity instead.

---

## 6. Verify

```powershell
# Service is running
Get-Service TestAgentGrpc

# Agent is listening on its gRPC port
Get-NetTCPConnection -LocalPort 5200 -State Listen

# Logs
Get-Content "C:\TestAgentSolution\Agent\Logs\*.log" -Tail 50
```

The agent should appear as **Ready** in the Controller's agent list. Heartbeats flow
every `HeartbeatIntervalSeconds` (default 15s).

---

## Monitoring a service-hosted agent

A service-hosted agent has **no visible system tray** (Session 0 isolation), so use one
of these to watch live activity:

| Method | How |
|--------|-----|
| **Tail the logs** | `Get-Content "C:\TestAgentSolution\Agent\Logs\*.log" -Tail 50 -Wait` |
| **TestAgentDisplay** | Dedicated viewer app — subscribes over gRPC (`SubscribeAgentEvents`) and streams live activity from any machine. The intended way to watch a service-hosted agent. |
| **Controller UI / Dashboard** | The agent shows as Ready with live status, heartbeats, and execution events pushed to the Controller. |
| **`/metrics` endpoint** | Prometheus-style live counters on the metrics port (default `5210`) when `MetricsEndpointEnabled` is true. |
| **Run interactively instead** | Stop the service and launch `TestAgentGrpc.exe` in your desktop session to get the real tray icon (the `Global\TestAgentGrpc-SingleInstance` mutex prevents both running at once). |

### Tray-only features and their service equivalents

The system tray (interactive mode only) shows live status and a few manual actions.
Under a service none of it is visible — here is where each capability still lives:

| Tray feature | Service equivalent |
|--------------|--------------------|
| Controller registration + connection status (colored icon) | Controller UI/Dashboard agent list, or TestAgentDisplay; also in the agent log. |
| State (Ready/Busy) + current activity ("what's running") | Pushed to the Controller as execution events — view in Controller UI/Dashboard or TestAgentDisplay. |
| Last command / uptime / heartbeats | Controller heartbeat + event stream; or tail the agent log. |
| CPU / Mem metrics | `/metrics` endpoint (default port `5210`). |
| **Re-Register to Controller** (manual button) | Automatic — `ConnectionHealthMonitor` re-registers after heartbeat loss. To force it, `Restart-Service TestAgentGrpc`. |
| Execution Monitor / Connection Details / Export Report windows | Run interactively (stop the service, launch the EXE) to use these dialogs. |

---

## Automated alternative

Instead of steps 3–5, you can run the bundled setup script on each agent node (elevated):

```powershell
.\Utilites\TestAgent_SetupScripts\Setup-AgentNode.ps1 `
  -AgentName JVGR1 -AgentPort 5200 -ControllerAddress "http://CONTROLLER01:5100" `
  -InstallDir "C:\TestAgentSolution\Agent" -InstallAsService -VerifyEndpoint
```

It checks the .NET runtime, opens the firewall, reserves the URL, writes an HTTP/2
`appsettings.json`, and installs the service. Re-run with `-Uninstall` to remove it.

To build + copy + configure from a central machine, see
[../deploy/deploy-agent.bat](../deploy/deploy-agent.bat).

---

## Common issues

| Symptom | Fix |
|---------|-----|
| Controller logs `HTTP_1_1_REQUIRED` (0xd) | Kestrel endpoint not set to HTTP/2 — add `Kestrel:EndpointDefaults:Protocols = Http2`. |
| Agent not registering | Check `ControllerAddress`, outbound firewall to port 5100, and that the Controller is up. |
| Port 5200 in use | Set `AllowPortFallback: true` (uses `FallbackPorts` 5201–5203) or change `GrpcPort`. |
| Service installed but won't restart after crash | Ensure `sc.exe failureflag TestAgentGrpc 1` was applied. |
| Tray icon missing under a service | Expected — services run non-interactively; use the tray only in interactive mode. |
| Service won't start (SCM 1069/1385) | Domain account lacks **Log on as a service** — grant it (secpol.msc / GPO). Verify the password too. |
| Remote `RunRemoteCommand` fails with `Invalid drive specification` / `0 File(s) copied` | Command targets a `c$` admin share; the agent's service account isn't a local admin on the target. Use a regular shared folder with read access granted. |
| Remote command fails with Access denied | The **agent's service account** (not the WPF app) lacks rights on the target path/share. Grant access to that account. |
