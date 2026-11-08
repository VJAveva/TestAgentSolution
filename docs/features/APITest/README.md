---
feature: APITest
status: Implemented
owner: Vinodkumar Javvadi
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
# Against a running WebApi:
$env:API_TEST_MODE = 'Live'; $env:API_TEST_BASEURL = 'https://localhost:7240'; dotnet test TestController.ApiTests
```

## Config / files
- Test configuration in `TestController.ApiTests/Infrastructure`.

## How to test
- This project is itself the test suite.

## Known gaps
- Live mode is wired (`TestConfig`, `ApiTestFixture`), but it differs from the spec: the URL variable is `API_TEST_BASEURL` (spec says `API_TEST_URL`), and fake-dependent tests `return` early in Live mode, so they report as passed rather than skipped.
- No CI pipeline runs Live mode.

## Links
- [ApiTestFramework_Implementation_Spec.md](ApiTestFramework_Implementation_Spec.md)
