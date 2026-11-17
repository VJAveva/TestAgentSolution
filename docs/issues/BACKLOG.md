# Backlog - issues found while building ContextMenu P3 to P8

Found along the way and deliberately NOT fixed (fix only what breaks the build or crashes the app).
Format: phase | file:line | one-line description.

| Phase | Where | Issue |
|---|---|---|
| P3 | TestControllerGrpc/ViewModels/MainViewModel.InitParameters.cs:280 | Apply Changes on an Initialize node writes the parameter file to disk; Undo restores the WatchList only, not that file. |
| P3 | TestControllerGrpc/ViewModels/MainViewModel.Helpers.cs:221 | Undo/Redo rebuild the trees through LoadTokensFromConfig, which clears every template's chosen pipeline context and the tree selection. |
| P3 | TestControllerGrpc/ViewModels/MainViewModel.SmartEdit.cs:92 | Field edits typed in Node Properties are not undoable until Apply Changes is pressed (they only mark the WatchList dirty). |
| P3 | TestControllerGrpc/ViewModels/MainViewModel.XmlEditor.cs:130 | Mojibake in a window title ("WatchList XML Editor ∩┐╜ Full"); same garbled dash appears in several log messages in ImportExport.cs and XmlEditor.cs. |
| P3 | TestController.WebApi/Endpoints/WatchListEndpoints.cs:21 | POST /api/watchlist/refresh is still open to any signed-in user (REG-006 Partial). |
| P3 | docs/issues/ISSUE_Flaky_Lock_And_Debounce_Tests.md (branch fix/flaky-lock-debounce) | The flaky lock/debounce test fix is on an unmerged branch; FleetMaintenanceServiceTests timeout is in the same family and not fixed. |
| P3 | (new task, after P3) | Window smoke test: open every separate window (Pipeline Builder, Change Password, Login, Pre-flight, ...) on a test host with fake services. Deferred by request. |
| P4 | TestControllerGrpc/ViewModels/MainViewModel.EditRights.cs:24 | WPF edits are local file writes, so they are gated by CapabilityChecker (cached role + assignments), not a server round trip; a stale assignment list allows or denies until the next /api/auth/me refresh. |
| P4 | TestControllerGrpc/ViewModels/MainViewModel.cs:768 | Editing a Library template is not blocked while a pipeline that Refs it is running (only the pipeline's own edits are). |
| P4 | TestControllerGrpc/Views/MainWindow.DragDrop.cs:84 | Drag-and-drop shows a valid drop target even when the edit is blocked; the drop is refused only after release (logged). |
| P4 | TestControllerGrpc.Tests/Rbac/*Tests.cs | TestOptionsMonitor / TestDbContextFactory are copied privately into 10+ test classes; one shared helper would do. |
| P5 | docs/features/ContextMenu/ContextMenu_Requirement.md 2.6 | Not built: Add Restart step after, Add Settle wait after, Change agent... (the exact restart/settle commands are not defined anywhere; agent is editable in Node Properties). |
| P5 | docs/features/ContextMenu/ContextMenu_Requirement.md 2.2 | Not built for WatchItem: Duplicate pipeline..., the blocked-Rename message with a Duplicate button, Edit XML of this group. |
| P5 | TestControllerGrpc/Views/MainWindow.xaml:36 | Delete is still a window-wide key (rule 10 says tree focus only); a TextBox handles its own Delete, other controls may not. |
| P5 | TestControllerGrpc/Views/MainWindow.xaml (Execution ribbon) | The ribbon's Trigger All button still runs without a confirm; only the keyboard path (Ctrl+Shift+F5) asks. |
| P5 | TestControllerGrpc/ViewModels/MainViewModel.NodeEdits.cs:46 | Structural edits rebuild the parent's rows, so the rows below lose their expanded/collapsed state and any run status shown on them. |
| P6 | TestControllerGrpc/Views/MainWindow.xaml (WatchListTreeView) | Double-click on a template call does not open Edit template (2.8 asks for double-click and Enter; only Enter is wired - double-click already toggles expand). |
| P6 | TestControllerGrpc/ViewModels/MainViewModel.TemplateNavigation.cs:52 | Back to pipeline is lost after Undo/Redo or a reload (the tree is rebuilt, so the remembered node no longer exists); it logs a warning instead. |
| P6 | TestControllerGrpc/ViewModels/MainViewModel.TemplateNavigation.cs:40 | The banner stays visible when the user leaves the template by other means (selecting another template); it only closes via its buttons. |
| P7 | TestControllerGrpc.Core/Models/WatchListConfig.cs:600 | Open last output shows the result summary (outcome, exit code, error) only: an action's output lines are not stored on ActionExecutionResult, just streamed to the Execution Log. |
| P7 | TestControllerGrpc/ViewModels/MainViewModel.RunAdditions.cs:27 | Run from here uses the whole-pipeline pre-flight; section 3 asks for pre-flight limited to the selected nodes plus Initialize. |
| P7 | TestControllerGrpc/ViewModels/MainViewModel.RunAdditions.cs:27 | Run from here is offered on pipeline groups/actions only - not inside a template call or in the Library. |
| P7 | TestControllerGrpc/ViewModels/MainViewModel.Execution.cs (TriggerEvent) | The partial-run marker is in the trigger name and the log line; the session/history views do not show a separate "partial" badge. |
| P8 | TestController.Api/Controllers/WatchListController.cs (GetNodeMenu) | On the standalone WebApi host, "is running" comes from its own session manager, which does not see runs started on the WPF controller, so Stop may not replace Run there. |
| P8 | TestController.Api/Controllers/WatchListController.cs (GetNodeMenu) | Ref rows and Initialize rows get no web menu (Ref keeps its existing "Run template" button; Initialize is not addressable by NodeAddressing). Template-mirrored children are not in the web tree. |
| P8 | TestController.WebClient/src/components/watchlist/NodeContextMenu.tsx | The web menu has no Open last output, Run from here or Retry Failed yet; keyboard (Shift+F10 / menu key) does not open it. |
| P8 | TestController.WebClient/src/components/watchlist/NodeContextMenu.tsx | The menu is placed at the cursor with no viewport clamping, so it can open partly off-screen near the bottom/right edge. |
| P8 | TestController.WebClient (eslint) | `npx eslint` fails: no eslint.config.js in the WebClient (ESLint 10 needs flat config), so web code is not linted. |
| P8 | TestController.WebApi.Tests/Execution/NodeRunEndpointTests.cs (RunNode_Should_ReturnPipelineLocked_When_HeldByAnotherUser) | Flaky under the full suite (failed once in 817; 3/3 isolated, full re-run 817/817). Likely shared lock state across fixtures - same family as the controller lock flakes. |
| PE1 | TestControllerGrpc.Core/Menus/NodeMenuProvider.cs (PipelineEditorIds) | Run items are left out of the editor's structure menu: the editor holds unsaved changes, so a run would run the saved pipeline. Revisit if users ask for "save and run". |
| PE2 | TestControllerGrpc.Core/Services/WatchListXmlParser.cs:594 | A non-numeric Timeout / PollInterval / MaxRetries is silently read as the default (AttrInt); the validator does not flag it, so the form cannot show an error under the field. |
| PE2 | TestControllerGrpc/Views/RawXmlEditorWindow.xaml (Properties) | Tokens are not coloured inside the text box itself; each field lists its tokens as chips underneath (red = unresolved, hover = value and source). |
| PE2 | TestControllerGrpc.Core/PipelineEditor/PipelineNodeForms.cs | Not built yet: Pipeline "Duplicate" button, Initialize "Open file" / "Reload" buttons, Ref "Edit template" / "Copy for this pipeline only" / "Replace" (Part 3). |
| PE2 | TestControllerGrpc.Core/Services/WatchListXmlEditorAnalyzer.cs (GetProfiles, line 338) | The Profile dropdown lists the profiles of the LAST ParameterFile in the document, not the selected Initialize node's file (matters in the full WatchList editor). |
| PE3 | TestControllerGrpc/ViewModels/PipelineEditorSessionViewModel.cs (Save) | "Changed by someone else" cannot name the user or time (WatchList.xml keeps no per-change author); the message says changed elsewhere, and Compare comes with What changed in Part 5. |
| PE3 | TestControllerGrpc/ViewModels/WatchItemXmlEditorViewModel.Templates.cs (ShowImpactProblems) | Problems a template change causes in other pipelines are listed on line 1 of the template tab and are cleared by the next background check (after typing); press Save again to see them. |
| PE3 | TestControllerGrpc.Core/Editing/NodeEdits.cs:59 (GroupAsTemplate) | The main tree's Save group as template copies only the group's children and leaves the group in place; the Pipeline Editor moves the whole group (keeps its execution type) and leaves a Ref. The two should agree. |
| PE3 | TestControllerGrpc/ViewModels/WatchItemXmlEditorViewModel.Templates.cs (AddTemplateSteps) | A template call inside a template step is shown but not expanded further (no nested expansion). |
| PE3 | TestControllerGrpc/Views/TemplateXmlEditorWindow.xaml | The old Template XML Editor window still exists (decision 4: retire it once Part 3 is accepted). |
| PE4 | TestControllerGrpc/ViewModels/WatchItemXmlEditorViewModel.FormView.cs | The chosen view (Form / XML), wrap setting, pane widths and font size are not remembered between sessions (section 1 asks for per-user layout). |
| PE4 | TestControllerGrpc/Views/RawXmlEditorWindow.xaml (FormView) | Step cards are read-only summaries; editing happens in Properties. Cards cannot be reordered by dragging (out of scope in section 10). |
