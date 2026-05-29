# TestController UI — dropdown theming, gRPC auto-fill, health-flyout overlap, log-pane resize

A batch of four related UI issues/enhancements found in the same review pass. Each is independently fixable; grouped here because they share the dark-theme design system and the popup/layout patterns already established in earlier tickets.

| # | Title | Type | Severity |
|---|---|---|---|
| 1 | Dropdowns use unstyled native chrome — light and broad, off-theme | Bug | Medium |
| 2 | gRPC address should auto-fill from hostname instead of localhost | Enhancement | Low-Medium |
| 3 | Controller health flyout overlaps the Execution Log | Bug | Medium-High |
| 4 | Execution Log pane is fixed-height / not expandable | Enhancement | Medium |

---

## Issue 1 — Dropdowns use unstyled native chrome (light, broad, off-theme)

**Type:** Bug — UI / Theming
**Severity:** Medium
**Area:** All `ComboBox` controls across the app (Theme selector, Execution Log filters: Session / Tag / Agent / Level, Build dropdown in Results Dashboard, etc.)
**Mockup reference:** *"Themed dropdown mockup"* (closed / hover / open states) in the design conversation.

### Current behavior

`ComboBox` controls render with the default Windows chrome: white/light-gray fill, gray 3D beveled border, square corners, a beveled drop arrow button, and an oversized hit area (~40 px tall). Against the dark application theme they read as holes punched in the UI. The Theme selector on the HOME ribbon is the most prominent example, but every dropdown in the app shares the problem (Execution Log filter row, Results Dashboard Build selector).

### Expected behavior

Dropdowns match the dark theme via a custom `ControlTemplate`:

- Fill `#22262E` (dark), not white.
- Border 0.5 px `rgba(255,255,255,0.12)`, not a gray 3D bevel.
- Corner radius 5 px, not square.
- Drop chevron is a flat icon (`ti-chevron-down`), not a beveled button; rotates/recolors on open.
- Control height ~32 px, not ~40 px.
- Width sized to content (or a sensible max), not a fixed broad box.
- Hover: border brightens to `rgba(74,144,226,0.5)` with a subtle 2 px focus glow.
- Open popup list: dark fill `#1E222A`, selected item on a blue-tinted row with a check glyph, items ~30 px tall, drop shadow `0 8px 20px rgba(0,0,0,0.45)`.

### Acceptance criteria

- [ ] A single reusable `Style`/`ControlTemplate` for `ComboBox` is defined once and applied app-wide.
- [ ] No dropdown renders white/light fill or a 3D bevel anywhere in the app.
- [ ] Closed, hover, focused, and open states all match the mockup.
- [ ] Colors via `DynamicResource`; the dropdown renders correctly in Light, Dark, and High Contrast (HC falls back to `SystemColors`).
- [ ] Applied to: Theme selector, Execution Log filters (Session/Tag/Agent/Level), Results Dashboard Build selector, and any other `ComboBox`.

### Suggested fix direction

Define a `ComboBox` `Style` with a custom `ControlTemplate` (ToggleButton + Popup + ItemsPresenter) in the shared theme dictionary. Style `ComboBoxItem` for the dark list rows. Apply implicitly (keyed by type) so existing dropdowns pick it up without per-instance changes.

---

## Issue 2 — gRPC address should auto-fill from hostname instead of localhost

**Type:** Enhancement — UX convenience
**Severity:** Low-Medium
**Area:** Agents panel → Registry → Agent Details (Add / Edit agent)
**Reference:** Screenshot 2 (Agent Details: Hostname/Agent Name = `jvgr1`, gRPC Address = `http://jvgr1:5200`).

### Current behavior

When registering or editing an agent, the operator types the Hostname / Agent Name and must separately type the gRPC Address. The address field defaults to (or retains) `localhost`, forcing manual correction to the actual host on every agent.

### Expected behavior

When the Hostname / Agent Name field changes (on commit / focus-leave / tab-change), auto-populate the gRPC Address as `http://<hostname>:5200` — replacing a `localhost`-based default with the entered hostname.

**Guard against clobbering manual input:** only auto-fill when the address field is empty, still at its `localhost` default, or was itself last set by auto-fill. If the operator has manually typed a custom address (different port, different scheme, explicit IP), do **not** overwrite it. Track an `addressManuallyEdited` flag to decide.

### Acceptance criteria

- [ ] Entering a hostname and leaving the field (or changing tabs) sets gRPC Address to `http://<hostname>:5200` when the field is empty or at the localhost default.
- [ ] A manually edited address is never overwritten by subsequent hostname changes.
- [ ] Default port (5200) is configurable, not hard-coded inline.
- [ ] Editing an existing agent does not destroy its stored custom address on open.

### Suggested fix direction

