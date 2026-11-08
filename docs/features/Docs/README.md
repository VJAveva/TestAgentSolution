---
feature: Docs
status: In progress
owner: TODO
code: [tools/Check-DocLinks.ps1, .github/copilot-instructions.md, .github/skills]
tests: []
last-verified: 2026-11-08
---

# Documentation tooling

## What it does
- Keeps `docs/` in line with the code: folder layout, link check, and per-feature READMEs with front matter.
- `tools/Check-DocLinks.ps1` checks every relative link in tracked `.md` files and in `.html` under `docs/`. It fails on a missing target or a letter-case mismatch.

## How to use
```powershell
.\tools\Check-DocLinks.ps1          # exit 0 = no broken links
.\tools\Check-DocLinks.ps1 -Quiet   # summary only
```

## Config / files
- Index: [docs/README.md](../../README.md).

## How to test
- Run the link check; it should report 0 broken.

## Known gaps
- Part 3 of the restructure prompt (maintenance rules, `update-docs` skill, feature template, `check-docs` with front-matter checks) is not done yet.

## Links
- [prompts/Prompt_Docs_Restructure.md](prompts/Prompt_Docs_Restructure.md)
