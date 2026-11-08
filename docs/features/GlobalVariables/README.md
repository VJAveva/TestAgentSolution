---
feature: GlobalVariables
status: Implemented
owner: Vinodkumar Javvadi
code: [TestControllerGrpc.Core/Models/GlobalVariablesConfig.cs, TestControllerGrpc.Core/Services/ParameterResolver.cs, TestControllerGrpc.Core/Services/ParameterRank.cs, TestControllerGrpc/Views/Dialogs/GlobalVariablesEditorWindow.xaml, TestControllerGrpc/ViewModels/MainViewModel.GlobalVariables.cs]
tests: [TestControllerGrpc.Tests/Services/GlobalVariablesLayerTests.cs]
last-verified: 2026-11-08
---

# Global Variables

## What it does
- One shared `GlobalVariables.json` selects a single build (`_ReleaseName`, `_BuildNumber`, `_DropLocation`) for every pipeline of that release.
- It is one layer in token resolution (`ParameterRank`); a trigger file's build still wins over it.
- Applies only to pipelines whose release matches `_ReleaseName`; others keep their own parameter file values.

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
- None recorded.

## Links
- [GlobalVariables_Requirement.md](GlobalVariables_Requirement.md), mockup [mockups/GlobalVariables_Dialog_Mockup.html](mockups/GlobalVariables_Dialog_Mockup.html), prompt [prompts/GlobalVariables_Copilot_Prompt.md](prompts/GlobalVariables_Copilot_Prompt.md)
