# Requirement — Pipeline Editor (re-engineered WatchItem XML Editor) with in-place template editing

**Area:** WPF controller, "WatchItem XML Editor" window. Web client later (view-only first).
**Problem today:** the screen is a raw XML editor. To change an agent, a timeout or a mail body the user must
type XML attributes. Template steps (`<Ref>`) are invisible here - the user has to leave for the Library tab.
The Structure Preview is read-only.
**Goal:** forms first, XML as the advanced view, and templates editable on the same screen.

**Builds on (do not rebuild):** ApplyEdit + undo (P3), Pipeline_Edit / Template_Edit permissions (P4),
NodeMenuProvider (P2), Edit-template navigation (P6), validator / pre-flight / token resolver,
`Prompt_XmlEditor_Intelligence.md` Part 1 (live checks, completion, quick fixes).

---

## 1. Screen layout

```
+---------------------------------------------------------------------------------------------+
| Pipeline Editor - <pipeline name>     [Validate] [Pre-flight] [What changed] [Save] [Revert] |
| Tabs:  Pipeline | Template: RevertSanity x | Template: PrepSanity x | Parameters             |
+----------------------+--------------------------------------+-------------------------------+
| STRUCTURE (tree)     | EDITOR                               | PROPERTIES (form)             |
| click = select       | view switch: [Form] [XML]            | fields of the selected node   |
| right-click = menu   |                                      |                               |
+----------------------+--------------------------------------+-------------------------------+
| PROBLEMS: errors / warnings / info, click = jump to node, one-click fix where possible      |
+---------------------------------------------------------------------------------------------+
```

- The three panes always show the **same selected node** (tree, XML line, form).
- Panes are resizable; layout and font size are remembered per user.
- Title uses "Pipeline" and "Name" on screen (XML keeps WatchItem / Tag).

---

## 2. Structure pane (was "Structure Preview")

1. Click a node -> selects it, scrolls the XML to its line, loads the Properties form.
2. Right-click -> the same context menu as the main tree (NodeMenuProvider): add, duplicate, move, skip, delete, run.
3. A `Ref` node can be **expanded** to show the template's steps underneath, greyed and marked "from template".
4. Badges on nodes: error / warning count, skipped, agent offline.
5. Search box filters the tree by name, agent or command text.

---

## 3. Properties pane (forms)

One form per node type. Every field edits the model through ApplyEdit (undoable); the XML updates at once.

| Node | Fields |
|---|---|
| Pipeline | Name (read-only, "name is its ID" hint + Duplicate button), trigger folder (browse), trigger file, build-number field, drop-location field, build base path, enabled |
| Event | Type, execution type |
| Initialize | Parameter file (browse under Parameters\), Profile (dropdown from that file), Open file, Reload |
| ActionGroup | Name, Sequential / Parallel, continue on failure, skip (+ reason) |
| Action (command) | Name, type, **Agent (dropdown with online / busy status)**, command, parameters (multi-line, token colouring), timeout, poll interval, continue on failure, restart step (IsReboot), secrets (SecretEnv: names only, values never shown) |
| Action (mail) | To, subject, **body as a multi-line box** (stored as `&#xA;` in XML), From |
| Ref | Template (dropdown of Library templates), "Used by N pipelines", buttons: **Edit template**, **Copy for this pipeline only**, Replace |

Rules:
- Inline validation under each field (same rules as the validator - no second rule set).
- `[_Token]` in any text field: coloured; hover shows value and source; typing `[_` opens the token list.
- Fields the user may not edit (no permission, running pipeline, template step) are read-only with a reason.

---

## 4. Editing templates on this screen

### 4.1 See the template without leaving
Expanding a `Ref` (section 2.3) shows its steps read-only, in the tree and as greyed lines in the Form view.

### 4.2 Edit template
- **Edit template** (button, context menu, double-click on a Ref, or Enter) opens a **new tab in this editor**:
  `Template: <TemplateID>`, with the same three panes, checks and tools.
- If the user started from a step under the Ref, the tab opens with **that step selected**.
- Banner at the top of the tab: "Template '<ID>' is used by N pipelines: <names>. Changes apply to all of them."
- Needs **Template_Edit**; without it the tab opens read-only ("You can view but not edit templates").
- The Library tab still works; both edit the same template (one model, no second copy).

### 4.3 Copy for this pipeline only
Replaces the `Ref` with a copy of the template's steps inside this pipeline (confirm first:
"This pipeline will no longer follow changes to template '<ID>'."). Undoable.

### 4.4 Save and impact
- Each tab shows a dot when it has unsaved changes. **Save** saves everything open; it lists what will be written:
  "Pipeline <name>" and "Template <ID> (affects N pipelines)".
- Before a template is saved, the checker runs on **every pipeline that uses it**. Errors in any of them block the
  save and are listed in Problems with the pipeline name.
- A template in use cannot be deleted or renamed from here (message lists the pipelines).
- If someone else changed the template or pipeline since it was opened: no silent overwrite - show
  "Changed by <user> at <time>" with Reload / Compare.

