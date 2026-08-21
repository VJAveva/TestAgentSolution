# Regression tab — UI implementation spec

Keyed to `regression-tab-mockup.html`. Open it and press **Annotate** in the ribbon to see the region badges (R1–R14) overlaid on the live mockup.

The mockup is a behavioural reference, not a design to pixel-match. It runs on real parsed data — 63 changes, 41 subsystems, real work item IDs — so every interaction it demonstrates is one that has to survive contact with your data.

---

## How to use this with Copilot

Commit both files to `docs/impact/`. Then scope each prompt to one region:

> Implement region **R8** of `docs/impact/regression-tab-mockup.html` per `docs/impact/RegressionTab-UI-Spec.md`. Match the ribbon and panel styling already used in the WatchList Editor XAML. View model only where the spec says view model — no data access in this step.

One region per prompt. Build and look at it before the next.

---

## Ribbon

### R1 — Scope selector
**Control** RibbonGroup "Track changes", four toggle buttons: Build · Weekly · Custom · Release
**Binds** `SelectedScope` (enum `ScopeKind`), `SelectScopeCommand`
**Behaviour** Selecting a preset computes `From`/`To` and pushes them into R2. Weekly = last 7 days ending at the newest change. Release = full span of the selected release. Build = the selected build's own date only.
**Source** `GET /api/impact/consolidated?from&to`
**Done when** Switching Release → Weekly visibly reduces the grid, and the plan panel below shrinks with it.

### R2 — Timeline
**Control** Two `DatePicker`s, From and To
**Binds** `From`, `To` (`DateOnly`), `ApplyRangeCommand`
**Behaviour** Editing either switches `SelectedScope` to Custom and re-queries. Debounce ~300ms — a date picker fires per keystroke when typed. Reject To < From with validation, not an exception.

### R3 — Category toggles
**Control** Two `ToggleButton`s, Runtime and Config, both on by default
**Binds** `ShowRuntime`, `ShowConfig`
**Behaviour** Client-side filter over already-fetched data — **do not re-query**. Rows categorised `both` show while either is on. `unclassified` always shows; hiding rows nobody has categorised is how they stay uncategorised forever.

### R4 — Execute
**Control** Dispatch Automated · Assign Manual · Export
**Binds** `DispatchCommand`, `AssignManualCommand`, `ExportCommand`
**Behaviour** Dispatch posts the current plan to the existing run endpoint. `CanExecute` false when the plan is empty or edits are unsaved. Confirm before dispatching more than ~50 suites.

---

## Header

### R5 — Scope summary bar
**Control** Status strip: window label, date range, change / subsystem / file counts, activity sparkline
**Binds** `ScopeLabel`, `RangeText`, `ChangeCount`, `SubsystemCount`, `FileCount`, `WeeklyActivity`
**Behaviour** Sparkline shows change volume per week across the full available span with the current window highlighted. It exists so a lead can see that a quiet week isn't worth a full pass.
**Source** `consolidated.summary`

### R6 — Counters
**Control** Three badges in the panel header: RUNTIME, CONFIG, NO SUITE
**Binds** `RuntimeCount`, `ConfigCount`, `NoSuiteCount`
**Behaviour** Reflect the *filtered* set, not the total. NO SUITE stays red at zero — it's the number that should provoke action.

### R7 — Quick filters and save
**Control** Chips (All / No suite / Has automated / Has manual / Critical & high), free-text search, Save button
**Binds** `QuickFilter`, `SearchText`, `SaveEditsCommand`, `PendingEditCount`
**Behaviour** Search spans subsystem, file path, work item id and suite id. Save button disabled at zero edits, labelled with the count otherwise. **Warn on tab close with unsaved edits.**
**Source** `POST /api/impact/suites`

---

## Grid

### R8 — Column filter row
**Control** A second header row inside the DataGrid, pinned below the column headers
**Binds** `ColumnFilters` (observable record: Category, Component, Subsystem, WorkItemType, WorkItemId, Summary, AutoSuite, ManualSuite, Risk)
**Behaviour** Dropdowns for Category, Component, Risk, WorkItemType, WorkItemId — populated from the current data, not hardcoded. Text filters elsewhere, contains-match, case-insensitive. All filters compose, and they stack on top of R1/R2 scope and R3 toggles. A `clear` button resets only R8.
**Note** The mockup puts two controls in the Changes column (type + specific work item). If your DataGrid supports it, one combined picker grouped by type reads better; keep the same filtering semantics either way.
**Done when** "IMS work items in PFServer in the last four weeks" is three interactions and the plan panel rebuilds to just those suites.

### R9 — The DataGrid
**Control** `DataGrid`, virtualisation on, `CanUserSortColumns` true
**Binds** `ObservableCollection<SubsystemRow>` via `ICollectionView` for sort and filter
**Columns**

