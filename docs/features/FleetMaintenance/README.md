---
feature: FleetMaintenance
status: Implemented
owner: Vinodkumar Javvadi
code: [TestControllerGrpc.Core/Maintenance, TestController.Api/Controllers/MaintenanceController.cs, TestControllerGrpc/Views/AgentWorkspace/MaintenanceView.xaml, TestControllerGrpc/ViewModels/AgentWorkspace/FleetVM.cs, deploy/Invoke-FleetDeployment.ps1]
tests: [TestControllerGrpc.Tests/Maintenance, TestController.WebApi.Tests/Maintenance, deploy/Invoke-NodeCleanup.Tests.ps1]
last-verified: 2026-11-08
---

# Fleet maintenance: revert, reboot, updates, golden image

## What it does
- Runs maintenance on agent machines from the controller: VM revert, reboot, Windows Update scan and install.
- Each operation is persisted so a controller restart can recover it; agents under maintenance are held out of runs and alerts.
- Also covers the on-demand agent update button.

## How to use
- WPF: Agents workspace, **Maintenance** view and fleet cards.
- REST: `/api/maintenance/*`.
- Agent binaries are rolled out with `deploy/Invoke-FleetDeployment.ps1`; see the `patch-agent-fleet` and `vm-revert-snapshot` skills.

## Config / files
- Fleet roster: `deploy/fleet-inventory.json`.
- Agent side: `WindowsUpdate` section in the agent's `appsettings.json`.
- Revert credentials come from the shared automation account (see the `vm-revert-snapshot` skill), never from files in the repo.

## How to test
```powershell
dotnet test TestControllerGrpc.Tests --filter "FullyQualifiedName~Maintenance"
```

## Known gaps
- The UI update install is a full persisted operation now (`MachineUpdateOperation`: precheck, out of rotation, search, install, reboot if needed, verify, quarantine on failure), gated by `Fleet_InstallUpdates`. The "Partial" rows for that path in [FLEET-UPDATE-SNAPSHOT.md](FLEET-UPDATE-SNAPSHOT.md) are out of date.
- Golden-image refresh is **not reachable**: `GoldenImageRefreshOperation` exists but `IGoldenImageRefreshOperation` is deliberately not registered (`FleetMaintenanceExtensions`), and the trigger from [NodeUpdateInstaller_Build_Spec.md](NodeUpdateInstaller_Build_Spec.md) Part 4 (`StartRefreshAsync`, `POST /refresh`, `AllowBaselineReplace` flag) is not built.

## Links
- [FleetUpdateOrchestration-Design.md](FleetUpdateOrchestration-Design.md), [NodeUpdateInstaller_Build_Spec.md](NodeUpdateInstaller_Build_Spec.md), [AgentUpdateButton_Requirements.md](AgentUpdateButton_Requirements.md), [FLEET-UPDATE-SNAPSHOT.md](FLEET-UPDATE-SNAPSHOT.md)
- UI spec: [mockups/FleetRevert-UI-Spec.html](mockups/FleetRevert-UI-Spec.html)
- Prompts: [prompts/FleetRevert-Workflow-and-CopilotPrompts.md](prompts/FleetRevert-Workflow-and-CopilotPrompts.md), [prompts/FleetMaintenance_Hardening_Prompt.md](prompts/FleetMaintenance_Hardening_Prompt.md)
- Operations: [AGENT_FLEET_PATCH_SETUP.md](../../operations/AGENT_FLEET_PATCH_SETUP.md)
