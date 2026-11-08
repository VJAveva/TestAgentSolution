# Action plan — React Pipeline Builder and Golden-image refresh

**Order:** Feature toggles (in progress) -> **React Pipeline Builder** -> **Golden-image refresh**.
Builder first: the backend already exists, low risk, every team benefits. Golden image second: it touches
the VM fleet and snapshots, so it needs more care and a rollback path.

Each phase = one Copilot prompt below. Investigate first; stop after each phase; tests red-proofed;
build/test with `-c Release`; report controller + WebApi + WebClient test counts.

## Decisions (2026-11-08) - these override the phase prompts below where they differ

**Builder**
- Save writes a config + WatchList **fragment** only, as WPF does; never the live WatchList. Fragments are never
  overwritten. Every create is audited. The user imports the fragment with the existing import option.
- Four steps, matching WPF (Target, Recipe, Agents, Review). No step editing in the Builder; that comes later via
  the XML editor's "Ask to change" (Prompt_XmlEditor_Intelligence Part 2).
- Validate = the builder's own checks. Pre-flight needs a pipeline in the live WatchList, so it runs after import.
- Every response masks secret values as `***` using the parameter grid's key list; Create rebuilds on the server
  and never sends a secret back.
- A1 status: **done** - see [../PipelineBuilder/README.md](../PipelineBuilder/README.md).

**Golden image**
- Snapshots keep a person in the loop: the pipeline does revert, stack update, health check and a dry run; an admin
  clicks **Promote to golden** with their own vCloud credentials (used once, never stored). Removing an old snapshot
  needs the same approval. Full automation only after the plain-text vCloud passwords are rotated and a dedicated,
  limited service account exists.
- vCloud keeps ONE snapshot per VM (Create replaces it), so "keep the previous / last 2" needs a vCloud-side answer
  (VM copy or catalog template) before B3 retention can be designed.
- Build on the existing `IVirtualizationProvider` / `Vm-Ops.vcloud.ps1` and `GoldenImageRefreshOperation`; no second
  snapshot service.
- Fleet "Install Windows updates" is **disabled by policy** (server refuses, button hidden); code kept for now.

---

## A. React Pipeline Builder

**Goal:** the same Pipeline Builder as WPF, in the web client. No second rule set: the web calls the same
`PipelineBuilderService` (Preview / Validate / Save) through the WebApi.

| Phase | What | Done when |
|---|---|---|
| A0 | Investigate | Gap report: what WPF Builder has, what WebApi exposes, what React has |
| A1 | WebApi endpoints | Recipes, Preview, Validate, Save behind `Pipeline_Author` (admin) + `PipelineBuilder` feature gate |
| A2 | React wizard | 5 screens working end to end against a test controller |
| A3 | Parity + polish | Same output XML as WPF for the same inputs; accessibility; docs |

### A0 — Investigate (no changes)
```
Investigate the Pipeline Builder. Do not change code.
Report with file:line:
1. PipelineBuilderService and its view model (Steps 1-5): inputs, recipes, AgentMapping modes
   (single / pool-fanout / ordered-first-then-rest), Preview, Validate, Save. Is the WPF XAML done?
2. Which WebApi endpoints exist for it today, and their auth (Pipeline_Author admin-only server-side?).
3. What React has: routes, components, API client, shared components we can reuse
   (agent picker, parameter grid, XML diff, pre-flight results).
4. Gaps for a React version that produces byte-identical WatchList XML to WPF for the same inputs.
Propose endpoint contracts (request/response JSON) and the screen list. Stop.
```

### A1 — WebApi endpoints
```
Add (or complete) Pipeline Builder endpoints. Thin controllers over PipelineBuilderService - no logic in controllers.
  GET  /api/pipeline-builder/recipes
  GET  /api/pipeline-builder/context      (agents+status, pools, templates, parameter files + profiles)
  POST /api/pipeline-builder/preview      -> { xml, plan[], problems[] }
  POST /api/pipeline-builder/validate     -> validator + pre-flight results (same as WPF)
  POST /api/pipeline-builder/save         -> writes WatchList (backup first), audit "<user> created <tag>"
All: [Authorize(Pipeline_Author)] enforced server-side + [RequireFeature("PipelineBuilder")].
Save never triggers or runs a pipeline. Unique Tag (<=36 chars, safe charset) and trigger Path+Filter clash checked.
Tests: non-admin 403 on every endpoint; feature off 404; preview = WPF output for 3 recipes;
save creates backup + audit; duplicate tag/trigger rejected. Red-proof each.
```

### A2 — React wizard
```
Build the Pipeline Builder in TestController.WebClient as a 5-step wizard, admin-only in nav:
 1. Recipe     - pick recipe (Install only, Sanity Smoke E2E, WARM setup, WARM tests, custom)
 2. Release    - release, use case -> parameter file <Release>-<UseCase>-pipeline-config.json + Profile dropdown
 3. Agents     - mapping mode (single / pool fan-out / ordered first-then-rest) with live agent status
 4. Steps      - review steps, timeouts, FailAndContinue; add mail; tokens shown with resolved value + layer
 5. Preview    - XML preview, problems list, pre-flight result; Save (blocked on errors)
Rules: all logic from the API (no client-side rule copies); bracketed text only for [_Tokens];
secrets masked; unsaved-changes guard; works at 1280px and on a laptop screen.
Tests (component + one end-to-end against a test WebApi): each step, validation blocks Save,
non-admin cannot reach the route. Red-proof.
```

