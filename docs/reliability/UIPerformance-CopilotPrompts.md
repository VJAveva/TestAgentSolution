# UI Performance Prompt Pack — TestControllerGrpc (WPF) + TestController.WebClient (React)

14 prompts, 5 phases, 3 gates. Run them in order. Do not skip Phase 0 — you cannot
optimize what you have not measured, and unmeasured "optimizations" are the main way
UI code gets slower and harder to read at the same time.

**Scope:** `TestControllerGrpc` (WPF desktop controller) and `TestController.WebClient`
(React frontend). Core, WebApi and TestAgentGrpc are in scope only where they feed the
UI (payload shape, event rate, endpoint chattiness).

---

## Ground rules — paste this at the top of every Copilot session in this pack

```
GROUND RULES FOR THIS SESSION — follow these literally.

1. You are auditing an existing, working codebase. Do not propose rewrites,
   new frameworks, new state libraries, or new UI toolkits.
2. Every finding must cite real evidence: file path, line number, and the actual
   code you read. If you did not open the file, say "not inspected" — do not guess.
3. If you cannot find something, say so explicitly. Never invent a file, a method,
   a component, or a measurement.
4. Do not change any code unless the prompt explicitly says "APPLY".
5. Prefer the smallest change that removes the cost. No refactoring for taste.
6. Stack is .NET 10 / C# / WPF / SignalR / gRPC on the desktop side, React on the
   web side. Do not conflate the two — WPF is not React and has no virtual DOM.
7. Output in the exact format the prompt asks for. No preamble, no summary essay.
```

---

# PHASE 0 — Map and measure (P01–P03)

## P01 — Inventory the UI surfaces

```
Read the TestControllerGrpc (WPF) and TestController.WebClient (React) projects and
produce an inventory of every user-visible screen, panel, and heavy control.

For each one give me a row:
| # | App (WPF/Web) | Screen or panel | Entry file | What data feeds it | Rough row/item count at realistic load | Update trigger (user action / SignalR / timer / gRPC stream) |

Rules:
- "Realistic load" means what this actually holds in a real regression run, not what
  it holds when empty. If the code caps or pages it, say so.
- Flag every surface whose update trigger is a push (SignalR, gRPC stream) or a timer,
  because those are the ones that repaint when nobody asked them to.
- Do not change any code.
```

## P02 — Instrument the WPF app

```
APPLY: add temporary, removable instrumentation to TestControllerGrpc so I can get
real numbers before we change anything.

Add:
1. A frame-time / render counter using CompositionTarget.Rendering, logging a rolling
   average and worst frame per 5 seconds.
2. A UI-thread responsiveness probe: post a Dispatcher action at DispatcherPriority
   .Background every second, measure how long it waits before running. That wait time
   IS the lag the user feels.
3. Turn on binding failure tracing:
   PresentationTraceSources.DataBindingSource with SourceLevels.Error, written to a
   log file. Silent binding errors are one of the most common invisible costs in WPF.
4. Wrap the 5 heaviest panels from P01 with a Stopwatch around their load / refresh
   path.

Put all of it behind a single flag (e.g. UiPerfDiagnostics) so it can be removed in
one commit. Tell me exactly which files you touched and how to enable it.
```

## P03 — Instrument the WebClient

```
APPLY: add temporary, removable instrumentation to TestController.WebClient.

Add:
1. A SignalR message counter: messages/second per event name, and the size of each
   payload, logged to console every 5 seconds.
2. A render counter for the top 10 components (a small useRenderCount hook is fine),
   logging component name + render count every 5 seconds.
3. performance.mark/measure around the initial load of each main route and around the
   heaviest list render.
4. A counter for REST calls: URL, count, and total bytes per 30 seconds.

Behind one flag. Then tell me how to capture a React DevTools Profiler trace and a
Chrome Performance trace for the worst screen.
```

### GATE 1 — do not continue until you have all of these

