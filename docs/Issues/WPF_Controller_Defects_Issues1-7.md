# Defect Report: WPF Controller — Execution Dashboard & Unified Log Issues

| Field | Value |
|---|---|
| **Document type** | Defect / Gap analysis |
| **Status** | Open for triage |
| **Reporter** | Vinod Kumar |
| **Component** | TestControllerGrpc (WPF Controller) |
| **Affected screens** | Agent Workspace, Execution Dashboard, Unified Log Viewer |
| **Total issues** | 7 distinct issues, 2 thematic groups |
| **Recommended priority** | Group A (P0-blocker UX) before next release; Group B (P1-quality) within sprint |

---

## Executive Summary

Seven user-facing defects were identified during testing, clustering around two themes:

| Theme | Issues | Impact |
|---|---|---|
| **A. Execution monitoring is unreliable** | #1, #2, #7 | Users cannot trust what they see — Fleet doesn't reflect reality, timeline doesn't render, progress stuck at 0% |
| **B. Unified Log usability is broken** | #3, #5, #6 | Users cannot filter, navigate, or extract logs effectively at scale |

Both themes share a common root pattern: **UI bindings are not being notified when underlying state changes**. This points to systemic gaps in the event-aggregation and view-refresh logic, not isolated bugs.

A single fix wave covering the data binding refresh model can resolve most of these. Individual cosmetic fixes (dropdown size, "empty" vs "All" label) are independent.

---

## Issue 1 — Agent Fleet does not show running pipeline status; auto-switches to Fleet from Monitor

**Priority:** P0  
**Severity:** S2 (high — primary monitoring view broken)

### Symptom

Two related behaviors:

1. **Stale Fleet view:** Agent Fleet panel does not reflect that pipelines are running. Agents that should be visually marked "Busy" appear "Idle" or show stale state.
2. **Forced navigation:** When a user opens the Monitor tab for a specific agent BEFORE a pipeline is triggered, then a pipeline starts, the UI **automatically switches back to Fleet view** — losing the user's intended Monitor focus.

### Expected Behavior

- Fleet panel updates in real time when any agent's lock/session/action state changes
- Monitor view persists once the user selects it — pipeline triggering should NOT navigate the user away from Monitor
- Status badges, progress, and last-action text on each Fleet card refresh within 1 second of the underlying state change

### Likely Root Causes

**Cause A — Missing event subscriptions in FleetVM:**

The Fleet panel should subscribe to five event types:
- `AgentLocksChangedEvent`
- `ExecutionStartedEvent`
- `ExecutionCompletedEvent`
- `NodeProgressEvent`
- `AgentStatusChangedEvent`

If any of these subscriptions are missing or were not preserved through later refactors, the Fleet panel will see stale state.

**Cause B — AgentWorkspaceVM has a "default tab" reset logic:**

The current mode-switching code probably contains a clause like "on session created, switch to Fleet" — intended for clarity but bugged: it overrides the user's explicit Monitor selection.

**Cause C — Card mutation pattern incorrect:**

If Fleet refreshes by clearing and rebuilding the entire `ObservableCollection<FleetCardVM>`, focused cards lose their visual state. The correct pattern is to mutate each card's properties in place (`card.Apply(dto)`), not replace the card.

### Acceptance Criteria

- **AC-1.1:** Triggering a pipeline shows the affected agents turning blue (Busy) within 1 second on the Fleet view, regardless of which mode the user is currently viewing
- **AC-1.2:** If the user is on Monitor mode when a pipeline starts, they STAY on Monitor — no auto-switch
- **AC-1.3:** Each Fleet card's status text updates in place as the agent's action progresses (no card replacement, no focus loss)
- **AC-1.4:** A user viewing Monitor for jvgr1 sees jvgr1's telemetry update even when other agents are running

### Investigation Steps

1. Open `FleetVM.cs` — confirm all five event subscriptions exist and call `RefreshOnUiThread()`
2. Open `AgentWorkspaceVM.cs` — search for any code that sets `CurrentMode = AgentWorkspaceMode.Fleet` outside of explicit user action
3. Open `FleetVM.Refresh()` — confirm it mutates existing cards rather than `Cards.Clear()` then re-populate

