# Prompt — Restructure docs so they stay in line with the code

> For Copilot agent mode in TestAgentSolution. One part at a time; stop after each part.
> **Investigate first. Moves use `git mv` only. No code changes. No content rewrites in move commits.**

**Target shape (adjust only if Part 0 finds a strong reason):**

```
docs/
  README.md                  <- index: one line per doc, grouped as below
  getting-started/           <- ONBOARDING, Team-Onboarding, DEPLOYMENT-GUIDE, MIGRATION-GUIDE
  architecture/              <- ARCHITECTURE*, ARCHITECTURE-DIAGRAMS, the Architecture .html, adr/
  features/
    <feature>/               <- one folder per feature that exists in code
      README.md              <- what it is, status, code paths, how to test
      requirement.md
      design.md              (if any)
      prompts/               <- Copilot prompts used to build it
      mockups/               <- html/png mockups for this feature
  operations/                <- RUNBOOK, TROUBLESHOOTING, CI-COMMANDS
  ui-specs/
  issues/                    <- open bug reports; fixed ones move to issues/closed/
  archive/                   <- superseded docs/prompts (kept, not deleted)
```

Feature folders expected (confirm in Part 0): PipelineBuilder, GlobalVariables, FIR (FIRFailedTest),
PreFlightCheck, RBAC, Impact, ClientNodeExecution, AzureIntegration, Reliability, Hardening (hardening-plan),
APITest, FeatureToggles.

---

## Part 0 — Investigate (no changes)

```
Investigate the documentation in this repo. Do not move or edit anything.
Report:
1. Every .md / .html / mockup file under docs/, Mockups/, the repo root and project folders: path, title,
   last git change date, and what it covers.
2. Map each doc to the code it describes (project / folder / main classes). Mark each:
   CURRENT (matches code), STALE (code changed after - say what differs), DUPLICATE (of which file),
   SUPERSEDED (replaced by which file), ORPHAN (feature not in code).
3. Features present in code that have NO doc.
4. Every inbound link to each doc (other .md files, README, .github/copilot-instructions.md,
   .github/skills/*, code comments, csproj/build scripts, CI) - so moves do not break anything.
5. Files that must NOT move (referenced by build, CI, tooling, or the published Architecture page).
6. Proposed move table: old path -> new path, per the target shape above. Flag any doc you would
   archive or merge, with the reason. Do not delete anything.
Then stop and wait for my approval of the table.
```

## Part 1 — Move (only after I approve the table)

```
Apply the approved move table.
1. git mv only; one commit "docs: restructure folders (moves only)". No content edits in this commit.
2. Move top-level Mockups/ items into docs/features/<feature>/mockups/ as approved.
3. Superseded/duplicate files go to docs/archive/ with a one-line note at the top:
   "Superseded by <new path> on <date>". Nothing is deleted.
4. Second commit "docs: fix links": update every inbound link found in Part 0 (relative links), including
   .github/copilot-instructions.md and .github/skills/*.
5. Run a link check over the repo (write a small script under tools/ if none exists) - 0 broken links.
Report: files moved, links fixed, link-check result.
```

## Part 2 — Index and per-feature README

```
1. docs/README.md: index grouped like the target shape, one line per doc, status (Current / Draft / Archived).
2. Each docs/features/<feature>/README.md with this front matter and sections:
   ---
   feature: PipelineBuilder
   status: Implemented | In progress | Planned
   owner: <name>
   code: [TestController.WebApi/..., TestControllerGrpc/...]   # real paths
   tests: [TestController.WebApi.Tests/...]
   last-verified: <date>
   ---
   What it does (3-5 lines) | How to use | Config / files | How to test | Known gaps | Links
3. For STALE docs from Part 0: fix only facts that are wrong against the code (paths, names, keys,
   defaults). List each change. Do not invent behaviour; if unsure, add "TODO verify:" instead.
4. Add a short "Docs" section to the repo README pointing to docs/README.md.
Report: features documented, stale facts corrected (with file:line of the code that proves each).
```

## Part 3 — Keep docs in line with code (maintenance rules)

```
1. .github/copilot-instructions.md - add a "Documentation" section:
   - A change that adds/changes a feature, config key, endpoint, CLI arg or file path must update
     docs/features/<feature>/README.md in the same change and bump last-verified.
   - New feature = new docs/features/<feature>/ folder from the template.
   - Prompts and mockups live with their feature, never in the docs root.
   - Never put real passwords, tokens or unsanitized logs/TRX in docs.
2. .github/skills/update-docs/SKILL.md: steps to update a feature README after a code change
   (read the diff, update code paths, config keys, how-to-test, last-verified, run link check).
3. docs/features/_template/README.md with the front matter above.
4. tools/check-docs script (PowerShell or .NET, no new heavy dependencies):
   - broken links
   - every `code:` path in feature front matter exists
   - every feature folder has a README with front matter
   - warn if a feature's code changed after its last-verified date (git log)
   Run it in CI as a warning first (non-blocking); tell me the command to make it blocking later.
Report: what was added, check-docs output on the current repo.
```

---

Stop after each part and report: what changed, anything found but not fixed. Never delete a doc; archive it.
