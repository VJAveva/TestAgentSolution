# Implementation Prompt: Optimize WatchList Editor Layout

| Field | Value |
|---|---|
| **Component** | TestControllerGrpc (WPF) — MainWindow |
| **Type** | Layout optimization (no logic change) |
| **Goal** | Reclaim ~130px vertical on laptops; more professional, comfortable layout |
| **Mockup** | `WatchList_Optimized_Layout_Mockup.html` |
| **Verify** | Launch on JVGR22 at 1366×768 and look — a build/test pass does NOT prove a WPF layout |

---

## Instructions for Copilot

Optimize the MainWindow layout to reclaim vertical space and look more
professional. This is **layout-only** — do not change execution, agent, or
pipeline logic. Match the mockup. Read MainWindow.xaml and its code-behind first.

**Hard guardrails (from a prior code audit — violating these causes silent
breakage):**
- Do NOT rename `ColTreePanel`, `ColNodeProperties`, `ColAgentPanel`, `RowLogPane`
  or any named grid column/row — the code-behind and UiLayoutStore reference them
  by name for pin/unpin and restore.
- Do NOT reorder or remove the splitter columns (Col 1, Col 3) — the code-behind
  indexes them positionally.
- Inside App-merged resource dictionaries use **DynamicResource**, never
  StaticResource — StaticResource is merge-order dependent and crashes at runtime
  even on a green build.
- Guard tests (CompactChromeTests, TokenAuditTests, ComponentAdoptionTests) pin
  exact markup strings and counts. Expect ~one guard failure per pattern changed —
  re-point those tests **deliberately**, never weaken or delete them.
- If pane semantics change, version UiLayoutStore so saved widths don't land on the
  wrong panes.

Work the changes below in priority order. After EACH, confirm the app builds AND
launch it on a laptop-sized window to check by eye.

---

## Change 1 — Collapse the ribbon (~88px → ~40px). Biggest win.

The ribbon is a two-row stacked band (large icon buttons + captions + group
labels like File / Test Plans / View). Collapse to **one compact toolbar row**:
- Icon + short label inline (not stacked), ~40px tall.
- Group captions removed; use thin vertical separators between groups instead.
- Keep EVERY existing command/button and its binding — only the size and
  arrangement change. Move the theme + Simplified toggles to the row's right end.
- If it's a WPF Ribbon control, reduce group sizes / use small images, or replace
  with a ToolBarTray + ToolBar carrying the same Command bindings.

This hands ~48px back to every pane below, on every screen. Verify every toolbar
button still fires after the change.

## Change 2 — Log pane becomes a collapsible strip. Space on demand.

Today Row 2 (the execution log) is `2*` and pinned open — it dominates the laptop
screen. Make it two states:
- **Collapsed (new default):** a ~28px strip showing a status dot, "Execution Log",
  the entry count, the error count, and the latest log line, plus an "Expand"
  affordance.
- **Expanded:** the full log pane at a user-draggable height (its current content:
  filters, Pause/Clear/Copy/Export, the log body — unchanged).
- One click toggles. Reuse the existing log entries collection for the "latest
  line" — no new data source.
- Keep RowLogPane's NAME; only change its default height/behaviour and what it
  hosts when collapsed.

This gives MORE space when triaging errors (expand) and LESS when editing
(collapsed) — solving "the log should occupy more space" the right way.

## Change 3 — Node Properties into a 2-column form (~115px back).

The properties panel stacks six full-width fields (Tag, Watch Path, Filter, Build
Number Field, Drop Location Field, Build Base Path), each ~570px wide holding
~25-character values. Pair them into **two columns** (see mockup):
- Long values (Tag, Build Base Path) span both columns; short ones pair up
  (Watch Path | Filter, Build Number Field | Drop Location Field).
- Group under the existing section headers (General / Trigger File Fields /
  Build Source).
- ~115px of vertical handed to the tree and agents panes. Contained to one file;
  guard suite already covers it.

## Change 4 — Agents KPI inline + surface OFFLINE (correctness).

- Replace the large stacked AGENTS / BUSY / FREE numerals with a compact inline
  row (saves ~25px in the pane with the most to show).
- **Surface the offline count.** FleetVM.cs already computes `OfflineCount` but the
  header only shows AGENTS/BUSY/FREE. Add offline to the inline KPI. This is a
  correctness fix: the header currently shows agents as "free" that are actually
  offline (e.g. JVKPRI with a cancelled gRPC connection reads as free). Show
  offline distinctly (amber/red).

## Change 5 — Tree label clipping at depth 9 (optional, addresses a real pain).

Deeply nested nodes clip on the right, forcing a manual splitter drag to read
names. Let the tree text truncate cleanly with an ellipsis (with a tooltip showing
the full name) OR ensure the tree scrolls horizontally within its pane rather than
clipping. Small change; fixes the "can't read node names" annoyance.

## Skip: the ribbon horizontal dead zone.
The ribbon is ~43% empty between the View group and the user chip, but that's
HORIZONTAL space, which the laptop is not short of. Reclaiming it buys nothing.
Do not spend effort here.

---

## What "safe to change" per the audit

- Adjusting star ratios and minimum widths in MainWindow's grid — persisted values
  are re-clamped on load, so a stale saved layout can't strand you.
- Adding new styles to ControlStyles.xaml.
- Changing token VALUES in the three theme files — parity + contrast guards catch
  mistakes.

---

## Acceptance criteria

| ID | Criterion |
|---|---|
| AC-1 | Ribbon is one compact ~40px row; every command still fires |
| AC-2 | Log defaults to a ~28px collapsed strip showing latest line + counts; one click expands to a draggable full-height pane with all existing log tools |
| AC-3 | Node Properties renders as a 2-column form per the mockup |
| AC-4 | Agents KPI is inline AND shows the offline count; an offline agent is not shown as "free" |
| AC-5 | Tree labels no longer force a manual splitter drag to read (ellipsis+tooltip or horizontal scroll) |
| AC-6 | No named grid column/row renamed; splitters not reordered; UiLayoutStore still restores correctly |
| AC-7 | Guard tests re-pointed deliberately (not weakened); all tests pass |
| AC-8 | No execution/agent/pipeline logic changed |
| AC-9 | Verified by launching on a 1366×768 window and inspecting by eye |

---

## Build order

1. Change 1 (ribbon) — biggest visible win. Launch + eyeball.
2. Change 2 (log strip) — the "more space on demand" fix. Launch + eyeball.
3. Change 3 (2-column properties). Launch + eyeball.
4. Change 4 (agents inline + OFFLINE). 
5. Change 5 (tree clipping) if tokens allow.

Start with Change 1. Show me the current ribbon markup and how it's structured
before editing, then collapse it — preserving every command binding.

---

## The one rule that matters most

A WPF layout change is NOT proven by a passing build or test — resource
resolution and layout only fail at runtime. After every change, the app must be
launched on a laptop-sized window (1366×768) and inspected by eye. Treat "it
builds" as necessary but never sufficient.
