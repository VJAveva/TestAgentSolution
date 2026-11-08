---
feature: Reliability
status: Implemented
owner: Vinodkumar Javvadi
code: [TestControllerGrpc/Diagnostics/UiPerfDiagnostics.cs, TestController.WebClient/src/lib/uiPerf.ts, TestController.WebClient/src/hooks/useRenderCount.ts, TestControllerGrpc.Core/Services/SecurityRedactor.cs]
tests: [TestControllerGrpc.Tests/Services/CommandExecutionE2ETests.cs, TestController.WebClient/src/hooks/useExecutionDashboard.test.ts]
last-verified: 2026-11-08
---

# Reliability and UI performance

## What it does
- Command execution hardening: timeouts that kill the whole process tree, retries, and redaction of secrets in output.
- Temporary UI performance instrumentation for WPF (`UiPerfDiagnostics`) and the web client (`uiPerf.ts`, `useRenderCount`).
- Failure triage feasibility work that led to [FIR](../FIR/README.md).

## How to use
- WPF instrumentation: set `UiPerf:Enabled=true` in `appsettings.json`.

## Config / files
- `UiPerf:Enabled`; `Controller:Timeouts` for command timeouts.

## How to test
```powershell
dotnet test TestControllerGrpc.Tests --filter "FullyQualifiedName~CommandExecution"
```

## Known gaps
- The WPF UI performance baseline is not filled in yet: [ui-perf-baseline.md](ui-perf-baseline.md).

## Links
- [08_Tier1_Reliability_Hardening_Spec.md](08_Tier1_Reliability_Hardening_Spec.md), [UIPerformance-CopilotPrompts.md](UIPerformance-CopilotPrompts.md), [ui-perf-checklist.md](ui-perf-checklist.md), [ui-perf-baseline.md](ui-perf-baseline.md)
- [FailureTriage-Feasibility-AuditPrompt.md](FailureTriage-Feasibility-AuditPrompt.md), [FailureTriage-Feasibility-AuditReport.md](FailureTriage-Feasibility-AuditReport.md)
