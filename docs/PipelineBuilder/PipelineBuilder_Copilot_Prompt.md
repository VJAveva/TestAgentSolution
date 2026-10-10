# Copilot Prompt — Pipeline Builder (generic authoring, WPF + Web)

> Requirement: `PipelineBuilder_Requirement.md`. Mockup: `PipelineBuilder_Mockup.html`.
> Read the real code first; reuse the WatchList model + serializer + validator, the agent registry,
> `AgentResolver`, ParameterResolver, pre-flight and the existing per-release configs. Show the design before
> coding. **Generic by design: hard-code NO use case, product, release or agent.** One engine in Core; WPF and
> Web are thin screens. Decisions are resolved (see each step). Do the steps in order.

---

## Step 1 — Fix the parallelism cap (standalone)

```
MaxParallelDegree is a private const 50 applied per ExecuteChildrenAsync call, so two concurrent 50-node
stages put 100 actions in flight. Make it a configurable value (appsettings / controller config) enforced
GLOBALLY across concurrent stages, not per call. Default to today's 50. This is a correctness fix independent
of the builder.
Tests: two concurrent fan-out stages never exceed the global cap; default unchanged at 50.
```

## Step 2 — A1: Catalog derivation (read-only, no new data)

```
Add a read-only TargetCatalog derived from the Global layer of the existing per-release pipeline-config.json
files (_Installer, _Response, _BinariesFolder, _SetupFolder, _InstallFolder, _Product, _OrgName). Do NOT author
a new catalog file - that would be a second source of truth that drifts from what the executor reads.
Output: a list of releases/targets with their resolved paths, for the builder to read.
Tests: the catalog reflects exactly what the configs contain; adding a release config surfaces it with no code change.
```

## Step 3 — A2: Recipe model + PipelineBuilderService.Preview (pure, no writes)

```
Add PipelineBuilderService to TestControllerGrpc.Core. GENERIC - no built-in use case/product/release/agent.
Reuse the WatchList model, serializer, validator, AgentResolver, ParameterResolver, registry and the derived catalog.

1. Recipe model (data; files teams add, no rebuild):
   Recipe = { Name, Stages[ { Name, TemplateId, AgentMapping } ] }.
   AgentMapping enum: single | pool-fanout | ordered-first-then-rest. Discover recipes from a folder at runtime.
   Ship ONE example recipe only - prove a team adds a second by dropping a file, zero code change.

2. Templates: the builder MINTS its own AGENT-GENERIC templates using [_AgentN] tokens (values come from the
   profile via AgentResolver). It does NOT reuse or migrate the live hostname-baked Sanity templates (decision P1).

3. Preview(BuilderRequest) -> BuilderResult, NO writes:
   BuilderRequest = { Release, Recipe, Profile OR explicit agent list (0..N, 50+ allowed) }.
   BuilderResult =
     - <Release>-<Recipe>-pipeline-config.json content (paths from the derived catalog; Profile from the chosen
       agents; NO constants/build),
     - the WatchList item (WatchItem + Initialize -> that config + the recipe's stages + result email),
     - the agent-generic templates it minted,
     - resolved preview (tokens via ParameterResolver, secrets masked).

4. Fan-out = emit CONCRETE nodes (decision P2): pool-fanout -> one Parallel group of N concrete nodes from the
   agent-generic template; ordered-first-then-rest -> first node, then a Parallel group for the rest. Honor the
   Step-1 global parallelism cap. Ref-reuse does not apply to fan-out stages.

5. Tag rules (decision P7): generate a Tag in charset [A-Za-z0-9._-] (no '+' or space), within a length budget
   (<=36 unless the PipelineId column is widened first); keep a separate human Title. Tag is immutable after create.

Tests: an arbitrary recipe (not the built-in) + a release + agents produces a config + WatchList item +
agent-generic templates that pass the validator; pool-fanout over 50 agents emits a correct Parallel group under
the global cap; Preview writes nothing; generated tag passes the charset/length rules. Show the recipe schema +
BuilderRequest/Result + agent-mapping modes FIRST.
```

## Step 4 — A3: Validate + Create + merge/import

```
Add Validate() and Create() to PipelineBuilderService.

1. Validate (SPLIT gate, decision P5): block on structure + settings + token-definedness (what authoring controls).
   Render build-dependent pre-flight (Controller files / Install sources probing [_DropLocation], [_SetupFolder],
   [_BinariesFolder]) as INFORMATIONAL "verified at run time" - a newly authored pipeline has no build by design
   (build comes from GlobalVariables/trigger), so gating on it would keep Create disabled forever.

2. Create(request, targetDir) writes ONLY after Validate passes: the new config under Parameters\, and a WatchList
   fragment to import (atomic writes; keep .bak). Do NOT mutate the open/live tree.

3. Merge/import conflict rules (P4): on import, detect duplicate Tag, duplicate Template ID, and - the dangerous
   case - an incoming Template whose ID matches an existing one but whose CONTENT differs. Never silently reuse
   that (it would repoint live pipelines); surface it as a conflict for the user to resolve.

4. Trigger (decision P8): Create the trigger folder; set Path + a Filter that does not collide with another
   WatchItem sharing that folder. A WatchItem whose Path does not exist fails the run - folder creation is part of Create.

Tests: Create blocked on structure/settings error but NOT on absent build; import flags Tag/TemplateID/content
conflicts; a content-differing template with a matching ID is never silently merged; trigger folder is created and
filter collisions are detected.
```

## Step 5 — RBAC authoring permission (append-only)

```
Add a new Permission for authoring/editing WatchList content (create via the builder). Append-only; mirror it in
the four places the enum is wired, and update the exact per-role counts in capabilities.test.ts. Govern Create
with the new permission; running stays under the existing run permission.
```

## Step 6 — B/C: WPF panel, then React (thin over the engine)

```
B (WPF): "New Pipeline" panel over PipelineBuilderService, 4 steps (Target, Recipe, Agents, Review & Create),
matching PipelineBuilder_Mockup.html. Agent picker scales to 50+ (virtualized, search, filter by pool/status,
select-all-in-pool). Step 4 shows the resolved tree + files to write + the split pre-flight; Save/Import disabled
until the authoring gate is green. Write to disk + fragment to import; do not mutate the open tree.

C (Web): POST /api/pipeline-builder/preview and /create on the controller (secrets masked); React screen mirrors
the WPF flow. Identical output to WPF for the same inputs.

Tests: building any recipe needs no manual path/agent typing; 50-agent pool previews correctly; Save blocked on
authoring error only; Web and WPF produce identical files for the same request.
```

## Notes for you

- Nothing touches a live production pipeline before the builder is proven - it never reuses the hostname-baked templates.
- 50 is the AUTHORING target now; running 50 agents (locks, Fleet UI) is a separate hardening track - do not block on it.
- Ship one example recipe; prove a second is added by dropping a file.
