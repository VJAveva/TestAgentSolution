---
feature: GlobalVariables
status: Implemented
owner: Vinodkumar Javvadi
code: [TestControllerGrpc.Core/Models/GlobalVariablesConfig.cs, TestControllerGrpc.Core/Services/ParameterResolver.cs, TestControllerGrpc.Core/Services/ParameterRank.cs, TestControllerGrpc/Views/Dialogs/GlobalVariablesEditorWindow.xaml, TestControllerGrpc/ViewModels/MainViewModel.GlobalVariables.cs]
tests: [TestControllerGrpc.Tests/Services/GlobalVariablesLayerTests.cs]
last-verified: 2026-11-18
---

# Global Variables

## What it does
- One shared `GlobalVariables.json` selects a single build (`_ReleaseName`, `_BuildNumber`, `_DropLocation`) for every pipeline of that release.
- It is one layer in token resolution (`ParameterRank`): Global 10 -> Profile 20 -> GlobalVars 30 -> Pipeline entry 35 -> Trigger 40 -> Run override 50. A pipeline's own `pipelines` entry and a trigger file both win over it.
- Applies only to pipelines whose release matches `_ReleaseName`; others keep their own parameter file values.
- **Apply Build to All Pipelines** leaves pipelines whose own entry sets `_BuildNumber`/`_DropLocation` unchanged and lists them as "not changed: has its own build".

## How to use
- WPF: tree context menu **Edit Global Variables…** (hidden when the `GlobalVariables` feature is off).

## Config / files
- Default path `C:\TestControllerService\Parameters\GlobalVariables.json` (`GlobalVariablesConfig.DefaultPath`); a WatchList can point elsewhere with its `GlobalVariablesFile` attribute.
- Sample: [GlobalVariables.json](GlobalVariables.json).

## How to test
```powershell
dotnet test TestControllerGrpc.Tests --filter "FullyQualifiedName~GlobalVariablesLayerTests"
```

## Known gaps
- Choosing a different GlobalVariables file is undoable (Ctrl+Z on the tree); "Use this build" writes to every parameter file on disk and is not undoable.

## Links
- [GlobalVariables_Requirement.md](GlobalVariables_Requirement.md), mockup [mockups/GlobalVariables_Dialog_Mockup.html](mockups/GlobalVariables_Dialog_Mockup.html), prompt [prompts/GlobalVariables_Copilot_Prompt.md](prompts/GlobalVariables_Copilot_Prompt.md)
