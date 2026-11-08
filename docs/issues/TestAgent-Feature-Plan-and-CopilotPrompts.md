# TestAgentSolution — Six-Feature Build Plan & Copilot Prompt Pack

Covers six requests:

| # | Workstream | What it is |
|---|---|---|
| A | Code Churn work item policy | Categorise by linked Feature; exclude Task work items from email + Excel + export |
| B | Pipeline Skip & Comments | Right-click Skip on tree nodes; Comment field in WatchList editor |
| C | ADO authentication | Credential dialog in WebClient (and WPF) so Code Churn works when the PAT expires |
| D | RBAC | Code Churn + Report Card restricted to Administrator and Sr Manager |
| E | Maintenance panel layout | Horizontal scroll; Action column is currently clipped and unusable |
| F | Foundation | Identity plumbing that C and D both need |

---

## Recommended order

Do **not** build these in the order you listed them. C and D both need the same
identity foundation, and B has a safety implication that needs the run-report
change landed first.

| Order | Workstream | Why here | Rough size |
|---|---|---|---|
| 1 | **E** — Maintenance layout | Isolated, zero risk, restores a broken feature today | Half a day |
| 2 | **F** — Identity foundation | Prerequisite for C and D. Nothing else works cleanly without it | 1 day |
| 3 | **D** — RBAC | Gives you `IUserContext`, which C keys per-user credentials on | 2–3 days |
| 4 | **C** — ADO authentication | Unblocks WebClient Code Churn; also retires the hardcoded PAT | 2–3 days |
| 5 | **A** — Code Churn work item policy | Core-only, independent, no UI dependency | 2 days |
| 6 | **B** — Skip & Comments | Largest blast radius — touches model, persistence, executor, two UIs | 3–4 days |

Build **B last** deliberately. Skipping a revert node changes what actually runs on
hardware. You want the rest of the system stable before you introduce a way to
silently not do things.

---

## Ground rules — paste this once per Copilot session

```
Ground rules for this session:

1. This is TestAgentSolution: .NET 10. TestControllerGrpc (WPF desktop, embedded
   Kestrel) and TestController.WebApi (ASP.NET Core) are two independent hosts
   that BOTH consume TestControllerGrpc.Core. Core must contain no host-specific
   types. TestAgentGrpc is a WinForms tray agent on each VM (port 5200).
   TestController.WebClient is React.

2. Engine first. Build and prove the Core logic before any UI work. If I ask for
   UI before the engine exists, tell me.

3. Do not rewrite files. Give me the minimal diff.

4. If you are inferring the shape of code you cannot see, label it
   "ASSUMPTION:" and stop rather than inventing an API.

5. Never invent .NET 10 APIs. If unsure a method exists, say so.

6. Anything ambiguous fails toward INCLUSION, never silent exclusion. If data is
   dropped, filtered, or skipped, that fact is a first-class output — it appears
   in the report, not in a log warning.

7. Missing data fails loudly with an explicit error. Never degrade silently to
   an empty result.
```

---

# Workstream E — Maintenance panel horizontal scroll

## The problem

The Agents panel Maintenance tab has nine columns. The last one — the per-row
action button — is clipped to `Ac...`. That column holds the Reboot control, so
the feature is not just ugly, it is **unreachable**. The summary card row and the
`Reboot all ready` button are also being cut at the right edge.

## Root cause candidates (in likelihood order)

1. All `DataGridColumn` widths are `*` — star sizing shrinks to fit the viewport
   and therefore never overflows, so no scrollbar is ever generated. The columns
   just squash until text ellipsises.
2. The `DataGrid` sits inside a `ScrollViewer` (or `ListView` template) whose
   `HorizontalScrollBarVisibility` is `Disabled`, which is the WPF default in
   several control templates.
3. A parent `Grid`/`DockPanel` has no `MinWidth`, so the panel measures to the
   available width and clips children rather than scrolling.

## Target behaviour

- Horizontal scrollbar appears when content exceeds the panel width.
- Agent column frozen so the name stays visible while scrolling right.
- Explicit `MinWidth` per column so total content width can exceed the viewport.
- Below a threshold width, low-priority columns (`Detected by`, `Last install`)
  collapse instead of squashing everything.
