# Installer + Setup Guide — approach and Copilot prompts

> Revised 2026-11-10 after a review against the code and the live JVGR22 / agent setup.
> The first draft assumed Windows services and a single web port; neither matches the product.
> "What changed from the first draft" at the end lists every correction and why.

## What actually gets installed (three roles, no Windows services)

| Role | Process | How it starts today | Ports | Install folder |
|---|---|---|---|---|
| Controller | `TestControllerGrpc.exe` (WPF desktop app, tray icon) | A person launches it; no service, no task. The X button hides it to the tray; only tray **Exit** or File -> Exit stops it. | 5100 gRPC (agents), 5200 embedded REST API | `C:\TestControllerService` |
| Web tier | `TestController.WebApi` + React SPA in IIS (InProcess, "No Managed Code" pool) | IIS site, AlwaysRunning pool | 81 (HTTP); proxies to the controller on 5200 | `C:\inetpub\TestControllerWeb` |
| Agent | `TestAgentGrpc.exe` | Scheduled task **"TestAgentGrpc Interactive"** (AtLogon, Interactive, Highest) as a domain user, optional auto-logon, because UI tests need a real desktop session | 5200 gRPC | `C:\TestAgentService` |

Everything the installer does must fit this model. In particular:
- **Never** create a Windows service for the controller or the agent.
- **Never** kill the controller. A force-kill writes a ~600 MB crash dump, which has filled C: twice. Upgrade asks
  the user to exit from the tray and waits for 5100/5200 to close.

## The approach (one path)

**One release ZIP, one double-click installer, one team settings file — built ON the scripts we already run.**

`Install.ps1` is an **orchestrator**, not a rewrite. Each step calls a script that already works in production:

| Step | Existing script (verify behaviour in Part 0) |
|---|---|
| IIS features, Hosting Bundle, .NET >= 10.0.12, controller firewall, starter WatchList | `Utilites/TestAgent_SetupScripts/Prepare-ControllerNode.ps1` |
| IIS site + app pool | `Utilites/TestAgent_SetupScripts/Deploy-WebToIIS.ps1` |
| Agent: .NET, run-as account (Get-Credential), scheduled task, auto-logon, ControllerAddress merge | `Utilites/TestAgent_SetupScripts/Prepare-AgentNode-Full.ps1` (v1.1) |
| Agent task restart policy | `Utilites/TestAgent_SetupScripts/Configure-AgentTaskRecovery.ps1` |
| PASS/FAIL connectivity table | `Utilites/TestAgent_SetupScripts/Test-NodeConnectivity.ps1` |
| Upgrade: payload-only copy, backup, SHA256 check, rollback, protected files | `deploy/Invoke-Patch.ps1` |
| What must never be overwritten or deleted | `deploy/fleet-inventory.json` (`preserveFiles` / `preserveFolders`) |
| Crash-dump / log cleanup | `deploy/Invoke-LogCleanup.ps1` |

MSI can come later. The PowerShell installer reuses proven code and is much faster to build and fix.

### Package layout
```
TestAgentSolution-<version>.zip
  Install.cmd                  <- double-click: self-elevates, runs Install.ps1 with -ExecutionPolicy Bypass
  Install.ps1                  <- -Role Controller|Agent  -Verify  -Upgrade  -Uninstall [-Purge]  -WhatIf
  team-setup.sample.json       <- the ONLY file a team edits (copy to team-setup.json)
  controller\                  <- published WPF controller (framework-dependent, keep runtimes\win-x64\native\ as published)
  web\                         <- published WebApi WITH the freshly built SPA in wwwroot\
  agent\                       <- published TestAgentGrpc
  prereqs\                     <- offline: Windows Desktop Runtime + ASP.NET Core Hosting Bundle (>= 10.0.12)
  scripts\setup\               <- the existing setup scripts above (called by Install.ps1)
  scripts\common\              <- generic pipeline helpers every team needs (revert, copy, restart)
  scripts\samples\WAS\         <- our team-specific batch files as EXAMPLES only (never installed as live files)
  samples\Parameters\          <- <Release>-<UseCase>-pipeline-config.json examples + GlobalVariables.json (placeholders)
  samples\WatchList\           <- install-only, smoke examples (tags <= 36 chars, no / or +)
  docs\SETUP-GUIDE.md  docs\TROUBLESHOOTING.md  docs\APPSETTINGS-REFERENCE.md
  VERSION.txt  SHA256SUMS.txt
```
`Features.json` is NOT shipped as a sample: a missing file means every feature is ON. The installer writes
`<controller>\Config\Features.json` only when `team-setup.json` switches something OFF.

