# Controller health indicators lack threshold coloring, semantic grouping, and clear labels

**Type:** Bug + Enhancement — UX / Readability
**Severity:** Low-Medium (data is present but not glanceable — operators must read each number to know if anything is wrong)
**Area:** TestController desktop UI — main shell ribbon, right-side telemetry strip
**Related:**
- Sibling of `ISSUE_Ribbon_Sessions_Dropdown` (same physical strip).
- Sibling of `ISSUE_Ribbon_Responsive_Layout` (ribbon-bar wrap on laptop resolutions).
- Depends on `ISSUE_Child_HighContrast_Accessibility` for HC-safe color fallbacks.

---

## Summary

The controller health indicators (CTRL, CPU, MEM, DISK, NET, AGENTS, SESSIONS, ITEMS) at the right of the ribbon convey information accurately but not glanceably. There is no color encoding for threshold crossings, no separation between system telemetry and workload counters, and one label ("CTRL") is opaque enough that operators cannot tell what it represents without onboarding. The result: an operator must *read* every number to know if anything is wrong, defeating the purpose of an always-visible health strip.

## Current behavior

1. **No threshold coloring.** Every metric renders in the same muted color regardless of value. `CPU 5%` (healthy) and `DISK 75%` (approaching warning) look identical at a glance.
2. **`CTRL` label is opaque.** A new operator cannot tell what `CTRL` represents. Most likely intended as "Controller" but not clear.
3. **Two categories share one undifferentiated strip.** System telemetry (CPU / MEM / DISK / NET) and workload counters (AGENTS / SESSIONS / ITEMS) are different mental models but render with no visual separator or grouping.
4. **Micro-bars under each value read as decoration.** They are too small to convey trend or threshold — they just add visual noise.
5. **Healthy states are not visually positive.** `AGENTS 5/5` (a healthy "all online" state) renders in the same color as every other number, so the one piece of clearly-good news is buried.
6. **No recent history.** A 5% CPU at this instant could be hiding a spike 30 seconds ago. No sparkline or rolling view is available.

## Expected behavior

- Each metric uses **threshold coloring** on the value text:
  - Green = healthy
  - Amber = warning
  - Red = critical
- `CTRL` label is **spelled out** (`Controller`) or removed if context already makes it clear.
- Visual **grouping** separates system telemetry from workload counters — vertical divider, group label, or move workload counters to the bottom status bar.
- Micro-bars are either **removed** (cleaner) or **replaced with a small sparkline** per metric showing the last 60 seconds.
- Healthy states render in a **positive color** so they read as good news at a glance.
- Hovering any metric shows a **tooltip** with: full label, current value, threshold values, last 60-second history.

## Suggested threshold defaults (operator-configurable)

| Metric | Green | Amber | Red |
|---|---|---|---|
| CPU | < 60% | 60 – 85% | > 85% |
| MEM | < 70% | 70 – 85% | > 85% |
| DISK | < 70% | 70 – 85% | > 85% |
| NET (latency) | < 50 ms | 50 – 150 ms | > 150 ms |
| AGENTS | all online | partial outage | majority down |

## Scope

Main shell ribbon — right-side telemetry strip only. Does **not** include the Sessions dropdown work or the broader ribbon-wrap layout work — both are tracked separately.

## Acceptance criteria

- [ ] Each health metric color-codes by threshold; green / amber / red applied to value text.
- [ ] `CTRL` label is removed or replaced with `Controller`.
- [ ] Visual grouping separates system telemetry from workload counters.
- [ ] Micro-bars are either removed or replaced with informative sparklines.
- [ ] AGENTS "all online" state renders in the green / healthy color.
- [ ] Hover tooltip on each metric shows threshold values and 60-second history.
- [ ] Threshold values are operator-configurable (settings dialog or config file).
- [ ] Color choices use theme-aware brushes; High Contrast theme uses `SystemColors` fallbacks per the HC accessibility issue.

## Root-cause hypothesis

The strip was implemented as a row of `TextBlock`s bound directly to live system metrics, with styling applied uniformly rather than per-value. There is no threshold-aware brush logic, no grouping container, and no shared severity model. The micro-bars were likely added for visual richness without an information-design intent.

## Suggested fix direction

- Introduce a `HealthMetricViewModel` with `Value`, `Thresholds`, and computed `Severity` (Healthy / Warning / Critical).
- Add a `SeverityToBrushConverter` — or `DataTrigger`s on the `Style` — that maps `Severity` to the right brush.
- Wrap system-telemetry metrics in one styled `Border` group; workload counters in another (or move workload counters to the bottom status bar — same recommendation that came out of the ribbon-responsive ticket).
- Remove micro-bars **or** replace with a `Polyline`-based sparkline bound to a rolling 60-sample history exposed by the view model.
- Severity brushes via `DynamicResource` against the theme dictionaries; under HC, severity collapses to `SystemColors.HighlightColor` for amber/red and `SystemColors.WindowTextColor` for healthy — colors alone cannot carry meaning in HC, so add a small icon prefix (e.g. `!` for warning, `×` for critical) as a non-color cue.
- Operator-configurable thresholds: store in app settings, expose in a "Health thresholds" preferences page.
