---
feature: FeatureName
status: Planned
owner: Your Name
code: [TestControllerGrpc.Core/FeatureName, TestController.Api/Controllers/FeatureNameController.cs]
tests: [TestControllerGrpc.Tests/FeatureName]
last-verified: 2026-01-01
---

<!--
Copy this folder to docs/features/<FeatureName>/ and fill it in.
- feature: the folder name exactly.
- status: Implemented | In progress | Planned
- code / tests: real repo-relative paths (files or folders). tools/Check-Docs.ps1 fails if one does not exist.
- last-verified: the date (yyyy-MM-dd) you last checked this README against the code.
Put prompts in prompts/ and mockups in mockups/ inside this folder. Delete this comment.
-->

# Feature name

## What it does
- Three to five lines: what the user or operator gets, not how it is built.

## How to use
- WPF: where to find it (ribbon, menu, view).
- Web: page or tab.
- REST: `GET /api/...`

## Config / files
- Config keys (section and default), files it reads or writes, feature toggle and permission that gate it.

## How to test
```powershell
dotnet test TestControllerGrpc.Tests --filter "FullyQualifiedName~FeatureName"
```

## Known gaps
- What the spec describes that is not built yet. Use "TODO verify:" when you are not sure.

## Links
- Specs in this folder, prompts in `prompts/`, mockups in `mockups/`. Use relative links.
