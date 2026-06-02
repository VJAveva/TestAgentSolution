# Implementation Audit — P0 Bugs + P1/P2 Enhancements

**Date:** 2026-06-02
**Author:** AI-assisted implementation
**Build Status:** ✅ All projects compile cleanly (0 errors)

---

## Item 3 (P0): Trigger Checkbox State Not Propagating to Children

**Root Cause:** `OnIsEnabledChanged` in `TreeNodeViewModel` only synced to the underlying `WatchItemConfig.IsEnabled` model property. When a user toggled `IsEnabled` on the WatchList root node via the static XAML `ContextMenu`, child WatchItem nodes were unaffected — meaning "Trigger All" still executed items the user expected to be disabled.

**Files Changed:**
- `TestControllerGrpc/ViewModels/TreeNodeViewModel.cs` — Added parent→child propagation in `OnIsEnabledChanged`: when a WatchList node is toggled, all WatchItem children are set accordingly. Added `_suppressIsEnabledPropagation` flag to avoid infinite recursion. Added `ComputeChildrenEnabledState()` helper for three-state UI display.
- `TestControllerGrpc/Views/MainWindow.xaml` — Removed the static `ContextMenu` from `HierarchicalDataTemplate` (it applied "Include in Trigger All" to ALL node types, including Events/Actions where it's meaningless). Context menus are now fully built dynamically in code-behind.
- `TestControllerGrpc/Views/MainWindow.xaml.cs` — Added "Enable All WatchItems" and "Disable All WatchItems" menu items to the WatchList right-click context menu, giving users explicit bulk enable/disable control.

---

## Item 4 (P0): Import WatchItem Failing Silently

**Root Cause:** The catch block in `ImportWatchItems()` only called `AddLog(...)` with the error message, which writes to the internal log panel. Users who don't notice the log panel have no visibility into the failure — no dialog, no file name, no actionable error type.

**Files Changed:**
- `TestControllerGrpc/ViewModels/MainViewModel.ImportExport.cs` — Enhanced the catch block to:
  1. Log the full exception with file path via `_appLogger.Error()`
  2. Show a `ThemedMessageBox` error dialog with the file name, exception type, and message
  3. Continue logging to the log panel for audit trail

---

## Item 1 (P1): Global Variables File at WatchList Level

**Design:** A WatchList-level parameter file that provides tokens across ALL WatchItems. Tokens from this file are loaded first (lowest priority) and can be overridden by per-WatchItem Initialize files.

**Files Changed:**
- `TestControllerGrpc.Core/Models/WatchListConfig.cs` — Added `GlobalVariablesFile` property (string path)
- `TestControllerGrpc.Core/Services/WatchListXmlParser.cs` — Added read/write support for `GlobalVariablesFile` attribute on the root `<WatchList>` element
- `TestControllerGrpc/ViewModels/MainViewModel.GlobalVariables.cs` — **New file.** Partial class with commands: `BrowseGlobalVariablesFile`, `EditGlobalVariables`, `SaveGlobalVariables`, `AddGlobalVariableEntry`, `RemoveGlobalVariableEntry`. Reuses existing `ParameterEntryViewModel` and `ParameterResolver.ParseParameterFile/SaveParameterFile`.
- `TestControllerGrpc/ViewModels/MainViewModel.Helpers.cs` — Updated `LoadTokensFromConfig()` to load global variables file first (before scanning Initialize nodes), ensuring global tokens are available but overridable.
- `TestControllerGrpc/Views/MainWindow.xaml.cs` — Added "Edit Global Variables…" context menu item on WatchList root node.

**XML Format Example:**
```xml
<WatchList GlobalVariablesFile="C:\Config\GlobalVars.txt">
  <WatchItem Tag="Build" ... />
</WatchList>
```

---

## Item 2 (P2): Individual Template Editing

**Design:** Users can now edit a single template's XML without opening the full multi-template editor. Includes reference-safety: warns when renaming a template ID that has active Ref references.

**Files Changed:**
- `TestControllerGrpc/ViewModels/MainViewModel.TemplateCrud.cs` — Added `EditSingleTemplateXml` command that serializes only the selected template, opens the XML editor, and on save: parses the result, warns about broken Refs if the ID changed, replaces in model + tree. Added `CountTemplateReferences` / `CountRefsInChildren` helpers.
- `TestControllerGrpc/Views/MainWindow.xaml.cs` — Added "Edit Template XML…" context menu item at the top of the Template node menu.

---

## Summary Table

| # | Priority | Type | Description | Status |
|---|----------|------|-------------|--------|
| 3 | P0 | Bug | Trigger checkbox not propagating to children | ✅ Fixed |
| 4 | P0 | Bug | Import failing silently | ✅ Fixed |
| 1 | P1 | Enhancement | Global Variables at WatchList level | ✅ Implemented |
| 2 | P2 | Enhancement | Individual Template XML Editing | ✅ Implemented |
