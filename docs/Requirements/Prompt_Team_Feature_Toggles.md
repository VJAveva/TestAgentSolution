# Prompt — Per-team feature toggles (hide Results, Code Churn, etc.) + per-team Code Churn branches

> Goal: share TestAgentSolution with other product teams. Each team installs its own controller and
> turns off features it does not use. Code Churn repos/branches become team config, not code.
> Paste the code-audit ground rules first. One part at a time. **Investigate first; stop after each part.**

**Design decision (do not re-open):** one `Features.json` per controller (= per team install), admin-only
to change, enforced on the server, hidden in WPF and web. Default = everything ON, so JVGR22 and AUT1
behave exactly as today.

---

## Part 0 — Investigate (no code changes)

```
Investigate how features are wired today in the WPF controller, the WebApi and the React client. Do not change code.
Report, with file:line:
1. Every user-facing feature area (e.g. Pipelines, Library, Agents, Pre-flight, Results / FIR, Code Churn,
   Impact mapping, Pipeline Builder, GlobalVariables, Reports, Admin). For each: WPF view/tab/menu,
   WebApi controllers/endpoints, React routes/nav items, background services/timers it starts.
2. Where Results/FIR assume C# / MSTest TRX (parsers, WASSet workbook, Run-WASTests).
3. Where Code Churn gets its repos, branches, paths and credentials (hard-coded? appsettings? which file:line).
4. Existing config loading and RBAC (Pipeline_Author admin-only) that a feature gate should reuse.
5. Cross-feature dependencies (e.g. FIR needs Results; pipelines call churn/impact) - what breaks if one is off.
Propose the feature-key list and the order. Then stop.
```

## Part 1 — Feature gate core (server is the source of truth)

```
Add per-controller feature toggles.

1. Config file C:\TestControllerService\Config\Features.json (path from appsettings, not hard-coded):
   {
     "TeamName": "WAS-QA",
     "Features": {
       "Results": true, "FirReport": true, "CodeChurn": true, "ImpactMapping": true,
       "PipelineBuilder": true, "GlobalVariables": true, "Reports": true
     }
   }
   Use the keys agreed in Part 0. Missing file or missing key = ON (no regression). Unknown key = warning in log.
   Pipelines, Agents, Library, Pre-flight and Admin are core - never toggleable.
2. IFeatureGate service (one implementation, shared by WPF host and WebApi): IsEnabled(key), All(),
   reload on file change (reuse the existing file-monitor pattern; keep last good copy on bad JSON and log it).
3. WebApi: [RequireFeature("CodeChurn")] attribute / endpoint filter on every endpoint of a gated feature
   -> 404 with problem detail "feature disabled on this controller". GET /api/features returns the map
   (no secrets) for the clients.
4. Background services/timers of a disabled feature do not start (e.g. churn polling, results indexer).
5. Dependencies: turning off Results also turns off FirReport (computed, shown as "off because Results is off").
Tests: missing file = all on; key false -> endpoint 404, service not started; dependency rule; bad JSON keeps
last good copy; red-proof each. Build/test -c Release; report controller + WebApi suite counts.
```

## Part 2 — Hide in WPF and React

```
Use IFeatureGate / GET /api/features to hide disabled features.
1. WPF: hide tabs, menu items, toolbar buttons, context-menu entries and dashboard tiles of disabled features
   (Visibility via a FeatureVisibility converter / binding, not code-behind per control). No empty gaps.
2. React: filter nav items and routes; a direct URL to a disabled route shows "This feature is turned off on
   this controller" (not a crash, not a blank page). Fetch /api/features once at start + on focus.
3. Pipeline steps that call a disabled feature: pre-flight warning naming the step and the feature.
4. Toggle change takes effect without restarting the controller (WPF refreshes; web on next fetch).
Tests: each gated feature hidden in WPF and web when off; direct URL message; pre-flight warning; red-proof.
Respect the Ui ratchets (DynamicResource, AutomationProperties.Name).
```

## Part 3 — Admin screen

```
Add Admin > Features (WPF + web), visible only to the admin role (same server-side check as Pipeline_Author).
1. List each feature: on/off switch, one-line description, "depends on" note.
2. Save writes Features.json (timestamped backup first, preserve unknown keys), shows "Saved at <time>";
   a failed save shows the reason - never silent.
3. Audit entry: "<user> turned <feature> off/on".
4. Non-admins: GET /api/features works (read-only); PUT returns 403.
Tests: admin save persists through reload + restart; backup created; non-admin 403; audit written; red-proof.
```

## Part 4 — Code Churn per team (repos and branches in config)

```
Move every hard-coded Code Churn repo/branch/path found in Part 0 into config.
1. Same Features.json (or CodeChurn.json next to it - pick one and say why):
   "CodeChurn": {
     "Repositories": [
       { "Name": "WAS", "Url": "<repo url>", "Branches": ["main", "release/SP2026R2"],
         "IncludePaths": ["src/"], "ExcludePaths": ["tests/"], "CredentialRef": "<secret name>" }
     ]
   }
   Credentials only by reference to the existing secret store / SecretEnv - never the value in the file or logs.
2. Churn UI: repository + branch dropdowns from config; nothing team-specific left in code.
3. Validation: unknown branch / unreachable repo -> clear message on the Churn screen and in the log.
4. Admin > Features gets a Code Churn section to edit repos/branches (admin only, backup, audit).
Tests: two repos with different branches; empty list shows "no repositories configured"; credential value never
logged or returned by the API; red-proof each.
```

## Part 5 — Package for other teams (docs only, no code)

```
Write docs/Team-Onboarding.md:
1. Install controller + agents (Prepare-ControllerNode.ps1, Prepare-AgentNode-Full.ps1 -Controller <name>).
2. Copy Features.json, turn off what the team does not use; Code Churn repos/branches.
3. Parameters folder layout and <Release>-<UseCase>-pipeline-config.json naming; GlobalVariables.json.
4. Non-C# teams: Results/FIR off by default; how to run their own runner via RunRemoteCommand.
5. What NOT to copy from our controller: any pipeline-config with real passwords, our Features.json,
   our logs/TRX (sanitize first).
Also produce a clean Features.sample.json with everything ON and placeholder repo values.
```

---

Stop after each part and report: what changed, test counts before/after (controller and WebApi suites),
anything found but not fixed.

**Later, separate decision (not in this prompt):** Results for non-C# teams (JUnit / NUnit / pytest XML parsers)
behind the same "Results" gate.
