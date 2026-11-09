# Requirement — Context-aware right-click menu in the WatchList tree

**Area:** WPF controller (pipeline tree), then web client with the same rules.
**Problem:** right-clicking any node (ActionGroup, Action, Initialize…) shows the same WatchList-level menu
(Enable/Disable All WatchItems, Add WatchItem, New Pipeline, Import/Export, Edit WatchList XML, Edit Global
Variables). The user cannot run, add, delete or move the node they clicked.
**Goal:** the menu shows only what makes sense for the node that was right-clicked.

---

## 1. Rules for every menu

1. **Menu depends on the node clicked.** Selection follows the right-click (the clicked node becomes selected).
2. **Order:** Run items -> Add items -> Edit items -> Move/Copy -> Enable/Disable -> Delete (last, separated).
3. **Disabled vs hidden:** items that never apply to this node type are **hidden**. Items that apply but are
   not allowed right now (pipeline running, no permission, agent offline) are **shown disabled with a tooltip
   saying why**.
4. **Permissions (checked in the view model AND on the server):** Run needs the existing trigger permission for that
   pipeline. Add/Edit/Delete/Move need the new **Pipeline_Edit** permission, scoped per pipeline through assignments;
   Administrator always has it. Editing **templates** needs **Template_Edit** (Administrator only at first, because a
   template change affects every pipeline that uses it). Pipeline creation stays admin-only (Pipeline_Author).
5. **While a pipeline is running:** all edit items are disabled for that pipeline ("Pipeline is running");
   Run items become **Stop / Cancel**.
6. **Template steps inside a pipeline** (nodes that come from a `<Ref>`) are read-only in the pipeline: edit items
   are replaced by **Edit template** (opens the Library at that node, see 2.8.1) and **Delete template call**.
7. **Every change** goes through the WatchList model (never raw XML), is **undoable** (Ctrl+Z), marks the
   pipeline dirty, and is saved only with Apply Changes / Save. Validation runs after each change.
8. **Delete** always asks for confirmation naming the node and its child count; Undo restores it.
9. **Run** always runs pre-flight first; on Failed it stops and shows the pre-flight window. Run never edits files.
10. **Keyboard (tree focus only, never in the log or text boxes):** Del = delete, F2 = rename Tag, Ctrl+D = duplicate,
    Alt+Up/Down = move, Ctrl+C / Ctrl+V = copy/paste, Ctrl+Z / Ctrl+Y = undo/redo, **F5 = run the selected node
    (with confirm)**, Shift+F10 / Menu key opens the menu. **Trigger All moves to Ctrl+Shift+F5 and always asks
    to confirm.** Shortcuts are shown next to each item.
11. The current WatchList-level menu stays, but only on **empty tree space** and the **root** node.
12. **One menu definition:** a C# `NodeMenuProvider` in Core decides the items; WPF renders it and the web gets
    the same list from the API. No second copy of the rules in TypeScript.
13. **Disable / Enable on groups and actions = Skip / Unskip** (existing Skip with reason; default reason
    "Disabled from menu by <user>"). Only WatchItems use IsEnabled.

---

## 2. Menu per node type

### 2.1 Empty space / WatchList root (today's menu, unchanged)
Add WatchItem · New Pipeline… · Import WatchItems… · Export All WatchItems… · Enable All / Disable All
WatchItems · Edit WatchList XML… · Edit Global Variables…

### 2.2 WatchItem (pipeline)
| Group | Items |
|---|---|
| Run | **Run pipeline…** (asks build number / drop location, prefilled from last run) · **Run pre-flight** · Stop (when running) · Open last execution log |
| Add | Add Event |
| Edit | Edit properties (Path, Filter, BuildBasePath…) · Edit this pipeline's XML… · Rename… is **blocked**: message "The pipeline name is its ID (permissions, history). Use Duplicate pipeline to create it under a new name." with a Duplicate button |
| Copy | Duplicate pipeline… (new unique Tag ≤36 chars + new trigger file name) · Export this pipeline… |
| State | Enable / Disable pipeline |
| Delete | Delete pipeline… |

### 2.3 Event
| Group | Items |
|---|---|
| Add | Add Initialize (only if none) · Add ActionGroup · Add Action |
| Edit | Edit properties (Type, ExecutionType) |
| Delete | Delete event… |

