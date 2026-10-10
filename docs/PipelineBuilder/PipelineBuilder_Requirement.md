# Requirement: Pipeline Builder (generic guided authoring, WPF + Web)

> Status: requirement + scope, decisions resolved against the live-system review. Mockup: `PipelineBuilder_Mockup.html`.
> A platform feature for **all product teams** — not specific to Smoke / WARM / Sanity. Those are example
> recipes. The engine is generic over any use case, any release/target, and any number of agents.

## 1. Purpose

Let any team build any pipeline from a few picks — the WatchList item, its `pipeline-config.json`, the
templates, and the Global Variables wiring — with no hand-edited XML/JSON and no typed paths or agent names.
Deterministic (recipe-driven), validated before it writes. The engine hard-codes nothing about a product,
use case, release or agent.

## 2. What already exists and is reused (from the code review)

| Block | Reuse |
|---|---|
| WatchList model + serializer | `SerializeWatchItemsToXml(items, referencedTemplates)` + `CollectRefTemplateIds` already emit a self-contained fragment |
| Validator | `WatchListValidator.Analyze` works on an in-memory config |
| Pre-flight | `PreflightRequest.Config` takes an in-memory `WatchListConfig` — no live-file dependency |
| Agent registry | `AgentRegistry.GetAll()` + `PreflightAgentFacts` (online/busy/locked/maintenance/disk/reboot) |
| Ref → template | `AgentResolver.CollectFromNodes` expands Refs with a cycle guard |
| Token resolution | `AgentResolver.ResolveVariable` resolves `[_AgentN]` from profile — present, just unused for agent names today |

So the Builder is mostly assembly over existing parts.

## 3. Generic building blocks (all data, not code)

| Concept | What it is |
|---|---|
| **Recipe** | A named, ordered list of **stages**; each stage maps to a template and an agent mapping (`single` / `pool-fanout` / `ordered-first-then-rest`). Teams add recipe files — no rebuild. |
| **Template** | A reusable, **agent-generic** action group (uses `[_AgentN]` tokens; the builder generates these for new pipelines — see §9 P1). |
| **Target / Release catalog** | **Derived read-only** from the Global layer of the existing per-release `pipeline-config.json` files (`_Installer`, `_Response`, `_BinariesFolder`, …). No new file. |
| **Profile / Pool** | A named set of agents the profile supplies as `_Agent1.._AgentN`. |
| **Global Variables** | Environment constants + active build (separate feature, already built). |

## 4. What the user does (4 steps)

1. **Target / Release** — from the derived catalog.
2. **Recipe** — any recipe the team defined (N possible).
3. **Agents** — a Profile/pool, or multi-select from the live registry. Scales in authoring to 50+ (search, filter, select-all-in-pool, status badges).
4. **Review & Create** — resolved tree + exact files to write; split pre-flight (see §9 P5); Create.

## 5. What it generates (for any recipe)

- **`<Release>-<Recipe>-pipeline-config.json`** — target paths from the derived catalog; Profile from the chosen agents; no constants/build (those come from GlobalVariables / trigger).
- **The WatchList item** — `WatchItem` + `Initialize` + the recipe's stages + result email.
- **Templates** — the builder mints its own **agent-generic** templates (`[_AgentN]`); a `pool-fanout` stage is expanded into **N concrete nodes** in a Parallel group at Create time.
- **Trigger** — creates the folder and sets `Path` + a collision-free `Filter` (`<Recipe>.txt`).
- **Global Variables** — constants pulled in; build left to `GlobalVariables.json` / trigger.

## 6. Scale: authoring vs running (decided)

- **Authoring any N is in scope now** — fan-out handles 50 nodes; no hard cap in the builder.
- **Running 50 agents is a separate hardening track** (50 locks, Fleet UI, parallelism) — the fleet is 9 today; not a builder blocker.
- **Fix now regardless:** `MaxParallelDegree` is a private `const 50` applied per call, so two concurrent 50-node stages put 100 in flight. Make it configurable and globally enforced — a correctness bug even at 9 agents.

## 7. Multi-team

- Recipes, templates and profiles are **data files a team owns**, loaded from a shared location — add a file, no deploy.
- **RBAC** governs create vs run (new append-only authoring permission; mirror in the four places + update `capabilities.test.ts` counts).
- Teams see their own; shared recipes are opt-in.

## 8. Write safety

- Phase 1 **writes to disk** (new config under `Parameters\` + a WatchList fragment to import). Live files untouched until import.
- **Merge/import** needs conflict rules: duplicate Tag, duplicate Template ID, and the dangerous case — an incoming Template whose ID matches an existing one but whose content differs (never silently reuse; that repoints live pipelines).

## 9. Resolved decisions (from the review)

- **P1 — templates:** the builder **generates its own agent-generic templates**; it does **not** migrate the 3 shared live Sanity templates. That risky migration is off the critical path (do it later, proven byte-identical, only if wanted).
- **P2 — fan-out:** one agent-generic template as source → builder **expands to N concrete nodes**. Ref-reuse does not apply to fan-out stages (inherent).
- **P3 — catalog:** **derived read-only** from existing per-release configs. No second source of truth.
- **P5 — Create gate (split):** block on **structure + settings + token-definedness**; render **build-dependent** checks (`_DropLocation`, `_BinariesFolder`) as informational "verified at run time".
- **P7 — Tag:** safe charset `[A-Za-z0-9._-]` (no `+`/space), length budget, **immutable** after creation; keep a separate display Title. Reconcile the false contract (widen `PipelineId` max to ~64).
- **P8 — Trigger:** create the folder; detect Filter collisions in a shared folder before writing.

## 10. Phasing (revised)

| # | Scope | Why here |
|---|---|---|
| 1 | Fix `MaxParallelDegree` → configurable + global cap | Standalone correctness bug |
| 2 | **A1** Catalog derivation (read-only) | Kills P3 with no new data |
| 3 | **A2** Recipe model + `Preview()` (pure, no writes) | The correctness surface; zero risk |
| 4 | **A3** `Validate()` + `Create()` + merge/import conflict rules + tag/trigger guards + split gate | Resolves P4/P5/P7/P8 |
| 5 | RBAC authoring permission (append) | P6 |
| 6 | **B / C** WPF panel, then React over the same engine | Thin by construction |

Nothing touches a live production pipeline before the builder is proven, because the builder never reuses the hostname-baked templates.

## 11. Out of scope (for now)

- AI free-text drafting (can wrap the recipe engine later).
- Editing an existing pipeline in place.
- Migrating the existing shared templates to agent-generic (separate, proven task).
- Running 50 agents at runtime (separate hardening track).