- Baseline numbers written down for the 5 worst screens (WPF and Web).
- Worst-case UI-thread wait time in the WPF app, in milliseconds.
- SignalR messages/second and render counts under a real regression run.
- The binding-error log file, with a count.

Write these into a file `ui-perf-baseline.md` and keep it. Every later fix is judged
against this file, not against how it feels.

---

# PHASE 1 — Read-only audit (P04–P08)

No code changes in this phase. The output of each prompt is a findings table.

## P04 — WPF threading and dispatcher audit

```
Audit TestControllerGrpc for UI-thread and dispatcher problems. Read the actual code.

Look specifically for:
- ObservableCollection (or any bound collection) mutated from a non-UI thread, from a
  gRPC callback, SignalR handler, or Task continuation.
- Per-item Dispatcher.Invoke / BeginInvoke in a loop — one marshal per incoming event
  instead of one marshal per batch.
- Dispatcher.Invoke used where InvokeAsync would do (Invoke blocks the caller).
- DispatcherTimer instances with intervals under 500ms, and what they do on each tick.
- .Result / .Wait() / GetAwaiter().GetResult() on the UI thread.
- async void handlers doing real work.
- Event handlers subscribed and never unsubscribed (leaked handlers keep dead views
  alive and repainting).

Output:
| # | Region tag | File:line | Problem | Why it costs UI time | Fix size (S/M/L) |

Map each finding to the WPF region tags R1–R14 where you can. Do not change code.
```

## P05 — WPF virtualization and visual tree audit

```
Audit TestControllerGrpc XAML for layout and virtualization problems.

Check every ItemsControl, ListView, ListBox, DataGrid and TreeView for:
- VirtualizingStackPanel disabled, or replaced by a StackPanel in ItemsPanelTemplate
  (this silently realizes every row).
- A ScrollViewer with CanContentScroll="False" wrapping a virtualized list — this kills
  virtualization completely.
- An ItemsControl nested inside a ScrollViewer or a StackPanel that gives it infinite
  height — same effect.
- VirtualizingPanel.IsVirtualizingWhenGrouping not set on grouped/grouped-rows views.
- DataGrid columns with Width="Auto" or SizeToCells, which force a measure pass over
  every row.
- Deeply nested Grid/Border/StackPanel layers in DataTemplates (report the depth).
- Templates that could be a single Grid but are 4 nested panels.

Then separately: report the visual tree depth of the Code Churn ribbon surface and the
Fleet panel, and any element with Effect, DropShadowEffect, OpacityMask, BitmapEffect,
or a non-frozen Brush created in code.

Output:
| # | Region tag | File:line | Problem | Rows/items affected at realistic load | Fix size (S/M/L) |

Do not change code.
```

## P06 — WPF binding and rendering audit

```
Audit TestControllerGrpc for binding and rendering cost.

Look for:
- OnPropertyChanged(null) or OnPropertyChanged("") — these refresh every binding on
  the object.
- Property setters that raise PropertyChanged even when the value did not change.
- IValueConverter implementations that allocate, format strings, or do lookups on every
  call (converters run on every render pass of every visible item).
- Bindings with long paths, or bindings to a method/indexer resolved by reflection.
- Brushes, Pens and Geometries created per item instead of shared as frozen resources.
- {DynamicResource} used where {StaticResource} would work.
- Collections re-created and reassigned wholesale where an in-place update would do,
  and vice versa: many small Add calls where one Reset would be cheaper.
- Anything in a DataTemplate that hits the network, the file system, or SQLite.
- Images loaded without DecodePixelWidth.

Also list every unique binding error from the trace log produced in P02, grouped by
count. A binding error that fires per row per refresh is a real cost, not noise.

Output:
| # | Region tag | File:line | Problem | Estimated frequency (per render / per item / per event) | Fix size (S/M/L) |

Do not change code.
```

## P07 — WebClient render audit

