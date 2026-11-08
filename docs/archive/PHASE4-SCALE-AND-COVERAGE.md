# Phase 4 — Scale Validation & Test Coverage (Implementation Plan)

> **Created:** 2026-06-19
> **Scope:** The "as capacity allows" items — large-file streaming, end-to-end scale *validation*, and frontend test backfill.
> **Stack touched:** `TestControllerGrpc.Core` (TRX), a new load-test harness, and `TestController.WebClient` tests.
> **Depends on:** Phase 1 (A1/A2 give the store APIs that the new frontend tests lock in).
> **Goal:** Prove the system holds at fleet scale, stop large TRX files from spiking memory, and raise frontend coverage on the core flows.

---

## Important context — scaling is largely already built

The repo already contains **`docs/Issues/SCALING-IMPLEMENTATION-PLAN.md`** (8 phases, 200-agent target) and most of it is **implemented**: SignalR heartbeat + output batching (`AgentHeartbeats`/`AgentOutputBatch` in `useSignalR.ts`), fleet snapshot cache (`TestController.WebApi.Tests/FleetSnapshotCacheTests.cs`), bounded pipeline parallelism (`TestControllerGrpc.Tests/Scaling/PipelineParallelismTests.cs`), ThreadPool pre-warm + rate-limit config (`RateLimitConfigTests.cs`), and event-aggregator stress tests (`EventAggregatorStressTests.cs`).

**So D3 is reframed:** not "design scaling" (done) but **"validate it end-to-end."** The existing tests cover each *mechanism in isolation*; what's missing is a single test that drives a live N-agent fleet and measures the documented success criteria. Don't re-implement the scaling fixes.

---

## Summary

| # | Item | Area | Impact | Effort | Risk |
|---|------|------|--------|--------|------|
| **D1** | Stream large TRX via `XmlReader` (hybrid by size) | Perf (memory) | Med (conditional) | M | Low |
| **D3** | End-to-end load/soak harness validating existing scaling | Scale assurance | High (confidence) | L | Low |
| **E** | Frontend test backfill on core flows | Quality/regression | Med | M | Low |

**Recommended order:** E → D1 → D3. (E is quick and locks in Phase 1/2 work; D1 is contained; D3 is the largest build.)

**Decision needed for D1:** are real TRX files actually large (100MB+)? If they're a few MB, D1 is unnecessary (the existing path + cache is fine) — treat it as conditional, like A3.

---

## D1 — Stream large TRX files via `XmlReader`

### Problem
`ParseFile` loads the **entire** TRX into memory with `XDocument.Load`. A 100MB+ TRX (large suites with verbose stdout) spikes memory — multiplied if several are parsed in a build folder scan.

### Evidence
- `TestControllerGrpc.Core/Services/TrxResultsParser.cs:137` — `var doc = XDocument.Load(trxFilePath);` then full navigation.
- Cache (path+timestamp, 500 cap) already avoids *re-parsing* — but first parse still loads the whole document.

### Fix — hybrid: keep `XDocument` for small files, stream big ones
The bulk of a TRX is the repeated `<UnitTestResult>` elements. Stream those one subtree at a time with `XmlReader` + `XNode.ReadFrom`, so only a single result element is materialized at once. Counters/Times are small and read on the same forward pass. Keep the current `XDocument` path for files under a threshold so the common case is unchanged.

