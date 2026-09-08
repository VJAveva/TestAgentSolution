# Code Churn Header Compaction — Copilot Prompt Pack

**Target:** `TestController.WebClient` (React) — Code Churn page
**Goal:** Reclaim ~190px of vertical chrome for the regression grid, fold the six metrics into the header, and make an expanded row stop destroying the reading experience.
**Prompts:** P01–P16 across 6 phases, each phase gated by a checkpoint.

---

## Ground rules — paste this block at the top of every Copilot session

```
GROUND RULES FOR THIS SESSION

1. Read before you write. Open the actual file, quote the actual JSX/CSS you are
   changing, and show me the current line numbers. Do not describe code you have
   not opened.
2. If a file, component, or class name I mention does not exist, STOP and report
   the mismatch. Do not invent a substitute name and proceed.
3. No new npm dependencies. No UI library swaps. No CSS-in-JS migration. Work
   inside the styling approach the file already uses.
4. Do not restructure state management, data fetching, or the grid's data model.
   This is a layout and density change only.
5. One prompt at a time. Stop at the checkpoint at the end of each phase and wait
   for me to confirm before continuing.
6. Never delete a metric, filter, or count without moving it somewhere I can still
   see it. Removing duplication is allowed. Removing information is not.
7. Preserve every existing keyboard interaction, aria-label, and test id. If you
   move an element, move its test id with it.
```

---

## Phase 0 — Baseline and inventory

The point of this phase is to stop guessing. You cannot claim to have saved 190px if nobody measured the 300px.

### P01 — Measure the current chrome

```
Open the Code Churn page in TestController.WebClient. Walk down from the top of
the page to the first data row of the regression grid and list, in order, every
horizontal band of chrome:

  - the component file and line range that renders it
  - its CSS class
  - its rendered height in px at a 1440x900 viewport
  - the data it displays

Give me a table and a total. Do not change any code yet.

Expected bands, for your orientation (verify, do not trust this list):
  app nav / page title + breadcrumb + ADO status / branch+range+component row /
  context+author+workitem+coverage+noise+actions row / six metric tiles /
  AI summary strip / summary chip row / grid header row.
```

### P02 — Find the duplicated data

```
Using the table from P01, identify every value that is rendered in more than one
band. For each duplicate give me: the value, every location it appears, and which
single location should survive.

I already believe these are duplicates. Confirm or correct with real file
references:

  1. "695 changes / 13 subsystems / 425 files" appears in BOTH the metric tile
     strip AND the summary chip row below the AI summary.
  2. "RUNTIME 9 / CONFIG 6" chips duplicate the counts already shown on the
     Context filter toggles.
  3. "NO SUITE 1" chip duplicates both the "No suite mapped 1" Coverage control
     and the "No suite mapped" metric tile.
  4. The date range "2026-09-01 - 2026-09-08" duplicates the Range selector
     ("Weekly" resolves to exactly that window).
  5. The breadcrumb "AppServer OMI / All components" duplicates the Component
     dropdown on the far right of the filter row.

Output a deletion plan. Do not delete anything yet.
```

**Checkpoint 0 — do not continue until all three are true**
- Total chrome height is a real measured number, not an estimate.
- Every duplicate has exactly one designated survivor.
- Nothing has been edited yet.

---

## Phase 1 — Delete the duplication

The cheapest 70px you will ever reclaim. Two full bands disappear and nothing is lost.

### P03 — Remove the summary chip row

```
Delete the summary chip row entirely (the band containing the RUNTIME / CONFIG /
NO SUITE chips and the "N changes - N subsystems - N files - date range" text).

Before deleting, verify each value has a surviving home:
  - RUNTIME count  -> badge on the Context "Runtime" toggle (already there)
  - CONFIG count   -> badge on the Context "Config" toggle (already there)
  - NO SUITE count -> badge on the Coverage control (already there)
  - changes / subsystems / files -> metric strip (stays, restyled in Phase 2)
  - date range -> move to a secondary line inside the Range control's tooltip,
    and to the aria-label of the Range control. Do not give it its own element.

If any value has no surviving home, stop and tell me which one before deleting.

Remove the now-dead CSS rules too. Report the height reclaimed.
```

