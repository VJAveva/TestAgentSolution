---
feature: ExecutionDashboard
status: Implemented
owner: Vinodkumar Javvadi
code: [TestControllerGrpc/Views/ExecutionDashboardWindow.xaml, TestControllerGrpc/ViewModels/Execution/ExecutionDashboardVM.cs, TestController.WebClient/src/hooks/useExecutionDashboard.tsx, TestController.WebClient/src/components/execution, TestController.Dashboard]
tests: [TestControllerGrpc.Tests/ViewModels/ExecutionDashboardFilterTests.cs, TestControllerGrpc.Tests/ViewModels/ExecutionDashboardLifecycleTests.cs, TestControllerGrpc.Tests/ViewModels/ExecutionDashboardToolbarTests.cs, TestController.WebApi.Tests/ExecutionDashboardEndpointsTests.cs, TestController.WebClient/src/hooks/useExecutionDashboard.test.ts]
last-verified: 2026-11-08
---

# Execution dashboard and live log

## What it does
- Shows running and recent sessions, their agents and a live, filterable log, in WPF and the web client.
- Updates come from execution events pushed over gRPC and SignalR, not polling.
- `TestController.Dashboard` is a separate read-only WPF viewer.

## How to use
- WPF: the execution dashboard window; web: the Execution views.
- REST: `/api/execution/*`. On the IIS tier the web client tries `/api/execution/proxy/dashboard-sessions` and falls back to the local path.

## Config / files
- No files; live state only.

## How to test
```powershell
dotnet test TestControllerGrpc.Tests --filter "FullyQualifiedName~ExecutionDashboard"
dotnet test TestController.WebApi.Tests --filter "FullyQualifiedName~ExecutionDashboard"
```

## Known gaps
- Open UI issues: [ISSUE_Sessions_Health_Implementation_Gaps.md](../../issues/ISSUE_Sessions_Health_Implementation_Gaps.md), [WPF_Controller_Defects_Issues1-7.md](../../issues/WPF_Controller_Defects_Issues1-7.md) (issue 2 fixed, the rest open).

## Links
- [LiveExecutionLog_SessionsPanel_Spec.md](LiveExecutionLog_SessionsPanel_Spec.md)
- Mockups: [mockups/LiveExecutionLog_Mockup.html](mockups/LiveExecutionLog_Mockup.html), [mockups/UseCaseExecution_Fixed_Mockup.html](mockups/UseCaseExecution_Fixed_Mockup.html), [mockups/execution_dashboard_mockup_v2 (2).html](<mockups/execution_dashboard_mockup_v2 (2).html>)
