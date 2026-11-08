# Consolidate footer into one status bar; escalate critical alerts to a health pill

**Type:** Bug + Enhancement — UX / Information design
**Severity:** Medium (current footer is cluttered, duplicated, and buries critical info)
**Area:** TestController desktop UI — bottom status bar / controller health
**Supersedes:** `ISSUE_Controller_Health_Indicators` (threshold-coloring portion is now implemented; this revises the *placement* approach).
**Mockup reference:** *"Controller health placement options"* (current 3-band footer vs. consolidated bar + escalating pill + flyout) in the design conversation.

---

## Background

Telemetry was moved out of the ribbon's top-right corner (correctly — this resolved the Sessions-popup overlap). But the move produced **three stacked footer bands** doing the job of one, with duplicated data, and it placed the most urgent information (critical alerts) in the least-visible zone of the app.

The fix is to separate information **by urgency, not by type**: routine telemetry stays ambient in a single status bar; critical alerts escalate into a prominent pill with click-through detail.

## Current behavior (attached screenshot)

Three separate horizontal bands at the bottom:

1. Telemetry strip — `Controller · CPU 26% · MEM 15.1 · DISK 81% · NET 999 · AGENTS 0/1 · SESSIONS 0 · ITEMS 0w 1τ`
2. Centered alerts line — `3 critical · Memory pressure · Network degraded · 1 agent offline`
3. Real status bar — `File: …WatchList.xml | Watchers 0 · Templates 1 | Agents 0/1 online`

Problems:
- **Duplication:** `AGENTS 0/1` (band 1) vs `Agents 0/1 online` (band 3); `SESSIONS`/`Watchers`; `ITEMS`/`Templates`.
- **Three rows of vertical space** for what belongs in one.
- **Orphaned alerts line** floating, centered, aligned to nothing.
- **Critical info in the lowest-visibility zone** — alerts like "Network degraded / 1 agent offline" must be noticeable during a run, not buried.

## Expected behavior

### 1. One consolidated status bar

Collapse all three bands into a single bottom `StatusBar` row. Deduplicate. Suggested left-to-right layout:

```
[file] WatchList.xml │ [health pill] │ CPU 26%  MEM 15.1 GB  DISK 81%  NET 999 ms   …………   Watchers 0 · Templates 1 · Agents 0/1
```

- File context on the far left.
- Health pill next (see #2).
- System telemetry group (CPU / MEM / DISK / NET) with threshold coloring already implemented — keep it.
- Right-aligned: workload counters, deduplicated to a single canonical set: `Watchers · Templates · Agents`. Drop the redundant `SESSIONS`/`ITEMS` duplicates (Sessions already has its own ribbon button; Items = Watchers).

### 2. Escalating health pill (replaces the orphaned alerts band)

A single pill that changes appearance by severity:

| State | Appearance | Behavior |
|---|---|---|
| Healthy | `✓ Healthy` — green, muted | Not clickable (or click shows "all green" panel) |
| Warning | `⚠ N warnings` — amber | Click opens detail flyout |
| Critical | `⚠ N critical` — red, higher-contrast | Click opens detail flyout |

- The pill sits in the status bar but its red/amber fill makes it stand out against the muted telemetry text — it is the one element designed to catch the eye.
- Clicking opens a flyout (anchored above the pill) listing each issue with an icon, label, and current value:
  ```
  Controller health — 3 issues
  ⊘ Memory pressure        15.1 / 16 GB
  ⊘ Network degraded       999 ms
  ⊘ 1 agent offline        [View]
  ```
- The flyout's "View" on an agent issue navigates to that agent in the Agents panel.

### 3. Open decision — how loud should a *new* critical event be? (team to decide)

The status bar is the least-watched part of the screen. For an operator mid-run, an agent dropping offline arguably deserves more than a quiet status-bar pill. Two options — pick one, this ticket does not prescribe:

- **Option A — Transient toast on state-change.** When health first transitions to critical, show a dismissible toast at the top of the content area. Operator sees it, dismisses it; ongoing state then lives in the status-bar pill. Best visibility; slightly more work.
- **Option B — Health pill in the ribbon.** Put the escalating pill in the top ribbon (high-visibility zone, now empty after telemetry moved down) instead of the status bar. Simpler; but reintroduces one element into the corner we just cleared.

Default recommendation if no decision is made: **Option A** — it gives "impossible to miss when it happens" plus "quietly available afterward," and keeps the corner clear.

## Acceptance criteria

- [ ] Bottom footer is a single `StatusBar` row — the three stacked bands are gone.
- [ ] No data appears twice (Agents / Watchers / Templates each shown once).
- [ ] System telemetry retains threshold coloring (CPU/MEM/DISK/NET).
- [ ] Health pill present; renders green Healthy / amber Warning / red Critical with correct counts.
- [ ] Clicking the pill opens a flyout listing each issue with label + current value; agent issues link to the agent.
- [ ] Critical-state pill is visually prominent against the muted status-bar text.
- [ ] The chosen new-event behavior (toast or ribbon pill) is implemented per the team's decision on #3.
- [ ] Theme-aware: pill and telemetry colors via `DynamicResource`; HC uses `SystemColors` + an icon prefix (`!` / `×`) so severity is not color-only.
- [ ] Verified at 1366 px laptop width — single bar fits, no wrap, no overlap.

## Root-cause hypothesis

The relocation moved the existing telemetry strip and alerts line wholesale into the footer region without merging them with the pre-existing status bar, so three bars now coexist. There was no consolidation step and no severity-based separation of routine vs. critical information.

## Suggested fix direction

- Build one `StatusBar` (`DockPanel.Dock="Bottom"`); delete the separate telemetry strip and the centered alerts band.
- Merge workload counters from the old strip and the old status bar into one deduplicated set.
- Add a `HealthPill` control bound to `ControllerHealth.Severity` (Healthy / Warning / Critical) and `ControllerHealth.IssueCount`.
- Implement the flyout as a `Popup` (`Placement="Top"`) over an `ItemsControl` bound to `ControllerHealth.Issues`.
- For Option A: a toast service that fires on the `Severity` transition into Critical/Warning; dismissible, auto-hide after N seconds optional.
- All brushes via `DynamicResource`; HC severity falls back to `SystemColors` plus the icon-prefix cue.

## Note for the reviewer

The threshold-coloring work from the prior health ticket is done and should be preserved — this ticket is about **placement and consolidation**, not re-doing the colors. Confirm in the PR that the footer is a single bar (look at the bottom of the window — three bands = not done) and that the health pill actually escalates (force a critical condition and verify it turns red and opens the flyout).