- Summary cards wrap rather than clip.

### P01 — Diagnose the clipping

```
#file: [the Maintenance tab XAML — likely MaintenanceTabView.xaml or similar]

The last DataGrid column is clipped and no horizontal scrollbar appears.

Diagnose in this order and tell me which applies:
1. Are all DataGridColumn widths star-sized (*)? Star sizing shrinks to the
   viewport, so content never overflows and no scrollbar is generated.
2. Is the DataGrid inside a ScrollViewer or ListView whose
   ScrollViewer.HorizontalScrollBarVisibility is Disabled?
3. Does any parent container lack a MinWidth, causing it to clip rather than
   scroll?

Report the actual cause from the XAML. Do not propose a fix yet.
```

### P02 — Fix the grid

```
#file: [Maintenance tab XAML]

Apply the minimal XAML change so that:

- ScrollViewer.HorizontalScrollBarVisibility="Auto" on the DataGrid
- Each column has an explicit MinWidth (Agent 120, Update state 110,
  Last install 110, Pending 70, Detected by 130, Last report 100,
  Dispatch 90, Action 110)
- Column widths use SizeToHeader or Auto rather than * for all columns, so the
  total can exceed the viewport
- DataGrid.FrozenColumnCount="1" so the Agent column stays pinned
- The DataGrid's container has MinWidth set so it scrolls instead of clipping

Show the diff only. Do not restructure the view.
```

### P03 — Summary cards and toolbar

```
#file: [Maintenance tab XAML]

The Windows-updates summary cards row (agents reporting / reboot required /
updates pending / install failed) and the "Reboot all ready" button are clipped
at the right edge on narrow panel widths.

Change the cards container to a WrapPanel so the cards flow onto a second line
rather than clipping. Keep "Reboot all ready" always visible and right-aligned;
if it cannot fit, it should wrap with the cards, never be cut off.

Minimal diff.
```

### P04 — Responsive column priority

```
#file: [Maintenance tab XAML + its code-behind or ViewModel]

Add width-responsive column visibility to the maintenance DataGrid.

Rules:
- Panel width >= 900: all columns visible
- 700–899: hide "Detected by"
- < 700: hide "Detected by" and "Last install"
- Agent, Update state, Pending, Dispatch, Action are NEVER hidden

Implement with a SizeChanged handler on the panel that sets column Visibility,
or a MultiDataTrigger bound to ActualWidth — whichever is cleaner for this
codebase. Explain which you chose and why.
```

### P05 — WebClient equivalent

```
#file: [the WebClient maintenance table component]

Apply the same fix on the React side:
- Wrapper div with overflow-x: auto
- Table with min-width so it can exceed the wrapper
- position: sticky; left: 0 on the first column cell and header, with a
  background colour and a right border so it reads as pinned
- The action column must never be the one that gets cut

Use the existing design tokens; do not introduce new colour values.
```

**Gate E:** shrink the Agents panel to its minimum width. The Action column must
still be reachable by scrolling, and the Agent name must stay visible.

---

# Workstream F — Identity foundation

Both RBAC and per-user ADO credentials need to know *who is asking*. Build this
once, in Core, before either.

### P06 — IUserContext in Core

```
In TestControllerGrpc.Core, create the identity abstraction that both hosts will
implement.

public interface IUserContext
{
    string UserId { get; }          // stable key, e.g. domain\samaccountname
    string DisplayName { get; }
    IReadOnlySet<string> Roles { get; }
    bool IsAuthenticated { get; }
}

Requirements:
- No System.Windows and no Microsoft.AspNetCore types in this file.
- Add IUserContextAccessor for cases where the context must be resolved
  mid-call rather than injected.
- UserId must be normalised to lowercase and trimmed so it is safe as a
  database key.

Then create the two host implementations:
- WPF: WindowsUserContext using WindowsIdentity.GetCurrent()
- WebApi: HttpUserContext reading from HttpContext.User

Register each in its own host's composition root. Core registers only the
interface. Show me the registration lines for both hosts.
```

### P07 — Role resolution from AD groups

