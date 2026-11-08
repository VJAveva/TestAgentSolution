# Enhancement: Redesign TestAgent CI results email (professional, concise, Outlook-native)

**Component:** TestAgent CI — HTML email report generator
**Reference artifact:** `OAK_main_20260601.7` use-case results email
**Mockup:** `results-email-mockup.html` (table-based, 600px, populated with real run data)
**Type:** Enhancement (design + content)
**Severity:** Medium
**Related:** ISSUE-TestAgentCI-email-formatting (stray `<br>` structural bug — fix first)

## Summary

The current results email reads as a generic dashboard: a header that never says what ran, a 920px width that clips in Outlook, four oversized stat cards, and CSS (gradients, `border-radius`, `box-shadow`) that the Outlook Word engine drops. Redesign it to a concise, single-screen, Outlook-native notification that states the executed scope, surfaces failures with a triage classification, and renders identically in Outlook desktop, Outlook Web, and Gmail. The agreed design is captured in `results-email-mockup.html`.

## Layout spec (top to bottom)

1. **Header (deep emerald band).** Background `#0F5132`, white text, light-green eyebrow, 3px brighter-green bottom rule (`#3FB950`). Contents in order:
   - Eyebrow: the component under test, parameterized — `{Component}` (e.g. `AppServer`). This replaces the old hardcoded "TestAgent CI" label and is bound from the action/pipeline parameter.
   - Status pill: `PASSED` / `FAILED`, right-aligned. Stays semantic (red for failed, green for passed) regardless of header color.
   - Title: `{Product} — {TestType}` (e.g. `AVEVA System Platform — WAS Smoke Test`).
   - Meta line: `Build {Build} · Controller {ControllerNode} · {RunDate}`. Note **Controller**, not Machine — the controller node is where results aggregate; the executing agent is shown per-row in the table below.
   - The previous "Executed: Set1 (25) · …" line is **removed** from the header; the same scope is conveyed by the Use Case Results table.

2. **Metrics strip (one compact row).** Pass Rate · Total · Passed · Failed · Duration. Replaces the four large stat cards. Pass Rate red when failures exist, green otherwise; Passed green; Failed red.

3. **Use Case Results table.** Columns, in order:
   `Agent | Use Case | Pass % | Progress | Pass | Fail | N/E | Duration`
   - **Agent** is the first column (the agent/machine that executed the suite, e.g. `JVGR22`).
   - **Progress** is a mini inline bar (green fill = pass %, red remainder), built as a nested table with `bgcolor` + `font-size:0` spacer cells for Outlook reliability.
   - Header row uses the same emerald `#0F5132` with light text, consistent with the page header.
   - Pass % is color-coded: green at 100%, amber when below, red on heavy failure.

4. **Failed Tests table (red theme — unchanged family).** Columns: `Test | Set | Type | Error`.
   - **Type** is a colored classification pill (see below).
   - Red header `#7f1d1d`, light-red rows `#fef6f6`. Errors stated in plain terms.

5. **Failure Classification legend.** Color key beneath the failed tests:
   - **Critical** (`#b91c1c`) — fails consistently across runs; a confirmed product defect that blocks the use case.
   - **New** (`#b45309`) — passed in the previous build but failed in this one; a likely regression to triage first.
   - **Flaky** (`#6d28d9`) — passes and fails intermittently with no code change; unstable or timing-sensitive test.

6. **Footer.** Results path (monospace) + automated-notification line. Minimal.

## Color tokens

| Token | Hex | Use |
|---|---|---|
| Header / table-header band | `#0F5132` | Deep emerald frame |
| Header accent rule | `#3FB950` | 3px bottom border |
| Eyebrow text | `#86EFAC` | Component label on emerald |
| Header meta text | `#b7e4c7` | Muted on emerald |
| Pass green | `#15803d` / bar `#22c55e` | Pass %, progress fill |
| Fail / Critical | `#b91c1c` | Fail counts, status pill, Critical |
| New | `#b45309` | New regression |
| Flaky | `#6d28d9` | Flaky |
| Failed-tests header | `#7f1d1d` | Red section header |
| Neutral text / muted | `#1f2937` / `#6b7280` | Body, secondary |

## Email-client constraints (must hold)

- Table-based layout, `role="presentation"`, inline styles + `bgcolor` attributes. No flexbox, grid, or positioning.
- Fixed 600px container with an `<!--[if mso]>` wrapper that pins width and forces the font stack.
- Font stack `'Segoe UI',Calibri,Arial,sans-serif`; charset declared UTF-8.
- No reliance on gradients, `border-radius`, or `box-shadow` — treat as progressive enhancement; design must degrade to solid fills / square corners in the Word engine.
- No `<br>` between table-structure tags (carries over from the structural bug fix).

## Classification logic (generator side)

The Type column is derived, not hardcoded. Suggested rules using TRX history per test:
- **New** — failed this build, passed in the immediately previous build.
- **Critical** — failed in this build and the previous N consecutive builds (persistent).
- **Flaky** — pass/fail oscillates across recent builds with no code change between passing and failing runs.
- Default when history is unavailable: omit the pill or mark `New`.

(In the mockup the four real `OAK_main_20260601.7` failures — all SmokeTestSOD — are tagged across the three types purely to demonstrate the color system; live values come from the rules above.)

## Acceptance criteria

- Header states component, product, test type, status, build, controller, and run date — readable without scrolling. No "Executed" line.
- Container width 600px; no horizontal scroll in Outlook desktop, Outlook Web, Gmail, or mobile.
- Use Case Results table leads with **Agent** and includes a **Progress** bar column.
- Header meta shows **Controller**, not Machine.
- Header and Use Case table header share the emerald theme; Failed Tests stays red.
- Each failed test carries a Type pill (Critical / New / Flaky) backed by the legend.
- Layout is table-based with inline styles; renders consistently with gradients/shadows removed (verified in Outlook desktop).
- Font stack Segoe UI → Calibri → Arial; UTF-8.
- Manual visual check on one real result set across Outlook desktop, Outlook Web, and Gmail passes.
