# UI performance checklist — TestAgentSolution

One page. Read before a PR that touches `TestControllerGrpc` XAML/ViewModels or
`TestController.WebClient` components. These are the traps found in this repo,
not generic advice.

## WPF — TestControllerGrpc

- [ ] **Does a handler run once per agent output line?** `AgentOutputEvent` is
  published per stdout/stderr line (`MainViewModel.Helpers.cs` `OnOutputReceived`)
  and `IEventAggregator.Publish` fans out on the ThreadPool. One
  `Dispatcher.InvokeAsync` in that handler = one dispatcher item + one closure per
  line. Use the **self-coalescing pump**: enqueue to a `ConcurrentQueue`, and only
  post when `Interlocked.CompareExchange(ref _scheduled, 1, 0) == 0`; reset the flag
  at the *start* of the drain so events arriving mid-drain can schedule the next pump.
  Existing examples: `ExecutionDashboardVM.OnAgentOutput`, `MonitorVM.OnAgentOutput`,
  `ExecutionHistoryPanelVM.OnNodeProgress`.

- [ ] **Are you adding a second marshal next to an existing batcher?**
  `LogBufferService` already batches to the UI thread every 100 ms. Per-entry UI
  bookkeeping belongs in its `BatchProcessed` event, not in a parallel
  `Dispatcher.InvokeAsync` per entry.

- [ ] **A bare `ItemsControl` does not virtualize and has no ScrollViewer.**
  Its default items panel is `StackPanel` and its default template is just an
  `ItemsPresenter`. Wrapping it in a `<ScrollViewer>` gives it infinite height and
  realizes every item. Setting `ScrollViewer.*` attached properties on it does
  nothing. Fix = template the ScrollViewer *inside* the control with
  `CanContentScroll="True"`, set `ItemsPanel` to `VirtualizingStackPanel`, and add
  `VirtualizingPanel.ScrollUnit="Pixel"` when rows vary in height. See
  `ExecutionDashboardWindow.xaml` `PipelineSessionList`.

- [ ] **Nested `ItemsControl`s multiply.** Session card → agent rows → action pills is
  three levels. A cap of 50 cards is not a cap on visuals.

- [ ] **New `DispatcherTimer`?** There are already 100 ms / 1 s ×2 / 3 s / 5 s ×3 /
  30 s / 60 s timers running for the whole app lifetime. `FleetVM` and `RegistryVM`
  are constructed eagerly by `AgentWorkspaceVM`, so their 5 s probes run even when
  their tab is not visible. Prefer scoping a timer to load/unload the way
  `MonitorVM.LoadAgent`/`UnloadAgent` does, or debounce like
  `FleetVM._refreshDebounce` (250 ms).

- [ ] **Event subscriptions disposed?** `IEventAggregator.Subscribe` returns an
  `IDisposable`. `ExecutionDashboardVM` and `ExecutionHistoryPanelVM` keep and
  dispose theirs; `FleetVM`/`RegistryVM`/`FleetUpdatesVM` discard the token, so those
  handlers live for the process. A leaked handler keeps a dead view repainting.

- [ ] **Turn on binding-error tracing before claiming a screen is clean.**
  `UIPERF=1` writes every binding error to `ui-binding-errors-*.log`. A binding error
  that fires per row per refresh is a real cost.

## React — TestController.WebClient

- [ ] **Never un-batch `AgentOutputBatch`.** The server batches output every ~500 ms
  *specifically* to cut message rate. `for (const d of batch) handleOne(d)` turns one
  message into N state writes and N renders. Consume the whole batch in one write.
  Guarded by `src/hooks/useExecutionDashboard.test.ts`.

- [ ] **`connection.off('EventName')` with no handler removes EVERY subscriber's
  handler for that event**, across the whole app. Three modules subscribe to
  `AgentOutput` / `AgentOutputBatch` / `LogEntry`. Always pass the handler reference:
  `connection.off('AgentOutputBatch', handleBatch)`.

- [ ] **Context `value={{ ... }}` is a new object every render** and re-renders every
  consumer. `useExecutionDashboard` has six consumers. Wrap it in `useMemo`.

- [ ] **Nothing derived in a provider body may be unmemoized.** `filteredLogs` was
  filtering up to `maxLogs` (10 000) entries on every render, and the provider
  re-renders on every SignalR message.

- [ ] **`[...prev, entry]` copies the whole array per message.** With
  `MAX_LOG_ENTRIES = 50000` that is a 50 000-element copy per log line. Use `concat`
  with a batch.

- [ ] **Windowing.** Lists over a few hundred rows use `@tanstack/react-virtual`
  (`WatchListTree`, `FleetPage`, `LogViewer`, `LiveLogger`, `LogPanel`,
  `UnifiedLogView`). `RegistryPage`, `BuildList` and `TimelineView` are **not**
  windowed — fine at 9 agents, not fine if the fleet grows.

- [ ] **Adding `memo`/`useMemo`/`useCallback`?** Only with a render count from
  `useRenderCount` proving the cost. `FleetCard` and `TreeNodeRow` are memoized for
  measured reasons; do not blanket the rest.

- [ ] **Is a poll duplicating a push?** `SessionList` and `ExecutionMonitor` both poll
  `/api/execution/sessions` every 2 s while SignalR also pushes `ExecutionStarted` /
  `ExecutionCompleted`. `useExecutionDashboard` polls the proxy on purpose (WPF-side
  sessions do not reach this hub) — that one is justified; document any new poll the
  same way or delete it.

## Rule

Measure → change one thing → measure again. A change under 10 % that costs
readability is a loss; revert it. Capture procedure and the (still empty) baseline
table are in [ui-perf-baseline.md](ui-perf-baseline.md).
