---
feature: FeatureToggles
status: Implemented
owner: TODO
code: [TestControllerGrpc.Core/Features, TestController.Api/Controllers/FeaturesController.cs, TestController.Api/Security/RequireFeatureAttribute.cs, TestControllerGrpc/Views/FeatureSettingsWindow.xaml, TestController.WebClient/src/components/admin/FeatureSettingsPanel.tsx, TestController.WebClient/src/stores/featureStore.ts]
tests: [TestControllerGrpc.Tests/Features, TestController.WebApi.Tests/Features, TestController.WebClient/src/components/admin/FeatureSettingsPanel.test.tsx]
last-verified: 2026-11-08
---

# Per-team feature toggles

## What it does
- One `Features.json` per controller turns off features a team does not use: Results, FirReport, CodeChurn, ImpactMapping, PipelineBuilder, GlobalVariables, Reports.
- A missing file or key means on. Turned-off features are hidden in WPF and the web client, their APIs return 404 "Feature disabled", and their background services do not start.
- The same file holds the team's Code Churn repositories and branches (`CodeChurn:Repositories`).

## How to use
- Administrators: WPF **ADMIN > Features**, or the web **Features** tab (Secured mode). Saving makes a timestamped backup and writes an audit entry.
- REST: `GET /api/features`, `PUT /api/features`, `PUT /api/features/code-churn`.

## Config / files
- `Features:ConfigPath` (default `<controller folder>\Config\Features.json`). Sample: `deploy/Features.sample.json`.
- Team setup guide: [Team-Onboarding.md](../../getting-started/Team-Onboarding.md).

## How to test
```powershell
dotnet test TestControllerGrpc.Tests --filter "FullyQualifiedName~Features"
dotnet test TestController.WebApi.Tests --filter "FullyQualifiedName~FeatureEndpointTests"
```

## Known gaps
- Background services of a turned-off feature start only after a controller restart when the feature is turned back on.
- The IIS web tier reads its own copy unless `Features:ConfigPath` points at the controller's file.

## Links
- Requirement prompt: [prompts/Prompt_Team_Feature_Toggles.md](prompts/Prompt_Team_Feature_Toggles.md)
