# Utilites - scripts and how to run them

Every `.ps1` here has full help: `Get-Help .\<script>.ps1 -Full` lists every parameter with its default and
shows examples. This page is the index. Run from Windows PowerShell 5.1 unless noted; scripts that change
the machine need an elevated prompt (the two `Prepare-*` scripts relaunch themselves elevated).

List parameters (IPs, nodes, machines, architectures) accept a comma-separated string, so they also work
through `powershell -File` and the `.bat` wrappers.

Never put a password on a command line or in a pipeline. vCloud scripts read `RCLOUD_USER`,
`RCLOUD_PASSWORD` and `RCLOUD_ORG` from the environment; setup scripts prompt (masked).

## Node setup - `TestAgent_SetupScripts\`

| Script | What it does | Run |
|---|---|---|
| `Prepare-AgentNode-Full.ps1` | Full agent prep: folders, .NET 10, firewall, `appsettings.json` (merges `AgentSettings.ControllerAddress`), run-as rights, UAC/trust, logon task, start + verify 5200 | `.\Prepare-AgentNode-Full.ps1 -Controller AUT1` |
| `Prepare-ControllerNode.ps1` | Full controller prep: IIS, .NET 10 Hosting Bundle, firewall, run-as rights, config, UAC/trust | `.\Prepare-ControllerNode.ps1 -AgentIPs 10.48.190.213,10.228.117.101 -ExtraInboundPorts 81` |
| `Run-ControllerSetup.bat` | Wrapper for `Prepare-ControllerNode.ps1`; edit the values at the top | Right-click, Run as administrator |
| `Setup-AgentNode.ps1` | Lighter agent setup: firewall, WinRM, HTTP/2 `appsettings.json`, optional service | `.\Setup-AgentNode.ps1 -ControllerAddress http://JVGR22:5100 -VerifyEndpoint` |
| `Run-AgentSetup.bat` | Wrapper for `Setup-AgentNode.ps1` | Right-click, Run as administrator |
| `Setup-DisplayNode.ps1` | Dashboard (TestAgentDisplay) node: outbound firewall, .NET check | `.\Setup-DisplayNode.ps1 -AgentIPs 10.48.190.60,10.48.190.254` |
| `Run-DisplaySetup.bat` | Wrapper for `Setup-DisplayNode.ps1` | Right-click, Run as administrator |
| `Configure-AgentTaskRecovery.ps1` | Restart/recovery settings on the agent logon task. Dry run unless `-Apply` | `.\Configure-AgentTaskRecovery.ps1 -ComputerName JVGR1,WARMGR -Apply` |
| `Deploy-WebToIIS.ps1` | Turns an already-published WebApi folder into the IIS site + pool, writes `ControllerProxyUrl` | `.\Deploy-WebToIIS.ps1 -AppPath C:\inetpub\TestControllerWeb -SitePort 81` |

## Diagnostics (read-only)

| Script | What it does | Run |
|---|---|---|
| `TestAgent_SetupScripts\Test-NodeConnectivity.ps1` | PASS/FAIL per node: ping, DNS, SMB, ports, WinRM, firewall, .NET, registration | `.\Test-NodeConnectivity.ps1 -Controller JVGR22 -Nodes JVGR1,WARMGR` |
| `TestAgent_SetupScripts\Run-NodeConnectivity.bat` | Wrapper for the above | `Run-NodeConnectivity.bat` |
| `TestAgent_SetupScripts\Diagnose-Http2Protocol.ps1` | Detects the gRPC HTTP_1_1_REQUIRED (0xd) condition. Exit 0/1/3/4 | `.\Diagnose-Http2Protocol.ps1 -AgentHost JVGR1` |
| `TestAgent_SetupScripts\Watch-AgentListener.ps1` | Watches port 5200 during an install and records why it stops | `.\Watch-AgentListener.ps1 -Minutes 120 -IntervalSeconds 10` |
| `Diagnose-ZombieListener.ps1` | Mines agent logs around an incident time | `.\Diagnose-ZombieListener.ps1 -IncidentTime "2026-06-15 21:56:00"` |
| `Sample-AgentHealth.ps1` | Samples agent process health to CSV until stopped | `.\Sample-AgentHealth.ps1 -IntervalSeconds 15` |
| `Verify-CrossSubnetFirewall.ps1` | Evidence for a cross-subnet firewall request; run on both hosts | `.\Verify-CrossSubnetFirewall.ps1 -ControllerIp 10.48.190.248 -AgentIp 10.48.220.132` |

