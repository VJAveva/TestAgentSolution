---
name: update-docs
description: 'Update a feature README under docs/features/<feature>/ after a code change: read the diff, fix code/tests paths, config keys, endpoints, how-to-test and known gaps, bump last-verified, then run tools/Check-Docs.ps1. Use for update docs, docs out of date, README stale, last-verified, new feature folder, check-docs failed.'
---

# Update docs after a code change

Rules live in `.github/copilot-instructions.md` (Documentation section). Index: `docs/README.md`. Template: `docs/features/_template/README.md`.

## Procedure
1. **Read the change.** `git diff --stat <base>..HEAD` (or `git diff --cached --stat` for staged work), then the hunks of the files that matter.
   - Only changes to code, config, endpoints, CLI args or file paths need a docs update. A pure refactor with no visible effect does not.
2. **Find the feature.** Match each changed path against the `code:` and `tests:` front matter:
   `Select-String -Path docs/features/*/README.md -Pattern '<changed folder or file>'`.
   - No match + new user-visible feature: copy `docs/features/_template/` to `docs/features/<Feature>/`, fill it in, and add one line to `docs/README.md`.
   - No match + existing feature: add the path to that feature's `code:` or `tests:`.
3. **Update the README, facts only.** Verify each one in the code before you write it:
   - `code:` / `tests:`: renamed, added or removed paths.
   - **Config / files**: section names, keys, defaults (read `appsettings*.json` and the options class).
   - **How to use**: endpoints (route attribute or `Map*` call), menu or ribbon names, CLI args.
   - **How to test**: a filter that actually matches tests; run it once.
   - **Known gaps**: remove gaps the change closed; add gaps it opened. Write "TODO verify:" instead of guessing.
4. **Bump** `last-verified:` to today (yyyy-MM-dd), only for READMEs you checked against the code.
5. **Moved or renamed a doc?** Use `git mv`, then fix every inbound link: `.md` files, `.github/copilot-instructions.md`, `.github/skills/*`, code comments.
6. **Check.** Stage the new files first (the link check only sees tracked or staged files), then run:
   ```powershell
   .\tools\Check-Docs.ps1
   ```
   Fix every ERROR. For a WARN about "code changed after last-verified", either update that README or explain why the change does not affect it.

## Never
- Put real passwords, tokens, machine credentials, or unsanitized logs/TRX into docs (use the `sanitize-logs` skill first).
- Put prompts or mockups in the docs root. They go in `docs/features/<feature>/prompts/` or `mockups/`.
- Delete a doc. Move a superseded one to `docs/archive/` with a line at the top: "Superseded by <new path> on <date>".
- Bump `last-verified` without checking the README against the code.
