---
feature: APITest
status: Implemented
owner: TODO
code: [TestController.ApiTests/Infrastructure, TestController.ApiTests/Data]
tests: [TestController.ApiTests/Workflows, TestController.ApiTests/Contracts]
last-verified: 2026-11-08
---

# API workflow test framework

## What it does
- End-to-end tests of the REST API in-process via `WebApplicationFactory`, with a fake agent dispatcher, so no real agents are needed.
- Covers trigger, execute, results, failure recovery, concurrency and cancellation workflows, plus health and registry contracts.

## How to use
```powershell
dotnet test TestController.ApiTests
```

## Config / files
- Test configuration in `TestController.ApiTests/Infrastructure`.

## How to test
- This project is itself the test suite.

## Known gaps
- TODO verify: the spec's "live" mode against a running host, and whether it is wired.

## Links
- [ApiTestFramework_Implementation_Spec.md](ApiTestFramework_Implementation_Spec.md)
