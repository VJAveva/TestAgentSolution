---
feature: AzureIntegration
status: Implemented
owner: Vinodkumar Javvadi
code: [TestControllerGrpc.Core/Ado/AdoClient.cs, TestControllerGrpc.Core/Ado/AdoOptions.cs, TestControllerGrpc.Core/Ado/AdoServiceCollectionExtensions.cs, TestControllerGrpc.Core/Ado/PatTokenProvider.cs, TestControllerGrpc.Core/Ado/InteractiveTokenProvider.cs, TestControllerGrpc.Core/Ado/ServicePrincipalTokenProvider.cs]
tests: [TestController.WebApi.Tests/Regression/AdoUserTokenTests.cs, TestController.WebApi.Tests/Regression/AdoErrorBodySummaryTests.cs, TestController.WebApi.Tests/Impact/AdoImpactWorkItemClientTests.cs]
last-verified: 2026-11-08
---

# Azure DevOps integration (shared by Code Churn and Impact)

## What it does
- One Azure DevOps client and credential setup used by [Code Churn](../CodeChurn/README.md) and [Impact](../Impact/README.md).
- Auth modes: PAT (from an environment variable), service principal, or interactive sign-in; web users can pass their own delegated token.
- This folder keeps the design documents that cover both features; documents about only one of them live in that feature's folder.

## How to use
- Configure the `Ado` section on the controller (and on the web tier if it serves Code Churn). Check with `GET /api/impact/health`.

## Config / files
- `Ado` section: `Enabled`, `Organization`, `Project`, `OmiProject`, `AuthMode`, `SecretEnvVarName`, `TenantId`, `ClientId`, `WebClientId`.
- Secrets only by environment-variable name. After changing a Machine-scope variable, run `iisreset` for the web tier.

## How to test
```powershell
dotnet test TestController.WebApi.Tests --filter "FullyQualifiedName~Ado"
```

## Known gaps
- None recorded here; see the Code Churn and Impact READMEs.

## Links
- [FEATURE-ARCHITECTURE.md](FEATURE-ARCHITECTURE.md), [ImpactMapping-Copilot-BuildGuide.md](ImpactMapping-Copilot-BuildGuide.md), [ProjectlevelComponents.md](ProjectlevelComponents.md)
