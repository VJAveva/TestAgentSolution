---
feature: BuildReportCard
status: Implemented
owner: TODO
code: [TestController.Api/Controllers/BuildReportCardController.cs, TestControllerGrpc.Core/Services/BuildReportAggregator.cs, TestController.WebClient/src/components/reportcard/ReportCardView.tsx, TestControllerGrpc/ViewModels/MainViewModel.ReportCard.cs]
tests: [TestControllerGrpc.Tests/Services/BuildReportAggregatorTests.cs, TestControllerGrpc.Tests/Services/BuildReportEmailHtmlTests.cs]
last-verified: 2026-11-08
---

# Build Report Card

## What it does
- Aggregates a build's results across agents into one card: grade, pass rate, trends and per-agent status.
- Available in WPF and the web client; gated by the `ReportCard_View` permission and the `Reports` feature.

## How to use
- WPF: **Report Card** ribbon button; web: Report Card tab.
- REST: `GET /api/reportcard/builds`, `GET /api/reportcard?build={buildNumber}`.

## Config / files
- Reads the build results folders configured in the `BuildResults` section.

## How to test
```powershell
dotnet test TestControllerGrpc.Tests --filter "FullyQualifiedName~BuildReport"
```

## Known gaps
- TODO verify: the PSR results input described in [BuildReportCard_Requirements.md](BuildReportCard_Requirements.md) (machine-readable PSR output) and whether it exists yet.

## Links
- [BuildReportCard_Requirements.md](BuildReportCard_Requirements.md), mockup [mockups/BuildReportCard_Mockup.html](mockups/BuildReportCard_Mockup.html)
