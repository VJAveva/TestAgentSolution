# Status bar metrics are stacked, tiny, and the health warning is mispositioned

**Type:** Bug + Enhancement — UI / Readability
**Severity:** Medium
**Area:** TestController desktop UI — bottom status bar (controller metrics + health warning)
**Related:** Builds on `ISSUE_Health_StatusBar_Consolidation` (the consolidation landed; this refines the metric *layout and typography* within the bar).
**Mockup reference:** *"Statusbar metrics redesign"* (current stacked layout vs. inline single-row layout) in the design conversation.

---

## Summary

The controller metrics in the status bar are rendered in a cramped two-row, label-over-value format at a very small font (~9 px), making them hard to read at a glance. The metrics also do not use the horizontal space available to them. Additionally, the `4 critical` health warning is positioned at the far left of the bar, disconnected from the metrics it summarizes — it should sit on the right, beside the metrics.

## Current behavior (attached screenshot)

- Each metric is stacked: label on top (`CPU`), value beneath (`10%`), both at ~9 px.
- Metrics are squeezed into a narrow cluster despite ample empty horizontal space in the bar.
- The `4 critical` warning pill is on the **far left**, next to the filename, away from the CPU/MEM/DISK/NET/AGENTS metrics on the right.
- Overall the bar is low-legibility — values must be read deliberately, not glanced at.

## Expected behavior

1. **Inline metrics (label + value side by side).** Each metric renders as `CPU 10%`, `MEM 15.1 GB`, `DISK 81%`, `NET 999 ms`, `AGENTS 0/1` — label and value on the same line, not stacked.
2. **Larger font.** Metric values at ~13 px (roughly double the current ~9 px), labels slightly smaller (~11 px) and muted. Values bold for emphasis.
3. **Thin dividers between metrics** (1 px, `rgba(255,255,255,0.10)`) to separate them cleanly without boxes or clutter.
4. **Health warning on the right, beside the metrics.** Move the `4 critical` pill from the far left to the right end of the bar, immediately after the metrics group, separated by a divider. It remains a clickable pill that opens the health-detail flyout.
5. **Single row.** Everything fits on one line: filename (left) → spacer → metrics group → divider → health pill (right).
6. **Threshold coloring preserved** on values (CPU green, DISK amber, NET red, etc.).

## Layout (left to right)

```
[file] WatchList.xml ……………………  CPU 10% │ MEM 15.1 GB │ DISK 81% │ NET 999 ms │ AGENTS 0/1  ║  ⚠ 4 critical ▴
```

## Acceptance criteria

- [ ] Each metric shows label and value inline on one line (no stacked label-over-value).
- [ ] Metric value font is ~13 px; clearly larger than the current ~9 px.
- [ ] Metrics are separated by thin vertical dividers.
- [ ] The health warning pill is positioned at the right end of the bar, beside the metrics — not on the left.
- [ ] The warning pill remains clickable and opens the health-detail flyout (per the consolidation ticket; flyout must not overlap the Execution Log).
- [ ] Threshold coloring retained on all metric values.
- [ ] Entire bar fits on a single row at 1366 px laptop width without wrapping or clipping.
- [ ] Theme-aware: colors via `DynamicResource`; HC uses `SystemColors`.

## Root-cause hypothesis

The metrics were implemented as a row of small stacked `StackPanel`s (label `TextBlock` over value `TextBlock`) with a fixed small font, likely carried over from the original cramped ribbon-corner placement before the status-bar move. The warning pill's left placement is an artifact of where the alert line originally lived; it was not repositioned when the bar was consolidated.

## Suggested fix direction

- Replace each stacked metric `StackPanel` with a horizontal one: `[label TextBlock][value TextBlock]` inline, label `FontSize≈11` muted, value `FontSize≈13` `FontWeight=SemiBold`.
- Insert `Separator`/thin `Border` dividers between metrics.
- Use a `DockPanel` or `Grid` for the bar: filename `DockPanel.Dock=Left`, health pill `DockPanel.Dock=Right`, metrics group filling the middle (right-aligned next to the pill).
- Keep the existing `SeverityToBrushConverter` bindings on values so threshold colors persist.
- Verify the health pill still anchors its flyout upward without covering the Execution Log (cross-reference the flyout-overlap fix).
