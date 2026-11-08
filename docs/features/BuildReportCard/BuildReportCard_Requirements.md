# Build Report Card — Requirements & Implementation Spec

| Field | Value |
|---|---|
| **Feature name** | Build Report Card |
| **Location in UI** | Results window (new tab/view alongside existing Results Browser) |
| **Component** | TestControllerGrpc (WPF Controller) |
| **Type** | New feature |
| **Priority** | P1 |
| **Estimated effort** | 4-6 days |
| **Reporter** | Vinod Kumar |

---

## 1. Problem Statement

### Current State

A single build run produces:
- **10 CIs** (Configuration Items) under test
- **10 agents** each executing automation
- **~6,000 test cases** total, each producing TRX result files
- **5 PSR** (Production Scenario Runs) validating customer-like deployments

Today, results are communicated by sending **per-use-case emails** to product owners. With 6,000 test cases, this floods inboxes with hundreds or thousands of individual emails. The consequences:

- Product owners cannot get a single, clear assessment of build health
- Critical failures are buried among routine passing notifications
- No way to see "is this build shippable?" at a glance
- No prioritization — a regression looks the same as a known-flaky test
- Cross-agent and cross-CI patterns are invisible (each email is isolated)
- PSR results are disconnected from the test automation results

### Desired State

A **single-page Build Report Card** in the Results window that consolidates everything:
- One letter grade (A-F) and verdict for the entire build
- Per-CI and per-agent breakdowns
- PSR run status alongside automation results
- Prioritized failure list with pattern classification
- Historical trend context
- One consolidated email to product owners instead of thousands

---

## 2. Goals and Non-Goals

### Goals

- Provide a single screen that answers "Is this build shippable?" in under 10 seconds
- Aggregate TRX files from all 10 agents into unified CI-level and agent-level views
- Surface the most urgent failures (regressions) above routine ones (chronic/flaky)
- Integrate PSR run results into the same view
- Replace the per-use-case email flood with one consolidated report email
- Enable export (PDF) and sharing (link) for distribution

### Non-Goals

