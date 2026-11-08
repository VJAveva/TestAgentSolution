---
feature: AgentDiagnostics
status: Implemented
owner: Vinodkumar Javvadi
code: [TestAgent.Diagnostics]
tests: []
last-verified: 2026-11-08
---

# TestAgent Diagnostics (log viewer)

## What it does
- A separate read-only WPF tool for reading controller and agent logs.
- The Failures view lists which run failed at which action; the Logs view drills down into the log lines for that run, agent and time window.
- Can follow the newest log files live (Live Status view); parses text, JSON and crash logs.

## How to use
- Build and run the `TestAgent.Diagnostics` project, then open one or more log files.

## Config / files
- Reads log files only; no configuration and no database.

## How to test
- No automated tests.

## Known gaps
- None known against the spec: both views are built, and so is the "later phase" live follow (`LogFileTail`, `LiveStatusView`, auto-follow in `LogsViewModel`).
- No automated tests (see above).

## Links
- [TestAgent_Diagnostics_BuildSpec.md](TestAgent_Diagnostics_BuildSpec.md)
