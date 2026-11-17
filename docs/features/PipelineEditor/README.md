---
feature: PipelineEditor
status: In progress
owner: Vinodkumar Javvadi
code: [TestControllerGrpc.Core/PipelineEditor, TestControllerGrpc/ViewModels/WatchItemXmlEditorViewModel.cs, TestControllerGrpc/ViewModels/WatchItemXmlEditorViewModel.Structure.cs, TestControllerGrpc/ViewModels/WatchItemXmlEditorViewModel.Properties.cs, TestControllerGrpc/ViewModels/WatchItemXmlEditorViewModel.Templates.cs, TestControllerGrpc/ViewModels/WatchItemXmlEditorViewModel.FormView.cs, TestControllerGrpc/ViewModels/PipelineEditorSessionViewModel.cs, TestControllerGrpc/Views/RawXmlEditorWindow.xaml, TestControllerGrpc/Views/Dialogs/TemplateNameDialog.xaml]
tests: [TestControllerGrpc.Tests/PipelineEditor, TestControllerGrpc.Tests/ViewModels/WatchItemXmlEditorViewModelTests.cs]
last-verified: 2026-11-17
---

# Pipeline Editor

## What it does
- Edits one pipeline (WatchItem) or the whole WatchList in three panes: Structure tree, editor, Properties.
- The editor pane has two views of the same text: **Form** (default) shows the pipeline as step cards in order
  (name, agent, command, timeout), nested by group; **XML** is the full editor with folding, checks, completion
  and an optional long-line wrap. Switching never loses edits. While the XML is not well-formed the form shows
  "Fix the XML error on line N to use the form" and every field is read-only until it is fixed.
- The tree, the XML caret and Properties always show the same node; Problems jump to the node at fault.
- Right-click in the tree gives the same menu rules as the main tree (move, duplicate, delete, skip / include,
  execution type). Every change is one undo step in the editor; only the edited node's text changes, so XML
  comments, formatting and line endings elsewhere stay exactly as typed.
- Properties is a form per node type (pipeline, event, initialize, group, command action, mail action, template
  call). Each field edits its XML attribute at once (one Ctrl+Z step); editing the XML updates the form.
  Problems show under the field they are about. Tokens in a field are listed under it with value and source;
  typing `[_` opens the token list. The agent dropdown shows each agent's status; steps whose agent is offline
  or not registered get a badge in the tree. A collapsed node's badge counts the problems hidden under it.
- Templates on the same screen: a template call (Ref) expands to the template's steps, greyed and read-only.
  **Edit template** (menu, double-click or Enter) opens a `Template: <ID>` tab with the step you came from
  selected and a "used by N pipelines" banner; without Template_Edit the tab is read-only.
  **Copy for this pipeline only** replaces the Ref with the template's steps (after a confirm; Ctrl+Z restores).
  **Save group as template...** names the template, moves the group into it and leaves a Ref behind.
- Save checks and saves every open tab together and lists what it will write. Before a template is written the
  checker runs on every pipeline that uses it; a new error in any of them blocks the save and names the pipeline.
  If the pipeline or a template changed since the editor opened (another user, or WatchList.xml reloaded), nothing
  is overwritten: reload the latest version or keep editing.
- The result is applied to the WatchList as one undo step; the file is written by the main Save.

## How to use
- WPF: select a pipeline -> "Edit XML" (or the full WatchList XML editor).
- Keys: Ctrl+Z / Ctrl+Y in the editor, Shift+F10 for the tree menu, Ctrl+Plus / Ctrl+Minus for font size.

## Config / files
- Opens only with edit rights (Pipeline_Edit for the pipeline, admin for the whole WatchList) - `EnsureCanEdit`.
  Template tabs need Template_Edit to change anything; it is checked again when the result is applied.
- No config keys.

## How to test
```powershell
dotnet test TestControllerGrpc.Tests -c Release --filter "FullyQualifiedName~PipelineEditor|FullyQualifiedName~WatchItemXmlEditorViewModelTests"
```

## Known gaps
- Secrets: SecretEnv shows names only, Password is masked, and a field whose value looks like a credential is
  shown redacted and read-only (edit it in the XML or move it to SecretEnv).
- Part 5 of the requirement is not built yet (parameters tab, quick actions, What changed / Compare).
  See [PipelineEditor_ActionPlan.md](PipelineEditor_ActionPlan.md).

## Links
- [PipelineEditor_Requirement.md](PipelineEditor_Requirement.md), [PipelineEditor_ActionPlan.md](PipelineEditor_ActionPlan.md)