```
Create AdGroupRoleResolver in Core.

It maps Windows group membership to application roles using a configuration file
(security.roles.json), NOT hardcoded group names.

Config shape:
{
  "roleMappings": {
    "Administrator": ["MAGELLANDEV2000\\TestAgent-Admins"],
    "SrManager":     ["MAGELLANDEV2000\\TestAgent-SrManagers"],
    "Engineer":      ["MAGELLANDEV2000\\TestAgent-Engineers"]
  },
  "defaultRole": "Viewer"
}

Requirements:
- Group comparison is case-insensitive.
- A user in no mapped group gets defaultRole, never an empty role set.
- Missing or malformed security.roles.json throws at startup with a message
  naming the file and the specific problem. Do not fall back to a built-in
  default silently.
- Resolved roles cached per user for the process lifetime, with an explicit
  invalidation method.

Write the class plus a unit test covering: multi-group user, no-group user,
missing config file.
```

**Gate F:** log the resolved identity and roles at startup in both hosts. Confirm
the same user resolves to the same roles in WPF and WebApi.

---

# Workstream D — RBAC

## Design decisions to lock in first

**Permissions, not roles, in code.** Never write `if (user.Role == "Administrator")`.
Write `if (user.Has(Permissions.CodeChurnView))`. Roles map to permissions in
config, so changing who can do what is a config edit, not a rebuild.

**Default Mode is a policy, not a bypass.** Your banner currently says
*"Default Mode — No authentication required. WPF has full access."* Keep that,
but implement it as a permission policy that grants everything — **not** as an
`if (defaultMode) return true;` short-circuit before the check. One code path.
Bypass branches are how authorization bugs happen.

**The API is the gate. The UI is a courtesy.** Hiding a ribbon button is not
security. The WebApi authorization policy is the security. Both are needed —
for different reasons.

**Disable, don't hide.** A greyed-out Code Churn button with the tooltip
*"Requires Administrator or Sr Manager"* tells the user who to ask. A missing
button generates a support ticket.

### P08 — Permission catalogue

```
Create the permission catalogue in TestControllerGrpc.Core.

public static class Permissions
{
    public const string CodeChurnView    = "codechurn.view";
    public const string CodeChurnExport  = "codechurn.export";
    public const string ReportCardView   = "reports.reportcard.view";
    public const string ReportCardExport = "reports.reportcard.export";
    public const string ResultsView      = "results.view";
    public const string FleetView        = "fleet.view";
    public const string FleetRevert      = "fleet.revert";
    public const string PipelineView     = "pipeline.view";
    public const string PipelineExecute  = "pipeline.execute";
    public const string PipelineSkip     = "pipeline.skip";
    public const string SecurityManage   = "security.manage";
}

Then create security.permissions.json with the default role -> permission map:

- Administrator: all permissions
- SrManager: codechurn.*, reports.*, results.view, fleet.view, pipeline.view
- Engineer: results.view, fleet.*, pipeline.view, pipeline.execute, pipeline.skip
- Viewer: results.view, fleet.view, pipeline.view

Add IPermissionEvaluator with:
    bool Has(IUserContext user, string permission)

CRITICAL: implement Default Mode as a policy source that returns the full
permission set for every user — NOT as an early-return that skips the check.
The evaluation code path must be identical in both modes. Explain in a comment
why.
```

### P09 — WebApi enforcement

```
#file: [WebApi Program.cs / service registration]

Register one ASP.NET Core authorization policy per permission constant, driven
by IPermissionEvaluator. Do not hand-write 11 policies — generate them from the
Permissions catalogue via reflection or an explicit array.

Then apply [Authorize(Policy = Permissions.X)] to the relevant endpoints across
the ~23 REST endpoints. Start with:
- All Code Churn endpoints  -> CodeChurnView (export endpoints -> CodeChurnExport)
- Report Card endpoints     -> ReportCardView
- Fleet revert endpoints    -> FleetRevert

List every endpoint you are changing and the policy you are applying, as a table,
BEFORE showing code. I want to review the mapping.

Denied requests return 403 with a ProblemDetails body containing
"code": "PERMISSION_DENIED" and the required permission name — so the client can
render a useful message rather than a generic error.
```

### P10 — WPF ribbon gating

