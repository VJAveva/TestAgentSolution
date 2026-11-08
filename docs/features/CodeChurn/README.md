---
feature: CodeChurn
status: Implemented
owner: Vinodkumar Javvadi
code: [TestControllerGrpc.Core/Ado, TestController.Api/Controllers/ImpactController.cs, TestControllerGrpc/ViewModels/Regression/RegressionViewModel.cs, TestControllerGrpc/Views/Regression/RegressionWindow.xaml, TestController.WebClient/src/components/regression/RegressionView.tsx]
tests: [TestController.WebApi.Tests/Regression, TestControllerGrpc.Tests/Features/CodeChurnRepositoryTests.cs]
last-verified: 2026-11-08
---

# Code Churn (Regression tab)

## What it does
- Shows what changed in Azure DevOps per component and branch: commits, pull requests, work items and files.
- Classifies each component as Runtime or Config and builds a regression plan; reports export to Excel, CSV or HTML, or go by email.
- Which repositories and branches are scanned can be team configuration in `Features.json` (`CodeChurn:Repositories`).

## How to use
- WPF: **CodeChurn** ribbon button; web: Code Churn tab. Both have repository and branch pickers.
- REST: `/api/impact/*` (for example `/api/impact/consolidated`, `/api/impact/branches`, `/api/impact/repositories`).

## Config / files
- `Ado` section (organization, projects, auth mode, `SecretEnvVarName`, `ComponentMapPath`, `ComponentCategoriesPath`, `RepositoryPrefix`), `Ado:Llm` for AI summaries, `CodeChurn:WorkItemPolicy`.
- Component map `docs/impact/component-usecase-map.v1.json` and categories `docs/impact/component-categories.xml` (must stay in `docs/impact/`; the code looks for them there).
- The PAT is read from the environment variable named by `Ado:SecretEnvVarName`, never from a file.

## How to test
```powershell
dotnet test TestController.WebApi.Tests --filter "FullyQualifiedName~Regression"
dotnet test TestControllerGrpc.Tests --filter "FullyQualifiedName~CodeChurn"
```

## Known gaps
- Azure DevOps calls use one controller credential; per-repository `CredentialRef` is only checked for presence.
- A repository `Url` must be in the organization and project Code Churn reads.

## Links
- [Azure-DevOps-Integration-Guide.md](Azure-DevOps-Integration-Guide.md), [RegressionTab-UI-Spec.md](RegressionTab-UI-Spec.md), [WebClient-Parity-Architecture.md](WebClient-Parity-Architecture.md)
- Shared with Impact (kept in AzureIntegration): [FEATURE-ARCHITECTURE.md](../AzureIntegration/FEATURE-ARCHITECTURE.md), [ProjectlevelComponents.md](../AzureIntegration/ProjectlevelComponents.md), [ImpactMapping-Copilot-BuildGuide.md](../AzureIntegration/ImpactMapping-Copilot-BuildGuide.md)
- Prompts: [prompts/Copilot-Prompts-AdoIntegration.md](prompts/Copilot-Prompts-AdoIntegration.md), [prompts/CodeChurn-Header-Compaction-CopilotPrompts.md](prompts/CodeChurn-Header-Compaction-CopilotPrompts.md)
- Mockups: [mockups/](mockups/)
