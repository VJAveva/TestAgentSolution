# Copilot implementation prompts

### Azure DevOps ingest → Regression tab, in the order you should build it

Companion to `Azure-DevOps-Integration-Guide.md`. Work top to bottom. Each prompt has a gate — do not move on until it passes.

---

## Setup, once

**Put the reference material where Copilot can see it.** Commit these into `docs/impact/` in the TestAgentSolution repo:

- `Azure-DevOps-Integration-Guide.md`
- `component-usecase-map.v2.json`
- `ImpactModel-Standard-Structure.md`

Then `#codebase` prompts can read the schema you are targeting instead of inventing one.

**Create `.github/copilot-instructions.md`** with the primer below. Copilot loads it automatically for every request in the repo, which beats pasting context each session.

```markdown
# Project context

TestAgentSolution — distributed test execution and orchestration for AVEVA System Platform.

## Stack
.NET 10, C# 13, nullable enabled. ASP.NET Core, gRPC, SignalR, EF Core + SQLite, WPF, WinForms.
Azure DevOps org: AVEVA-VSTS. Projects: "AppServer OMI", "System Platform".

## Architecture keystone
Two front doors, one engine. TestControllerGrpc (WPF) and TestController.Api (web) both
consume TestControllerGrpc.Core through their own DI registrations. Shared interfaces live
in Core; each host supplies its own concrete implementations. There is no cross-process
singleton and no notification bridge between hosts.

## Hard rules
- No secrets in source, config files, or scripts. Ever. Credentials come from an injected
  provider that reads Windows Credential Manager, environment variables, or a certificate store.
- Azure DevOps REST api-version=7.1. ADO resource ID for Entra tokens:
  499b84ac-1321-427f-aa17-267ca6975798 (public constant, not a secret).
- Use HttpClient + System.Text.Json. Do NOT add Microsoft.TeamFoundationServer.Client.
- Every async method takes a CancellationToken.
- Log through IAppLogger with the existing correlation-id pattern.
- PowerShell must be 5.1 compatible with ASCII-only console output. JSON files are UTF-8 no BOM.
- WPF and web are separate. Never conflate them in code, comments, or docs.

## Impact model
The canonical data model is docs/impact/component-usecase-map.v2.json (schema 2.0).
Entities: Component, Area, UseCase, Suite, TestCase, Change. Every edge carries
`evidence` and `confidence` (assumed | declared | observed). Observed beats declared;
conflicts are logged, never silently overwritten.
```

**Three rules to repeat in every discovery prompt.** Copilot will confidently describe files that do not exist, and a wrong answer here propagates into everything downstream.

> 1. Cite file path and line number for every fact.
> 2. If something is not in the codebase, write `NOT FOUND`. Do not infer or fill from general knowledge.
> 3. No code in this response. Extraction only.

---

# Phase A — Discovery

Run all four before generating a single line. The answers determine whether the generated code fits your solution or fights it.

### A1 — Solution shape

> List every project in this solution: name, path, target framework, project type (library / WPF / ASP.NET Core / WinForms / test), and which projects reference which. Then show me the folder structure of `TestControllerGrpc.Core` two levels deep. Cite paths.

### A2 — The DI pattern I must match

> Find the shared dependency-injection registration in `TestControllerGrpc.Core`. Show the extension method in full. Then find one interface defined in Core that has a **different** concrete implementation registered by the WPF host and by the WebApi host — show the interface, both implementations, and both registration call sites. I need the exact pattern to copy.

### A3 — Existing HTTP, config and logging conventions

> Show me: every existing use of `HttpClient` or `IHttpClientFactory` in this solution; how configuration is read (`IOptions`, `IConfiguration`, static class); the `IAppLogger` interface with all members and one example call site showing correlation-id usage; whether Polly or any resilience library is already referenced; and the contents of `.editorconfig` and any `Directory.Build.props`.

### A4 — WPF shell and test conventions

> For the WPF project: what UI framework provides the ribbon in `WatchList Editor` — is it Fluent.Ribbon, MahApps.Metro, Syncfusion, DevExpress, or hand-rolled? Show the XAML that defines the HOME / EXECUTION / TOOLS / ADMIN tabs and the file it lives in. Is MVVM used, and if so which toolkit? Separately: which test framework do the test projects use, and show me one existing test class as a style reference.

**Gate:** you can name the DI extension method, the ribbon framework, the test framework, and whether Polly is present. If Copilot returned `NOT FOUND` for the ribbon, open the XAML yourself before continuing — Phase F depends on it.

---

# Phase B — Auth seam

### B1 — The interface and PAT provider

