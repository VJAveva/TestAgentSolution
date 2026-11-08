---
feature: ClientNodeExecution
status: Implemented
owner: Vinodkumar Javvadi
code: [TestController.WebClient/src/components/watchlist/NodeRunDialog.tsx, TestController.Api/Controllers/ExecutionController.cs]
tests: [TestController.WebClient/src/components/watchlist/NodeRunDialog.test.tsx, TestController.WebApi.Tests/Execution/NodeRunEndpointTests.cs, TestController.WebApi.Tests/Execution/NodeRunAuthzTests.cs]
last-verified: 2026-11-08
---

# Node-wise execution from the web client

## What it does
- Runs one node of a pipeline (group, action or template), not only the whole pipeline, from the web client.
- Optionally includes the pipeline's Initialize so tokens resolve; the build defaults to the pipeline's effective build.
- Uses the same permission (`Pipeline_Trigger`) and pipeline lock as a full run.

## How to use
- Web: WatchList tree, then **Run** on a node, choose the scope in the dialog.
- REST: `GET /api/execution/pipelines/{watchItemTag}/nodes`, `POST /api/execution/pipelines/{watchItemTag}/nodes/run`.

## Config / files
- None; uses the pipeline's parameter file.

## How to test
```powershell
dotnet test TestController.WebApi.Tests --filter "FullyQualifiedName~NodeRun"
```
Web: `npx vitest run src/components/watchlist/NodeRunDialog.test.tsx` in `TestController.WebClient`.

## Known gaps
- None recorded.

## Links
- [WebClient_NodeWise_Execution_Spec.md](WebClient_NodeWise_Execution_Spec.md), mockup [mockups/WebClient_NodeWise_Execution_Mockup.html](mockups/WebClient_NodeWise_Execution_Mockup.html)
