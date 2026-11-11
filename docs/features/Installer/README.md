---
feature: Installer
status: Planned
owner: Vinodkumar Javvadi
code: [Utilites/TestAgent_SetupScripts/Prepare-ControllerNode.ps1, Utilites/TestAgent_SetupScripts/Prepare-AgentNode-Full.ps1]
tests: []
last-verified: 2026-11-10
---

# Installer and team setup guide

## What it does
- Planned: one release ZIP, one double-click installer (`Install.cmd`) and one team settings file (`team-setup.json`), so another team can stand up a controller and agents without hand-editing configs.
- Builds on the existing node preparation scripts listed under `code`.

## How to use
- Not built yet. The approach and the Copilot prompts to build it are in `prompts/`.

## Config / files
- Planned: `team-setup.json` (the only file a team edits).

## How to test
- Nothing to test yet.

## Known gaps
- The whole installer is unbuilt; see the prompt pack for the planned parts.

## Links
- Approach and prompts: [prompts/Prompt_Installer_SetupGuide.md](prompts/Prompt_Installer_SetupGuide.md)
