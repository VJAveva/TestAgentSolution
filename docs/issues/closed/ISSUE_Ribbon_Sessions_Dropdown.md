# Ribbon right-strip layout breaks above 1 active session — needs consolidated Sessions dropdown

**Type:** Bug + Enhancement — UI / Scalability
**Severity:** Medium-High (the layout is already breaking at 1 session)
**Area:** TestController desktop UI — main shell ribbon, right-side strip
**Related:**
- Sibling of `ISSUE_Ribbon_Responsive_Layout` (ribbon-bar wrap on laptop resolutions) — fix together if possible.
- Sibling of `ISSUE_Controller_Health_Indicators` (same physical strip, independent fix).

---

## Summary

The ribbon's right-side strip currently combines four distinct kinds of information into one horizontal flow: workspace counts (WatchItems, Templates), active session chip(s), system health (CPU / MEM / DISK / NET), and workload counters (Agents, Sessions, Items). Even with a single session running, content already truncates — the "Templates" label is partially cut off in the attached screenshot. The design does not scale: with 5+ concurrent sessions each rendered as its own inline chip, the strip will overflow at any laptop resolution and likely on desktop as well.

## Current behavior (attached screenshot, one session active)

- "4 WatchItems, 2 Templates" indicator truncates to "…plates".
- One active session is rendered as an inline chip: `[0d0592] Sanity Tests on Five nodes 0% ×`. The chip takes ~250 px.
- Health metrics (CPU / MEM / DISK / NET) and workload counters (AGENTS / SESSIONS / ITEMS) compete with the chip for the remaining width.
- No overflow handling — when sessions > 1, chips will push counters off the visible strip.

## Expected behavior

- All session chips collapse into a single fixed-width **`Sessions ▾`** button in the ribbon, with a numeric badge showing active count.
- Clicking the button opens a dropdown listing every active session with: short ID, pipeline name, status pill, progress bar, elapsed time, agent count, and inline actions (Pause / Cancel / Retry / Open).
- WatchItems / Templates counter, system health, and workload counters each get fixed, non-competing space.
- The strip looks identical with 1, 5, or 50 active sessions — only the badge number changes.

## Sessions dropdown — content spec

Per session row:
- Short ID (6-char monospace, e.g. `0d0592`)
- Pipeline name
- Status pill: `RUNNING` (amber), `PASSED` (green), `FAILED` (red), `QUEUED` (gray), `PAUSED` (gray)
- Progress bar with percentage
- Elapsed time
- Agent count and action-progress (e.g. `5 agents · 23/46`)
- Inline icon buttons appropriate to status: Pause (running), Retry (failed), Cancel (queued), Archive (passed), Open (always)

Dropdown header: title, count badge, **+ New run** button.
Dropdown footer: "Show last 24h" link, filter affordance, total elapsed across active sessions.

**Visual reference:** mockup attached to the original conversation (collapsed button + expanded dropdown with 5 example sessions including running, passed, failed, and queued states). Reproduce that layout faithfully — colors, pill styles, and left-bar status indicators are deliberate and match the rest of the app's design language.

## Acceptance criteria

- [ ] Inline session chips are replaced by a single `Sessions (N) ▾` button in the ribbon.
- [ ] Button width is independent of session count; only the badge updates.
- [ ] WatchItems / Templates indicator never truncates at any width ≥ 1024 px.
- [ ] Dropdown lists all active sessions; rows show ID, name, status, progress, elapsed, agent count, inline actions.
- [ ] Dropdown updates live as sessions transition (running → passed / failed / queued).
- [ ] Inline action buttons fire the matching command (Pause = pause; Retry = re-run; Cancel = abort; Open = focus that session in the Execution Dashboard).
- [ ] QA at 1, 3, 5, and 10 simulated concurrent sessions — no horizontal overflow at any tested width.

## Root-cause hypothesis

The ribbon was built around the assumption of zero-or-one active session. The inline session-chip pattern was a quick way to surface "something is running" but doesn't compose. There is no allocated container for variable-count items, so they push neighbours off-screen instead of folding.

## Suggested fix direction

- Add a `SessionsViewModel` exposing `ObservableCollection<SessionSummary> ActiveSessions`.
- Replace the inline chip(s) with a `ToggleButton` styled per the mockup; bind the badge to `ActiveSessions.Count`.
- Implement the dropdown as a `Popup` anchored to the button, with an `ItemsControl` over `ActiveSessions`.
- Per-row commands (`PauseSessionCommand`, `RetrySessionCommand`, `CancelSessionCommand`, `OpenSessionCommand`) hang off `SessionSummary`.
- Live updates: subscribe to the same execution-service events used by the Execution Dashboard so both views stay in sync.
- All colors via `DynamicResource` (cf. parent theming work) so the dropdown survives Light/Dark/HC theme switches.
