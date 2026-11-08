---
feature: RunEmail
status: Implemented
owner: Vinodkumar Javvadi
code: [TestControllerGrpc/Services/ConsolidatedRunMailer.cs, TestControllerGrpc.Core/Services/ConsolidatedRunEmailBuilder.cs, TestControllerGrpc.Core/Services/ConsolidatedRunReportBuilder.cs, TestControllerGrpc.Core/Models/BuildResultsConfig.cs]
tests: [TestControllerGrpc.Tests/Services/ConsolidatedRunEmailBuilderTests.cs, TestControllerGrpc.Tests/Services/ConsolidatedRunReportBuilderTests.cs, TestControllerGrpc.Tests/Services/ConsolidatedRunDataTests.cs]
last-verified: 2026-11-08
---

# Consolidated run email

## What it does
- Sends one summary email when a full pipeline run finishes, covering every agent's results.
- Runs as a hosted service in the WPF controller; it is off unless configured and is skipped when the `Reports` feature is off.

## How to use
- Turn on in `appsettings.json` under `BuildResults`: `SendConsolidatedEmail`, `ConsolidatedRecipients`, and optionally `ConsolidatedControllerUrl` / `ConsolidatedReportCardUrl` for links in the mail.

## Config / files
- `BuildResults` section (`BuildResultsConfig`), including the SMTP settings shared with other mails. `Notifications` section for alert and mute behaviour.

## How to test
```powershell
dotnet test TestControllerGrpc.Tests --filter "FullyQualifiedName~ConsolidatedRun"
```

## Known gaps
- The CI results email redesign is still an enhancement request: [ISSUE-TestAgentCI-email-redesign.md](ISSUE-TestAgentCI-email-redesign.md).

## Links
- Mockups: [mockups/ConsolidatedExecutionEmail_9Agents_Mockup.html](mockups/ConsolidatedExecutionEmail_9Agents_Mockup.html), [mockups/results-email-mockup.html](mockups/results-email-mockup.html)
