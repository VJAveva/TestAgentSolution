---
feature: BuildReportCard
status: Implemented
owner: Vinodkumar Javvadi
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
- PSR input is **not built**. `BuildReportAggregator.BuildPsrCards()` returns five hardcoded `Pending` placeholder cards; nothing reads PSR result files, because the PSR result schema in [BuildReportCard_Requirements.md](BuildReportCard_Requirements.md) (open question 2) is still undefined.

## Links
- [BuildReportCard_Requirements.md](BuildReportCard_Requirements.md), mockup [mockups/BuildReportCard_Mockup.html](mockups/BuildReportCard_Mockup.html)