---

## Issue 2 — Execution Dashboard timeline does not render

**Priority:** P1  
**Severity:** S2 (high — primary visualization broken)

### Symptom

The timeline visualization in the Execution Dashboard does not display as expected. Per the design intent, the dashboard should:
- Establish a **baseline duration** for each test action (derived from historical run times)
- Display a horizontal timeline per agent showing actual elapsed time vs baseline
- Highlight **burnout indicators** when an action is running significantly longer than its baseline

Currently, the timeline either does not render, renders blank, or does not animate.

### Expected Behavior

- For each running session, a timeline strip shows progress over time
- Each action on each agent has a baseline duration computed from the last N runs
- During execution, the action's bar grows from left to right
- If actual elapsed time exceeds 120% of baseline, the bar visually marks "burnout" (color shift, warning icon)
- Completed action bars show actual vs baseline as a quick comparison

### Likely Root Causes

**Cause A — Baseline computation missing:**  
There may be no service that calculates baseline durations from historical TRX/result data. The UI tries to render bars but has nothing to compare against.

**Cause B — Time-series data not flowing to UI:**  
Each `NodeProgressEvent` carries elapsed time and percent. The Timeline component may not be subscribed, or the data isn't being aggregated per-action.

**Cause C — XAML structure wrong for time-series:**  
WPF doesn't have a built-in timeline control. If the implementation is using a `Grid` with manual column widths, the geometry math may not be working when the window is resized.

### Acceptance Criteria

- **AC-2.1:** For each action type with at least 5 historical runs, a baseline duration is calculated and persisted
- **AC-2.2:** When a session is active, the dashboard shows a per-agent timeline with one bar per action
- **AC-2.3:** Bars grow in real time as actions execute (update at least every 2 seconds)
- **AC-2.4:** Bars visually distinguish: under baseline (green), approaching baseline (amber), exceeded baseline (red)
- **AC-2.5:** Resizing the dashboard window correctly scales all timeline bars

### Recommended Approach

Three sub-features bundled here:

1. **Baseline service** — read past TRX files for each action, compute `Median`, `P75`, `P95` runtimes, cache results
2. **Timeline ViewModel** — subscribe to `NodeProgressEvent`, build a list of `TimelineBarVM` per session/agent/action
3. **Timeline view** — Canvas-based or ItemsControl with width binding to `ElapsedMs / BaselineMs * MaxWidth`

This may need to be a multi-day feature, not a 2-hour bug fix.

---

## Issue 3 — Unified Log filters do not work as expected

**Priority:** P1  
**Severity:** S2 (high — log analysis is core workflow)

### Symptom

Multiple filter behaviors are broken:

1. **Session dropdown** does not show all currently-running sessions
2. **Selecting a session** from the dropdown does NOT filter the log to that session's entries
3. **Clicking an Agent name tab** loses any active log filter for that agent's commands

### Expected Behavior

- Session dropdown lists ALL active and recent sessions (last 24 hours)
- Selecting a session in the dropdown filters log entries to only that session
- Agent name tabs preserve any active filter — combining filters (Agent X AND Session Y) is the natural way to drill down
- Multiple filters compose as AND, not XOR

### Likely Root Causes

**Cause A — Session list source is stale:**  
The dropdown likely binds to a property that's populated once at view load. Sessions created after the log viewer opens never appear in the dropdown.

**Cause B — Filter properties not driving the collection view:**  
The filter UI changes likely update properties but don't invoke `CollectionView.Refresh()` or `Filter` predicate update.

**Cause C — Tab click resets state:**  
The Agent name tab click handler probably resets all filters in addition to switching agent context — likely an accident in the click handler.

**Cause D — Filter state not preserved across navigation:**  
When switching tabs, the filter state is reconstructed from scratch instead of preserved.

### Acceptance Criteria

- **AC-3.1:** Session dropdown updates within 1 second when a new session starts
- **AC-3.2:** Selecting a session filters log entries — both existing and incoming
- **AC-3.3:** Switching between Agent tabs preserves active session/level/text filters
- **AC-3.4:** Visual indicator shows count of active filters ("3 filters applied")
- **AC-3.5:** "Clear filters" button resets to unfiltered state