### Settings: two files, so upgrades never wipe a team's values
- `appsettings.json` — product defaults, **replaced on every upgrade**.
- `appsettings.Local.json` — **team values only**, written from `team-setup.json`, loaded LAST, **never
  overwritten** by an upgrade (backed up before any change).
- Web tier: keep `appsettings.Production.json` for IIS; `appsettings.Local.json` is loaded after it.
- Today NO project loads a Local file (`TestControllerGrpc/App.xaml.cs` L98, `TestAgentGrpc/Program.cs` L38 load
  `appsettings.json` only), so Part 1 is a code change that must ship before the installer depends on it.
- This split also fixes a known defect. Today every deploy protects `appsettings.json`, so **new keys added
  in the repo never reach an installed box**.
- **Existing installs (JVGR22, AUT1)** hold hand-tuned values inside `appsettings.json`. The FIRST upgrade
  must run a one-time extraction: diff the installed file against the shipped defaults, write only the
  differing leaves to `appsettings.Local.json`, and prove "0 values lost" by a leaf diff before replacing.

### team-setup.json (what a team fills in)
```json
{
  "TeamName": "WAS-QA",
  "Controller": {
    "MachineName": "CTRL01",
    "InstallPath": "C:\\TestControllerService",
    "GrpcPort": 5100,
    "ApiPort": 5200,
    "StartAtLogon": true
  },
  "Web": {
    "Enabled": true,
    "SiteName": "TestControllerWeb",
    "Port": 81,
    "InstallPath": "C:\\inetpub\\TestControllerWeb",
    "AppPoolAccount": "ApplicationPoolIdentity"
  },
  "Agents": [ "agent01", "agent02" ],
  "Agent": { "InstallPath": "C:\\TestAgentService", "Port": 5200, "RunAsUser": "DOMAIN\\svc-testagent", "AutoLogon": true },
  "Email": { "SmtpServer": "<smtp host>", "From": "<from address>", "Recipients": [] },
  "Features": { "Results": true, "FirReport": true, "CodeChurn": false, "ImpactMapping": false },
  "CodeChurn": { "Repositories": [] }
}
```
Rules for this file:
- **No secrets, ever.** Account passwords are asked at install time (`Get-Credential`) and kept only by
  Windows: the scheduled-task logon, or LSA for auto-logon. Product secrets (`ADO_PAT`, `RCLOUD_*`) are optional
  masked prompts written to **Machine-scope** environment variables. IIS only sees them after `iisreset`;
  an app-pool recycle is not enough. Nothing secret is written to disk or to the install log.
- `Web.AppPoolAccount`: `ApplicationPoolIdentity` cannot read UNC build shares, so the web build picker stays
  empty. Teams whose builds are on a share must give a domain account (password prompted).
- `Admins` is deliberately absent. How the first RBAC admin is created must be confirmed in Part 0; the
  installer leaves the controller in Default mode, and SETUP-GUIDE explains switching to Secured.

## Step by step for the person installing

