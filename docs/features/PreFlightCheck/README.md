---
feature: PreFlightCheck
status: Implemented
owner: Vinodkumar Javvadi
code: [TestControllerGrpc.Core/Preflight, TestController.Api/Controllers/PreflightController.cs, TestController.WebApi/Endpoints/PreflightEndpoints.cs, TestControllerGrpc/Views/Dialogs/PreflightDialog.xaml, TestControllerGrpc/ViewModels/MainViewModel.Preflight.cs]
tests: [TestControllerGrpc.Tests/Preflight, TestControllerGrpc.Tests/ViewModels/PreflightDialogViewModelTests.cs, TestControllerGrpc.Tests/Features/FeatureUsagePreflightTests.cs]
last-verified: 2026-11-08
---

# Pre-flight check

## What it does
- Before a run, checks what would make it fail, without touching any machine.
- **Settings**: parameter file, unresolved tokens, steps that call a turned-off feature.
- **Controller files**: watch folder, scripts, `WASSmokeTest.dll`.
- **Install sources** and **Agents**: online, idle, not locked, not in maintenance.
- **Structure** and **Disk**.
- A failure blocks the run; a warning asks.

## How to use
- WPF: run a pipeline or node; the pre-flight dialog opens when there is anything to report.
- REST: `POST /api/preflight/{tag}`.

## Config / files
- Reads the pipeline's layered `*-pipeline-config.json` (global, profiles, pipelines) and `GlobalVariables.json`.
- Schema and sample: [pipeline-config.schema.json](pipeline-config.schema.json), [pipeline-config.sample.json](pipeline-config.sample.json). Authoring workflow: `.github/skills/pipeline-config-json/SKILL.md`.
- The config guide under `docs/PreFlightCheck/` is git-ignored on purpose and is not part of the repo.

## How to test
```powershell
dotnet test TestControllerGrpc.Tests --filter "FullyQualifiedName~Preflight"
```

## Known gaps
- None recorded. Fix guidance for each failure: `.github/skills/preflight-triage/SKILL.md`.

## Links
- Test plan: [WATCHLIST-TEST-PLAN-JVGR22.md](WATCHLIST-TEST-PLAN-JVGR22.md)
