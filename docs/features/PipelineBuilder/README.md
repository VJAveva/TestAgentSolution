---
feature: PipelineBuilder
status: Implemented
owner: Vinodkumar Javvadi
code: [TestControllerGrpc.Core/PipelineBuilder, TestControllerGrpc/ViewModels/PipelineBuilder/PipelineBuilderViewModel.cs, TestControllerGrpc/Views/Dialogs/PipelineBuilderWindow.xaml, TestControllerGrpc/Services/PipelineBuilderFeatureExtensions.cs, TestController.Api/Controllers/PipelineBuilderController.cs, TestController.WebApi/Services/PipelineBuilderForwardingMiddleware.cs, pipelines/recipes, pipelines/templates]
tests: [TestControllerGrpc.Tests/PipelineBuilder, TestController.WebApi.Tests/PipelineBuilder]
last-verified: 2026-11-08
---

# Pipeline Builder

## What it does
- Guided authoring of a new pipeline (WatchItem) from a recipe in `pipelines/recipes` and XML templates in `pipelines/templates`. Four steps: Target, Recipe, Agents, Review.
- Previews the generated WatchItem and parameter config, validates tag, trigger and tokens, then writes a config file plus a WatchList **fragment**. The live WatchList is never edited and nothing runs; the user imports the fragment with the existing import option.
- Gated by the `Pipeline_Author` permission (Administrator) and the `PipelineBuilder` feature toggle. Every create writes an audit entry (`Pipeline_Author`, resource = tag, reason `pipeline-fragment-created`).

## How to use
- WPF: tree context menu **New Pipeline…** (hidden when the `PipelineBuilder` feature is off).
- REST (served by the controller app; the standalone WebApi forwards `/api/pipeline-builder/*` to it):
  - `GET  /api/pipeline-builder/catalog` -> targets, recipes, agents with status, problems
  - `POST /api/pipeline-builder/preview` -> tag, trigger, `configJson`, `watchItemXml`, issues
  - `POST /api/pipeline-builder/validate` -> `canCreate`, blocking, informational, token review
  - `POST /api/pipeline-builder/create` -> written, config and fragment paths, issues (422 when blocked)
  - Body for the POSTs: `{ "targetId": "...", "recipeName": "...", "agentNames": ["..."] }`. Create rebuilds the preview on the server and never accepts a config from the client.
  - 403 without `Pipeline_Author` in Secured mode; 403 for web Create in Default mode; 404 when the feature is off or the host has no builder.
- Secret values (keys matching the parameter grid's list: password, secret, token, pwd, apikey, credential, connectionstring) are shown as `***` in every response. Values that are only token references, like `[_RcloudPassword]`, stay visible. Usernames such as `_RcloudUser` are not on that list and are shown. The files written on the server keep the real values.

## Config / files
- Recipes: `pipelines/recipes/*.json`; templates: `pipelines/templates/*.xml` (copied to the build output).
- Writes `<tag>-pipeline-config.json` beside the release config under `C:\TestControllerService\Parameters`, and `<tag>.watchitem.xml` under `PipelineBuilder:FragmentDir` (default `C:\TestControllerService\PipelineFragments`). Neither is ever overwritten: Create is refused if either exists.
- Other keys: `PipelineBuilder:ParametersRoot`, `PipelineBuilder:RecipesRoot`, `PipelineBuilder:TemplatesRoot`, `PipelineBuilder:TriggerRoot` (default `C:\Triggers`).

## How to test
```powershell
dotnet test TestControllerGrpc.Tests --filter "FullyQualifiedName~PipelineBuilder"
dotnet test TestController.WebApi.Tests --filter "FullyQualifiedName~PipelineBuilderEndpointTests"
```

## Known gaps
- React wizard (plan phase A2) is not built yet; the endpoints above are its backend.
- Step editing is out of scope for the Builder; it comes later through the XML editor's "Ask to change" ([../XmlEditor/prompts/Prompt_XmlEditor_Intelligence.md](../XmlEditor/prompts/Prompt_XmlEditor_Intelligence.md) Part 2).
- Masking is by key/attribute NAME. A password typed into a free-text field such as `Parameters` cannot be detected; use `SecretEnv` or a `[_Token]`.

## Links
- [PipelineBuilder_Requirement.md](PipelineBuilder_Requirement.md), [WatchItemBuilder_Implementation_Spec.md](WatchItemBuilder_Implementation_Spec.md), [INSTALL-PLAN-FEASIBILITY.md](INSTALL-PLAN-FEASIBILITY.md)
- Plan: [../FleetMaintenance/Action_Plan_GoldenImage_ReactBuilder.md](../FleetMaintenance/Action_Plan_GoldenImage_ReactBuilder.md)
- Prompt: [prompts/PipelineBuilder_Copilot_Prompt.md](prompts/PipelineBuilder_Copilot_Prompt.md)
