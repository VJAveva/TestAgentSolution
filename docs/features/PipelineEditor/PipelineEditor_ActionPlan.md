# Pipeline Editor - review and action plan

Companion to [PipelineEditor_Requirement.md](PipelineEditor_Requirement.md). Written 2026-11-17 after Part 0
(investigation). Each part ends green (-c Release), with basic tests, one commit, and new issues appended to
`docs/issues/BACKLOG.md`. Nothing is pushed or deployed without the user's word.

## 1. What exists today (Part 0 findings)

| Area | Today | File |
|---|---|---|
| Window | Modal dialog: AvalonEdit XML editor, toolbar (Validate, Browse Parameter File, Format, Auto Fix, Revert, Preview, font), error grid with quick fixes, Save / Cancel | `TestControllerGrpc/Views/RawXmlEditorWindow.xaml(.cs)` |
| View model | Works on a **detached XML string**; Save hands `ResultXml` back to the caller | `TestControllerGrpc/ViewModels/WatchItemXmlEditorViewModel.cs` |
| Opening / applying | `MainViewModel.OpenRawXmlEditorWindow` (one WatchItem) and `OpenWatchListEditor` (whole file). Both check `EnsureCanEdit` first and apply the result as **one** `BeginEdit(...)` undo step | `TestControllerGrpc/ViewModels/MainViewModel.XmlEditor.cs` |
| Live checks, completion, hover, quick fixes | Done (XmlEditor intelligence Part 1). Diagnostics carry line, column and the validator's `NodePath` | `TestControllerGrpc.Core/Services/WatchListXmlEditorAnalyzer.cs` |
| Structure Preview | Display-only tree rebuilt from the XML. No link to a line, no selection, no menu | same view model |
| Node addressing | Structural paths `e0/c2/c1`, shared by REST node-run | `TestControllerGrpc.Core/Services/NodeAddressing.cs` |
| Menus | One menu definition for every host (P2), with host subsets (`WebIds`) | `TestControllerGrpc.Core/Menus/NodeMenuProvider.cs` |
| Structural edits | Pure list operations on the model (P5) | `TestControllerGrpc.Core/Editing/NodeEdits.cs` |
| Comments | Saving the WatchList keeps XML comments (`PreserveComments`); a node's own note is a `<Comment>` child element | `TestControllerGrpc.Core/Services/WatchListXmlParser.cs` |
| Template editor | A second, near-duplicate window + view model (~1,200 lines) | `TemplateXmlEditorWindow`, `TemplateXmlEditorViewModel` |

## 2. Review of the requirement - decisions

1. **One source of truth inside the editor: its XML document.** Section 3 says "every field edits the model through
   ApplyEdit". Applying every keystroke to the live model would break Cancel / Revert, put dozens of entries in the
   main undo history and show half-typed values in the main tree. Instead every edit in the editor (form, menu,
   quick action, typing) changes the editor's XML text through one Core helper that edits an `XDocument` with
   whitespace and comments preserved. The editor's own undo (AvalonEdit document) covers all of them, and **Save
   applies the result to the model as one ApplyEdit step**, exactly as today. This meets acceptance 2, 10 and 13
   and keeps comments.
2. **Selection is a structural path**, the same scheme as `NodeAddressing` (`e0/c1`), computed from the XML.
   `<Comment>` child elements are notes, not steps, and are skipped when counting. No new IDs in the file.
3. **Run items are left out of the editor menu.** The editor holds unsaved changes; "Run" from there would run
   the saved pipeline, not what the user sees. Run stays on the main tree. (Requirement 2.2 lists run - flagged.)
4. **One editor for pipelines and templates.** Template tabs (Part 3) reuse this view model with the Templates
   document kind; the duplicate template window is retired after Part 3 rather than extended.
5. **Server-side check (acceptance 9).** The WPF editor writes the local file, so its gate is `EnsureCanEdit`
   (P4) at open and at save. A server rejection only applies to REST writes (`PUT /api/watchlist`, Pipeline_Author).
   The web stays view-only (section 10).
6. **Changed by someone else (acceptance 12):** compare `NodeAddressing.RevisionOf` of the opened pipeline with the
   live model at Save; the WatchList hot-reloads from disk, so this also catches edits made outside the app.
7. **Auto Fix and today's quick fixes re-serialise the whole document**, which drops comments and formatting.
   Part 5 moves them onto the same XDocument edit helper.
8. **Smoke test (acceptance 15):** window smoke tests were deferred by the user (BACKLOG P3). Covered by view-model
   tests until that task is done.

## 3. Parts

| Part | Delivers | Main files | Acceptance |
|---|---|---|---|
| 1 | Three-pane window: Structure (click, search, badges, context menu from NodeMenuProvider) - XML - Properties (read-only attributes, secrets masked). Selection shared by tree, XML caret and Properties; Problems show and jump to the node. Structure edits from the menu: move, duplicate, delete, skip / include, execution type - all undoable, comments kept | Core `PipelineEditor/PipelineXmlMap.cs`, `PipelineXmlEdits.cs`; `NodeMenuProvider.PipelineEditorIds`; `WatchItemXmlEditorViewModel.Structure.cs`; `RawXmlEditorWindow` | 1, (15) |
| 2 | Properties forms per node type through `PipelineXmlEdits` (set attribute / mail body), inline validation from the analyzer, token colouring and `[_` completion, agent dropdown with status, read-only reasons | `PropertyForms/*`, view model partial | 2, 3, 10, 14 |
| 3 | Ref expands to template steps (read-only), Edit template tab (same view model, Templates kind) with the "used by N" banner, Copy for this pipeline only, Save group as template, save checks every pipeline that uses the template, revision check | view model partials, `MainViewModel.XmlEditor.cs` | 4-9, 12 |
| 4 | Form view (step cards) next to XML view; broken XML makes the form read-only with the line number | view + view model | 13 |
| 5 | Parameters tab (token, value, source, used by; secrets masked), quick actions, What changed (diff), backup and messages on save, per-problem fixes replace Auto Fix | `DiffPlex` (already in Core), view model partials | 11 |

## 4. Out of scope here
As in the requirement section 10. Also: retiring `TemplateXmlEditorWindow` happens only after Part 3 is accepted.
