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
| P5 | docs/features/ContextMenu_Requirement.md 2.6 | Not built: Add Restart step after, Add Settle wait after, Change agent... (the exact restart/settle commands are not defined anywhere; agent is editable in Node Properties). |
| P5 | docs/features/ContextMenu_Requirement.md 2.2 | Not built for WatchItem: Duplicate pipeline..., the blocked-Rename message with a Duplicate button, Edit XML of this group. |
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