### A3 — Parity and docs
```
1. Golden tests: same inputs in WPF and React produce identical XML (3 recipes, 3 mapping modes).
2. Accessibility: keyboard-only flow, labels, contrast in light and dark.
3. Update docs/features/PipelineBuilder/README.md (code paths, how to test, last-verified).
```

---

## B. Golden-image refresh

**Goal:** agents revert to a base snapshot ("golden image"). Today the update-install path works, but the
**snapshot step is missing** (FLEET-UPDATE-SNAPSHOT.md), so every revert goes back to an old image and
re-does the same updates. Refresh = bring one node up to date, verify it, take a new snapshot, keep the old
one for rollback.

**Hard rules (from earlier decisions):**
- The tool does **not install Windows updates.** It updates only our stack: .NET runtime, TestAgentService,
  test prerequisites, agent config.
- Credentials only via SecretEnv / secret store - never on command lines, in logs or in docs.
- Never snapshot a node that failed health checks. Always keep the previous snapshot until the new one is proven.
- One node first (canary), then the pool.

| Phase | What | Done when |
|---|---|---|
| B0 | Investigate | How revert/snapshot work today (RevertRcloudMachine.bat, vCloud API), what the update path does |
| B1 | Snapshot service | Create / list / delete-old snapshot via vCloud API, dry-run mode, audit |
| B2 | Refresh pipeline | Revert -> update stack -> health check -> snapshot -> verify revert, on ONE node |
| B3 | Pool rollout + rollback | Canary then pool; one-click rollback to previous snapshot |
| B4 | Schedule + docs | Optional monthly schedule (off by default), runbook |

### B0 — Investigate (no changes)
```
Investigate golden-image handling. Do not change code.
Report with file:line:
1. How revert works today: RevertRcloudMachine.bat, the vCloud/rCloud API calls, which snapshot it reverts to,
   how credentials reach it (SecretEnv?).
2. What the update-install path in FLEET-UPDATE-SNAPSHOT.md does now, step by step, and what is missing.
3. Whether the vCloud API we use supports create snapshot / list / remove, and the permissions needed.
4. What a "healthy agent" check exists (agent online, gRPC reachable, .NET version, service running).
5. Risks: snapshot size/quota, a VM with only one snapshot slot (create replaces old?), timing.
Propose the design and which node to use as canary (suggest warmhist or jvhist). Stop.
```

### B1 — Snapshot service
```
Add ISnapshotService over the vCloud API: List, Create(name, description), Remove(name), Revert(name).
- Names: golden-<pool>-<yyyyMMdd>; description includes stack versions.
- Dry-run mode logs what it would do.
- Credentials from the secret store only; redact in logs.
- Audit every create/remove/revert with user and node.
- Never remove the last good snapshot; Remove refuses if it is the only one.
Tests with a fake vCloud client: create/list/remove, refuse-last, dry-run makes no calls, secrets never logged. Red-proof.
```

### B2 — Refresh pipeline on one node
```
Add a "Golden image refresh" template (WatchList) for ONE node:
 1. Revert to current golden snapshot
 2. Update stack only: .NET runtime, TestAgentService (Prepare-AgentNode-Full.ps1 -Controller [_ControllerName]
    -NoPrompt), test prerequisites. NO Windows updates.
 3. Health check: agent online, gRPC reachable, versions as expected, service running after reboot
 4. If healthy: Create snapshot golden-<pool>-<date>
 5. Verify: revert to the NEW snapshot, health check again
 6. If any step fails: revert to the previous snapshot, mark refresh FAILED, mail
 7. Mail: old -> new versions, snapshot name, duration
Pre-flight: snapshot quota, node idle, no pipeline using the node. Admin-only to run.
Tests: happy path (fake client); health fail -> no snapshot + rollback; verify fail -> rollback. Red-proof.
```

### B3 — Pool rollout and rollback
```
1. Pool refresh: canary node first; continue to the rest only if the canary passes B2 step 5.
   Nodes in parallel within limits; skip busy nodes and report them.
2. Rollback: one action "Roll back pool to previous golden snapshot".
3. Keep the last 2 golden snapshots per node; remove older only after the new one passes verify.
4. Dashboard: per node current golden snapshot, date, stack versions.
Tests: canary fail stops the pool; rollback restores previous; retention keeps 2. Red-proof.
```

### B4 — Schedule and docs
```
1. Optional scheduled refresh (monthly, OFF by default), only when the pool is idle.
2. docs/features/GoldenImage/README.md + operations runbook: how to refresh, roll back, what is updated,
   what is NOT (Windows updates).
3. Feature key "GoldenImage" in Features.json (default OFF for other teams).
```

---

## What you do (not Copilot)
1. Confirm the canary node (suggest **warmhist**: least used).
2. Ask the vCloud admin whether our account can create/remove snapshots and the quota per VM.
3. Confirm the stack versions you want baked in (.NET runtime, agent build).