### P04 — Merge the breadcrumb into the component picker

```
The breadcrumb "AppServer OMI / All components" and the "Component" dropdown at
the right end of the filter row show the same state twice.

Change the breadcrumb's second segment into the component picker itself: render
it as a button styled like text that opens the existing component dropdown
menu, showing the current selection ("All components", or the selected component
name). Reuse the existing dropdown component and its onChange handler - do not
write a second picker.

Then delete the standalone Component field from the filter row.

Keep the first segment ("AppServer OMI") as a plain project label.
Keep the existing test id on whichever element now owns the interaction.
```

### P04a — Spec the component popover properly

```
The breadcrumb picker from P04 replaces a dropdown that may hold dozens of
components. A text button with no search would be a downgrade. Build the popover
properly.

First, tell me two things by reading the code, before writing anything:
  1. Is the existing Component filter single-select or multi-select?
  2. Where does its option list come from - the full component list for the
     project, or only components that have changes in the selected range?

Then build the popover with:

  - A filter input at the top, autofocused on open, filtering as you type.
  - Two labelled groups in the list:
      "Changed in range - N"     components with changes, each showing its count
      "No changes in range - N"  the rest, muted, count shown as an em dash
    A component with zero changes stays selectable. Selecting it must produce an
    explicit empty state in the grid - "No changes for <component> in this range"
    - not an unexplained blank grid.
  - Checkbox rows if the underlying filter is multi-select, single-select rows if
    it is not. Match what exists. Do not change the filter's cardinality.
  - A footer action that resets to all components.
  - Closes on Escape and on outside click, returning focus to the trigger.
  - Keyboard: Up/Down moves through options, Enter toggles, Tab moves to the
    footer.

Button label rules:
  0 selected -> "All components"
  1 selected -> the component name
  N selected -> "N components"

When 1 or more are selected the button takes the active-filter styling (accent
border and tint) so a narrowed scope is visible without opening the popover, and
the Clear button's count from P08 increments by one.

Affordance: the trigger is a real button with a 1px border and a chevron, not a
text link. It sits inside the breadcrumb but must not read as navigation.
```

**Checkpoint 1**
- Two bands gone, roughly 70px reclaimed.
- Selecting a component from the breadcrumb still filters the grid.
- The picker reads as a control, not a link — border, chevron, hover, focus ring.
- Selecting a zero-change component gives a worded empty state, not a blank grid.
- No count or label from P02 has disappeared from the screen.

> **If you would rather keep Component in the filter rail**, skip the second half of
> P04: keep the breadcrumb as static text, and place the Component field at the
> left of the rail next to Branch instead of at the far right. You keep the
> conventional dropdown affordance and lose about 170px of rail width, which
> means Component falls into the `⋯ More` overflow at 1024px. Both placements are
> in the mockup under the Component picker toggle — compare them before deciding.

---

## Phase 2 — Row 1: identity, metrics, actions on one line

### P05 — Build the metric strip as inline stats

```
Replace the six large metric tiles with a single inline stat strip that lives on
the same line as the page title. Target height for the strip: 24px.

Each stat renders as: bold value, then a lighter label at 12px, side by side, not
stacked. Separate stats with a 1px vertical divider and 12px of gap. Do not put
them in cards, do not give them borders or shadows, do not use all-caps labels.

The six stats, in this order and with these labels:
  695 Changes | 425 Files | 13 Subsystems | 1 No suite | v1 Impact map | 0 Unresolved

Tone rules:
  - "No suite" renders in the warning tone when > 0, muted when 0.
  - "Unresolved" renders in the danger tone when > 0, muted when 0.
  - NEVER hide a stat when its value is 0. A zero that used to be non-zero is
    information. Muted, always present.

Create the component at src/components/codechurn/ChurnStatStrip.tsx (adjust the
path to match the folder convention you actually find). Region anchor comment at
the top: // CC-H3 stat strip
```

