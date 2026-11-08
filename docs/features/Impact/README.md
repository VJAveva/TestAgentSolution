---
feature: Impact
status: Implemented
owner: TODO
code: [TestController.Impact, TestControllerGrpc.Core/Impact, TestController.WebApi/Endpoints/ImpactMappingEndpoints.cs, docs/impact/component-usecase-map.v1.json, docs/impact/component-categories.xml]
tests: [TestController.WebApi.Tests/Impact]
last-verified: 2026-11-08
---

# Impact mapping: impacted test cases for a change

## What it does
- Builds a search index of test cases and matches changed components to the test cases most likely affected (keyword scoring plus optional embeddings).
- Ranks and calibrates the matches, records outcomes, and can write a run plan.
- Index maintenance and outcome persistence run as background services, skipped when the `ImpactMapping` feature is off.

## How to use
- In Code Churn, expand a component row to see its impacted test cases.
- REST (web tier): `/api/impact-mapping/run`, `/api/impact-mapping/index/status`, `POST /api/impact-mapping/index/rebuild` (admin).
- Rebuild or prepare the index with `deploy/Rebuild-ImpactIndex.ps1` and `deploy/Prepare-ImpactIndexStorage.ps1`.

## Config / files
- `ImpactMapping` section (`ImpactMappingOptions`).
- Data files that **must stay** in `docs/impact/`: `component-usecase-map.v1.json`, `component-categories.xml`; the csproj copies them and `FindDocsImpactFile` looks for them there.

## How to test
```powershell
dotnet test TestController.WebApi.Tests --filter "FullyQualifiedName~Impact"
```

## Known gaps
- The WAND retrieval speed-up is a plan, not built: [WAND-Retrieval-Action-Plan.md](WAND-Retrieval-Action-Plan.md).

## Links
- [Algorithm-As-Built.md](Algorithm-As-Built.md), [Algorithm-Schema.md](Algorithm-Schema.md), [ImpactDb-Deep-Dive.md](ImpactDb-Deep-Dive.md), [Regression-Selection-Algorithm.md](Regression-Selection-Algorithm.md)
- Shared with Code Churn (kept in AzureIntegration): [ImpactMapping-Copilot-BuildGuide.md](../AzureIntegration/ImpactMapping-Copilot-BuildGuide.md), [FEATURE-ARCHITECTURE.md](../AzureIntegration/FEATURE-ARCHITECTURE.md), [ProjectlevelComponents.md](../AzureIntegration/ProjectlevelComponents.md)
- Prompt: [prompts/ImpactIndex-Storage-CopilotPrompts.md](prompts/ImpactIndex-Storage-CopilotPrompts.md)