## vCloud revert - `RevertAgents\` (deployed to `C:\TestSetup\RevertAgents\` on the controller)

| Script | What it does | Run |
|---|---|---|
| `Revert-RcloudMachines.ps1` | Reverts one or many VMs over one PowerCLI session | `.\Revert-RcloudMachines.ps1 -OrgName AppServerPool2 -Machines JVGR1,JVGR2` |
| `RevertRcloudMachine.bat` | Pipeline launcher for the above | `cmd /c RevertRcloudMachine.bat AppServerPool2 "warmhist,warmgr"` |
| `Revert-AgentVM.ps1` | Adapter the Fleet panel calls (`<VmName> <Snapshot>`) | `.\Revert-AgentVM.ps1 JVGR2 "Clean-SP2023R2SP1P03"` |
| `Vm-Ops.vcloud.ps1` | State / power / snapshot verbs for the maintenance engine | `.\Vm-Ops.vcloud.ps1 -Verb State -Vm "JVGR1,JVGR2"` |
| `UnblockTrustworthyFiles.ps1` | Removes the downloaded-file mark from every file under a folder | `.\UnblockTrustworthyFiles.ps1 C:\TestSetup\RevertAgents` |

Taking a new baseline snapshot: follow the `vm-revert-snapshot` skill (your own credentials, VM powered off).

## Agent service and start-up (legacy)

| Script | What it does | Run |
|---|---|---|
| `Install-TestAgentGrpcService.ps1` | Installs the agent as a Windows service (standalone boxes only) | `.\Install-TestAgentGrpcService.ps1 -ServiceUser "DOMAIN\user"` |
| `Uninstall-TestAgentGrpcService.ps1` | Removes that service | `.\Uninstall-TestAgentGrpcService.ps1` |
| `Setup-InteractiveAgent.ps1` | Auto-logon + interactive logon task for UI tests | `.\Setup-InteractiveAgent.ps1 -LogonUser "DOMAIN\user"` |
| `Start-TestAgent.ps1` | Starts the agent, killing a non-listening process first | `powershell -File C:\TestAgentService\Start-TestAgent.ps1` |
| `AgentSchedular.ps1` | Old logon task "TestAgentGrpc". Superseded by `Prepare-AgentNode-Full.ps1`; do not run both | `.\AgentSchedular.ps1` |
| `Patch-AgentFleet.ps1` | Old fleet patcher (PowerShell 7, service-based). Use `deploy\Invoke-FleetDeployment.ps1` | `.\Patch-AgentFleet.ps1 -BinaryPath \\jvgr22\share\TestAgentGrpc.exe -DryRun` |
| `Prepare-Agent.bat` | Run on an agent by a pipeline: pulls payload + binaries from the controller's C$ | `Prepare-Agent.bat CONTROLLER01 TestSetup TestSetup\FrameworkBinaries\SP2026` |

## Smoke tests - `TestSetup\Common\SmokeTest\` (deployed to `C:\TestSetup\Common\SmokeTest\`)

| Script | What it does | Run |
|---|---|---|
| `Run-WASTests.bat` | Runs a test set from `TestCaseList.json`, copies results to the controller | `Run-WASTests.bat OAK_main_20260406.5 All jvgr22 -Resume` |
| `Copy-DependentBinaries.bat` | Stages the binaries the smoke tests need | `Copy-DependentBinaries.bat C:\CISmokeTest -ListOnly` |
| `ParseTestList.ps1` | Flattens `TestCaseList.json` for `Run-WASTests.bat` | `.\ParseTestList.ps1 -JsonPath TestCaseList.json -SetName Set1 -OutPath parsed.txt` |

`TestAgent_SetupScripts\_archive\` holds superseded scripts, kept for reference; each names its replacement.