In the Agent Details view model, handle `HostName` `PropertyChanged`. If `!AddressManuallyEdited && (string.IsNullOrEmpty(GrpcAddress) || GrpcAddress.Contains("localhost"))`, set `GrpcAddress = $"http://{HostName}:{DefaultGrpcPort}"`. Set `AddressManuallyEdited = true` in the address field's user-edit handler (not on programmatic set).

---

## Issue 3 — Controller health flyout overlaps the Execution Log

**Type:** Bug — UI / Layout (popup collision)
**Severity:** Medium-High
**Area:** Status bar health pill → health detail flyout vs. Execution Log pane
**Reference:** Screenshot 3 — the "Controller health — 3 issues" flyout opens upward from the status-bar pill and renders on top of the Execution Log entries.

### Current behavior

Clicking the `⚠ 3 warnings` health pill opens the health-detail flyout upward. It overlaps the Execution Log pane, covering log lines (e.g. the `jvkbak unreachable` error is partially hidden behind the flyout). The flyout has no collision handling — same pattern as the earlier Sessions-popup overlap.

### Expected behavior

- The flyout anchors cleanly above the health pill and must not cover Execution Log content, OR
- If there is insufficient room above the pill, the flyout opens with a constrained max-height and its own scroll, sized to fit the available gap.
- A dimmer/scrim behind the flyout (consistent with the Sessions popup) makes it read as a temporary layer and closes it on outside click.
- The flyout never hides actively-streaming log lines that the operator may need to read while triaging the very alert the flyout describes.

### Acceptance criteria

- [ ] Opening the health flyout does not visually cover any Execution Log line.
- [ ] Flyout right/left-aligns to the pill and fits within the window without clipping.
- [ ] `StaysOpen="False"` — closes on outside click; optional scrim behind it.
- [ ] Verified at 1366 px laptop height where vertical room is tightest.

### Suggested fix direction

Anchor the flyout `Popup` to the pill with `Placement="Top"` and a computed `MaxHeight` = (pill top Y − some margin) so it can never extend past the available space; enable internal scrolling if issues exceed that height. Apply the same scrim approach used for the Sessions popup. This is the same fix family as the Sessions-popup-overlap ticket — consider a shared "anchored flyout" behavior/helper so every popup gets collision handling once.

---

## Issue 4 — Execution Log pane is fixed-height / not expandable

**Type:** Enhancement — UX / Layout
**Severity:** Medium
**Area:** Main window → Execution Log pane (bottom)
**Reference:** Screenshot 1 — Execution Log pinned to a short fixed height while many lines stream in.

### Current behavior

The Execution Log pane is locked to a small fixed height. When many log lines arrive (agent discovery, heartbeats, errors), the operator scrolls a tiny viewport. There is no way to enlarge the pane to see more lines at once.

### Expected behavior

- The Execution Log pane is **resizable** — a draggable `GridSplitter` between it and the content area above lets the operator grow/shrink it.
- The pane supports at least: a default compact height, a "tall" state (~30–40% of the window), and a maximized/expanded state.
- Optional: a one-click expand/collapse toggle in the pane header (e.g. chevron) that snaps between compact and tall without manual dragging.
- The chosen size persists for the session (and ideally across restarts).

> Note on the original request: the suggestion was "expand to 5–10%." That likely means *give it a larger share / let it grow*, not literally cap at 10% — 10% of the window is still small for a log. Recommended approach is a draggable splitter with a sensible default (~20–25%) and a max around 50%, rather than a fixed percentage. Confirm the intended default with the requester.

### Acceptance criteria

- [ ] A `GridSplitter` allows the operator to resize the Execution Log pane by dragging.
- [ ] Minimum height keeps the filter row + a few lines visible; maximum allows ~50% of window height.
- [ ] Optional expand/collapse toggle in the pane header snaps between compact and tall.
- [ ] Auto-scroll continues to work at any pane size.
- [ ] Pane size persists for the session.

### Suggested fix direction

Place the Execution Log in a `Grid` row whose `Height` is `Auto`/`*` with a `GridSplitter` on the shared edge; set `MinHeight` and `MaxHeight` on the row. For the toggle, bind a header chevron to a `LogPaneState` enum (Compact / Tall) that swaps the row height via a `DataTrigger`. Persist the last height to app settings.

---

## Cross-cutting notes

- Issues 1 and 3 both depend on the shared theme dictionary / popup-handling work from earlier tickets. Sequence them after that foundation lands.
- Issue 3 is the **third popup-overlap defect** (after the Sessions-popup-vs-TriggerAll and Sessions-popup-vs-alerts issues). Strongly consider building one reusable "anchored flyout" helper with built-in collision handling and a scrim, then routing every popup (Sessions, health, dropdowns) through it — fixing the class of bug once instead of per-popup.
- Reviewer check for Issue 1: open any dropdown in the app — if it's white or has a 3D bevel, it's not done.
