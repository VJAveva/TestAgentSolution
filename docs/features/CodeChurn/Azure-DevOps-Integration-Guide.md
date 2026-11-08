# Connecting to Azure DevOps

### A staged path from your PowerShell scripts to live data in the Regression tab

Everything the mockups display is currently parsed from files. This is how each column gets fed for real.

---

## Stage 0 — Before anything else

1. **Revoke the PAT** in `Tool/GetBuildChanges.ps1`, `Tool/GetBuildChanges_OMI.ps1` and `Tool/test1.ps1`. It is in plaintext, it left your network inside a zip, and it is currently the only credential in the system. Regenerating invalidates the old one immediately.
2. **Check git history.** Deleting the file does not remove the secret from previous commits. If those scripts are in ADO, the token is still readable in history.
3. **Issue the replacement with narrow scopes** (Stage 3) and put it somewhere that is not a source file.

Do not start Stage 1 until this is done. Every stage below assumes credentials that are not sitting in a repo.

---

## Stage 1 — Prove access from PowerShell first

You already have working scripts. Do not throw them away — use them as the spike, because a failure here is an *access* problem, and you want to find those before any C# exists.

Use `az` for auth rather than a PAT, so nothing is stored anywhere:

```powershell
az login
$org  = "https://dev.azure.com/AVEVA-VSTS"
$proj = "AppServer OMI"
$tok  = az account get-access-token `
          --resource 499b84ac-1321-427f-aa17-267ca6975798 `
          --query accessToken -o tsv
$H = @{ Authorization = "Bearer $tok" }

# 1. the two most recent completed builds for one definition
$b = Invoke-RestMethod -Headers $H -Uri `
  "$org/$proj/_apis/build/builds?definitions=4009&`$top=2&statusFilter=completed&queryOrder=finishTimeDescending&api-version=7.1"
$b.value | Select id, buildNumber, result, finishTime