```
#file: [the ribbon XAML with Results Viewer / Report Card / CodeChurn / Security]

Gate the ribbon buttons on permissions:
- CodeChurn button   -> Permissions.CodeChurnView
- Report Card button -> Permissions.ReportCardView
- Security button    -> Permissions.SecurityManage

Requirements:
- Buttons are DISABLED, not collapsed.
- Disabled buttons get a ToolTip: "Requires Administrator or Sr Manager".
  ToolTipService.ShowOnDisabled must be True or the tooltip will not appear.
- Implement via CanExecute on the existing commands where they exist, or a
  PermissionGate markup extension where they do not.

Show me the PermissionGate implementation if you add one.
```

### P11 — WebClient gating

```
#file: [WebClient routing + nav component]

Add permission-aware rendering:
- usePermission(permission) hook reading from a /api/security/me endpoint
  that returns { userId, displayName, roles, permissions }
- Route guards on the Code Churn and Report Card routes
- Nav items disabled with a tooltip rather than removed

The /api/security/me endpoint must NEVER return anything that could be used to
elevate — it is a read of the caller's own effective permissions only.

State explicitly in a code comment that this is UX only and the WebApi policy is
the actual enforcement.
```

### P12 — Security audit trail

```
Add a security audit log in Core.

Table security_audit: id, utc_timestamp, user_id, permission, resource,
granted (bool), host ("wpf" | "webapi").

Log:
- every DENIED permission check
- every GRANTED check for: fleet.revert, pipeline.execute, security.manage
- do NOT log granted checks for view permissions (noise)

Writes must be fire-and-forget with faults observed and logged — an audit write
failure must never block or fail the user's operation. Show me how you handle
the unobserved-task risk.
```

**Gate D:** log in as a non-privileged account. Confirm (1) the ribbon button is
disabled with a tooltip, (2) calling the Code Churn API directly with curl
returns 403, (3) the denial appears in `security_audit`. All three, or the gate
fails.

---

# Workstream C — ADO authentication

## Design decisions to lock in first

**One interface, two implementations.** Build the PAT flow now behind
`IAdoCredentialProvider`, so that swapping in Entra ID OAuth later — which is the
actual cure for "the PAT expires every time" — is an implementation change, not
a rewrite. Same shape as your two-front-doors pattern.

**Per-user, not per-service.** Store credentials keyed on `IUserContext.UserId`.
This is why F and D come first.

**Write-only.** The token goes in, never comes back out. No GET returns it. It is
masked in every UI to last-4. It never appears in a log, an exception message, or
a gRPC status detail.

**A 401 is not an empty result.** Right now WebClient shows nothing when the PAT
is dead. That is the silent-degradation failure mode. An expired token must
surface as a specific, actionable error that opens the re-auth dialog.

This workstream also retires the hardcoded PAT in `GetBuildChanges.ps1`,
`GetBuildChanges_OMI.ps1`, and `test1.ps1` — revoke that token before you start.

### P13 — Credential abstraction

```
In TestControllerGrpc.Core, create:

public sealed record AdoCredential(string Token, DateTimeOffset? ExpiresOnUtc);

public sealed record AdoCredentialStatus(
    bool IsConfigured,
    string? MaskedToken,        // e.g. "****9f2a"
    DateTimeOffset? ExpiresOnUtc,
    DateTimeOffset? LastValidatedUtc,
    bool IsExpired,
    int? DaysUntilExpiry);

public interface IAdoCredentialProvider
{
    Task<AdoCredential?> GetAsync(string userId, CancellationToken ct);
    Task StoreAsync(string userId, AdoCredential credential, CancellationToken ct);
    Task<AdoCredentialStatus> GetStatusAsync(string userId, CancellationToken ct);
    Task DeleteAsync(string userId, CancellationToken ct);
    Task<bool> ValidateAsync(AdoCredential credential, CancellationToken ct);
}

Rules:
- AdoCredential.ToString() must be overridden to NOT emit the token.
- GetStatusAsync returns the masked form only. There is no API that returns the
  full token to any caller outside the provider.
- ValidateAsync calls the ADO connectionData endpoint and returns false on 401,
  throws on transport failure. Distinguish "bad credential" from "ADO is down".

Write the interface and records. No implementation yet.
```

