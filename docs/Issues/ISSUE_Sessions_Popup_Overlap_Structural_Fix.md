# Sessions popup overlaps the Controller health strip and critical-alerts line — structural fix required

**Type:** Bug — UI / Layout (third occurrence, escalating)
**Severity:** High (popup hides critical health alerts at exactly the moment they matter)
**Area:** TestController desktop UI — ribbon top-right corner
**History:** Flagged in `ISSUE_Ribbon_Sessions_Dropdown`, again in `ISSUE_Sessions_Health_Implementation_Gaps`. Two placement-offset attempts have not resolved it because the cause is structural, not an offset value.
**Mockup reference:** *"Sessions popup placement fix"* (before/after) in the design conversation.

---

## Why the two previous fixes failed

Both prior attempts treated this as "the popup is in the wrong spot, adjust the offset." It is not. The **top-right corner is structurally overcrowded** — three independent UI clusters occupy the same horizontal band:

1. The `Sessions ▾` button
2. The Controller telemetry strip (`CPU · MEM · DISK · NET · AGENTS · SESSIONS · ITEMS`)
3. The critical-alerts line (`3 critical · Memory pressure · Network degraded · 1 agent offline`)

The Sessions popup drops downward from the button. Clusters (2) and (3) sit immediately below and to the right of the button. So the popup lands on top of them **by geometry** — no `VerticalOffset` / `HorizontalOffset` value can avoid it while all three clusters remain in that corner. Attempt one collided with `Trigger All Events`; attempt two collided with the alerts line. A third offset tweak will just relocate the collision again.

**The fix must remove a cluster from the corner, not reposition the popup within it.**

## What was fixed correctly in the last round — do not regress these

- ✓ `+ New run` button in popup header
- ✓ `Cancel all` link
- ✓ Session row metadata (`1 agent(s) · 0/0 actions · ⏱ 4:12`)
- ✓ Popup footer (`1 session(s) active · Total elapsed: 4:12`)
- ✓ Grammar (`1 watch item`, singular)
- ✓ Threshold coloring on health metrics (CPU green, DISK amber, NET red)

---

## The fix — move telemetry + alerts to a bottom status bar

This is the recommendation from `ISSUE_Ribbon_Responsive_Layout` and `ISSUE_Controller_Health_Indicators`. It resolves three open issues at once.

### Step 1 — Relocate the Controller telemetry strip to a bottom `StatusBar`

- Remove the telemetry strip (`Controller · CPU · MEM · DISK · NET · AGENTS · SESSIONS · ITEMS`) from the ribbon's top-right.
- Add a WPF `StatusBar` docked to the bottom of the main window (`DockPanel.Dock="Bottom"`).
- Move all telemetry items into it, left to right. Keep the threshold coloring already implemented.
- Move the critical-alerts line (`3 critical · …`) to the right end of the same status bar.

### Step 2 — The top-right ribbon corner now holds only the Sessions button

After Step 1, the corner contains only `1 watch item · 0 templates` and the `Sessions ▾` button. The popup drops into empty space.

### Step 3 — Anchor the popup correctly

```xml
<Popup x:Name="SessionsPopup"
       PlacementTarget="{Binding ElementName=SessionsButton}"
       Placement="Bottom"
       HorizontalOffset="0"
       VerticalOffset="4"
       StaysOpen="False"
       AllowsTransparency="True">
```

- Right-align the popup's right edge to the `Sessions` button's right edge (which is near the window's right edge), so it occupies the rightmost column cleanly.
- `StaysOpen="False"` closes it on outside click.
- Add a full-window dimmer behind the popup (a `Rectangle` at `#000` 30% opacity spanning the window, visible only while the popup is open) so it reads as a modal layer and it's visually obvious nothing behind it is active.

### If relocating telemetry is out of scope this sprint — minimum viable fix

If Step 1 cannot be done now, the popup must be pushed **below the entire top cluster** rather than below just the button:

- Set the popup's `VerticalOffset` so its top edge clears the bottom of the alerts line (measure the alert line's bottom Y, not the button's bottom Y).
- Right-align to the window edge.
- This is a band-aid — it wastes vertical space and the popup will sit oddly far from its button. Prefer Step 1.

---

## Remaining gaps still not addressed from the spec (fix in the same PR)

### A. Left-bar colored status indicator still missing

The session row has no colored left edge. Add a 2 px left border on the row container, color-coded: amber Running, green Passed, red Failed, gray Queued/Paused. (Shown in the mockup.)

### B. Inline action icons still reduced to a single ambiguous glyph

The row shows one `□` icon. Replace with the per-status icon set:
- Running → `Pause` + `Open`
- Queued → `Cancel` + `Open`
- Failed → `Retry` + `Open`
- Passed → `Archive` + `Open`

Each 13 px, muted, hover-brighten, with tooltips.

---

## Acceptance criteria

- [ ] Controller telemetry strip is in a bottom `StatusBar`, not the ribbon corner.
- [ ] Critical-alerts line is in the status bar (right-aligned), not the ribbon corner.
- [ ] With a session active AND alerts present, opening the Sessions popup hides **nothing** — alerts remain fully visible in the status bar.
- [ ] Popup right-aligns to the window edge and overlaps no other control.
- [ ] A dimmer layer appears behind the popup while open; clicking outside closes it.
- [ ] Threshold coloring preserved after the move (CPU/MEM/DISK/NET, AGENTS).
- [ ] Session row has a 2 px colored left bar matching status.
- [ ] Row action icons are per-status (Pause/Retry/Cancel/Archive + Open), not a single glyph.
- [ ] Verified at 1366 px laptop width: popup, status bar, and ribbon all fit with no overlap.

## Note for the reviewer

This is the third ticket on the same defect. The first two were closed after offset tweaks that did not address the structural cause. Please confirm in the PR that the telemetry has actually been **moved out of the corner** (Step 1) — not that the offset was adjusted again. If the implementer proposes another offset-only change, it will fail a fourth time. The before/after mockup shows the target end state explicitly.