### P06 — Make the stats do something

```
A metric that only displays is wasted horizontal space. Wire each stat to apply
its own filter on click, using the existing filter state setters:

  Changes     -> no-op (it is the total; render as non-interactive text)
  Files       -> no-op
  Subsystems  -> no-op
  No suite    -> sets Coverage = "No suite mapped"
  Impact map  -> opens the existing impact-map version popover if one exists;
                 if none exists, render as non-interactive text and tell me
  Unresolved  -> sets Noise = unresolved paths, if that filter value exists;
                 if it does not exist, stop and tell me rather than inventing it

Interactive stats get role="button", tabIndex, an aria-label of the form
"Filter to 1 file with no suite mapped", visible focus ring, and a subtle hover
background. Non-interactive stats get none of those. Clicking an already-applied
stat clears that filter.
```

### P07 — Assemble Row 1

```
Build the header's first row as a single flex line, 40px tall, three clusters:

  LEFT   "Code churn" at 14px/600, then a thin divider, then the breadcrumb from
         P04 at 12px muted.
  CENTER the P05 stat strip, flex: 1, aligned left with 24px margin from the
         title cluster.
  RIGHT  ADO status pill, "updated 08:42 PM" at 12px muted (full timestamp in
         the title attribute), refresh icon button, a divider, the AI summary
         button, the Export split button.

Each cluster is a <div class="unit"> with white-space: nowrap and
display: inline-flex, matching the .unit convention already used in this
toolbar.

Overflow behaviour for the stat strip:
  - below 1280px: keep the first four stats, collapse the rest into a "+2"
    button that opens a popover with the full list
  - below 1024px: collapse the entire strip into one button reading
    "695 changes, 425 files" that opens the popover

Region anchor: // CC-H2 identity row
```

**Checkpoint 2**
- Row 1 is one line at every width from 1024px to 1920px. No wrapping, ever.
- All six stats reachable at every width, via popover if collapsed.
- Clicking "No suite" filters the grid; clicking it again clears the filter.
- Keyboard tab order runs left to right through the row.

---

## Phase 3 — Row 2: the filter rail

### P08 — Collapse the two filter rows into one

```
Merge the two filter bands into a single 40px row. Remove the standalone text
labels ("Branch", "Range", "Context", "Author", "Work item", "Coverage",
"Noise") - each control carries its own label internally instead, either as
placeholder text or as a "Label: value" pattern inside the control.

Order the controls by how often they are used, most-used leftmost:

  Range (segmented: Build / Weekly / Custom / Release)
  Branch
  Context (Runtime / Config toggles, counts as badges)
  Author
  Work item (Bug / Story / IMS)
  Coverage
  Noise
  [Clear]

Each control group wrapped in <div class="unit"> with white-space: nowrap, so a
control and its badge can never be split across a wrap point. This is the same
fix already applied to the chip row - reuse the existing .unit rule, do not
write a second one.

"Clear" is hidden entirely when every filter is at its default. It appears the
moment any filter is non-default and reads "Clear 3 filters" with the live count.

Region anchor: // CC-H4 filter rail
```

### P09 — Overflow into a "More" popover

```
Add overflow handling to the filter rail so it never wraps to a second line.

Use a ResizeObserver on the rail. Measure each .unit child. When total width
exceeds available width, move controls from the right end into a trailing
"More" button that opens a popover containing them, stacked vertically. Keep
moving until it fits. On widen, move them back.

The More button shows a badge with the number of hidden filters that are
currently non-default, so an active filter is never invisible.

Debounce the measurement at 100ms. Do not run the measurement loop on every
render - only on resize and on filter-set change.

Never move Range or Branch into the overflow. Those two are always visible.
```