| # | Column | Type | Source |
|---|---|---|---|
| 1 | Category | Badge, colour by Runtime/Config/Both/Unclassified | derived |
| 2 | Component | Text | `vobs.csv` component id |
| 3 | Subsystem | Text, emphasised | `pathRules` / declared sub-component |
| 4 | Files modified | 2 shown, expander for the rest | `git/.../commits/{sha}/changes` |
| 5 | Changes / work items | Hyperlinks, glyph + colour per type | `build/builds/{id}/workitems` + `wit/workitems` |
| 6 | Summary of change | One line, `+N more` when several | `System.Title` / commit message |
| 7 | Risk | Badge | component `riskTier` |
| 8 | **Automation suite** | Editable chips | map `suites` + user edits |
| 9 | **Manual suite** | Editable chips, linked where an id exists | work item `TestedBy-Forward` relations |
| 10 | Est | Right-aligned duration | computed |

**Left border** encodes category. **Red inset marker** means the row has unsaved edits.

**Work item links** — IMS `◆` violet, Bug `●` red, User Story `▲` green, Feature `✦` amber, PR and commit demoted to grey. Open with `Process.Start` and `UseShellExecute = true`. **URLs come from the API already resolved.** The UI must never build Azure DevOps URLs itself, or the repository-alias logic ends up duplicated in C# and XAML and drifts.

**Editing columns 8 and 9** — add by typing and pressing Enter, remove with `×`. Every edit recomputes the row's Est, the R6 counters, and the R11–R13 plan panel immediately; persistence waits for Save. Edits are held per subsystem and **survive rescoping** — set a suite in the Release view and it's still there in Weekly.

**Manual suite linking** — an entry with a recognisable work item id renders as a link; free text renders dashed with a "no test suite linked" tooltip. That distinction is the point: in the current data all four manual entries are unlinked prose, and the dashed borders are the visible backlog.

### R10 — Expanded row
**Control** `RowDetailsTemplate`, toggled by clicking the category cell, subsystem name, or the `+N more` affordance
**Contents** Two columns — left: every file path, then every change summary as a bulleted list; right: all linked work items plus a repo link, use cases, and a window line ("4 changes · last 2026-06-02")
**Behaviour** Multiple rows may be open at once. Expansion state resets on scope change.

---

## Plan panel

### R11 / R12 — Runtime and Configuration columns
**Control** Two scrollable panels, each with a header line of counts
**Binds** `RuntimePlan`, `ConfigPlan` (`PlanColumn`: Subsystems, Suites, ManualSuites, Gaps, Minutes)
**Behaviour** Grouped by subsystem, green chips for automated suites, violet for manual. At the foot, in red, the subsystems with nothing mapped, under "Needs a decision".
**Source** `GET /api/impact/scope?from&to&category`
**Why split** Runtime work needs a deployed Galaxy, Config work mostly doesn't. Two setups, often two people. That's the scheduling constraint the split encodes — it isn't cosmetic.

### R13 — Totals
**Control** Four stat tiles plus two action buttons
**Binds** `TotalAutomatedSuites`, `TotalManualSuites`, `TotalGaps`, `ParallelDuration`, `DispatchCommand`, `AssignManualCommand`
**Behaviour** Parallel duration divides by live free agent count from the fleet, not a hardcoded 4.

---

## Status bar

### R14 — Sync freshness
**Control** Status strip: state, source, org, map version, machine
**Binds** `SyncState`, `LastSyncTime`, `MapVersion`
**Behaviour** **If Azure DevOps is unreachable, show cached data with "data as of {time}" — never an empty grid.** A blank table reads as a bug; stale data with a timestamp is usable information. Surface unresolved repositories here too, since those are the rows whose links won't work.

---

## Cross-cutting

**Recalculation order.** Scope (R1/R2) → server query. Category toggles (R3), quick filters (R7), column filters (R8) → client-side only. Everything downstream — R5, R6, R9, R11–R13 — recomputes from the filtered set. Getting this wrong means a filter change triggers an API call and the grid flickers on every keystroke.

**Estimates are formulas, not measurements.** The mockup uses 12 minutes per automated suite and 25 per manual, both invented. Wire real per-case durations from TRX output before anyone schedules a shift around the number, and until then label the column as an estimate.

**Colour is never the only signal.** Category also carries a text badge, coverage also carries a label, work item type also carries a glyph. Some of your QA engineers will be on projectors and some will be colourblind.

**Virtualisation is not optional.** A full backfill produces thousands of rows.

---

## Build order

R9 first with static data — the grid is the feature, everything else adjusts it. Then R1/R2 scoping, then R7/R8 filters, then R11–R13, then R10, then R5/R6/R14. Editing (columns 8 and 9) last, because it's the only part that writes.