# 2. commits that went into the newest one
$id = $b.value[0].id
(Invoke-RestMethod -Headers $H -Uri `
  "$org/$proj/_apis/build/builds/$id/changes?`$top=200&api-version=7.1").value |
  Select id, message, @{n='author';e={$_.author.displayName}}

# 3. work items linked to that build   <-- the IMS / Bug / US column
(Invoke-RestMethod -Headers $H -Uri `
  "$org/$proj/_apis/build/builds/$id/workitems?api-version=7.1").value
```

**Save every response to disk as JSON.** Those files become your unit-test fixtures in Stage 4, and they let you build the whole mapping layer with no network at all.

`499b84ac-1321-427f-aa17-267ca6975798` is Azure DevOps' fixed resource ID in Entra — it is the same for every organisation, not a secret, and not something you generate.

**Exit criteria:** you can produce, from the command line, the build number, the commit list, and the work item IDs for one build in each project. If any call 401s or 403s, it is a permissions problem — fix it here where the feedback loop is seconds long.

---

## Stage 2 — The call chain

Each UI column traces to one endpoint. This table is the actual specification for the ingest layer.

| Column in the Regression tab | Endpoint | Field |
|---|---|---|
| *(which builds exist)* | `GET {org}/{project}/_apis/build/builds?definitions={id}&$top=2&statusFilter=completed&queryOrder=finishTimeDescending` | `id`, `buildNumber`, `result`, `finishTime` |
| Component | your own `vobs.csv` → `buildDefinitionId` | — |
| **Changes / work items** | `GET .../build/builds/{buildId}/workitems` then `GET {org}/_apis/wit/workitems?ids={csv}&$expand=relations` | `System.WorkItemType` → IMS / Bug / User Story, `System.Title` |
| **Summary of change** | same work item call, plus `.../build/builds/{buildId}/changes` | `System.Title`, commit `message` |
| **Files modified** | `GET .../git/repositories/{repoId}/commits/{sha}/changes` | `changes[].item.path` |
| Subsystem | derived from those paths + your `pathRules` | — |
| *(repo name resolution)* | `GET {org}/{project}/_apis/git/repositories` | `id`, `name` |
| **Manual suite links** | work item `relations` where `rel` is `Microsoft.VSTS.Common.TestedBy-Forward` | linked test case IDs |
| Test plans and suites | `GET .../testplan/Plans`, `.../Plans/{id}/suites` | plan and suite IDs |
| *(version movement)* | `GET .../build/builds/{buildId}/artifacts` | manifest artifact, if published |

Use `api-version=7.1` throughout. Two practical notes: the work item batch call takes **up to 200 IDs per request**, and `.../builds/{id}/changes` returns the commits *since the previous build of that definition* — which is exactly the diff `CompareBuildsForDiff` currently reconstructs by scraping console logs. Confirm that on a build you already know the answer for, then retire the log-scraping path.

**The repositories call solves a real problem.** The mockups guess repo names as `AppServer.<component>`, which is why the Azure links are marked provisional. One call returns the true list, and that becomes your alias table.

---

## Stage 3 — Credentials

There are two consumers with genuinely different needs, and that is not a complication to work around — it is the same two-front-doors shape as the rest of your platform.

### The WPF Controller — acts as the signed-in engineer

Delegated (on-behalf-of-user) flow. The engineer's own ADO permissions apply, which means the links they click in the grid resolve to things they can actually open, and the audit trail names a person.

```csharp
// Azure.Identity
var cred = new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions {
    TenantId = tenantId, ClientId = clientId,
    TokenCachePersistenceOptions = new TokenCachePersistenceOptions()   // survives restarts
});
var token = await cred.GetTokenAsync(
    new TokenRequestContext(new[] { "499b84ac-1321-427f-aa17-267ca6975798/.default" }), ct);
```

### The WebApi host and the scheduler — acts as itself

Client credentials against an Entra app registration. Prefer a **certificate** over a client secret; secrets expire silently at 3am.

```csharp
var cred = new ClientCertificateCredential(tenantId, clientId, certificate);
var token = await cred.GetTokenAsync(
    new TokenRequestContext(new[] { "499b84ac-1321-427f-aa17-267ca6975798/.default" }), ct);
```

**One extra step people miss:** an Entra service principal is not automatically an Azure DevOps user. After registering the app, add it to the organisation — *Organization Settings → Users → Add users*, pick **Service Principal**, give it Basic access, and grant it project access. Until you do, every call returns 401 no matter how valid the token is.

### The seam

```csharp
public interface IAdoTokenProvider {
    Task<string> GetBearerAsync(CancellationToken ct);
}
```

Three implementations: `PatTokenProvider` (Stage 4, `Basic base64(":" + pat)`), `UserTokenProvider` (WPF), `ServicePrincipalTokenProvider` (web host). Each host registers its own. Nothing above this interface knows or cares which is in play, so swapping PAT for Entra later is a DI line, not a rewrite.

### If you must start with a PAT

Legitimate for Stage 4, as long as it is temporary and scoped. Microsoft is explicit that PATs are for prototyping and that Entra tokens should replace them. Scopes needed, and nothing more:

| Scope | Why |
|---|---|
| Build (Read) | builds, changes, artifacts |
| Code (Read) | repositories, commits, pull requests |
| Work Items (Read) | IMS / Bug / User Story, relations |
| Test Management (Read) | plans, suites, linked test cases |

Storage, in order of preference: Windows Credential Manager via DPAPI (WPF), environment variable or Windows-protected config (service), `dotnet user-secrets` (dev only). Never `appsettings.json`, never a `.ps1`.

Also worth knowing: the older **Azure DevOps OAuth 2.0** is deprecated — it stopped accepting new registrations in April 2025 with full removal planned for 2026. Do not build on it. Entra OAuth is the path.

---

## Stage 4 — The C# client

Plain `HttpClient` plus `System.Text.Json`, not the `Microsoft.TeamFoundationServer.Client` SDK. The SDK gives typed clients but pulls a large legacy dependency graph that has historically lagged on new .NET versions, and you are on .NET 10. You need eight endpoints; hand-written DTOs are less work than fighting the dependency tree.

```
TestControllerGrpc.Core/Ado/
  IAdoTokenProvider.cs
  AdoClient.cs              // typed HttpClient, Polly retry, rate-limit aware
  IBuildQueries.cs          // GetRecentBuilds, GetBuildChanges, GetBuildWorkItems
  IWorkItemQueries.cs       // GetWorkItemsBatch (200 max), GetRelations
  IGitQueries.cs            // GetRepositories, GetCommitChanges
  ITestPlanQueries.cs       // GetPlans, GetSuites, GetLinkedTestCases
  Dto/                      // one record per response shape
```

Register with `AddHttpClient<AdoClient>()`, a named policy handler, and `IAdoTokenProvider` resolved per host.

Rules that will save you:

- **Every method takes a `CancellationToken`.** A build brief that hangs is worse than one that fails.
- **Honour `Retry-After`.** Azure DevOps rate-limits with a sliding window and tells you when to come back. Read `X-RateLimit-Remaining` and back off before you hit zero rather than after.
- **Batch work items at 200.** A release with 46 changes is one call, not 46.
- **Log the request URL and correlation ID on every failure**, through `IAppLogger`. When this breaks at 2am on a scheduled run, the URL is the entire diagnosis.
- **Test against the Stage 1 fixtures.** The mapping layer should have full unit coverage with zero network calls.

---

## Stage 5 — Map into the model

The client returns ADO shapes. `component-usecase-map.v2.json` is your shape. Keep them apart — one translator class, thoroughly tested, is the difference between a system you can reason about and one where an ADO field rename breaks the UI.

```
AdoBuild        -> Change      (ref, date, buildId, result)
AdoWorkItem     -> WorkItem    (type: ims|bug|story|feature, id, title)
AdoCommitChange -> File        (path) -> Subsystem (via pathRules)
AdoRelation     -> TestCase    (linked suites for the Manual column)
```

Two decisions to make deliberately rather than by accident:

**Work item type mapping.** `System.WorkItemType` will contain your organisation's process template names. `IMS Internal Request`, `Bug` and `User Story` appear in your churn workbook, but confirm the exact strings from a live response — the mockup's type filter depends on them matching.

**Precedence.** When ADO says something different from `vobs.csv`, ADO wins and the conflict is logged. That is the `observed` beats `declared` rule from the impact model, and this is where it first gets exercised for real.

---

## Stage 6 — Make it survive daily use

- **Cache by build ID.** A completed build is immutable. Fetch once, store, never re-fetch. This alone removes most of your API traffic.
- **Sync incrementally.** Track the last processed `finishTime` per definition and ask only for what is newer.
- **Persist changes to SQLite via EF Core.** The node definitions can stay in the JSON file, but `changes` accumulates and needs to be queryable across releases — that is what makes the Weekly and Custom scopes work over months rather than one release.
- **Degrade honestly.** If ADO is unreachable, show the last successful sync time in the status bar rather than an empty grid. "Data as of 06:02" is useful; a blank table is alarming.

---

## Stage 7 — Wire the UI

Only now. In order:

1. `GET /api/impact/consolidated?from&to` — feeds the scope selector and the subsystem grid
2. `GET /api/impact/scope` — feeds the recommended plan panel
3. `POST /api/impact/suites` — persists the inline Automation / Manual suite edits
4. SignalR `BuildBriefReady` — the grid refreshes itself when a build completes

The WPF Regression tab and the web client both call the same endpoints. Neither holds ADO credentials of its own beyond its token provider.

---

## Order of work

| Step | Done when |
|---|---|
| 0 | Old PAT revoked, history checked, replacement stored outside source |
| 1 | PowerShell returns builds, commits and work items for both projects; fixtures saved |
| 2 | Every column in the table above traced to a real response you have seen |
| 3 | Entra app registered, service principal added as an ADO org user, `IAdoTokenProvider` defined |
| 4 | `AdoClient` passes unit tests against fixtures, no network |
| 5 | Translator maps a real build into the v2 model, verified against a known release |
| 6 | Caching and incremental sync working; a re-run costs near-zero API calls |
| 7 | Grid populated from `/api/impact/consolidated` |

Steps 0–2 are days. Steps 4–5 are the bulk of the code. Step 3 is short but involves other people — start the Entra app registration request early, because waiting on an admin is the thing most likely to stall you.

---

## Things that will go wrong

**401 with a valid token.** The service principal was never added to the ADO organisation. Stage 3.

**403 on work items but 200 on builds.** Scopes too narrow, or the identity lacks project-level read on work item tracking.

**`changes` returns nothing.** The build had no new commits, or it is the first build of the definition and there is no predecessor to diff against. Handle both — the second is not an error.

**Commit `changes` returns 200 with an empty list.** Merge commits often have no direct file changes. Follow the parent commits, or use the pull request's iteration changes instead.

**A repo name does not match your guess.** Expected — that is why Stage 2 calls `_apis/git/repositories`. Build the alias table from the API, not from a pattern.

**Rate limited during a backfill.** Loading historical releases hits limits fast. Throttle deliberately on backfill; the daily incremental sync will never come close.