### 2.4 Initialize
| Group | Items |
|---|---|
| Run | Run pre-flight (parameters) |
| Edit | Edit properties (ParameterFile browse, Profile dropdown) · Open parameter file · Reload parameters |
| Delete | Delete Initialize… (warns: tokens will stop resolving) |

### 2.5 ActionGroup
| Group | Items |
|---|---|
| Run | **Run this group** (uses the pipeline's Initialize values) · Run from here to end · Stop (when running) |
| Add | Add Action inside · Add ActionGroup inside · Add from template (inserts `<Ref>`) · Add ActionGroup above / below |
| Edit | Edit properties (Tag, ExecutionType Sequential/Parallel, FailAndContinue) · Rename Tag (F2) · Edit XML of this group… |
| Move/Copy | Move up · Move down · Duplicate · Copy · Paste inside · Wrap in new group · Ungroup (move children up one level) |
| State | Skip / Unskip group (rule 13) |
| Template | Save group as template… (admin) |
| Delete | Delete group… (shows child count) |

### 2.6 Action (RunCommand / RunRemoteCommand)
| Group | Items |
|---|---|
| Run | **Run this action only** (resolves tokens from the pipeline's Initialize; checks agent online/idle) · Run from here to end · Show resolved command (tokens replaced, secrets masked) · Open last output |
| Add | Add Action above / below · Add Restart step after (RunRemoteCommand only) · Add Settle wait after |
| Edit | Edit properties (AgentName dropdown with status, Command, Parameters, Timeout, PollInterval, FailAndContinue, IsReboot, SecretEnv) · Rename Tag (F2) · Change agent… (also offers "apply to same step for other agents in this group") |
| Move/Copy | Move up · Move down · Duplicate · Duplicate for agent… (pick agents/pool) · Copy · Paste below |
| State | Skip / Unskip action (rule 13) |
| Delete | Delete action… |

### 2.7 Action (SendMail)
Edit: To / Title / Body · Move, Duplicate, Skip/Unskip, Delete as 2.6. (Send test mail: deferred until the user
identity carries an email address.)

### 2.8 Ref (template call inside a pipeline) and its child nodes
| Group | Items |
|---|---|
| Edit | **Edit template** (default item, also on double-click and Enter) — see 2.8.1 |
| Run | Run this part (as section 3) |
| Other | Show expanded steps · Replace with another template… · Detach as copy (confirm) · Move up / Move down |
| Delete | **Delete template call…** — removes only this `<Ref>` from this pipeline; the template itself stays in the Library. Confirm text: "Remove '<TemplateID>' from '<pipeline Tag>'? The template is not deleted and other pipelines are not affected." Undo restores it. |

Right-clicking a **child node shown under a Ref** (an ActionGroup or Action that comes from the template) shows
the same **Edit template** and **Delete template call…** items, plus Run this part. No direct edits on these
nodes (rule 6).

#### 2.8.1 Edit template — navigation
1. Switches to the **Library** tab.
2. Expands and **selects the template** with that TemplateID.
3. If the user right-clicked a **child node** under the Ref, it selects the **same node inside the template**
   (matched by its path in the template, e.g. group index + Tag), expands its parents and scrolls it into view;
   the properties panel shows that node ready to edit.
4. Shows a banner on the Library tab: "Editing template '<TemplateID>' — used by N pipelines: <names>.
   Changes apply to all of them." with a **Back to pipeline** link that returns to the original pipeline node.
5. If the template is missing: no navigation; message "Template '<TemplateID>' not found in Library" with
   **Create template** (admin) and **Delete template call** options.
6. Unsaved changes in the current pipeline are kept (navigation does not discard or auto-save them).
7. Permission: Edit template is enabled only with edit rights on templates; otherwise it opens the Library
   node read-only with a tooltip "You can view but not edit templates".

### 2.9 Template (in Library) and its nodes
Same Add/Edit/Move/Delete items as 2.5–2.7, editing the template definition. Extra: **Where used** (list of
pipelines with a Ref) · Duplicate template · Delete template (blocked while any pipeline uses it; shows where).
Run: keep the existing **Execute Template** (runs with a chosen pipeline's context). Delete/Edit need Template_Edit.

---

## 3. "Run part of a pipeline" behaviour
- Uses the same pipeline's Initialize (ParameterFile + Profile) and the build number the user enters.
- Shows a confirm: "Run '<node Tag>' on <agents> for build <n>? Steps before it will NOT run."
- Pre-flight is limited to the selected nodes plus the Initialize.
- The execution log entry says "partial run: <node path>" so results are not confused with a full run.
- Locks the agents it uses, like a full run.

---

## 4. Out of scope (separate decisions)
- Drag-and-drop reordering (Move up/down covers it for now).
- Editing JSON values from the menu (Parameter grid save bug is tracked separately).
- Free-text "Ask to change" (XML editor intelligence prompt, Part 2).

---

## 5. Acceptance tests
1. Right-click each node type shows exactly the items in section 2; nothing from 2.1 on non-root nodes.
2. Items not allowed now are disabled with a reason tooltip (running pipeline, no permission, agent offline).
3. Add / Duplicate / Move / Delete on Action and ActionGroup: model updated, validation re-run, undo restores,
   Apply Changes saves, reload shows the change.
4. Delete asks confirmation with child count; Undo restores including children and NodeIds.
5. Template call (Ref) and its child nodes:
   a) menu shows Edit template and Delete template call; no direct edit items on child nodes;
   b) Edit template on the Ref switches to Library and selects the template; on a child node it selects the
      matching node inside the template, expanded and scrolled into view; banner shows "used by N pipelines"
      and Back to pipeline returns to the original node;
   c) Delete template call removes only that Ref; the template and other pipelines are unchanged; Undo restores;
   d) missing template shows the "not found" message with Create / Delete options;
   e) unsaved pipeline edits survive the navigation.