> Create `TestControllerGrpc.Core/Ado/IAdoTokenProvider.cs`:
>
> ```csharp
> public interface IAdoTokenProvider {
>     Task<AuthenticationHeaderValue> GetAuthHeaderAsync(CancellationToken ct);
>     string Describe();   // for logging: "PAT (env)", "Entra user", "Entra service principal"
> }
> ```
>
> Add `PatTokenProvider` implementing it. It reads the PAT from an injected `IAdoCredentialStore` abstraction — never from `IConfiguration` directly, never from a file in the repo. Header format is `Basic` with base64 of `":" + pat`. Provide two `IAdoCredentialStore` implementations: `EnvironmentCredentialStore` (reads `ADO_PAT`) and `WindowsCredentialStore` (Windows Credential Manager via DPAPI, WPF host only, guarded so it does not break on non-Windows builds).
>
> If the credential is missing, throw a specific `AdoCredentialMissingException` with a message naming exactly where to put it. Do not fall back to an empty string.
>
> No code in the WebApi or WPF projects yet. Core only.

### B2 — Entra providers

> Add `Azure.Identity` to `TestControllerGrpc.Core`. Create two more `IAdoTokenProvider` implementations:
>
> - `UserTokenProvider` — `InteractiveBrowserCredential` with `TokenCachePersistenceOptions` enabled so the engineer is not prompted on every launch. For the WPF host.
> - `ServicePrincipalTokenProvider` — `ClientCertificateCredential` preferred, `ClientSecretCredential` as a fallback constructor. For the WebApi host and scheduler.
>
> Both request scope `499b84ac-1321-427f-aa17-267ca6975798/.default`. Cache the token in memory and refresh when under five minutes from expiry — Entra tokens last one hour. Tenant ID, client ID and certificate thumbprint come from `IOptions<AdoOptions>`; the certificate is loaded from the Windows certificate store by thumbprint, never from a file path in config.
>
> Add XML doc on each explaining which host registers it and why.

**Gate:** `dotnet build` clean. `PatTokenProvider` unit test proves the header is `Basic` + correct base64. No secret appears in any tracked file — verify with `git grep -iE "pat|token|secret|password"` over the new code.

---

# Phase C — The client

### C1 — DTOs from real responses

> Using the JSON fixtures in `tests/Fixtures/Ado/` (captured from live Azure DevOps), create records in `TestControllerGrpc.Core/Ado/Dto/` for these responses: build list, build changes, build work items, work item batch with `$expand=relations`, repository list, commit changes, and test plan suites.
>
> Records with init-only properties, `JsonPropertyName` where the wire name differs from C# convention, nullable reference types honest to what the API actually omits. Use `System.Text.Json` source generation via a `JsonSerializerContext`. Model only the fields the fixtures contain — do not add speculative properties from the API documentation.

*If you skipped Stage 1 of the integration guide and have no fixtures, stop and go capture them. Generating DTOs from documentation instead of real responses is the single most common way this goes wrong — optional fields, casing, and nesting all differ from the docs in practice.*

### C2 — AdoClient

> Create `TestControllerGrpc.Core/Ado/AdoClient.cs` as a typed `HttpClient` client.
>
> - Base address `https://dev.azure.com/{organization}/`, organization from `IOptions<AdoOptions>`.
> - `api-version=7.1` appended to every request centrally, not repeated at call sites.
> - Auth header from `IAdoTokenProvider` on every request.
> - One generic `GetAsync<T>(string path, CancellationToken ct)` plus a `GetPagedAsync<T>` that follows `continuationToken`.
> - On non-success: throw `AdoApiException` carrying status code, request path, and the ADO error body. Log the request path and correlation ID via `IAppLogger` before throwing — when a scheduled run fails at 2am the URL is the whole diagnosis.
> - Never log the auth header, and never log the token provider's output.
>
> Register with `AddHttpClient<AdoClient>()` in the Core DI extension, following the pattern from A2.

### C3 — Resilience

> Add resilience to `AdoClient` using `Microsoft.Extensions.Http.Resilience` (or Polly directly if it is already referenced — check first).
>
> - Retry on 429 and 5xx, **honouring the `Retry-After` header** when present rather than using a fixed backoff. Azure DevOps rate-limits on a sliding window and tells you when to return.
> - Read `X-RateLimit-Remaining` and `X-RateLimit-Reset` from every response and expose them on the client so a backfill can throttle proactively instead of waiting to be rejected.
> - Maximum three retries, then fail loudly.
> - A timeout per request, cancellable by the caller's token.
> - Do not retry 401 or 403 — those are configuration problems and retrying hides them.

### C4 — Query surfaces

