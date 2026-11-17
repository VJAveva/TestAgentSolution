---
feature: PipelineEditor
status: In progress
owner: Vinodkumar Javvadi
code: [TestControllerGrpc.Core/PipelineEditor, TestControllerGrpc/ViewModels/WatchItemXmlEditorViewModel.cs, TestControllerGrpc/ViewModels/WatchItemXmlEditorViewModel.Structure.cs, TestControllerGrpc/ViewModels/WatchItemXmlEditorViewModel.Properties.cs, TestControllerGrpc/Views/RawXmlEditorWindow.xaml]
tests: [TestControllerGrpc.Tests/PipelineEditor, TestControllerGrpc.Tests/ViewModels/WatchItemXmlEditorViewModelTests.cs]
last-verified: 2026-11-17
---

# Pipeline Editor

## What it does
- Edits one pipeline (WatchItem) or the whole WatchList in three panes: Structure tree, XML, Properties.
- The tree, the XML caret and Properties always show the same node; Problems jump to the node at fault.
- Right-click in the tree gives the same menu rules as the main tree (move, duplicate, delete, skip / include,
  execution type). Every change is one undo step in the editor; only the edited node's text changes, so XML
  comments, formatting and line endings elsewhere stay exactly as typed.
- Properties is a form per node type (pipeline, event, initialize, group, command action, mail action, template
  call). Each field edits its XML attribute at once (one Ctrl+Z step); editing the XML updates the form.
  Problems show under the field they are about. Tokens in a field are listed under it with value and source;
  typing `[_` opens the token list. The agent dropdown shows each agent's status; steps whose agent is offline
  or not registered get a badge in the tree. A collapsed node's badge counts the problems hidden under it.
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
- Secrets: SecretEnv shows names only, Password is masked, and a field whose value looks like a credential is
  shown redacted and read-only (edit it in the XML or move it to SecretEnv).
- Parts 3-5 of the requirement are not built yet (template tabs, form view, parameters tab, diff).
  See [PipelineEditor_ActionPlan.md](PipelineEditor_ActionPlan.md).

## Links
- [PipelineEditor_Requirement.md](PipelineEditor_Requirement.md), [PipelineEditor_ActionPlan.md](PipelineEditor_ActionPlan.md)