```
Audit TestController.WebClient for unnecessary React re-renders. Use the render counts
captured in P03 as evidence.

Look for:
- Context providers holding a value object rebuilt on every render, re-rendering every
  consumer.
- Objects, arrays, or arrow functions created inline and passed as props.
- Missing React.memo / useMemo / useCallback on components that render large lists or
  sit under a high-frequency provider — but only where the render count proves it,
  not everywhere.
- List keys derived from array index, or regenerated per render.
- Long lists rendered in full with no windowing, and their realistic item count.
- State kept higher in the tree than it needs to be, so a local change repaints a page.
- useEffect with a missing or unstable dependency array causing repeat work.
- Heavy computation (sorting, filtering, grouping, formatting) done in render instead
  of memoized — especially on impact-mapping result sets.

Output:
| # | Region tag | File:line | Component | Renders per 5s at baseline | Root cause | Fix size (S/M/L) |

Map to WebClient region tags W1–W12 where you can. Do not change code.
```

## P08 — WebClient data and transport audit

```
Audit how TestController.WebClient gets its data, using the P03 counters as evidence.

Look for:
- SignalR handlers that call setState once per message, on a stream that arrives many
  times per second. Report the actual measured rate per event name.
- SignalR subscriptions re-registered on every render (handler churn), or never
  unsubscribed.
- State arrays that grow without bound — log lines, agent events, run history — with
  no cap, no windowing and no trimming.
- Polling loops (setInterval / repeated fetch) and their intervals, including any that
  poll data SignalR already pushes.
- N+1 REST patterns: a list fetch followed by one call per row across the ~23 endpoints.
- The same endpoint called by more than one component with no shared cache or
  deduplication.
- Payloads that carry far more fields than the UI renders.
- Requests fired without cancellation on unmount or on rapid filter changes.

Output:
| # | Region tag | File:line | Problem | Measured rate or count | Fix size (S/M/L) |

Do not change code.
```

### GATE 2 — do not continue until you have

One merged findings table across P04–P08. Every row carries a file:line and a measured
or counted number. Any row with no evidence gets deleted, not kept "just in case".

---

# PHASE 2 — Rank (P09)

## P09 — Build the fix order

```
Take the merged findings table from Phase 1 and rank it.

Score each finding:
- Cost: how much measured UI time or how many renders/frames it accounts for (from
  ui-perf-baseline.md). High / Medium / Low, with the number that justifies it.
- Effort: S / M / L.
- Blast radius: how many screens the fix touches.
- Risk: could this change behaviour, not just speed?

Then give me exactly one ordered fix list, top to bottom. Not options — one list.
Put anything that is High cost / Small effort at the top.

For each of the top 10, state in one line what the user will actually notice when it
is fixed ("the fleet grid stops freezing for ~2s when 5 agents report at once").

Output the list and nothing else.
```

### GATE 3 — you approve the top 5 before any code changes

---

# PHASE 3 — Fix, one at a time (P10–P13)

Each of these is run per finding, not per category. One fix, one measurement, one
commit. If the number does not move, revert it — a change that costs readability and
buys nothing is a loss.

## P10 — Apply WPF threading and dispatcher fixes

```
APPLY fix #<N> from the ranked list, and only that one.

Rules for this category:
- Marshal to the UI thread once per batch, not once per event. Coalesce bursts of
  incoming gRPC/SignalR events into a single dispatcher post, using a short window
  (50-100ms) or a bounded queue drained on a timer.
- Use BindingOperations.EnableCollectionSynchronization where a collection is genuinely
  written from a background thread, instead of dispatching every mutation.
- Replace blocking Dispatcher.Invoke with InvokeAsync unless the result is needed inline.
- Never do I/O, SQLite, or gRPC work on the UI thread.

After applying:
1. State exactly what changed, file by file.
2. Re-run the P02 instrumentation and report the new numbers next to the baseline.
3. If the improvement is under 10%, say so plainly and recommend reverting.
```

