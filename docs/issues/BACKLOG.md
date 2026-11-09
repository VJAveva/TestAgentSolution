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
