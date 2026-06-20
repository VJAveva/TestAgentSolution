# TestAgent Diagnostics — Build Spec
### One thin client: Failure Tracer + Log Explorer

---

## Goal (read this first)

A standalone, read-only WPF utility (its own `.exe`) to **find and trace failures** across the TestController service, Core Engine, clients, and agents — without reading raw logs line by line. One window, two linked views over **one shared parsed log set**:

- **Failures** (landing view): which pipeline runs failed, and the exact action that broke (Install, Deploy, Execute…), with the subsystem and agent.
- **Logs** (detail view): the full timestamped, filterable log with an exception / stack-trace pane.

One click on a failed action opens **Logs pre-filtered** to that run, action, agent, and time window. It is built to scale to 50+ pipelines and 100+ agents by leading with failures and correlating everything by **run id**.

---

## Scope — keep it tight

- A **separate** utility, not part of the main app. It depends only on **reading log output** — no database, no live calls into the running services.
- **Read-only.** Loads one or more log files. (Optional folder "follow / tail" is a later phase, not v1.)
- MVVM (CommunityToolkit.Mvvm), reuse the suite's shared `Themes/` dictionary so it gets Light / Dark / High-Contrast for free.

---

## Step 0 — confirm before building (the one dependency)

The whole tool relies on each log line carrying a few fields. Before writing UI, inspect how the **shared `IAppLogger`** writes logs and confirm (or produce) these fields per entry:

`timestamp` · `severity` · `component` (subsystem) · `agent` · `runId` (one id per pipeline run) · `pipeline` · `action` (step name) · `message` · `exception` + `stackTrace`

- If logs are **structured already** (e.g. JSON lines): write a parser that maps those fields.
- If logs are **free-form text**: either (a) update `IAppLogger` to emit structured lines (JSON-lines preferred), or (b) write a tolerant parser that extracts the fields.

**Report which case is true, then proceed.** Without `component` + `agent` + `runId` + `action`, the views cannot group failures or jump to the right lines — these fields are the enabler.

---

## Shared data model — one parse, two views

Parse all loaded files **once** into an in-memory record:

```
LogRecord { Timestamp, Severity, Component, Agent, RunId, Pipeline, Action, Message, Exception, StackTrace }
```

Both views read this same collection. **Virtualize**: at 50 pipelines × 100 agents the logs are large — stream-parse the files and use UI virtualization in the grid. Never load the whole log into the UI at once (the target machine is RAM-constrained).

---

## View A — Failures (the landing view)

Failure-first, three levels of drill-down:

1. **Failing runs list** — one row per failed run: `Pipeline`, "failed at `<Action>`", `Component`, `Agent`. Minimal filters: Pipeline and Agent. Default shows **failures only**.
2. **Action sequence** (when a run is selected) — the steps in order (e.g. Initialize → Revert → Install → Configure → Deploy → Execute), each marked **passed / failed / not-run**. The failed step is highlighted; passed steps appear before it, not-run steps after.
3. **Failed-action detail** — the error message, `Subsystem` (component), `Agent`, timestamp, and a **"View logs"** action.

`View logs` → opens **View B** filtered to `{ RunId + Action + Agent + time window }`.

---

## View B — Logs (the detail view)

- **Grid** (virtualized): `Time | Severity | Component | Agent | Message`. Severity colour-coded: Error = red, Warning = amber, Info = neutral/blue, Debug = muted.
- **Filters**: Component, Agent, Severity, RunId, free-text, time range.
- **Details pane**: full message + exception + stack trace for the selected row.
- Works **standalone**, or arrives **pre-filtered** from View A. Show a "filtered by: `<run / action / agent>`" chip with a one-click **Clear filter**.
- **Export** the current (filtered) view to a file.

---

## The link — the whole point of combining them

- View A tells you **where** it broke; View B tells you **why**, on the exact lines.
- `View logs` carries the filter context (run, action, agent, time) so you land on the relevant lines — not the whole log.
- A **"back to failure"** action returns to that run in View A.

---

## Build directives (new project)

- **ADD** a new WPF project `TestAgent.Diagnostics` that produces an independent `.exe`.
- **ADD** the data layer: the `LogRecord` model, a log parser (per Step 0), a multi-file loader. (Folder-watch "Follow" = later phase.)
- **ADD** View A (Failures) + view-model: failing-runs list, action-sequence drill-down, failed-action detail, the `View logs` command.
- **ADD** View B (Logs) + view-model: virtualized grid, the filter set, details / stack-trace pane, export, the "filtered by" chip.
- **WIRE** the link: `View logs` sets View B's filters and switches to it; `back to failure` returns to View A.
- **REUSE** the shared `Themes/` dictionary via `DynamicResource` so it themes with the rest of the suite.

---

## Constraints / conventions

- **Virtualize the grid + stream-parse** the files — must stay responsive on large logs and a RAM-constrained machine.
- **Read-only** — it never writes to the system; it only reads log files.
- **CommunityToolkit.Mvvm + DI**, matching the main app's conventions.
- No heavy new dependencies — standard WPF plus the existing logging types.
- **Defaults already chosen (no decisions needed):** severities = Error / Warning / Info / Debug; v1 = load-files + manual **Reload** (tail is optional later); **RunId is a first-class field and filter**.

---

## Acceptance — how to know it works

1. Open log files → **Failures** lists the failing runs (e.g. *Marathon_FullDeploy failed at Install*).
2. Click a run → the **action sequence** shows the failed step highlighted, passed steps before, not-run steps after.
3. Click **View logs** on the failed action → **Logs** opens filtered to that run + action + agent; the failed line and its stack trace are visible.
4. In **Logs**, change Component / Agent / Severity / text filters directly → the grid updates and stays responsive on large files.
5. Themes follow the suite (Light / Dark / High-Contrast).

---

*Two views, one parsed log set, linked by "View logs". Start in Failures to find what broke; land in Logs to see why.*