**Checkpoint 3**
- Filter rail is exactly one line from 1024px to 1920px.
- Shrinking the window moves controls into More rather than wrapping.
- An active filter hidden in More is signalled by the badge count.
- Clear button count matches the number of non-default filters.

---

## Phase 4 — AI summary and scroll behaviour

### P10 — Collapse the AI summary to one line

```
The AI summary strip currently renders full text and pushes the grid down.

Rebuild it as a 28px single line: the "AI summary" label, then the summary text
truncated with text-overflow: ellipsis, then a "Details" disclosure button at
the right end. Expanding reveals the full text in a panel with
max-height: 25vh and overflow: auto, so a long summary can never push the grid
off screen.

Default state is collapsed. Persist the expanded/collapsed choice in the same
place this app already stores UI preferences - find it and tell me what it is
before writing. Do not add a new persistence mechanism.
```

### P11 — Condensed header on scroll

```
Make the whole header sticky and add a condensed state.

  - Wrap Row 1, Row 2 and the AI summary strip in a single sticky container:
    position: sticky, top: 0, z-index above the grid, solid background, 1px
    bottom border. Region anchor: // CC-H1 header shell
  - Place a 1px sentinel div immediately above the header. Use an
    IntersectionObserver on the sentinel; when it leaves the viewport, add
    .is-condensed to the header.
  - .is-condensed collapses the header to a single 44px line containing:
    the title, the active filter summary as small chips, the first three stats,
    and the Export button. Row 2 and the AI summary strip are hidden.
  - Removing .is-condensed restores the full header.
  - Transition height and opacity over 150ms. Respect
    prefers-reduced-motion: reduce by skipping the transition.

Do not use a scroll event listener. IntersectionObserver only.
```

**Checkpoint 4**
- Expanded header measures ~108px (40 + 40 + 28). Condensed measures ~44px.
- Scrolling the grid condenses the header; scrolling to top restores it.
- No layout shift or flicker at the transition boundary.
- Total reclaimed vs. the P01 baseline is ~190px. State the actual number.

---

## Phase 5 — Give the grid the space, and fix row expansion

This is the half of the problem the header work does not solve. A 195-row-tall expanded node is its own bug.

### P12 — Bind the grid height to the header height

```
Stop hardcoding the grid's height offset.

Have the header publish its own rendered height to a CSS custom property on the
page root, updated by a ResizeObserver:

  document.documentElement.style.setProperty('--cc-chrome-h', height + 'px')

The grid's scroll container then uses:

  height: calc(100dvh - var(--cc-chrome-h) - var(--app-nav-h));

Use dvh, not vh, so mobile browser chrome does not clip the last row. Give the
grid its own overflow: auto and make the grid's own column header row sticky at
top: 0 inside that container.

Search the codebase for any existing hardcoded pixel offset used for this
purpose and remove it. Report what you found.
```

### P13 — Tame the MANUAL column

```
The MANUAL column renders every mapped test case as an inline chip. On the
aabootstrap row that is 25+ chips, which is what actually makes the row 400px
tall - not the header.

Collapsed row: render at most 3 chips, then a "+22 more" button. The row's
collapsed height must be capped so that no row is taller than 2 chip lines.

Clicking "+22 more" opens a popover listing all of them, scrollable, with the
full test case names. It does NOT expand the row.

Apply the same treatment to the SUBSYSTEMS and FILES columns, which have the
same unbounded-list problem: show the first few, then "+N more".

Do not truncate the underlying data. Every test case must still be reachable.
```

### P14 — Bound the expanded row

```
Change row expansion so an expanded row can never exceed 40% of the viewport.

  - The expanded detail panel gets max-height: 40vh and overflow: auto.
  - Only one row expands at a time (accordion). Expanding a second row collapses
    the first. Add a small pin affordance on the expanded row that opts it out of
    auto-collapse, for the case where two rows genuinely need comparing.
  - On expand, scroll the expanded row's top edge to just below the sticky
    header, so the user is never left staring at the middle of a row.
  - On collapse, restore the previous scroll position.

Then tell me honestly whether an inline expansion is the right pattern here at
all, or whether a right-side detail drawer would be better - a drawer keeps every
grid row a uniform height, which is what would let us virtualize this grid later.
Give me the trade-off, do not just implement your preference.
```

