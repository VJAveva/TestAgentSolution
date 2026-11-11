---
feature: ContextMenu
status: In progress
owner: Vinodkumar Javvadi
code: [TestControllerGrpc.Core/Menus, TestControllerGrpc.Core/Editing, TestControllerGrpc/Views/MainWindow.ContextMenus.cs, TestController.Api/Controllers/WatchListController.cs, TestController.WebClient/src/components/watchlist/NodeContextMenu.tsx]
tests: [TestControllerGrpc.Tests/Menus, TestControllerGrpc.Tests/Editing, TestController.WebApi.Tests/WatchListNodeMenuTests.cs]
last-verified: 2026-11-10
---

# Context-aware right-click menu

## What it does
- Right-click on a pipeline or Library node shows only the actions that make sense for that node: run, add, edit, move, skip, delete.
- One menu definition (`NodeMenuProvider` in Core) drives both the WPF trees and the web tree.
- Every edit goes through one undoable path (Ctrl+Z / Ctrl+Y) and is checked against `Pipeline_Edit` / `Template_Edit`.
- Run additions: Run from here to end, Show resolved command (secrets masked), Open last output, Stop.

## How to use
- WPF: right-click any node in the WatchList or Library tree. Keys: F2, Ctrl+D, Ctrl+C/V, Alt+Up/Down, F5, Enter (Edit template), Ctrl+Z/Y.
- Web: right-click a WatchItem, Event, Group or Action row (run, pre-flight, stop, resolved command only; no editing).
- REST: `POST /api/watchlist/node-menu`, `POST /api/watchlist/resolved-command` (body: `watchItemTag`, `nodePath`).

## Config / files
- Permissions: `Pipeline_Edit` (assignment-scoped), `Template_Edit` (admin), `Pipeline_Trigger`, `Pipeline_Cancel`.
- Edit history: in memory, 50 steps, cleared when WatchList.xml reloads.

## How to test
```powershell
dotnet test TestControllerGrpc.Tests --filter "FullyQualifiedName~NodeMenuProviderTests|FullyQualifiedName~EditHistoryTests|FullyQualifiedName~NodeEditsTests|FullyQualifiedName~EditPermissionTests"
dotnet test TestController.WebApi.Tests --filter "FullyQualifiedName~WatchListNodeMenuTests"
```

## Known gaps
- Open issues found while building P3-P8 are in [docs/issues/BACKLOG.md](../../issues/BACKLOG.md).

## Links
- Requirement and phase plan: [ContextMenu_Requirement.md](ContextMenu_Requirement.md)
