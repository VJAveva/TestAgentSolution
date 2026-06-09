# Sessions dropdown + Controller health — partial implementation; critical UX elements from mockup still missing

**Type:** Bug — Incomplete implementation against approved spec
**Severity:** Medium-High (popup positioning blocks existing controls; row data is insufficient for triage)
**Area:** TestController desktop UI — Sessions dropdown + Controller telemetry strip
**Related:**
- Reopens / extends `ISSUE_Ribbon_Sessions_Dropdown` (partial fix delivered, gaps remain).
- Reopens / extends `ISSUE_Controller_Health_Indicators` (label rename done, threshold coloring missing).
- Mockup reference: *"Testcontroller sessions dropdown mockup"* in the design conversation. **Compare implementation row-by-row against the mockup before closing this ticket.**

---

## Context

The previous corrective work delivered the Sessions dropdown button, popup, session row, and renamed `CTRL` → `Controller`. Several spec items were not implemented and the popup placement now blocks the existing **Trigger All Events** button. This ticket captures only the deltas — what was missed, what is broken, and exactly what to do about each.

## What was implemented correctly — preserve these

- ✓ `Sessions (N) ▾` button in the ribbon with count badge.
- ✓ Popup opens on click; header shows `Active Sessions ( N )`.
- ✓ `Cancel All` affordance in the header.
- ✓ Session row shows ID (`7a6b79`), pipeline name, progress bar, percentage, status pill.
- ✓ `Controller` label spelled out (no more `CTRL`).
- ✓ Aggregate health summary line — `3 critical · Memory pressure · Network degraded · 1 agent offline` — this was **not** in the spec but is a genuinely good addition. Keep it.

---

## Critical fixes (these are blocking)

### 1. Popup positioning overlaps `Trigger All Events` button

**Observed.** When the Sessions popup opens, it renders directly on top of the `Trigger All Events` button in the panel below. The button becomes unclickable while the popup is open, and the text `1 essentially active` is half-hidden behind the popup.

**Fix.**
- Anchor the `Popup` to the Sessions button's lower edge with `PlacementMode="Bottom"` and `HorizontalOffset` aligned to the button's right edge.
- Set `AllowsTransparency="True"` and `StaysOpen="False"` so it closes on outside click.
- The popup must not overlap any control in the main UI; if there's insufficient room beneath the ribbon, flip to `PlacementMode="Right"` or shift left until it fits.
- Add a `Topmost` background dimmer (`Rectangle` over the rest of the window at 30% opacity) while the popup is open, so it's clear the rest of the UI is temporarily inactive.

### 2. Session row layout is cramped; `X` button overlaps the progress display

**Observed.** The X (close/cancel?) button sits flush against the percentage text. Its purpose is also ambiguous — does it close the popup or cancel the session?

**Fix.**
- Use a fixed-grid row layout, not a `StackPanel`. Suggested columns:
  ```
  [ID 60px] [Name *] [Status pill auto] [Progress 100px] [% 40px] [Meta auto] [Actions auto]
  ```
- Replace the lone `X` with the per-status icon buttons from the spec — see fix #5 below.

---

## Missing elements from the mockup — add these

### 3. Session row metadata (elapsed, agents, actions) — currently absent

**Fix.** Each row must display three additional pieces of information beneath or beside the progress bar. From the mockup spec:

```
5 agents · 23/46 actions · started 23:35    ⏱ 1:23:16
```

For the current screenshot's single session, the row should read approximately:
```
7a6b79  Single Node Setup  [RUNNING]  [▓░░░░░░░░] 0%  1 agent · 0/4 actions · ⏱ 0:00:32  [pause] [open]
```

Bind to: `Session.AgentCount`, `Session.CompletedActions / Session.TotalActions`, `Session.Elapsed` (TimeSpan, formatted `h:mm:ss`).

### 4. Left-bar colored status indicator missing

**Fix.** Each row needs a 2px colored vertical bar on its left edge, color-coded to status:
- Amber `#F0B070` — Running
- Green `#5DD0A8` — Passed
- Red `#D04848` — Failed
- Gray `#A8ACB5` — Queued / Paused

Implement as `BorderThickness="2,0,0,0"` on the row container with `BorderBrush` bound through a status-to-brush converter. This carries the status signal **at the edge** so the eye can scan a long list of sessions and find the failing one without reading the pills.

### 5. Inline action buttons missing — only ambiguous `X` present

**Fix.** Each row gets per-status action icons on the right, not just an X. Use Tabler / Material.Icons:

| Status | Show these icons (in order) | Command |
|---|---|---|
| Running | `Pause`, `ExternalLink` | `PauseSession`, `OpenSessionInDashboard` |
| Queued | `X` (cancel), `ExternalLink` | `CancelQueuedSession`, `OpenSessionInDashboard` |
| Failed | `Refresh` (retry), `ExternalLink` | `RetrySession`, `OpenSessionInDashboard` |
| Passed | `Archive`, `ExternalLink` | `ArchiveSession`, `OpenSessionInDashboard` |
| Paused | `Play`, `X`, `ExternalLink` | `ResumeSession`, `CancelSession`, `OpenSessionInDashboard` |

All icons sized 13 px, muted color `#8A8F99`, hover brightens to `#E6E7EB`. Each icon needs a `ToolTip` describing its action.