### Investigation Steps

1. Check `UnifiedLogVM.AvailableSessions` — is it bound to a live collection (subscribes to session events) or a snapshot taken on view load?
2. Check filter setter logic — does setting `SelectedSession` invoke `LogView.Refresh()`?
3. Check Agent tab click handler — does it reset other filter properties?

---

## Issue 5 — Dropdown UX problems (5-item visible limit, "empty" instead of "All")

**Priority:** P2  
**Severity:** S3 (low — cosmetic but cumulative annoyance)

### Symptom

Two related dropdown UX problems:

1. The Raw Execution Log dropdown (and likely other dropdowns) shows only **5 items at a time** with the rest requiring scroll. With 50+ entries, users have to scroll heavily to find anything.
2. **Empty default state** shows blank or "empty" placeholder text. Users expect this to say "All" — meaning "no filter applied, show everything."

### Expected Behavior

- Dropdowns display 8-12 items visible at once (configurable, but default higher than 5)
- The "no filter" / default option is labeled **"All"** — explicit, not empty
- Searchable dropdowns for lists > 20 items (type-ahead filtering)

### Likely Root Causes

**Cause A — ComboBox MaxDropDownHeight not set:**  
WPF ComboBox default `MaxDropDownHeight` is typically the height of about 5-6 items. Without explicit setting, it stays small.

**Cause B — Null/empty default option in source collection:**  
The collection bound to the dropdown likely starts with `null` or an empty string as the default. Without an explicit "All" entry, the UI displays it as blank.

### Acceptance Criteria

- **AC-5.1:** All dropdowns show at least 10 items before scrolling
- **AC-5.2:** All dropdowns have an explicit "All" option as the default
- **AC-5.3:** Dropdowns with > 20 items support type-ahead text filtering
- **AC-5.4:** Behavior is consistent across all dropdowns in the application (single shared style)

### Recommended Fix

Define a shared ComboBox style in `App.xaml`:

```xml
<Style TargetType="ComboBox">
    <Setter Property="MaxDropDownHeight" Value="320"/>
    <Setter Property="IsEditable" Value="False"/>
</Style>
```

For the "All" default, in each VM that exposes a filter collection:

```csharp
public ObservableCollection<string> AvailableLevels { get; } = new()
{
    "All",   // explicit default
    "Info", "Warning", "Error"
};
[ObservableProperty] private string _selectedLevel = "All";
```

Filter predicate then treats "All" as "no filter":
```csharp
predicate = entry => SelectedLevel == "All" || entry.Level == SelectedLevel;
```

---

## Issue 6 — "Copy All" does not respect active filters