### 4.5 Create a template from here
Select a group -> **Save group as template...** (Template_Edit) -> name it -> the group is replaced by a `Ref`.

---

## 5. Editor pane: Form and XML views

- **Form view (default):** the pipeline as readable step cards in order (name, agent, command, timeout),
  nested by group; click a card to select it. No XML knowledge needed.
- **XML view:** today's editor, kept for advanced users: folding, line numbers, Format, plus the live checks,
  completion and quick fixes from `Prompt_XmlEditor_Intelligence.md` Part 1. Long lines wrap (toggle).
- Switching views never loses edits. If the XML is not well-formed, Form view shows "Fix the XML error on
  line N to use the form" and the form is read-only until fixed.
- XML comments are preserved.

---

## 6. Parameters tab
Read-only table for this pipeline: token | value now | comes from (Global / Profile / Pipeline / GlobalVars /
Trigger) | used by (steps). Secrets masked. Unused and unresolved tokens flagged. "Open parameter file" button.
(Editing values stays in the Initialize parameter grid.)

---

## 7. Toolbar and quick actions
Validate - Pre-flight - What changed - Save - Revert - Undo / Redo - Font size.
Quick actions on the selected node: **Add step**, **Add restart after**, **Add wait after**, **Change agent**,
**Apply this step to all agents in a pool**, **Skip / Unskip**.
"Auto Fix" is replaced by per-problem fixes in the Problems list (each one named, each undoable) -
no single button that rewrites the document.

## 8. "What changed" and save
- **What changed:** before / after side by side, per pipeline and per template, changed lines highlighted.
- **Save** is blocked on errors; warnings allow save. A timestamped backup is written first.
  After saving: "Saved <what> at <time>" or the reason it failed - never silent.
- **Revert** discards unsaved changes after a confirm. Closing with unsaved changes asks first.
- Saving never triggers or runs a pipeline.

## 9. Rules that always apply
Permissions checked in the app and on the server - Edits blocked while that pipeline runs (view still allowed) -
Square brackets only for `[_Tokens]` - Secrets never shown, logged or put in diffs - Audit entry for each
saved pipeline / template change (who, what) - Keyboard access for every action; theme brushes only.

## 10. Out of scope
Free-text "Ask to change" (XmlEditor intelligence Part 2) - drag-and-drop reordering - editing parameter values
on the Parameters tab - web editing (web gets this screen view-only first).

---

## 11. Acceptance tests
1. Selecting a node in tree, XML or form selects the same node in all three.
2. Changing agent / timeout / mail body in the form updates the XML; editing the XML updates the form.
3. Mail body with line breaks round-trips (form <-> XML <-> saved file) unchanged.
4. Expanding a Ref shows the template steps read-only.
5. Edit template opens a tab with the right template, the right step selected, and the "used by N" banner.
6. A template edit shows up in every pipeline that uses it after save; "What changed" lists those pipelines.
7. A template change that breaks another pipeline blocks the save and names that pipeline.
8. Copy for this pipeline only: Ref replaced by steps; other pipelines and the template unchanged; undo restores.
9. No Template_Edit: template tab is read-only; server rejects a save attempt.
10. Undo / redo works for every form edit and quick action; Revert restores the opened state.
11. Save blocked on errors; backup written; success and failure messages shown.
12. Changed-by-someone-else is detected; no silent overwrite.
13. Broken XML: Form view read-only with the line number; fixing it restores the form.
14. Secrets never appear in forms, diffs, logs or the Parameters tab.
15. Every window / tab in this editor opens without a crash (smoke test).

---

## 12. Copilot prompt (paste after the code-audit ground rules)

```
Implement docs/features/PipelineEditor/PipelineEditor_Requirement.md. One part at a time; stop after each
and report what changed, test counts (controller / WebApi / WebClient) and anything found but not fixed.

Part 0 - Investigate (no changes): the current WatchItem XML Editor window (control used, Structure Preview,
Validate / Format / Auto Fix / Preview / Browse Parameter File), how it syncs with the model, what P2-P6 already
give us (NodeMenuProvider, ApplyEdit + undo, permissions, Edit-template navigation), and what of
Prompt_XmlEditor_Intelligence Part 1 exists. Report gaps against sections 1-9, risks, and propose the order. Stop.

Part 1 - Three-pane sync: clickable Structure pane, selection shared by tree / XML / form, context menu from
NodeMenuProvider, Problems list. (Acceptance 1, 15)
Part 2 - Properties forms for every node type through ApplyEdit, with inline validation and token help.
(Acceptance 2, 3, 10, 14)
Part 3 - Templates in this screen: expand Ref, Edit template tab with banner, Copy for this pipeline only,
Save group as template, save with impact check across all using pipelines, change-by-others detection.
(Acceptance 4-9, 12)
Part 4 - Form view (step cards) + XML view switch, broken-XML behaviour, comments preserved. (Acceptance 13)
Part 5 - Parameters tab, quick actions, What changed, save / backup / messages; replace Auto Fix with
per-problem fixes. (Acceptance 11)

Basic unit tests per part as agreed; a test for every acceptance item by the end of Part 5.
Build / test with -c Release. Do not push or deploy.
```