### P14 — Encrypted storage

```
Implement DataProtectionAdoCredentialProvider : IAdoCredentialProvider.

Storage: SQLite table ado_credentials
  (user_id TEXT PRIMARY KEY, cipher BLOB, expires_on_utc TEXT NULL,
   created_utc TEXT, last_validated_utc TEXT NULL)

Encryption: ASP.NET Core Data Protection IDataProtector with purpose string
"TestAgentSolution.AdoCredential.v1".

CRITICAL: the Data Protection key ring must be persisted to disk with an
explicit PersistKeysToFileSystem path, and protected at rest. The default
key ring is ephemeral in some hosting models — every restart would silently
invalidate every stored credential, which looks exactly like "the PAT expired
again". Set this up explicitly and tell me the path you chose.

Also:
- Put this database in its own directory, separate from impact-index.db.
  impact-index.db is a regenerable cache that gets deleted on -Force rebuild.
  Credentials must never sit where a rebuild flag can reach them.
- Set busy_timeout and WAL on the connection.

Write the implementation plus tests for: store/retrieve round-trip, overwrite,
delete, retrieve-missing returns null.
```

### P15 — WebApi endpoints

```
Add the credential endpoints to TestController.WebApi:

POST   /api/ado/credential          body { token, expiresOnUtc? }  -> 204
GET    /api/ado/credential/status                                  -> AdoCredentialStatus
POST   /api/ado/credential/validate body { token }                 -> { valid: bool, message }
DELETE /api/ado/credential                                          -> 204

Rules:
- All four require an authenticated user; each acts ONLY on the caller's own
  credential. There is no userId parameter — it comes from IUserContext.
  Do not add an admin override.
- POST /credential validates the token before storing. Reject invalid tokens
  with 400 and a clear message.
- The token field must be excluded from request logging. Show me how you
  suppress it.
- Rate-limit the validate endpoint (it hits ADO).
```

### P16 — 401 propagation

```
Fix the silent-empty-result failure.

When any Code Churn operation fails ADO authentication:
1. Core throws AdoUnauthorizedException (new type).
2. WebApi maps it to 401 with ProblemDetails
   { "code": "ADO_CREDENTIAL_INVALID", "detail": "..." }.
3. WebClient intercepts that code globally and opens the credential dialog.
4. WPF intercepts it and opens its dialog.

Audit #folder: [code churn folder] for every place an ADO call result is used.
Find anywhere a failed or unauthorized response currently produces an empty
collection instead of an error. List each one. That silent degradation is the
actual bug behind "webclient is not able to retrieve the code".
```

### P17 — WebClient credential dialog

```
#file: [WebClient — add a new component]

Build the ADO credential dialog:

- Modal, opened from a toolbar chip in the Code Churn view and automatically on
  ADO_CREDENTIAL_INVALID
- Password-type input, paste-friendly, with a show/hide toggle
- Optional expiry date field
- "Validate" button calling /api/ado/credential/validate, showing a clear
  success or failure message, BEFORE the Save button enables
- After save, the dialog never displays the token again — only the masked form
- Link to the ADO PAT creation page with the required scopes listed
  (Code: Read, Build: Read, Work Items: Read)

Also add a status chip to the Code Churn toolbar:
- Green "ADO connected"
- Amber "ADO token expires in N days" when N <= 14
- Red "ADO token expired — click to reconnect"

Use existing design tokens. No new colour values.
```

### P18 — WPF dialog + PowerShell retirement

```
Two parts.

Part 1: add the equivalent credential dialog to the WPF host, consuming the same
IAdoCredentialProvider from Core. Same validate-before-save flow, same masking.

Part 2: change GetBuildChanges.ps1, GetBuildChanges_OMI.ps1, and test1.ps1 to
read the ADO token from Windows Credential Manager instead of a hardcoded
literal. Use a named target such as "TestAgentSolution:AdoPat".

Requirements for the scripts:
- ASCII-only console output (PowerShell 5.1 compatibility)
- If the credential is absent, fail with an explicit error naming the credential
  target and how to create it. Do not fall back to a prompt in a script that may
  run unattended.
- Idempotent and safe to re-run after a VM revert.

Reminder: these fixes must also be baked into Prepare-AgentNode.ps1, or the
snapshot revert will restore the old versions.
```