**Checkpoint 5**
- Every collapsed row is the same height, within one chip line.
- The tallest possible expanded row is 40vh.
- Grid header row stays visible while scrolling.
- Resizing the window resizes the grid, with no gap and no clipped last row.

---

## Phase 6 — Density, accessibility, responsive

### P15 — Density toggle

```
Add a Compact / Comfortable density toggle to the header's right cluster,
persisted alongside the AI summary preference from P10.

  Comfortable (default): 36px rows, 13px text
  Compact:               28px rows, 12px text, tighter chip padding

Density affects grid rows only, not the header. Implement with a data-density
attribute on the grid container and CSS custom properties for row height and
font size. Do not duplicate the row component.
```

### P16 — Final audit

```
Audit the finished header against this checklist and give me a pass/fail per
line with a file reference for each failure:

LAYOUT
  [ ] Header is exactly 2 rows + optional AI strip, expanded
  [ ] Nothing wraps at any width from 1024px to 1920px
  [ ] Condensed header is a single 44px line
  [ ] Grid height derives from --cc-chrome-h, no hardcoded offsets remain

INFORMATION
  [ ] All six metrics reachable at every width
  [ ] Zero-valued "No suite" and "Unresolved" are muted but never hidden
  [ ] No value from the P02 duplicate list was lost, only de-duplicated
  [ ] Active filters hidden in the More popover are signalled by a badge

ACCESSIBILITY
  [ ] Every interactive stat has role, tabIndex, aria-label, visible focus ring
  [ ] Tab order is left-to-right, Row 1 then Row 2
  [ ] Popovers close on Escape and return focus to their trigger
  [ ] prefers-reduced-motion disables the condense transition
  [ ] Colour is never the only signal for warning/danger stats - include text

REGRESSIONS
  [ ] Every test id from before the change still resolves
  [ ] Every filter still produces the same grid result as before
  [ ] Refresh, AI summary, Export all still work
  [ ] Dark mode renders correctly (the toggle in the nav bar)

Then report: baseline chrome height from P01, final chrome height, px reclaimed,
and how many additional grid rows that is at Comfortable and at Compact density.
```

---

## Anti-patterns — tell Copilot these are out of bounds

- **Do not hide zero-valued risk metrics.** `Unresolved paths: 0` is a fact worth showing. A metric that vanishes when it is 0 makes it impossible to notice when it becomes 1. Coverage gaps are output, not noise.
- **Do not put the metrics in cards.** Six bordered tiles is what created the problem. Inline text with dividers.
- **Do not use all-caps labels or middle-dot meta strings.** `RUNTIME 9 · CONFIG 6 · NO SUITE 1` reads as generic dashboard chrome. Sentence case, real dividers.
- **Do not add a scroll event listener.** IntersectionObserver and ResizeObserver only.
- **Do not solve row height with `overflow: hidden` on the row.** That hides test case chips with no way to reach them. Cap the list, add a popover.
- **Do not introduce a virtualization library in this pass.** P14 sets up the conditions for it. Doing both at once makes the diff unreviewable.

---

## Expected outcome

| | Before | After (expanded) | After (condensed) |
|---|---|---|---|
| Page title band | ~48px | merged into Row 1 | merged |
| Filter bands (2) | ~88px | 40px | hidden |
| Metric tiles | ~62px | merged into Row 1 | 3 stats inline |
| AI summary | ~28px | 28px, collapsible | hidden |
| Summary chip row | ~40px | deleted | deleted |
| **Total chrome** | **~266px** | **~108px** | **~44px** |

At Compact density that is roughly 6 more visible grid rows in the expanded state and 8 more once the header condenses on scroll.