- Replacing the detailed Results Browser (this card complements it, links into it)
- Real-time live updating during execution (this is a post-run summary; live monitoring is the Execution Dashboard's job)
- Historical analytics dashboards beyond a simple trend strip
- Editing or re-running tests from the card (read-only assessment view; actions link out)

---

## 3. Where It Lives

The Build Report Card is a **new view within the existing Results window**.

```
Results Window
├── Results Browser (existing — tree of builds/sets/tests)
├── Build Report Card (NEW — this feature)
└── [other existing result views]
```

Entry points:
- A "Report Card" tab or button in the Results window toolbar
- Selecting a build in the Results Browser, then clicking "View Report Card"
- Optionally: auto-open the report card when a build completes

---

## 4. Layout — Six Sections (top to bottom)

### Section 1: Hero Header + Letter Grade

A prominent banner showing the overall build assessment.

**Left side:**
- Label: "BUILD REPORT CARD"
- Build number (e.g., `OAK_main_20260601.7`) in monospace
- Metadata line: triggered timestamp, who/what triggered it, total duration, time window

**Right side (the grade block):**
- A large circular badge showing the **letter grade** (A/B/C/D/F) and the **pass percentage**
- The grade circle is color-coded (green for A, amber for B/C, red for D/F)
- A verdict status line: "Ship it" / "Ship with caveats" / "Do not ship"
- Summary counts: pass / fail / skip
- Notable issues: count of regressions, flaky tests, PSR errors
- Comparison vs last build: "↓ 1.8%" with directional color

### Section 2: KPI Strip

A horizontal row of 6 key metrics directly below the hero:

| KPI | Example | Sub-text |
|---|---|---|
| Total tests | 6,000 | across 10 CIs |
| Pass rate | 94.3% | target: 98% |
| Failures | 218 | in 9 of 10 CIs |
| Regressions | 5 | passed yesterday |
| Agents | 10 / 10 | all completed |
| PSR runs | 4 / 5 | 1 failed |

### Section 3: CI Results Grid

A grid of cards, one per CI (10 cards). Each card shows:
- CI name
- Status dot (green/blue/amber/red based on pass rate thresholds)
- Pass count / total / percentage
- A thin progress bar colored by health
- Card background tinted by severity (green pass / amber warn / red fail)
- Clickable → drills into that CI's detailed results

**Color thresholds:**
- Green (pass): ≥ 98%
- Blue (acceptable): 95% – 97.9%
- Amber (warn): 90% – 94.9%
- Red (fail): < 90%

### Section 4: Agent Execution Table

One row per agent (10 rows). Columns:
- Agent name with status dot
- A horizontal stacked bar showing pass/fail/skip proportions
- Pass count
- Fail count (red)
- Skip count
- Execution time
- Pass percentage

Rows are tinted by severity (warn-tint amber, fail-tint red) so problem agents stand out. Clickable → drills into that agent's TRX files.

### Section 5: PSR Validation + Trend (side by side)

**Left (PSR — wider):** A grid of cards, one per PSR run (5 cards). Each shows:
- PSR name and scenario (e.g., "PSR-01 · Refinery")
- Status (PASSED / PASSED w/ WARN / FAILED) with duration
- Key stats: tags processed, throughput, errors/warnings
- Card border + background colored by outcome

**Right (Trend — narrower):** A small bar chart showing pass rate across the last 10 builds, with the current build highlighted. A caption shows the delta vs the average.

### Section 6: Top Failures Table

A prioritized table of the most important failures (top 8-10, with a "view all" link). Columns:
- Test name
- CI it belongs to
- Which agents it failed on
- **Pattern** classification (the key column — see below)
- Owner (team responsible)
- First seen (when this failure started)

**Pattern classification tags:**

| Tag | Meaning | Visual |
|---|---|---|
| REGRESSION | Passed in previous build, fails now | Red outlined badge — highest urgency |
| NEW | First appearance of this test/failure | Red badge |
| FLAKY (n/m) | Intermittent across recent runs | Amber badge |
| CHRONIC | Failing for many consecutive builds | Gray badge — known issue |

Sort order: REGRESSION first, then NEW, then FLAKY, then CHRONIC. This puts the "panic now" items at the top and the "already known" items at the bottom.

### Footer: Actions

A footer bar with:
- Generation timestamp and auto-distribution schedule
- Buttons: "Export PDF", "Copy link", "Email to owners" (primary)

---

## 5. The Grade Algorithm

The letter grade is the heart of the feature. It must be **deterministic, explainable, and trustworthy**.

### Proposed grading formula

Start with the raw pass rate, then apply penalties for high-severity issues:

```
base_score = pass_rate (0-100)

penalties:
  - each REGRESSION:        -2.0 points
  - each PSR failure:       -3.0 points
  - each PSR warning:       -0.5 points
  - CI below 90%:           -1.0 points per CI

final_score = base_score - total_penalties (floored at 0)

grade mapping:
  A:  final_score >= 97
  B:  final_score >= 93
  C:  final_score >= 88
  D:  final_score >= 80
  F:  final_score <  80

verdict mapping:
  A, B:  "Ship it" / "Ship with caveats"
  C:     "Review required"
  D, F:  "Do not ship"
```

### Example (matching the mockup)

```
Pass rate:        94.3
Penalties:
  5 regressions:   -10.0
  1 PSR failure:    -3.0
  1 PSR warning:    -0.5
  2 CIs below 90%:  -2.0
Total penalty:     -15.5

Wait — that would give 78.8 (grade F)...
```

**IMPORTANT DESIGN DECISION NEEDED:** The penalty weights above are a starting proposal. The exact weights must be tuned with the QA team so the grade matches human intuition. The mockup shows a "B" grade for this build, which implies lighter penalties than the example above. The implementer should:

1. Make penalty weights **configurable** in appsettings.json
2. Show a "grade breakdown" tooltip so users can see WHY a build got its grade
3. Calibrate weights against 10-20 historical builds where the QA team already has an intuitive grade

### Grade breakdown transparency

When a user hovers over the grade, show the calculation:

```
Grade: B (94.3%)
  Base pass rate:     94.3%
  Regressions (5):    counted, no auto-fail
  PSR failures (1):   noted
  Verdict: Ship with caveats — 5 regressions must be
           triaged before release
```

---

## 6. Data Sources

The report card aggregates from existing data:

| Section | Data source |
|---|---|
| CI / Agent results | TRX files each agent produces, parsed by existing TRX parser |
| Pass/fail/skip counts | Aggregated from TRX outcome elements |
| Pattern classification | Failure Pattern Analyzer (existing/specced) — compares against build history |
| Owner assignment | A mapping file (CI → owning team) — new config needed |
| Trend chart | Historical build summaries from the results store |
| PSR results | PSR run output files (format TBD — needs PSR result schema) |
| Comparison vs last build | Previous build's stored summary |

### New data needs

1. **CI-to-owner mapping** — a config file mapping each CI to a responsible team/email:
   ```json
   {
     "Scripting": { "team": "scripting-team", "email": "scripting@aveva.com" },
     "DeadBand": { "team": "platform-team", "email": "platform@aveva.com" }
   }
   ```

2. **PSR result schema** — a defined format for PSR runs to report status, duration, tags, throughput, errors. If PSRs don't currently produce machine-readable output, this needs to be added.

3. **Build summary persistence** — to compute trends and "vs last build", each build's summary must be stored (small JSON per build is sufficient).

---

## 7. Functional Requirements

| ID | Requirement |
|---|---|
| RC-01 | Report card is accessible from the Results window |
| RC-02 | Card aggregates TRX files from all agents for a selected build |
| RC-03 | Overall grade is computed via the configurable grade algorithm |
| RC-04 | Grade breakdown is viewable (tooltip or expandable) |
| RC-05 | CI grid shows one card per CI with pass rate and color coding |
| RC-06 | Agent table shows per-agent pass/fail/skip with visual bar |
| RC-07 | PSR section shows status of all PSR runs for the build |
| RC-08 | Top failures table classifies each failure by pattern |
| RC-09 | Failures are sorted REGRESSION → NEW → FLAKY → CHRONIC |
| RC-10 | Each failure shows the owning team |
| RC-11 | Trend chart shows pass rate for the last 10 builds |
| RC-12 | Clicking a CI card drills into that CI's detailed results |
| RC-13 | Clicking an agent row drills into that agent's TRX files |
| RC-14 | "Export PDF" produces a shareable PDF of the card |
| RC-15 | "Email to owners" sends ONE consolidated email (replaces per-use-case emails) |
| RC-16 | Card handles missing data gracefully (e.g., PSR results not yet available) |
| RC-17 | Card renders within 3 seconds for a 6,000-test build |

---

## 8. Non-Functional Requirements

| ID | Requirement | Target |
|---|---|---|
| RC-NFR-01 | TRX aggregation for 6,000 tests across 10 agents | < 3 seconds |
| RC-NFR-02 | Card renders without freezing the UI thread | Parse on background thread, render on UI thread |
| RC-NFR-03 | Memory footprint for one build's data | < 200 MB |
| RC-NFR-04 | PDF export | < 5 seconds |
| RC-NFR-05 | No hard-coded colors — all theme tokens | All status colors via resources |
| RC-NFR-06 | Responsive layout — usable from 1000px to 1920px wide | Grids reflow at breakpoints |

---

## 9. Architecture

### Components to build

```
TestControllerGrpc.Core/
  Services/
    BuildReportAggregator.cs    - reads TRX from all agents, builds the model
    GradeCalculator.cs          - applies the grade algorithm
    CiOwnerResolver.cs          - maps CI → owning team
    BuildSummaryStore.cs        - persists/loads build summaries for trends
  Models/
    BuildReportCard.cs          - the full aggregated model
    CiResult.cs
    AgentResult.cs
    PsrResult.cs
    FailureEntry.cs

TestControllerGrpc/
  ViewModels/Results/
    BuildReportCardVM.cs        - top-level VM
    CiCardVM.cs
    AgentRowVM.cs
    PsrCardVM.cs
    FailureRowVM.cs
  Views/Results/
    BuildReportCardView.xaml    - the full card layout
    BuildReportCardStyles.xaml  - shared styles for cards/bars/tags
```

### Data flow

```
User selects a build in Results window
  → BuildReportCardVM.LoadBuild(buildNumber)
  → BuildReportAggregator.Aggregate(buildNumber)
      → reads TRX files from all agent result folders (parallel)
      → groups results by CI and by agent
      → counts pass/fail/skip
      → for each failure, calls FailurePatternAnalyzer to classify
      → reads PSR result files
      → loads previous build summary for comparison
  → GradeCalculator.ComputeGrade(aggregatedModel)
  → CiOwnerResolver assigns owners to failures
  → BuildSummaryStore.Save(thisBuildSummary)  [for future trends]
  → VM populates observable collections
  → View renders all six sections
```

### Performance approach

- Parse TRX files in **parallel** (one task per agent) using `Parallel.ForEach` or `Task.WhenAll`
- Cache parsed results so re-opening the same build is instant
- Render progressively — show the grade and KPIs first, fill in tables as data arrives
- Do NOT block the UI thread during aggregation (background task + Dispatcher marshalling for the final render)

---

## 10. Implementation Plan (Phased)

### Phase 1: Data aggregation (1.5 days)
- BuildReportAggregator reads and merges TRX from all agents
- Models defined (BuildReportCard, CiResult, AgentResult, FailureEntry)
- Parallel TRX parsing with caching
- Unit tests with sample TRX files

**Exit:** Aggregator returns a correct model for a known build, in < 3 seconds.

### Phase 2: Grade algorithm (0.5 day)
- GradeCalculator with configurable weights
- Grade breakdown explanation
- Unit tests covering A/B/C/D/F boundaries

**Exit:** Grades match QA team intuition on 10 historical builds.

### Phase 3: CI and agent sections (1 day)
- CI grid cards with color coding
- Agent table with stacked bars
- Drilldown navigation into existing Results Browser

**Exit:** Both sections render with correct data and drilldown works.

### Phase 4: Failures + pattern classification (1 day)
- Top failures table
- Integration with Failure Pattern Analyzer
- Owner resolution from config
- Correct sort order

**Exit:** Failures classified and sorted; owners shown correctly.

### Phase 5: PSR + trend (0.5 day)
- PSR result cards
- Trend chart from build summary store

**Exit:** PSR status and trend render correctly.

### Phase 6: Actions + polish (1 day)
- Export PDF
- Consolidated email to owners
- Responsive layout
- Theme token compliance

**Exit:** Card is shareable, emailable, and renders at all window sizes.

---

## 11. Acceptance Criteria

| ID | Criterion |
|---|---|
| AC-01 | Opening the report card for a 6,000-test build renders within 3 seconds |
| AC-02 | The grade matches the configured algorithm and the breakdown is viewable |
| AC-03 | All 10 CIs appear with correct pass rates and color coding |
| AC-04 | All 10 agents appear with correct pass/fail/skip counts |
| AC-05 | All 5 PSR runs appear with correct status |
| AC-06 | Regressions appear at the TOP of the failures table |
| AC-07 | Each failure shows the correct owning team |
| AC-08 | Clicking a CI card navigates to that CI's detailed results |
| AC-09 | "Email to owners" sends ONE email, not per-use-case emails |
| AC-10 | The trend chart shows the last 10 builds with the current one highlighted |
| AC-11 | PDF export produces a readable, shareable document |
| AC-12 | No hard-coded colors — verified by grep for hex codes in views |
| AC-13 | Card handles a build with missing PSR data gracefully (shows "PSR pending") |
| AC-14 | Aggregation does not freeze the UI thread |

---

## 12. Open Questions for Decision

1. **Grade weights:** What penalty should each regression / PSR failure carry? Needs calibration with QA team against known-good and known-bad builds.
2. **PSR result format:** Do PSRs currently produce machine-readable output? If not, what schema should they emit?
3. **CI-to-owner mapping:** Where does this mapping live, and who maintains it?
4. **Auto-distribution:** Should the report email send automatically on build completion, or only on demand?
5. **Trend depth:** Is 10 builds the right window for the trend chart, or should it be configurable?
6. **Drilldown target:** Should CI/agent drilldown open the existing Results Browser, or a dedicated detail view?
7. **Grade thresholds:** Are the A/B/C/D/F cut points (97/93/88/80) aligned with how the team thinks about build quality?

---

## 13. Consolidated Email (Replaces the Flood)

The "Email to owners" action sends ONE email containing an HTML version of the report card. Key differences from the current per-use-case emails:

- **One email per build**, not one per test case
- **Sent to all product owners** (or filtered to owners whose CIs had failures)
- **HTML table-based layout** (email clients can't render the full interactive card — needs inline-styled tables)
- **Microsoft Fluent font stack** (`Segoe UI`, fallback Arial)
- **Links back** to the full interactive report card in the Controller
- **Subject line** encodes the grade: `[Build Report] OAK_main_20260601.7 — Grade B (94.3%) — Ship with caveats`

The subject line alone gives product owners the assessment before they even open the email — the exact opposite of the current flood where no single email conveys build health.

---

## 14. Reference Mockup

A visual mockup of this report card has been produced as an HTML file
(`BuildReportCard_Mockup.html`). It shows the exact layout, colors, and
data presentation described in this document. Use it as the visual
reference when implementing the XAML.

---

## Instructions for Implementer / Copilot

When implementing this feature:

1. **Start with Phase 1 (data aggregation)** — everything depends on correctly merging TRX files from all agents
2. **Reference the HTML mockup** for exact layout, spacing, and color usage
3. **Use existing services** where possible — the TRX parser, Failure Pattern Analyzer, and email service already exist or are specced
4. **Make the grade algorithm configurable** — do not hard-code penalty weights
5. **Parse on background threads** — never block the UI thread during aggregation
6. **Use theme tokens only** — no hard-coded hex colors in views
7. **Provide for each phase:** the code, the reasoning, and verification steps
8. **Flag the open questions** in Section 12 — these need decisions before some phases can complete
9. **Be opinionated** — recommend one approach per decision, not multiple options

---

**End of document.**