```csharp
private const long StreamingThresholdBytes = 25 * 1024 * 1024; // 25 MB

public TrxTestRun ParseFile(string trxFilePath)
{
    var lastWrite = File.GetLastWriteTimeUtc(trxFilePath);
    var cacheKey = $"{trxFilePath}|{lastWrite:O}";
    if (_fileCache.TryGetValue(cacheKey, out var cached)) return cached;

    var info = new FileInfo(trxFilePath);
    var result = info.Length >= StreamingThresholdBytes
        ? ParseFileStreaming(trxFilePath)   // NEW: XmlReader, bounded memory
        : ParseFileBuffered(trxFilePath);   // existing XDocument.Load body, extracted verbatim

    /* existing cache-evict + size-cap block unchanged */
    _fileCache[cacheKey] = result;
    return result;
}
```
`ParseFileStreaming` walks the reader, materializing one element subtree at a time and **reusing the existing per-element navigation** (so the parsing logic isn't duplicated):
```csharp
private TrxTestRun ParseFileStreaming(string path)
{
    var fileName = Path.GetFileNameWithoutExtension(path);
    var testCases = new List<TrxTestCase>();
    long totalDurationTicks = 0;
    XElement? counters = null, times = null;

    using var reader = XmlReader.Create(path, new XmlReaderSettings { IgnoreWhitespace = true });
    while (reader.Read())
    {
        if (reader.NodeType != XmlNodeType.Element) continue;
        if (reader.LocalName == "Times")    { times    = (XElement)XNode.ReadFrom(reader); continue; }
        if (reader.LocalName == "Counters") { counters = (XElement)XNode.ReadFrom(reader); continue; }
        // Only top-level Results/UnitTestResult (skip InnerResults, handled within ParseUnitTestResult)
        if (reader.LocalName == "UnitTestResult" && reader.Depth <= /* Results depth */ 3)
        {
            var el = (XElement)XNode.ReadFrom(reader);
            testCases.Add(ParseUnitTestResult(el, fileName, ref totalDurationTicks));
        }
    }
    return BuildRun(fileName, times, counters, testCases, totalDurationTicks);
}
```
Refactor the per-result body (lines 158–235) into `ParseUnitTestResult(XElement, …)` and the summary assembly (lines 238–251) into `BuildRun(…)` so **both** the buffered and streaming paths call the same code.

### Caveats
- Confirm the depth/`Results` boundary so `InnerResults` aren't double-counted (they're parsed *inside* `ParseUnitTestResult`).
- Counters can appear after Results in TRX (inside `ResultSummary`) — a forward pass still catches it; the fallback counts from `testCases` already handle a missing Counters.
- Threshold is a guess — make it a config value and measure.

### Files
`TestControllerGrpc.Core/Services/TrxResultsParser.cs`; tests in `TestControllerGrpc.Tests/Services/` (extend `TrxModelsTests`/add `TrxResultsParserStreamingTests`).

### Acceptance criteria
- A large synthetic TRX (≥ 25MB) parses with **bounded** peak memory (streaming path) and yields the **same** `TrxTestRun` values as the buffered path on the same content (golden-master equality test).
- Small files still use `XDocument` (unchanged behavior + existing tests pass).

---

## D3 — End-to-end load/soak validation harness

### Problem
The scaling *fixes* exist and have *unit* tests, but nothing exercises a **live fleet** end-to-end. We can't currently answer "does the controller stay healthy with 100–200 agents heartbeating + streaming for an hour?" with data — only by inference.

### Evidence
- `SCALING-IMPLEMENTATION-PLAN.md` §"Success Criteria" lists measurable targets (FleetVM refresh ≤1/s, DOM <100, SignalR <10 msg/s, zero ThreadPool starvation, snapshot <10ms cached, pipeline peak ≤50) — but the verification column is manual/per-mechanism, not an automated fleet run.
- Tests like `EventAggregatorStressTests`, `PipelineParallelismTests` validate components, not the integrated system under sustained load.

