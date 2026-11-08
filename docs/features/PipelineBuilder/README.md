---
feature: PipelineBuilder
status: Implemented
owner: Vinodkumar Javvadi
code: [TestControllerGrpc.Core/PipelineBuilder, TestControllerGrpc/ViewModels/PipelineBuilder/PipelineBuilderViewModel.cs, TestControllerGrpc/Views/Dialogs/PipelineBuilderWindow.xaml, pipelines/recipes, pipelines/templates]
tests: [TestControllerGrpc.Tests/PipelineBuilder]
last-verified: 2026-11-08
---

# Pipeline Builder

## What it does
- Guided authoring of a new pipeline (WatchItem) from a recipe in `pipelines/recipes` and XML templates in `pipelines/templates`.
- Previews the generated WatchItem and parameter-file entries, validates the tag and targets, then writes both.
- Gated by the `Pipeline_Author` permission and the `PipelineBuilder` feature toggle.

## How to use
- WPF: tree context menu **New Pipeline…** (hidden when the `PipelineBuilder` feature is off).

## Config / files
- Recipes: `pipelines/recipes/*.json`; templates: `pipelines/templates/*.xml` (copied to the build output).
- Writes `WatchList.xml` and the release's `*-pipeline-config.json` under `C:\TestControllerService\Parameters`.

## How to test
```powershell
dotnet test TestControllerGrpc.Tests --filter "FullyQualifiedName~PipelineBuilder"
```

## Known gaps
- Web client entry point is planned but **not built**: [PipelineBuilder_Requirement.md](PipelineBuilder_Requirement.md) §10 step 6 says "WPF panel, then React over the same engine". Today the web client only knows the `PipelineBuilder` feature key (`featureStore.ts`); there is no REST endpoint or page.

## Links
- [PipelineBuilder_Requirement.md](PipelineBuilder_Requirement.md), [WatchItemBuilder_Implementation_Spec.md](WatchItemBuilder_Implementation_Spec.md), [INSTALL-PLAN-FEASIBILITY.md](INSTALL-PLAN-FEASIBILITY.md)
- Prompt: [prompts/PipelineBuilder_Copilot_Prompt.md](prompts/PipelineBuilder_Copilot_Prompt.md)
