---
feature: Hardening
status: In progress
owner: Vinodkumar Javvadi
code: [TestController.Api/Security, TestController.WebApi/Program.cs, TestControllerGrpc.Core/Services/SecurityRedactor.cs, .github/skills/code-audit/SKILL.md]
tests: [TestControllerGrpc.Tests/Security, TestController.WebApi.Tests/Security]
last-verified: 2026-11-09
---

# Hardening and audits

## What it does
- [REGISTER.md](REGISTER.md) is the single list of hardening findings, each with severity, status, the code that proves it and the test that guards it.
- The per-project hardening plans (00 to 09) and the audit documents feed the register.
- Folder audits use the `code-audit` skill (five passes, report only).

## How to use
- Before hardening work, read the register entry and its evidence; update its status in the same change that fixes it.

## Config / files
- None.

## How to test
- Each register entry names its guard test.

## Known gaps
- Open items (for example REG-001 AuthMode default, REG-003 and REG-022 no TLS) are listed in [REGISTER.md](REGISTER.md).

## Links
- [REGISTER.md](REGISTER.md), [SECURITY-GAPS.md](SECURITY-GAPS.md), [SECURITY-ROADMAP.md](SECURITY-ROADMAP.md), [HARDENING.md](HARDENING.md), [AUDIT.md](AUDIT.md)
- Plans: [00-overall-system-hardening-plan.md](00-overall-system-hardening-plan.md) and the numbered per-project plans in this folder
- Prompts: [Hardening_Prompt_Pack.md](Hardening_Prompt_Pack.md), [CodeAudit-Copilot-Prompts.md](CodeAudit-Copilot-Prompts.md), [Fix_ProtoConsolidation_ActionPlan.md](Fix_ProtoConsolidation_ActionPlan.md)