**Priority:** P2  
**Severity:** S3 (medium — users have a workaround but it's painful)

### Symptom

The "Copy All" button in the log viewer copies the **full unfiltered log** to clipboard, ignoring whatever filter is currently active. Users who filter to "show errors only" expect Copy All to copy only those errors.

### Expected Behavior

- "Copy All" copies entries matching the current filter view
- If no filter is active, copies the full log
- The button label should indicate this: when filters are active, label changes to **"Copy Filtered (47)"** showing count
- A separate menu option for "Copy Unfiltered (All)" provides escape hatch

### Likely Root Cause

The Copy command implementation reads from the underlying source collection (`AllLogEntries`) instead of the filtered view (`LogView`).

### Acceptance Criteria

- **AC-6.1:** When filters are applied, Copy All copies only filtered entries
- **AC-6.2:** Button label reflects current behavior: "Copy All" if no filter, "Copy Filtered (N)" if filter active
- **AC-6.3:** A secondary option (right-click menu or split button) offers "Copy Everything (Ignore Filters)"
- **AC-6.4:** Copied text format is identical between filtered and unfiltered modes

### Recommended Fix

```csharp
public IRelayCommand CopyLogCommand => new RelayCommand(() =>
{
    // Read from the filtered view, not the source
    var entries = LogView.Cast<LogEntryVM>().ToList();
    var text = string.Join(Environment.NewLine,
        entries.Select(e => $"{e.Timestamp:HH:mm:ss.fff} [{e.Level}] {e.Source} {e.Message}"));
    Clipboard.SetText(text);
});

public string CopyButtonLabel =>
    HasActiveFilters
        ? $"Copy Filtered ({LogView.Count})"
        : "Copy All";
```

---

## Issue 7 — Active Session progress stuck at 0% despite completed actions

**Priority:** P0  
**Severity:** S2 (high — users cannot tell if work is happening)

### Symptom

When a pipeline is actively running and individual actions have completed, the Active Session percentage in the dashboard still shows **0%**. Users cannot tell if any progress has been made.

### Expected Behavior

- Progress percentage reflects the ratio of completed actions to total actions across all agents in the session
- Updates within 2 seconds of each action completion
- Per-agent progress also visible separately (some agents may be ahead of others)
- When all actions complete, percentage reaches exactly 100% before the session moves to "Completed" state

### Likely Root Causes

**Cause A — Progress calculation only fires on session-level events:**  
If the progress calc only runs on `ExecutionStartedEvent` and `ExecutionCompletedEvent`, it never updates DURING execution. It needs to subscribe to per-action `NodeProgressEvent` or `ActionCompletedEvent`.

**Cause B — Total count is wrong/zero at the time of calculation:**  
If `ProgressPercent = Completed / Total * 100` and Total starts at zero or isn't populated until the session is fully constructed, the first updates are `N / 0 = NaN` or `0%`.

**Cause C — PropertyChanged not raised on derived property:**  
If `ProgressPercent` is a computed property reading `Completed` and `Total`, and the underlying counts change but `OnPropertyChanged(nameof(ProgressPercent))` isn't called, the UI never updates.

**Cause D — UI thread blocked at the moment progress would update:**  
If progress is computed but the UI thread is blocked doing something else (e.g., rendering a large log), the binding update is queued but never flushed.

### Acceptance Criteria

- **AC-7.1:** Progress percentage updates within 2 seconds of each action completing on any agent in the session
- **AC-7.2:** Visual indication is fine-grained (not just 0/100 — shows intermediate values like 23%, 47%, 89%)
- **AC-7.3:** Progress is correct regardless of session duration (works for 5-minute sessions and 2-hour sessions)
- **AC-7.4:** Per-agent progress shown separately from overall session progress
- **AC-7.5:** When the session finishes, progress is exactly 100% before state changes

### Investigation Steps

1. In `ExecutionSession.cs`, find the `ProgressPercent` property — confirm it's a getter that reads current counts
2. Find where `CompletedCount` is incremented — verify it raises `PropertyChanged(nameof(ProgressPercent))`
3. In the Execution Dashboard, confirm the bound element refreshes — check Visual Studio's Output window for binding errors

### Recommended Fix Pattern

```csharp
public partial class ExecutionSessionVM : ObservableObject
{
    [ObservableProperty] private int _totalActions;
    [ObservableProperty] private int _completedActions;
    
    public double ProgressPercent =>
        TotalActions == 0 ? 0 : (double)CompletedActions / TotalActions * 100;
    
    // CRITICAL: notify derived property when source counts change
    partial void OnTotalActionsChanged(int value) => 
        OnPropertyChanged(nameof(ProgressPercent));
    
    partial void OnCompletedActionsChanged(int value) => 
        OnPropertyChanged(nameof(ProgressPercent));
    
    // Subscribe to per-action events, not just session events
    public ExecutionSessionVM(IEventAggregator events)
    {
        events.Subscribe<ActionCompletedEvent>(e =>
        {
            if (e.SessionId == this.SessionId)
                _uiDispatcher.Invoke(() => CompletedActions++);
        });
    }
}
```

---

## Common Root Cause Analysis

Issues #1, #3, and #7 all share a common pattern: **the UI is not being notified of changes to underlying state**. This is the same defect in three different places:

| Issue | What's not refreshing |
|---|---|
| #1 | Fleet panel cards don't update on agent state change |
| #3 | Session dropdown doesn't update when new sessions appear |
| #7 | Session progress doesn't update when actions complete |

This suggests a **systemic gap in event-driven UI refresh** rather than three isolated bugs. Worth investigating whether:

1. `IEventAggregator` subscriptions are being lost (e.g., garbage collected because subscriber kept as weak reference)
2. `IEventAggregator.Publish()` is being called but on a thread that doesn't reach UI subscribers
3. ViewModels are constructed multiple times and only one is subscribed
4. `Dispatcher.Invoke` is missing where state mutations cross threads

If the root cause is in the event-aggregator wiring, **fixing it once fixes all three at the same time.** Worth investigating before doing targeted per-issue fixes.

---

## Implementation Plan

### Group A — Critical Path Fixes (do these first)

**Estimated effort:** 3 days combined

| Order | Issue | Effort | Rationale |
|---|---|---|---|
| 1 | Audit event subscriptions across FleetVM, ExecutionDashboardVM, UnifiedLogVM | 0.5 day | Common root cause investigation |
| 2 | Fix Issue #1 (Fleet not updating + auto-switch) | 0.5 day | Most-used view |
| 3 | Fix Issue #7 (Progress stuck at 0%) | 0.5 day | Trust signal — users need to see work happening |
| 4 | Fix Issue #3 (Log filters broken) | 1 day | Primary log analysis workflow |
| 5 | Regression test all three | 0.5 day | Verify common-cause fix worked |

### Group B — Quality and Polish (next sprint)

**Estimated effort:** 2 days

| Order | Issue | Effort | Rationale |
|---|---|---|---|
| 6 | Fix Issue #5 (dropdown size + "All" label) | 0.5 day | Affects every dropdown — share fix via Style |
| 7 | Fix Issue #6 (Copy All respects filters) | 0.5 day | Quick win, big UX improvement |
| 8 | Fix Issue #2 (Timeline visualization) | 1 day | Larger feature, may need more time depending on baseline-service complexity |

---

## Acceptance Test Plan

After all fixes are applied, run this manual test pass to confirm closure:

| Step | Action | Expected Result |
|---|---|---|
| 1 | Open Fleet view | All agents shown with current state |
| 2 | Trigger a pipeline against 2 agents | Both agent cards turn blue within 1s, status updates |
| 3 | While pipeline running, switch to Monitor for agent 1 | Stay on Monitor, do NOT auto-switch to Fleet |
| 4 | Trigger a second pipeline on agent 3 | Stay on Monitor for agent 1 (user explicit choice respected) |
| 5 | Open Execution Dashboard | Both sessions visible, progress > 0% within 5s |
| 6 | Wait for actions to complete | Progress visibly updates (5%, 12%, 25% etc.) |
| 7 | Open Timeline tab | Per-agent timeline shows bars growing in real-time |
| 8 | Open Unified Log | All filters dropdowns show "All" as default |
| 9 | Click Session dropdown | Lists both active sessions |
| 10 | Select session 1 | Log filters to session 1 entries only |
| 11 | Click Agent tab for agent 1 | Filter preserved (both session 1 AND agent 1 applied) |
| 12 | Click "Copy" | Copies filtered entries only, label shows count |
| 13 | All dropdowns | Show 8+ items, default "All" option visible |

---

## Recommendation: Investigate Common Cause First

Before doing targeted fixes for #1, #3, #7, spend half a day investigating the event-aggregator subscription pattern across the three affected ViewModels. If they all suffer from the same issue (likely weak-reference subscriptions being garbage collected, or thread context not being preserved), one fix in the EventAggregator infrastructure resolves all three.

The cosmetic issues (#5, #6) and the larger feature (#2) are independent and can be parallelized.

---

## Instructions for Copilot

When implementing fixes from this document:

1. **Start with the common-cause investigation** described in the section above before writing any code
2. **Reference my actual class names** — FleetVM, AgentWorkspaceVM, ExecutionDashboardVM, UnifiedLogVM, ExecutionSessionVM, IEventAggregator
3. **For each fix, provide:**
   - The specific class/file being modified
   - The exact code change (before/after diff)
   - Why this fix addresses the root cause
   - How to verify the fix works
4. **Group fixes by root cause** rather than by issue number — if fixing one thing fixes three issues, structure the work that way
5. **Be opinionated** — recommend one approach per issue, not multiple options
6. **Flag risks** — if a fix might break something else, say so explicitly
7. **Provide regression test steps** — what manual test confirms the fix didn't break adjacent functionality

---

**End of document.**
