---
feature: PipelineEditor
status: In progress
owner: Vinodkumar Javvadi
code: [TestControllerGrpc.Core/PipelineEditor, TestControllerGrpc/ViewModels/WatchItemXmlEditorViewModel.cs, TestControllerGrpc/ViewModels/WatchItemXmlEditorViewModel.Structure.cs, TestControllerGrpc/Views/RawXmlEditorWindow.xaml]
tests: [TestControllerGrpc.Tests/PipelineEditor, TestControllerGrpc.Tests/ViewModels/WatchItemXmlEditorViewModelTests.cs]
last-verified: 2026-11-17
---

# Pipeline Editor

## What it does
- Edits one pipeline (WatchItem) or the whole WatchList in three panes: Structure tree, XML, Properties.
- The tree, the XML caret and Properties always show the same node; Problems jump to the node at fault.
- Right-click in the tree gives the same menu rules as the main tree (move, duplicate, delete, skip / include,
  execution type). Every change is one undo step in the editor, and XML comments are kept.
- Save applies the edited pipeline to the WatchList as one undo step; the file is written by the main Save.

## How to use
- WPF: select a pipeline -> "Edit XML" (or the full WatchList XML editor).
- Keys: Ctrl+Z / Ctrl+Y in the editor, Shift+F10 for the tree menu, Ctrl+Plus / Ctrl+Minus for font size.

## Config / files
- Opens only with edit rights (Pipeline_Edit for the pipeline, admin for the whole WatchList) - `EnsureCanEdit`.
- No config keys.

## How to test
```powershell
dotnet test TestControllerGrpc.Tests -c Release --filter "FullyQualifiedName~PipelineEditor|FullyQualifiedName~WatchItemXmlEditorViewModelTests"
```

## Known gaps
- Properties are read-only in Part 1 (forms come in Part 2); secret values are masked, SecretEnv shows names only.
- Parts 2-5 of the requirement are not built yet (forms, template tabs, form view, parameters tab, diff).
  See [PipelineEditor_ActionPlan.md](PipelineEditor_ActionPlan.md).

## Links
- [PipelineEditor_Requirement.md](PipelineEditor_Requirement.md), [PipelineEditor_ActionPlan.md](PipelineEditor_ActionPlan.md)