> Create four interfaces and implementations in `TestControllerGrpc.Core/Ado/`, all methods taking `CancellationToken`:
>
> **`IBuildQueries`** — `GetRecentBuildsAsync(int definitionId, int top)`, `GetBuildChangesAsync(int buildId)`, `GetBuildWorkItemRefsAsync(int buildId)`, `GetBuildArtifactsAsync(int buildId)`
>
> **`IWorkItemQueries`** — `GetWorkItemsAsync(IReadOnlyCollection<int> ids)` which **must chunk at 200 ids per request** and expand relations; `GetLinkedTestCasesAsync(int workItemId)` reading relations where `rel` is `Microsoft.VSTS.Common.TestedBy-Forward`
>
> **`IGitQueries`** — `GetRepositoriesAsync(string project)`, `GetCommitChangesAsync(Guid repoId, string sha)`, `GetPullRequestAsync(Guid repoId, int prId)`
>
> **`ITestPlanQueries`** — `GetPlansAsync(string project)`, `GetSuitesAsync(string project, int planId)`
>
> Each returns Core domain types, not raw DTOs — the DTO layer stops here. Do not add methods beyond this list; extra surface area is extra maintenance.

**Gate:** with a real PAT in `ADO_PAT`, a console spike or integration test retrieves the last two builds for definition 4009, their commits, and their work items. Every call visible in the log with its URL. Nothing hardcoded.

---

# Phase D — Mapping

### D1 — Repository alias table

> Create `IRepositoryResolver` in Core. On first use it calls `IGitQueries.GetRepositoriesAsync` for both "AppServer OMI" and "System Platform", builds a case-insensitive map from component name to repository id and name, and caches it for the process lifetime.
>
> Resolution order: exact repo name match; then `"AppServer." + component`; then a known-alias dictionary seeded with `SysObj` → `AppServer.AASysObjects` and `aabootstrap` → `AppServer.AABootstrap`. Anything unresolved is logged once at warning with the component name and returns null — **do not guess a repository id**, because a wrong id produces a link that 404s and quietly destroys trust in the whole Changes column.
>
> Expose `GetUnresolvedComponents()` so the coverage endpoint can report them.

### D2 — The translator

> Create `TestControllerGrpc.Core/Impact/AdoChangeTranslator.cs` mapping ADO responses into the v2 model in `docs/impact/component-usecase-map.v2.json`.
>
> - `AdoBuild` → `Change` (changeRef, observedDate from `finishTime`, buildId, result)
> - `AdoWorkItem` → `WorkItem`, mapping `System.WorkItemType` to `ims | bug | story | feature`. **Read the actual type strings from the fixtures — do not assume them.** Log and pass through anything unrecognised as `other` rather than dropping it.
> - `AdoCommitChange.item.path` → `File`, then to `Subsystem` by applying the component's `pathRules`, falling back to the first path segment after `src`, `ExtInterfaces` or `interfaces`.
> - Work item relations of type `TestedBy-Forward` → linked test case ids populating the Manual suite column.
> - Every produced edge carries `evidence` = `"ado:build:{buildId}"` and `confidence` = `"observed"`.
> - Where an observed value contradicts a `declared` value from `vobs.csv`, the observation wins and a structured conflict entry is written to the result. Never overwrite silently.
>
> Pure functions over DTOs — no HTTP, no I/O, no clock. Inject `TimeProvider` if a timestamp is needed.

### D3 — Tests

> Write unit tests for `AdoChangeTranslator` using the fixtures, covering: work item type mapping for every type present in the fixtures; unknown type passthrough; path-to-subsystem resolution including a path with no recognisable segment; merge commits returning an empty change list (this is normal, not an error); a build with no predecessor so `changes` is empty; observed-beats-declared conflict producing a logged conflict entry; and repository resolution failure returning null rather than a guessed id.
>
> Also one test asserting the real `component-usecase-map.v2.json` loads and validates clean. Match the test conventions from A4. No network in any test.

**Gate:** one real build translates end to end into model objects, and you have manually checked three of its work items and file paths against the Azure DevOps web UI.

---

# Phase E — Persistence, sync, API

### E1 — Storage

> Add EF Core entities and a migration for `Change`, `WorkItem`, `ChangedFile`, `SubsystemRollup` and `SyncState`. `SyncState` records, per build definition, the last processed build id and finish time.
>
> Node definitions (Component, Area, UseCase, Suite) stay in the JSON file — they belong in source control where they diff in a pull request. Only observed change history goes to SQLite. Add an index on `Change.observedDate` and on `Change.componentId`; the weekly and custom scopes query on both.

### E2 — Incremental sync