### 6. `+ New Run` button missing in header

**Fix.** Add `[+ New run]` button in the popup header, mirroring the `Cancel All` link on the opposite end:

```
Active sessions (1)          [+ New run]              [Cancel all]
```

Wire to the same command the ribbon's `Execute All` / `Trigger Item` button fires today, scoped to the current Test Plan.

### 7. Popup footer missing entirely

**Fix.** Add a footer strip below the session list with:
- `[icon] Show last 24h` — link that switches the popup view from active-only to "active + recently ended"
- `[icon] Filter` — opens a small filter affordance (by status / pipeline name)
- Right-aligned: `Total elapsed: H:MM:SS` — sum of all active sessions' elapsed times

---

## Controller health indicators — threshold coloring still missing

### 8. Metric values render in the same color regardless of severity

**Observed.** `CPU 14%`, `MEM 15.1 GB`, `DISK 81%`, `NET 999 ms` are all the same muted color. `NET 999 ms` is a critical latency value (3× the warning threshold), yet visually identical to a healthy `CPU 14%`.

**Fix.** Apply the threshold colors from the health-indicators spec to the metric **values** (not the labels):

| Metric | Green (healthy) | Amber (warning) | Red (critical) |
|---|---|---|---|
| CPU | < 60% | 60–85% | > 85% |
| MEM | < 70% | 70–85% | > 85% |
| DISK | < 70% | 70–85% | > 85% |
| NET | < 50 ms | 50–150 ms | > 150 ms |

For the values in the screenshot, the strip should currently render:
- `CPU 14%` → **green**
- `MEM 15.1 GB` → green (assuming < 70% of total)
- `DISK 81%` → **amber**
- `NET 999 ms` → **red**

Implement via `SeverityToBrushConverter` keyed off a `HealthMetric.Severity` enum (Healthy / Warning / Critical). All brushes via `DynamicResource` per theming work.

### 9. `AGENTS 0/1` should color when degraded

**Observed.** `AGENTS 0/1` renders in muted color — the same as `5/5` would. The aggregate line correctly says "1 agent offline" but the counter itself doesn't carry the signal.

**Fix.** Apply severity coloring to the AGENTS counter:
- `N/N` (all online) → green
- `(N-1)/N` and similar partial outage → amber
- `< 50% online` or `0/N` with N > 0 → red

### 10. No visual grouping between system telemetry and workload counters

**Fix.** Insert a 1 px vertical divider at `rgba(255,255,255,0.08)` between the system-telemetry group (`CPU MEM DISK NET`) and the workload-counter group (`AGENTS SESSIONS ITEMS`). Keep the aggregate health summary line full-width beneath both.

---

## Polish

### 11. Pluralization bug: `1 WatchItems`

**Observed.** The label reads `1 WatchItems, 0 Templates`. Should be singular when count is 1.

**Fix.** Use a value-converter or string format that pluralizes correctly:
- `0 watch items` / `1 watch item` / `N watch items`
- `0 templates` / `1 template` / `N templates`

Also recommend lowercase (`watch items`, `templates`) — the camel-case `WatchItems` is internal class naming leaking into the UI.

### 12. Whitespace inside parens: `Active Sessions ( 1 )`

**Fix.** Trim to `Active sessions (1)` — sentence case for headers, no inner padding.

---

## Acceptance criteria

- [ ] Popup positions correctly without overlapping any existing control; `Trigger All Events` remains clickable visually (popup closes first on outside click).
- [ ] Each session row shows: ID, name, status pill, progress bar, percentage, agent count, action progress (`N/M actions`), elapsed time.
- [ ] Each session row has a 2 px colored left bar matching status (amber / green / red / gray).
- [ ] Inline action icons present per status (Pause / Retry / Cancel / Archive / Open) — no ambiguous lone X.
- [ ] Popup header includes both `+ New run` and `Cancel all`.
- [ ] Popup footer includes `Show last 24h`, `Filter`, and right-aligned `Total elapsed`.
- [ ] All four health metrics (CPU / MEM / DISK / NET) color-code by threshold; tested with `NET 999 ms` → red.
- [ ] `AGENTS` counter colors when degraded.
- [ ] Vertical divider separates system telemetry from workload counters.
- [ ] Pluralization correct for any count: `1 watch item`, `2 watch items`, `0 templates`, etc.
- [ ] Side-by-side visual diff against the mockup attached to the original ticket — no element from the mockup is missing without a documented reason.

## Suggested approach for the implementer

This ticket is a delta against the existing implementation, not a rewrite. The fastest path:

1. **Open the mockup screenshot side-by-side with the running app.** Walk row by row, element by element. Every difference is a fix.
2. **Fix popup placement first** (item #1) — it's blocking other controls in production, highest urgency.
3. **Expand the session row layout** (items #2–5) — these are all changes inside one `DataTemplate`; do them together.
4. **Add header `+ New run` and footer** (items #6–7) — small additions to the popup template.
5. **Threshold coloring on health metrics** (items #8–9) — requires `SeverityToBrushConverter` + bindings; do in one pass.
6. **Polish** (items #10–12) — cosmetic, low effort, batch at the end.

If any spec element cannot be implemented as described, raise that explicitly in the PR rather than silently dropping it — silent simplification is what produced this partial implementation in the first place.