### A. Controller machine (once)
1. Copy `TestAgentSolution-<version>.zip` to the machine, run `Unblock-File` on the ZIP (or tick *Unblock* in
   Properties) **before** extracting, then extract to `C:\TestAgentSetup\<version>\`.
2. Copy `team-setup.sample.json` to `team-setup.json` and fill it in. Run `Install.cmd -Verify -WhatIf` first:
   it validates the file and checks the machine and network without changing anything.
3. Right-click `Install.cmd` -> **Run as administrator** -> choose **Controller**. Answer the prompts
   (web pool account if not ApplicationPoolIdentity, optional ADO PAT / vCloud credentials).
4. The installer runs, printing one line per step:
   1. Prerequisite check (OS, admin rights, >= 10 GB free on C:, ports 5100/5200/81 free).
   2. .NET and the Hosting Bundle from `prereqs\` if they are missing.
   3. IIS features.
   4. Folders and binaries.
   5. `appsettings.Local.json` and `Config\Features.json`.
   6. Samples into `Parameters\Samples\`, never over a real file.
   7. Firewall rules for 5100, 5200 and 81 only.
   8. IIS site and pool, plus the WebSocket section unlock.
   9. Agent share `\\<controller>\TestAgentSetup$`.
   10. A Start-menu shortcut and, if `StartAtLogon`, an interactive logon task for the controller.
   11. A daily log-cleanup task.
5. The installer starts the controller **in the logged-on user's session** and prints the Verify table.

### B. Each agent machine
6. Log on as an administrator and run **one line**:
   `powershell -NoProfile -ExecutionPolicy Bypass -File \\<controller>\TestAgentSetup$\Install-Agent.ps1`
   The script first copies the package to `C:\TestAgentSetup\<version>\` and unblocks it. Running from the share
   breaks on machines with a restricted execution policy or Mark-of-the-Web.
7. Answer the `Get-Credential` prompt for the run-as account (`Agent.RunAsUser`) and confirm auto-logon.
8. Installer: .NET if missing -> files -> `appsettings.Local.json` (`AgentName` = computer name, `AgentEndpoint` =
   `http://<agent>:<port>`, `ControllerAddress` = `http://<controller>:<grpc port>`) -> firewall rule for the
   agent port -> scheduled task + restart policy -> starts the task -> Verify: task Running, port listening,
   agent registered on the controller.

### C. Prove it works
9. On the controller: `Install.cmd -Verify` prints the table below. Every FAIL line names the fix.
10. In the WPF app: copy a sample parameter file and the install-only WatchList, rename them for your release,
    run **Check only** (pre-flight), then run the pipeline.