> Create `AdoSyncService` as a `BackgroundService` in Core.
>
> - For each definition in the map, fetch builds newer than `SyncState.lastFinishTime`.
> - **A completed build is immutable — never re-fetch one already stored.**
> - Translate, persist, update `SyncState` transactionally per definition so a partial failure does not lose ground.
> - A separate explicit `BackfillAsync(from, to)` path for loading history, deliberately throttled using the rate-limit headers from C3. Backfill and incremental sync must not share a code path; their traffic profiles are completely different.
> - Raise SignalR `BuildBriefReady` on completion — host-local, no bridge.
> - Log a one-line summary per run: definitions checked, builds ingested, API calls made, duration.

### E3 — Endpoints

> Add to the WebApi host:
>
> | Method | Route | Returns |
> |---|---|---|
> | `GET` | `/api/impact/consolidated?from&to` | subsystem rollup for a window |
> | `GET` | `/api/impact/scope?from&to&category` | recommended regression plan |
> | `GET` | `/api/impact/coverage` | gap scoreboard incl. unresolved repositories |
> | `POST` | `/api/impact/suites` | persist inline Automation / Manual suite edits |
> | `POST` | `/api/impact/sync` | trigger a sync manually |
>
> Every response includes the ADO links already resolved server-side — the UI must never build Azure URLs itself, or the alias logic ends up duplicated in two languages.
>
> Register `IAdoTokenProvider` as `ServicePrincipalTokenProvider` in this host only, following the A2 pattern.

**Gate:** `/api/impact/consolidated` returns real data for last week. Restarting the service does not re-fetch anything already stored.

---

# Phase F — The Regression tab

Use whatever A4 reported. If it came back `NOT FOUND`, open the XAML and answer it yourself first.

### F1 — Shell

> Add a REGRESSION tab to the WPF ribbon, between EXECUTION and TOOLS, matching the existing tab and ribbon-group markup exactly — same control types, same styling approach, same resource dictionary. Groups: **Track changes** (Build, Weekly, Custom, Release), **Timeline** (two date pickers), **Show** (Runtime, Config toggles), **Execute** (Dispatch Automated, Assign Manual, Export).
>
> Content area: a DataGrid above, a three-column regression plan panel below, matching the Node Properties / Agents panel styling already in the app. Bind to a `RegressionViewModel` following the MVVM toolkit identified in A4. XAML and view model only in this step — no data access.

### F2 — Grid

> Bind the DataGrid to `ObservableCollection<SubsystemRow>` with columns: Category, Component, Subsystem, Files modified, Changes/work items, Summary of change, Risk, Automation suite, Manual suite, Est.
>
> - Changes column renders work items as hyperlinks using the URLs supplied by the API, colour-coded by type (IMS, Bug, User Story, Feature) and opened with `Process.Start` and `UseShellExecute = true`.
> - Automation suite and Manual suite are editable — add and remove entries inline, `POST /api/impact/suites` on commit, with an unsaved-changes indicator.
> - Per-column filtering: dropdowns for Category, Component, Risk and work item type; a work item picker; text filters elsewhere. Filters compose and stack on top of the date scope.
> - Sortable on every column.
> - Virtualisation enabled — a release backfill produces thousands of rows.

### F3 — Plan panel and live refresh

> Bind the lower panel to Runtime / Configuration / Totals from `/api/impact/scope`, recomputing whenever the scope or filters change. Subscribe to SignalR `BuildBriefReady` and refresh the grid when a build completes.
>
> Show the last successful sync time in the status bar. If Azure DevOps is unreachable, display the cached data with a clear "data as of {time}" indicator rather than an empty grid — a blank table looks like a bug, stale data with a timestamp is useful.

---

## Working notes

**One prompt at a time.** Copilot degrades badly on multi-part requests. Build, run the tests, read the diff, then move on.

**Read every generated file before accepting.** Particularly Phase B — a token provider that silently falls back to an empty credential will appear to work in dev and fail confusingly in production.

**When Copilot invents an API shape, go back to the fixtures.** The fixtures are the ground truth; the model's memory of the Azure DevOps API is not.

**Keep Phases A–D free of UI work.** The temptation to skip to the visible part is strong, and every hour spent on XAML before the translator is tested is an hour you will spend again.

## Order and rough shape of the effort

| Phase | Gate |
|---|---|
| A | DI pattern, ribbon framework, test framework all named with file paths |
| B | Build clean, header test passes, no secret in any tracked file |
| C | Real builds, commits and work items retrieved with a scoped credential |
| D | One build translates end to end, spot-checked against the ADO web UI |
| E | Endpoint returns last week's data; restart re-fetches nothing |
| F | Tab populated from the API, links open, edits persist |

A and B are short. C and D are most of the code. E is straightforward once D is right. F is sized by whatever A4 found — a well-structured existing ribbon makes it quick, a hand-rolled one does not.
