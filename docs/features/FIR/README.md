---
feature: FIR
status: Implemented
owner: Vinodkumar Javvadi
code: [TestControllerGrpc.Core/Fir, rules, TestController.Api/Controllers/ResultsController.cs, TestControllerGrpc/ViewModels/BuildResultsViewModel.Fir.cs]
tests: [TestControllerGrpc.Tests/Fir]
last-verified: 2026-11-08
---

# FIR: First Information Report for failed tests

## What it does
- For a failed test, finds the first real failure in its log, groups the evidence by cause and hides harness noise and secrets.
- Rules come from rule packs (`rules/*.fir.json`) loaded by `FirRulePackLoader`; `FirAnalyzer` applies them and `FirService` caches the result.
- Shown in the WPF results detail pane and served to the web client over REST.

## How to use
- WPF: Results, then select a failed test; the FIR section is at the top of the detail pane (hidden when `FirReport` or `Results` is off).
- REST: `GET /api/results/builds/{build}/test/{testName}/fir` (add `?refresh=true` to recompute).

## Config / files
- Rule packs: `rules/*.fir.json` (copied to the build output).
- **Test fixtures stay at `docs/FIRFailedTest/Fir/`** (outside this folder on purpose): `TestControllerGrpc.Tests/Fir/FirFixtures.cs` reads them from there. See [docs/FIRFailedTest/Fir/README.md](../../FIRFailedTest/Fir/README.md).

## How to test
```powershell
dotnet test TestControllerGrpc.Tests --filter "FullyQualifiedName~Fir"
```

## Known gaps
- Copying the test-data workbook back to the controller is parked: [TASK_CopyWorkbookToController.md](TASK_CopyWorkbookToController.md).

## Links
- [FIR_Requirement.md](FIR_Requirement.md), [FIR_Implementation_Plan.md](FIR_Implementation_Plan.md), mockup [mockups/FIR_Results_Mockup.html](mockups/FIR_Results_Mockup.html), prompt [prompts/FIR_Copilot_Prompt.md](prompts/FIR_Copilot_Prompt.md)
- Triage workflow: `.github/skills/triage-failed-run/SKILL.md`