## P11 — Apply WPF virtualization and rendering fixes

```
APPLY fix #<N> from the ranked list, and only that one.

Rules for this category:
- Restore virtualization before anything else: VirtualizingStackPanel as the items
  panel, ScrollViewer.CanContentScroll="True", VirtualizationMode="Recycling",
  IsVirtualizingWhenGrouping="True" on grouped views.
- Never place a virtualized list inside a container that grants it infinite height.
- Replace Width="Auto" DataGrid columns with fixed or star widths where the audit
  showed a full-row measure pass.
- Flatten DataTemplates: fewer nested panels, prefer one Grid over stacked Borders.
- Freeze shared brushes/pens/geometries and move them to resources.
- Fix the binding errors found in P06 — they are cheap wins and they are per-row.
- Do not introduce new custom panels or virtualization libraries.

After applying: report changed files, then re-measure frame time and worst frame from
P02 against the baseline. Under 10% gain: recommend revert.
```

## P12 — Apply WebClient render fixes

```
APPLY fix #<N> from the ranked list, and only that one.

Rules for this category:
- Batch SignalR-driven state updates: buffer incoming messages and flush on an interval
  or animation frame, so N messages become one render, not N renders.
- Memoize context values; split a context that mixes fast-changing and slow-changing
  data into two.
- Add windowing to any list whose realistic item count exceeds a few hundred rows.
- Move heavy sorting/filtering/grouping out of render into a memo keyed on real inputs.
- Cap unbounded arrays (log lines, event feeds) with an explicit maximum and trim from
  the front.
- Add memo/useCallback only where the P07 render counts prove a cost. Do not blanket
  the codebase.

After applying: report changed files, then re-run the P03 counters and compare render
counts and interaction timings against baseline. Under 10% gain: recommend revert.
```

## P13 — Apply data and transport fixes

```
APPLY fix #<N> from the ranked list, and only that one.

Rules for this category:
- Delete polling where SignalR already pushes the same data.
- Collapse N+1 endpoint patterns into one call, or cache and dedupe shared calls.
- Trim payloads to the fields the UI renders. Coordinate with Core so both front doors
  (WPF and WebApi) stay consistent — this is the "one engine" rule; do not fix a payload
  in one host only.
- Cancel in-flight requests on unmount and on rapid filter changes.
- Debounce filter and search inputs that trigger fetches.

After applying: report changed files, then compare request counts, bytes and time-to-
render against baseline. Under 10% gain: recommend revert.
```

---

# PHASE 4 — Lock it in (P14)

## P14 — Regression guard and cleanup

```
Final pass.

1. Remove all P02/P03 instrumentation, or leave it behind its flag, default off. State
   which you did.
2. Write ui-perf-budgets.md containing: the final numbers, the budget for each of the
   5 key screens (worst frame time, UI-thread wait, renders per SignalR burst, initial
   route load), and what to do when a budget is breached.
3. Write a one-page UI performance checklist for this repo — the specific traps found
   in this audit, not generic advice. It should be short enough to read before a PR.
4. Add whatever automated guard is cheap and real: a test asserting no collection is
   mutated off the UI thread, a render-count assertion on the worst component, or a
   binding-error count assertion. Do not build a performance test framework.
5. Draft a decision journal entry: what was slow, why it was slow, what we changed,
   what we deliberately did not change and why.

List the files you created or changed. Nothing else.
```

---

## Two things to hold onto

**Measure, change one thing, measure again.** The fix order comes from numbers, and a
fix that does not move the number gets reverted. This is the same engine-first rule
already in the project: correctness and evidence before optimization.

**The two most likely culprits, before you even start.** On the WPF side: one dispatcher
marshal per incoming agent event, and a list somewhere that has quietly lost its
virtualization. On the WebClient side: one `setState` per SignalR message. Those three
patterns account for most "the UI freezes when the fleet gets busy" complaints. The
audit exists to prove or disprove that, not to assume it.