**Gate C:** deliberately revoke the PAT mid-session. WebClient must show the red
chip and open the dialog. It must not show an empty Code Churn result.

---

# Workstream A — Code Churn work item policy

## Design decisions to lock in first

**Exclude Task as a *row*, not as a *link*.** This is the important one. If a
commit's only linked work item is a Task and you filter Tasks out early, that
commit vanishes from the report entirely. Recall bias says never lose a change.
So: roll the Task up to its parent (Story/Bug/Feature) and attribute the commit
there, then suppress the Task as a displayed row.

**Filter once, render three times.** Build one `CodeChurnReportModel` in Core with
the policy already applied, then let the email renderer, the Excel exporter, and
the export renderer consume it. If filtering lives in three renderers, they will
drift — that is almost certainly why they disagree today.

**Orphans get a bucket, not a bin.** A work item with no parent Feature goes into
an explicit "Unassigned to Feature" group with a visible count. Never dropped.

### P19 — Policy model

```
In TestControllerGrpc.Core, create the Code Churn work item policy.

public sealed class CodeChurnWorkItemPolicy
{
    public IReadOnlySet<string> ExcludedTypes { get; init; }   // default: ["Task"]
    public bool RollUpToFeature { get; init; } = true;
    public string OrphanBucketName { get; init; } = "Unassigned to Feature";
    public int MaxHierarchyDepth { get; init; } = 6;
}

Bind from configuration section "CodeChurn:WorkItemPolicy" with IOptions<T> and
validate on startup. Type comparison is case-insensitive.

CRITICAL SEMANTICS — implement exactly this:
Excluding a type removes it as a REPORTED ROW. It does NOT remove the commits
linked to it. Before exclusion, every commit linked to an excluded work item is
re-attributed to that work item's nearest non-excluded ancestor. A commit is
never dropped because its only link was a Task.

Write the class and a comment block explaining that rule, so the next person
does not "simplify" it back into a plain filter.
```

### P20 — Feature hierarchy resolution

```
Create IWorkItemHierarchyResolver in Core.

Task<WorkItemHierarchy> ResolveAsync(IReadOnlyCollection<int> ids, CancellationToken ct);

It walks System.LinkTypes.Hierarchy-Reverse upward from each work item until it
reaches a work item of type "Feature" (configurable), or exceeds MaxHierarchyDepth,
or reaches the root.

PERFORMANCE — this is the part that will hurt if done naively:
- Do NOT call the ADO REST API once per work item. Batch with
  GET _apis/wit/workitems?ids=...&$expand=relations, max 200 ids per request.
- Deduplicate ids across the batch before requesting.
- Cache resolved ancestry for the duration of a run.
- Parent chains are shared; memoise so a Feature with 40 stories is walked once.

CORRECTNESS:
- Guard against cycles with a visited set. Malformed ADO links do happen.
- A work item that hits MaxHierarchyDepth without finding a Feature is an ORPHAN,
  not an error, and not a silent drop.
- If ADO returns 401, throw AdoUnauthorizedException. Never return partial
  hierarchy as if it were complete.

Write the interface, the implementation, and tests for: normal chain, orphan,
cycle, depth exceeded, batch boundary at exactly 200 and 201 ids.
```

### P21 — Single report model

```
Create CodeChurnReportModel in Core — the ONE shape that email, Excel, and export
all render from.

It must carry:
- Groups: Feature -> work items -> commits/files
- The orphan group, always present even when empty (with a zero count, so its
  absence is visibly zero rather than ambiguous)
- ExclusionSummary: for each excluded type, how many work items were suppressed
  and how many commits were re-attributed as a result
- CoverageGaps: commits with no resolvable work item at all

Then create CodeChurnReportBuilder that applies CodeChurnWorkItemPolicy and
produces this model.

Then audit #folder: [code churn reporting folder] and find every place that
currently filters, groups, or categorises work items inside a renderer. List
them. Those all need to be deleted and replaced by consumption of this model —
that duplication is why email and Excel disagree.

Do not change the renderers yet. Give me the list first.
```

### P22 — Renderer alignment

