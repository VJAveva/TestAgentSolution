---
feature: Docs
status: Implemented
owner: Vinodkumar Javvadi
code: [tools/Check-Docs.ps1, tools/Check-DocLinks.ps1, docs/features/_template/README.md, .github/copilot-instructions.md, .github/skills/update-docs/SKILL.md, .github/workflows/build.yml]
tests: []
last-verified: 2026-11-08
---

# Documentation tooling

## What it does
- Keeps `docs/` in line with the code: folder layout, link check, and per-feature READMEs with front matter.
- `tools/Check-Docs.ps1` runs the link check, then checks every `docs/features/<X>/README.md`: front matter present with all keys, every `code:`/`tests:` path exists, `last-verified` is a date. It warns when a feature's code was committed after its `last-verified` date.
- Rules for keeping docs current are in `.github/copilot-instructions.md` (Documentation section); the procedure is the `update-docs` skill.

## How to use
```powershell
.\tools\Check-Docs.ps1              # exit 1 on errors; warnings reported only
.\tools\Check-Docs.ps1 -Strict      # exit 1 on warnings too
.\tools\Check-DocLinks.ps1 -Quiet   # links only, summary line
```
- New feature: copy `docs/features/_template/` to `docs/features/<Feature>/` and add a line to [docs/README.md](../../README.md).
- Stage new files before running: the link check only sees tracked or staged files.

## Config / files
- CI: job `docs` in `.github/workflows/build.yml`, non-blocking (`continue-on-error: true`), full-history checkout for the `last-verified` check.
- To make CI blocking: delete `continue-on-error: true` from the `docs` job; add `-Strict` to the run line to also fail on warnings.

## How to test
- Run `.\tools\Check-Docs.ps1`; expect `0 error(s), 0 warning(s)`.
- No automated tests. It was red-proofed by hand with throwaway feature folders (missing README, bad path, missing key, bad date, stale date, unset owner, an open verify item, broken link): all eight were reported and the exit code was 1.

## Known gaps
- The `last-verified` check is per feature folder, not per file, and counts any commit (including comment-only ones) under the `code:` paths.
- Only `docs/features/*` READMEs have front matter; architecture, operations and getting-started docs are covered by the link check only.

## Links
- [prompts/Prompt_Docs_Restructure.md](prompts/Prompt_Docs_Restructure.md)
- Template: [../_template/README.md](../_template/README.md)