### Fix — build a simulated-fleet harness (new project `TestController.LoadTests`)
Two load surfaces; build the agent side first (it's the heavier path).

**1. Agent → Controller (primary):** a console harness that spins up **N lightweight `TestAgentService` gRPC servers** (or one server multiplexing N registered identities) which:
- register with a target controller, heartbeat at the real interval (15s), and
- on trigger, stream stdout at a realistic rate (e.g. 10 lines/s) for a configurable duration.

Drive it to 50 → 100 → 200 agents and capture controller-side metrics (CPU, working set, GC, SignalR send rate, snapshot build time) via the existing `/api/health/diagnostics` + `AppMetrics`/Prometheus exporter already in the WebApi.

**2. Controller → Browser (secondary):** N concurrent SignalR clients (use **NBomber** or a small `@microsoft/signalr` Node script — the repo already has `signalr-monitor-test.cjs` as a starting point) that connect, `RequestFleetSnapshot`, join session groups, and consume `AgentOutputBatch`/`AgentHeartbeats`. Assert no 429s and steady message rates.

**Wire the success criteria as assertions** so the harness is a pass/fail gate, not just a script. Add a CI "soak" job (nightly, not per-PR) running the 100-agent profile for ~15 min.

### Caveats
- Keep it out of the per-PR test run (slow); nightly/manual only.
- Simulated agents must use the **real** proto + client (`RemoteCommandStreamRunner` path) so the test exercises production code, not a mock.
- This is **validation**; if a target is missed, the fix belongs back in the scaling plan's owning phase — don't patch it in the harness.

### Files
New `TestController.LoadTests/` project (harness + agent simulator + assertions); a nightly CI job in `.github/workflows/`.

### Acceptance criteria
- Harness drives ≥100 simulated agents against a controller and reports the `SCALING-IMPLEMENTATION-PLAN.md` success-criteria metrics.
- A 15-min 100-agent soak shows stable memory (no unbounded growth) and zero ThreadPool-starvation / 429 events.
- Results documented (baseline numbers committed alongside the harness).

---

## E — Frontend test backfill (core flows)

### Problem
~7 test files cover ~80 source files. The highest-traffic logic — the execution log store, the watchlist tree build + status map, agent heartbeats, and the execution hook's 409 handling — is untested, so Phase 1/2 changes have no regression net.

### Evidence
- Existing tests: `lib/agentStatus`, `lib/errorThrottle`, `lib/capabilities`, `stores/lockStore`, `hooks/useAgentTelemetry`, 2 dialogs. Convention: vitest, `describe/it`, `Should_X_When_Y` names, `vi.mock` for store deps, store reset in `beforeEach` (see `stores/lockStore.test.ts`).
- Untested core: `stores/executionStore.ts`, `stores/agentStore.ts`, `stores/watchlistStore.ts`, `hooks/useExecution.ts`, `hooks/useSignalR.ts`.

### Fix — prioritized new test files (match existing conventions)
1. **`stores/executionStore.test.ts`** — locks in A1: `addLogs` appends a batch in one call; respects `maxLogs` trim; `addLog`/`addLogs` no-op when paused; `clearLogs` empties.
2. **`stores/agentStore.test.ts`** — A1: `applyHeartbeats` updates many agents in one call, leaves unknown names untouched, no-ops on empty.
3. **`stores/watchlistStore.test.ts`** — A2: `buildTree` shapes WatchList/WatchItem/Event/Action correctly; `updateNodeStatus` writes the status map (post-A2) and is case-insensitive; `setConfig` resets status.
4. **`hooks/useExecution.test.ts`** — `extractLockFrom409` returns the lock DTO on 409 and `null` otherwise; `triggerByTag` dispatches `pipeline-lock-conflict` on 409 (mock the data layer).

Example (matches house style), for A1:
```ts
import { describe, it, expect, beforeEach } from 'vitest';
import { useExecutionStore } from './executionStore';

describe('executionStore', () => {
  beforeEach(() => useExecutionStore.setState({ logs: [], isLogPaused: false, maxLogs: 2000 }));

  it('Should_AppendAll_When_AddLogsBatch', () => {
    const entries = Array.from({ length: 50 }, (_, i) => ({ message: `L${i}`, timestamp: '', severity: 'info' as const }));
    useExecutionStore.getState().addLogs(entries);
    expect(useExecutionStore.getState().logs).toHaveLength(50);
  });

  it('Should_TrimToMax_When_BatchExceedsCap', () => {
    useExecutionStore.setState({ maxLogs: 10 });
    useExecutionStore.getState().addLogs(Array.from({ length: 25 }, (_, i) => ({ message: `${i}`, timestamp: '', severity: 'info' as const })));
    expect(useExecutionStore.getState().logs).toHaveLength(10);
  });

  it('Should_NotAppend_When_Paused', () => {
    useExecutionStore.setState({ isLogPaused: true });
    useExecutionStore.getState().addLogs([{ message: 'x', timestamp: '', severity: 'info' }]);
    expect(useExecutionStore.getState().logs).toHaveLength(0);
  });
});
```

### Files
New `*.test.ts` under `stores/` and `hooks/`.

### Acceptance criteria
- New tests pass under `npm run test` and fail if the A1/A2 batch/map behavior regresses.
- Coverage on the four targeted modules is meaningful (happy + edge paths), not token.

---

## Verification
```powershell
# E (frontend)
cd TestController.WebClient; npm ci; npm run test

# D1 (TRX)
dotnet test TestControllerGrpc.Tests/TestControllerGrpc.Tests.csproj

# D3 (load harness — manual/nightly, not per-PR)
dotnet run --project TestController.LoadTests -- --agents 100 --duration 00:15:00
```

## Definition of done
- [ ] D1: hybrid TRX parse (buffered + streaming share one parse body); golden-master equality test; large-file memory bounded; small-file path unchanged. *(Or explicitly deferred if real TRX files are small.)*
- [ ] D3: load harness drives ≥100 simulated agents, asserts the existing scaling success criteria, baseline committed, nightly CI job added.
- [ ] E: four new test files covering executionStore/agentStore/watchlistStore/useExecution; green and regression-proof against Phase 1/2.
- [ ] No production behavior change from D1/E; D3 adds tooling only.
```