```
Update the three Code Churn renderers to consume CodeChurnReportModel and do no
filtering of their own:
1. the email renderer
2. the Excel exporter
3. the export report renderer

Each must render, in addition to the data:
- The Feature grouping, with the orphan bucket last
- A footer line stating the exclusions:
  "Excluded by policy: 47 Task work items (12 commits re-attributed to parents)."

That footer is required in ALL THREE outputs. Suppressed data that is invisible
is indistinguishable from data that was never there.

Show the diff per renderer. Confirm no renderer contains a work-item-type
condition after your change.
```

**Gate A:** run the same build through all three outputs. The Feature groups, the
row counts, and the exclusion footer must be identical across email, Excel, and
export. If they differ by one row, a renderer is still filtering.

---

# Workstream B — Pipeline Skip & Comments

## Design decisions to lock in first

**Skip is not Enabled.** You already have an `Enabled` checkbox on the WatchItem.
Keep them separate and define them clearly, or they will collide:
- `Enabled` = this WatchItem is armed to respond to trigger files.
- `Skip` = this node is bypassed during execution of a run.

**Skip cascades, and the cascade is computed, not stored.** A skipped Action Group
means its child Actions do not run. Store the flag only on the node the user
clicked; derive `IsEffectivelySkipped` by walking up. Storing it on children
creates a stale-state bug the first time someone unskips a parent.

**Skipped is a third result status.** Not success, not failure. `Skipped` appears
in the run report with the comment as its reason. A skipped step that reports
"passed" is a lie that will eventually cost you a debugging day.

**Skipping a revert is dangerous.** If someone skips `RevertWarmAgents` and the
run proceeds, every subsequent phase runs on a dirty machine. This needs a
pre-run confirmation listing exactly what will be skipped, plus a persistent
banner on the run report.

### P23 — Model and persistence

```
Extend the WatchList node model in TestControllerGrpc.Core.

Add to the node base type (WatchItem, Event, Action Group, Action):
    bool Skip { get; set; }                    // explicit, this node only
    string? SkipReason { get; set; }
    string? Comment { get; set; }              // free text, always shown
    DateTimeOffset? SkippedAtUtc { get; set; }
    string? SkippedBy { get; set; }

Add a computed, NOT stored:
    bool IsEffectivelySkipped => Skip || Parent?.IsEffectivelySkipped == true;

Explain in a comment why the cascade is computed rather than persisted on
children.

SERIALIZATION — backward compatibility is mandatory:
- Existing WatchList files have none of these attributes. They must load with
  Skip = false and null comment, with no warning and no migration step.
- Round-trip must preserve unknown attributes so a file edited by an older build
  is not silently stripped.
- Write as attribute skip="true" skipReason="..." and a <comment> child element.

Write the model change, the serializer change, and tests for: old file loads,
new file round-trips, unknown-attribute preservation.
```

### P24 — Execution gate

```
Add skip enforcement to the execution engine in Core.

1. Add Skipped to the result status enum (alongside Success/Failure). If the enum
   is persisted or sent over gRPC, assign it an explicit numeric value and do not
   renumber the existing members.

2. Create IExecutionGate:
       ExecutionDecision Evaluate(IWatchListNode node);
   returning Run or Skip-with-reason.

3. The executor consults the gate before dispatching each node. A skipped node:
   - is NOT dispatched
   - emits a result with status Skipped and the SkipReason/Comment as its detail
   - does NOT mark the parent as failed
   - does NOT stop the run

4. The run summary must count Skipped separately from Passed and Failed, and
   list every skipped node with its reason.

CRITICAL: enforcement lives in Core, at dispatch. It must NOT live in the WPF
tree or the React tree. Both front doors and any future scheduled trigger must
get identical behaviour from the one engine.

Write the gate, the executor change, and tests including: skipped parent means
children never dispatch, skipped node does not fail the run, summary counts.
```

### P25 — Pre-run confirmation