### Verify table (read-only, safe to run any time)
| Check | Controller | Agent |
|---|---|---|
| Prerequisites | .NET Desktop + ASP.NET >= 10.0.12, Hosting Bundle, IIS | .NET Desktop >= 10.0.12 |
| Files | binaries match `SHA256SUMS.txt`; `appsettings*.json` parse | same |
| Process | controller running (5100 + 5200 listening) — reported as "start the controller" if not, never auto-killed | task "TestAgentGrpc Interactive" Running, 5200 listening |
| Web | `/healthz/live`, `/healthz/ready` 200; SPA `index.html` references a bundle that returns 200 | — |
| Wiring | each agent in team-setup.json reachable and registered | controller 5100 reachable |
| Pipeline | sample pipeline passes pre-flight | — |
| Disk | >= 10 GB free; crash dumps in `Logs\` reported | same |

### Upgrade (`Install.cmd -Upgrade`)
- Refuses while the controller runs: it asks the user to **Exit from the tray** and waits for 5100/5200 to close.
- Uses `Invoke-Patch.ps1` semantics: copies only files in the new payload, backs up every file it overwrites,
  SHA256-checks every copy, and records `DEPLOYED_COMMIT.txt` plus a rollback stamp. `appsettings.json` is
  replaced; everything in `fleet-inventory.json` `preserveFiles`/`preserveFolders` is untouched
  (WatchList.xml, orchestrator.db with -wal/-shm, Parameters\, Logs\, impact-*.db, *.bat, Config\, appsettings.Local.json).
- Web tier: **stop** the app pool (a recycle or `app_offline.htm` does not release the DLL lock), copy, start,
  then health-check.
- Agents: the scheduled task is ended before copying and started after; same preserve list.
- Automatic rollback when the offline Verify checks fail. The online checks need the controller started, so a failure
  there prints the exact rollback command instead of rolling back silently.

### Uninstall (`Install.cmd -Uninstall [-Purge]`)
Removes the scheduled tasks, firewall rules, IIS site and pool, shortcuts and agent share. Keeps all data
(WatchList, DB, Parameters, Logs) unless `-Purge`. Never removes .NET or IIS (other software may use them).

### Hard rules for every part
- Never kill the controller, never install Windows updates, never disable the firewall or UAC.
- Install log at `C:\TestAgentSetup\logs\install-<date>.log`, secrets redacted.
- Scripts must run on **Windows PowerShell 5.1**:
  - ASCII-only source (non-ASCII has broken deploy-script parsing before).
  - Pester 3.4 syntax (`Should Be`).
  - Never use `$pid`, `$pwd`, `$host` or `$input` as variable names.
  - Absolute paths with any `[IO.File]` call.
- Every step is idempotent: a second run changes nothing and says so.
- No real machine names, accounts or secrets in samples or docs. Sample batch files must never `echo %*`
  (that leaked a vCloud password into the controller log once).

---

## Copilot prompts (one part at a time; stop after each; basic unit tests per our current mode)

Each part ends with: build green, tests listed, new issues appended to `docs/issues/BACKLOG.md`, one commit.
Parts 3+ are tried on AUT1 (or a spare VM) only — never JVGR22 until the user says so.

### Part 0 — Investigate (no changes)
```
Investigate how TestAgentSolution is installed today. Change nothing. Report with file:line evidence:
1. For each existing setup/deploy script (Utilites/TestAgent_SetupScripts/*.ps1, deploy/Invoke-Patch.ps1,
   deploy/Invoke-FleetDeployment.ps1, deploy/fleet-inventory.json, deploy/Invoke-LogCleanup.ps1): its
   parameters, what it changes on the machine, whether it is idempotent, whether it can run non-interactively,
   and any hard-coded machine name, path, share or credential.
2. Which existing scripts Install.ps1 can call as-is, which need a parameter added, and which must be split.
3. Every appsettings*.json key for controller, WebApi (incl. appsettings.Production.json) and agent: default,
   where it is read, required/optional, product default vs team value. Flag secrets.
4. How configuration is loaded in each host (TestControllerGrpc/App.xaml.cs, TestAgentGrpc/Program.cs,
   TestController.WebApi/Program.cs) and the smallest change that adds appsettings.Local.json LAST.
5. How the first RBAC administrator is created on a fresh orchestrator.db, and how Default -> Secured is switched.
6. Agent registration: what the controller needs to know about an agent (roster in appsettings vs
   self-registration) and which agent keys must be unique per machine.
7. Publish facts per project: framework-dependent vs self-contained, size, native files under runtimes\.
8. The exact prerequisite installers needed offline (Desktop Runtime, Hosting Bundle) and their silent switches.
Propose: the final package layout, the list of scripts for scripts\common vs scripts\samples\WAS, and any
correction to this document. Stop.
```

### Part 1 — Settings split (code change, ships to AUT1 before any installer work)
```
Load appsettings.Local.json LAST in the controller, the WebApi (after appsettings.{Environment}.json) and the
agent; optional, reloadOnChange where the host already reloads. Missing Local file = today's behaviour exactly.
Add tools/Split-TeamSettings.ps1: given an installed appsettings.json and the shipped defaults, write only the
differing leaves to appsettings.Local.json (backup first), then prove by a recursive leaf diff that the effective
config is unchanged ("lost or changed: 0"). Dry run by default, -Apply to write.
Add docs/APPSETTINGS-REFERENCE.md: one table per app - key, default, meaning, team value yes/no, secret yes/no.
Basic tests: Local overrides a default; missing Local works; Split-TeamSettings on a sample pair loses nothing.
```

### Part 2 — team-setup.json schema + validator (pure, no machine changes)
```
Add installer/TeamSetup.psm1 with Read-TeamSetup and Test-TeamSetup (PowerShell 5.1, ASCII only).
Validate every field with one clear message per problem: machine names resolvable, ports 1-65535 and distinct,
paths absolute, agent list non-empty and without the controller, no field that looks like a secret
(password|secret|token|pat|key), Features keys are real FeatureKeys names.
ConvertTo-LocalSettings maps team-setup.json to the appsettings.Local.json leaves for each role (controller,
web, agent), using the key names found in Part 0.
Ship installer/team-setup.sample.json with placeholders only.
Basic Pester 3.4 tests: valid sample passes; a password field and a duplicate port each fail with their message.
```

### Part 3 — Verify (read-only, useful on today's machines)
```
Add installer/Install.ps1 with -Verify only (other switches come later), plus Install.cmd that self-elevates
and calls it with -ExecutionPolicy Bypass. Verify builds the PASS/FAIL table in this document, reusing
Test-NodeConnectivity.ps1 for network checks. Each FAIL prints its fix. Exit 0 = all PASS, 1 = any FAIL.
It must never start, stop or kill anything. "Controller not running" is a FAIL with the fix
"start TestControllerGrpc.exe", not an action.
Run it read-only against AUT1 and report the table. Basic tests: a mocked closed port gives FAIL with its fix.
```

### Part 4 — Build the release ZIP
```
Add tools/Build-Release.ps1:
- npm ci + npm run build in TestController.WebClient, copy dist into the WebApi publish wwwroot, and assert
  index.html references the new bundle (Invoke-Patch -IncludeSpa does NOT build the SPA - do not rely on it).
- dotnet publish controller, WebApi, agent in Release (framework-dependent, keep runtimes\ as published).
- Copy scripts\setup (the existing setup scripts), scripts\common, scripts\samples\WAS, samples, prereqs, docs.
- Write VERSION.txt (version + git SHA; refuse a dirty tree) and SHA256SUMS.txt.
- Fail if any sample or script contains a password-looking value, a real machine name from
  deploy/fleet-inventory.json, a WatchItem tag over 36 chars or containing / or +, or an `echo %*`.
- Fail if any .ps1 in the package is not ASCII or does not parse under PowerShell 5.1.
Basic test: the ZIP has the expected layout and SHA256SUMS verifies.
```

### Part 5 — Install controller + web tier
```
Install.ps1 -Role Controller [-WhatIf]: validate team-setup.json (Part 2), then orchestrate - do not
re-implement - Prepare-ControllerNode.ps1 (prereqs, IIS, firewall), Deploy-WebToIIS.ps1 (site/pool on Web.Port,
WebSocket section unlock, pool identity from Web.AppPoolAccount with a prompted password), copy binaries,
write appsettings.Local.json + Config\Features.json (backup, merge, never overwrite), copy samples into
Parameters\Samples\ (never over a real file), share the agent package as TestAgentSetup$ (read-only for
Authenticated Users), Start-menu shortcut + optional interactive logon task for the controller (NOT a service),
daily Invoke-LogCleanup task, optional masked prompts for ADO_PAT / RCLOUD_* as Machine env vars (then iisreset).
Finally start TestControllerGrpc.exe in the logged-on session and run -Verify.
Idempotent: a second run reports "no change" per step. -WhatIf prints every step without changing anything.
Basic Pester tests (mocked): second run is a no-op; Local merge keeps existing keys; no secret reaches the log.
```

### Part 6 — Install agent
```
installer/Install-Agent.ps1 (published on the TestAgentSetup$ share): copy the package locally and Unblock-File
first, then orchestrate Prepare-AgentNode-Full.ps1 v1.1 (.NET, run-as account via Get-Credential, scheduled
task "TestAgentGrpc Interactive", optional auto-logon) and Configure-AgentTaskRecovery.ps1. Write
appsettings.Local.json with AgentName = computer name, AgentEndpoint = http://<agent>:<port>,
ControllerAddress = http://<controller>:<grpc port> (merge, never overwrite). Firewall rule for the agent
port only. Start the task, then Verify: task Running, port listening, agent registered on the controller.
Basic Pester tests (mocked): controller name required; existing agent config merged, not overwritten;
AgentName is the computer name.
```

### Part 7 — Upgrade + Uninstall
```
-Upgrade: refuse while the controller runs (ports 5100/5200 open) and tell the user to Exit from the tray;
never kill it. On first upgrade of a pre-split install run Split-TeamSettings -Apply and require "lost: 0".
Then apply Invoke-Patch.ps1 semantics (payload-only copy, backup of every overwritten file, SHA256 check,
DEPLOYED_COMMIT.txt + rollback stamp) with the preserve list read from fleet-inventory.json.
Web tier: stop app pool, copy, start, health-check. Agents: end task, copy, start task.
Offline Verify failure -> automatic rollback; online failure -> print the rollback command.
-Uninstall: remove tasks, firewall rules, IIS site/pool, share, shortcuts; keep data unless -Purge.
Basic Pester tests (mocked): upgrade refuses while 5100 is open; preserved files untouched; rollback restores.
```

### Part 8 — SETUP-GUIDE.md (for other teams)
```
docs/SETUP-GUIDE.md for someone who has never seen the product (also shipped in the ZIP):
1. What you get: controller (desktop app), web site, agents - one picture with ports 5100/5200/81.
2. Before you start: machines, run-as account for agents (and auto-logon), admin rights, ports, disk space,
   where builds live (UNC share -> web pool account).
3. Fill team-setup.json (every field explained; no secrets).
4. Install the controller (the steps in "Step by step" above, with expected output).
5. Install each agent (one line).
6. First pipeline: copy a sample parameter file + install-only WatchList, rename for your release, Check only,
   run it.
7. Turn features on/off (Config\Features.json).
8. Your own batch files: where to put them, how a step calls them, tokens [_Name] only, SecretEnv for credentials.
9. Switch to Secured mode and create the first admin (from Part 0).
10. Upgrade, uninstall, where logs are, top 10 problems (link TROUBLESHOOTING.md).
Every command copy-pasteable. Example machine names only.
```

### Part 9 — Prove it on a clean machine
```
Write and run a checklist on a spare VM reverted to a clean snapshot (not JVGR22):
install controller + one agent from the ZIP using ONLY SETUP-GUIDE.md, run the sample install-only pipeline,
then -Verify, -Upgrade to a newer build, -Uninstall. Record every place the guide was unclear or a step needed
knowledge not in the guide, fix the guide, and repeat until a clean pass.
```

---

## What changed from the first draft (review 2026-11-10)

| # | First draft | Reality (evidence) | Change |
|---|---|---|---|
| 1 | Controller and agent "registered as a service" | Controller is a WPF tray app launched by a person; agent runs from the scheduled task "TestAgentGrpc Interactive" because UI tests need a desktop (`Prepare-AgentNode-Full.ps1`, `Setup-AgentNode.ps1`) | No services. Shortcut/logon task for the controller, scheduled task for agents |
| 2 | `WebPort: 5000`, one web port | Web tier is an IIS site on 81 that proxies to the controller's embedded API on 5200; agents listen on 5200 | Three roles and four ports spelled out; Web section in team-setup |
| 3 | WebApi + WebClient inside `controller\` | Separate IIS deployment with its own pool, Hosting Bundle and WebSocket unlock (`Deploy-WebToIIS.ps1`) | Separate `web\` payload and install step |
| 4 | Installer "reuses Prepare-* logic" loosely | Working scripts exist for prereqs, IIS, agent task, recovery, verify, patch, preserve list | Install.ps1 orchestrates the existing scripts; Part 0 confirms each |
| 5 | Settings split as a fresh-install feature | No host loads a Local file today, and existing installs keep team values inside `appsettings.json` | Part 1 adds the loader AND a one-time extraction with a "lost: 0" proof |
| 6 | Ship `samples\Features.json` all ON | Missing Features.json already means all ON (`FeatureGate`) | Write `Config\Features.json` only to switch features off |
| 7 | `Admins: [DOMAIN\user]` | RBAC admin bootstrap is an in-app step; mapping to domain users is unverified | Removed; Part 0 must establish it, guide explains Secured mode |
| 8 | Agent runs `Install.cmd` straight from the share | Restricted execution policy and Mark-of-the-Web break that (seen on jvkpri) | Copy locally + Unblock-File first; `-ExecutionPolicy Bypass` |
| 9 | Upgrade "replace binaries" | Overwriting/mirroring has deleted live state before (natives, impact DB); controller must not be killed | Reuse Invoke-Patch semantics + fleet-inventory preserve list; refuse while running |
| 10 | Rollback whenever Verify fails | Controller is a desktop app, so online checks need it started by a person | Split offline (auto-rollback) and online (print rollback) checks |
| 11 | Secrets: "service logon / LSA" | Product secrets are Machine env vars; IIS needs iisreset to see them; web pool identity cannot read UNC shares | Masked prompts -> Machine env + iisreset; Web.AppPoolAccount option |
| 12 | No platform constraints | PowerShell 5.1: non-ASCII breaks parsing, Pester 3.4, reserved variable names | Listed as hard rules; build fails on non-ASCII .ps1 |
| 13 | Verify last, after install | Verify is read-only and useful on JVGR22/AUT1 today | Verify built first (Part 3) and reused by install/upgrade |
| 14 | SPA built "somewhere" in the ZIP build | `Invoke-Patch -IncludeSpa` ships the git-tracked wwwroot and does not build | Build-Release runs npm build and asserts the bundle |