6. Delete template blocked while used; message lists the pipelines.
7. Run this action / group: pre-flight first; Failed stops; log shows "partial run"; agent locked during run.
8. Show resolved command masks secrets; secrets never in logs.
9. Non-admin cannot see "Save as template" / "New Pipeline"; server rejects the call too.
10. Keyboard shortcuts work; Shift+F10 opens the menu; AutomationProperties.Name on every item; DynamicResource brushes.
11. Same menu rules in the web client (built from one shared menu definition, not two copies).

---

## 6. Copilot prompt (paste after the code-audit ground rules)

```
Implement docs/features/<PipelineTree>/ContextMenu_Requirement.md. One part at a time; stop after each.

Part 0 is done (agent review, 2026-11-09). Phases, in order:
P0 Security  - PUT /api/watchlist and POST /api/watchlist/import behind a permission on the server (+ web guard). Ship alone.
               DONE 2026-11-09: Pipeline_Author, refused for everyone in Default mode; web Import button disabled.
P1 Quick fix - right-click selects the node under the mouse (walk the visual tree to the nearest TreeViewItem);
               root menu only on root/empty space; Ref children show Edit template / Delete template call / Run. Ship alone.
               DONE 2026-11-09: TreeHitTest walks to the nearest row (empty space selects the root). A Ref and its
               mirrored children get Edit template (minimal: opens the Library tab, selects the template; full
               2.8.1 is P6), Execute Group/Action, Delete template call... (removes only this pipeline's Ref) and
               Move on the Ref itself. No undo until P3. Run on the Ref node itself waits for P7.
P2 NodeMenuProvider in Core (rule 12) - pure function, table-driven tests per node type.
               DONE 2026-11-09: TestControllerGrpc.Core/Menus (NodeMenuProvider, NodeMenuIds, NodeMenuContext).
               MainWindow.ContextMenus.cs only describes the node and renders; NodeKinds moved to Core. Same items as
               before, now ordered by rule 2 (root keeps today's order, rule 11); run items disabled with the reason.
P3 Undo + ApplyEdit - one edit path with snapshot undo/redo (stack of 50, cleared on reload); move existing commands onto it.
P4 Permissions - Pipeline_Edit (scoped by assignments) and Template_Edit (admin) via the add-rbac-permission skill.
P5 Missing edit commands (2.5-2.7) through ApplyEdit, then shortcuts (rule 10).
P6 Edit template navigation (2.8.1).
P7 Run additions - Run from here to end (engine), Show resolved command, Open last output, Stop.
P8 Web v1 - menu from the provider via API: Run, pre-flight, view, resolved command. No web editing yet.

Tests for every acceptance item in section 5, red-proofed. Build/test -c Release; report controller,
WebApi and WebClient counts. Respect the Ui ratchets.
```