```
Add a pre-run skip confirmation to Core plus both hosts.

Before a run starts, if any node is effectively skipped, produce a
SkipManifest: the ordered list of every skipped node, its type, and its reason.

WPF and WebClient both show a confirmation dialog listing the manifest, with the
run only proceeding on explicit confirm.

ELEVATED WARNING: if any skipped node is a revert or machine-state operation,
the dialog shows a distinct, stronger warning — subsequent phases will run on a
machine that was not reset. Make that warning unmissable, not a grey footnote.

The manifest is also attached to the run record and rendered as a banner at the
top of the run report. A run with skipped steps must be visibly different from a
clean run, forever, in the stored report.
```

### P26 — WPF tree: context menu and rendering

```
#file: [the Test Plans TreeView XAML and its ViewModel]

Add skip support to the WPF WatchList tree.

Context menu (right-click on any node):
- Skip this node            (visible when not skipped)
- Skip this node with reason...  (opens a small reason prompt)
- Skip subtree
- Unskip                    (visible when skipped)
- Unskip subtree
- Edit comment...

Visual treatment:
- Explicitly skipped node: strikethrough text, 55% opacity, a "//" prefix glyph
- Effectively skipped via an ancestor: 55% opacity, NO strikethrough, tooltip
  "Skipped because parent 'X' is skipped"
- These two states must be visually distinguishable. A user needs to know which
  node to unskip.
- A node with a comment shows a small comment glyph; the comment is the tooltip.

Implementation notes:
- Gate the menu items on Permissions.PipelineSkip via CanExecute.
- Use a MultiBinding converter for the effective-skip visual state; do not
  duplicate the cascade logic in the converter — call the model property.
```

### P27 — WatchList editor comment field

```
#file: [the Node Properties editor view — the panel with GENERAL / SOURCE /
BUILD SOURCE sections]

Add a COMMENT section to the node properties editor.

Placement: directly under GENERAL, above SOURCE. It is documentation about the
node, so it belongs near the tag and enabled state.

- Multi-line TextBox, AcceptsReturn, 3 rows default, vertically expandable
- Watermark: "Why does this node exist? What should the next person know?"
- Character limit 2000, with a live counter appearing past 1500
- Below it, a read-only skip status line: when skipped, show
  "Skipped by {SkippedBy} on {SkippedAtUtc:g} — {SkipReason}" with an Unskip button

The comment is independent of skip. A node can have a comment and still run —
that is the readability use case, and it is the more common one.
```

### P28 — WebClient tree parity

```
#file: [the WebClient WatchList tree component]

Mirror the WPF behaviour in React:
- Right-click context menu with the same six items
- Same visual distinction between explicitly-skipped and cascade-skipped
- Comment glyph with tooltip
- Comment editor in the node properties panel
- Menu items gated on the pipeline.skip permission from usePermission()

Reuse the existing REST endpoints; add PATCH /api/watchlist/node/{id}/skip and
PATCH /api/watchlist/node/{id}/comment if they do not exist. Both must record
SkippedBy from IUserContext server-side — never trust a user identity sent from
the client.
```

**Gate B:** skip `RevertWarmAgents` on your nine-node plan. Confirm: the
confirmation dialog fires with the elevated warning, the run proceeds, the report
shows Skipped (not Passed), the banner is present, and the WARM pool machines
were genuinely not reverted.

---

## Cross-cutting checks before you call any of this done

| Check | Applies to |
|---|---|
| No `System.Windows` or ASP.NET Core types added to Core | A, B, C, D, F |
| Every new async method takes and honours a `CancellationToken` | all |
| Every new SQLite access sets `busy_timeout` and uses WAL | C, D |
| No secret in a log, exception message, or gRPC status detail | C |
| Config changes have startup validation with a named-file error | A, C, D, F |
| New database files live outside the `impact-index.db` directory | C, D |
| Provisioning changes are baked into `Prepare-AgentNode.ps1` | C |
| Every suppression/exclusion/skip is visible in the output, not just logs | A, B |
| A decision journal entry is written at the moment of decision | all |

## Decision journal entries to write as you go

Not afterwards. Retrospective entries come out thin.

1. Why Task exclusion re-attributes commits instead of dropping them
2. Why effective-skip is computed rather than persisted on child nodes
3. Why Default Mode is a permission policy rather than a bypass branch
4. Why ADO credentials are per-user rather than a single service token
5. Why the Data Protection key ring path is explicit
6. Why `Skipped` is a third result status rather than a flavour of pass
