# 05 — Default Mode Design

> **Purpose**: specify the "Default mode" operational state — a no-authentication, single-operator configuration that preserves the current behavior of the application while keeping a clean upgrade path to full RBAC ("Secured mode"). This is the out-of-box state for new installs and the fallback for deployments where full RBAC is not warranted.

> **Prerequisite**: read `00_Master_Plan.md` and `01_System_Design.md` first. This document references the `IUserContext`, `Permission` catalog, `IAuthorizationService`, `LockRegistry`, and audit subsystem defined there.

---

## 1. The Mode Model

The system operates in exactly one of two modes at any time:

| Mode | Authentication | Authorization | Web Client | Audit |
|---|---|---|---|---|
| **Default** | None | All WPF actions allowed; Web is read-only | Sees pipelines, tree, logs, dashboard; **cannot trigger** | Captured with synthetic "Default user" as actor |
| **Secured** | Required (server-side sessions) | Role-based, per-pipeline assignments | Full RBAC per the design in `01_System_Design.md` | Captured with real user identity |

**Default mode is the initial state of every new install.** It matches the behavior of the application before this RBAC work was undertaken, with one addition: Web Client read-only access. Operators who never need RBAC can stay in Default forever; operators who do can switch to Secured at any time via Settings.

Switching modes is a one-click operation on the WPF Settings panel (Mockup 10). The wire-level config that drives the mode is a single boolean: `RBAC:Enabled`.

---

## 2. Configuration

### 2.1 The flag

```jsonc
// appsettings.json (default values for new installs)
{
  "RBAC": {
    "Enabled": false              // false = Default mode; true = Secured mode
  }
}
```

The flag is also exposed at runtime via a writable config provider so that the WPF Settings UI can flip it without manual edits to the JSON file. Implementation: `IWritableOptions<RbacOptions>` (a thin wrapper over `IOptionsMonitor<RbacOptions>` plus a serializer that writes back to the same `appsettings.json`).

### 2.2 Persistence

The flag is persisted in the same `appsettings.json` that ships with the API. On Windows installs the canonical path is `C:\ProgramData\TestAgent\appsettings.json` (writable, separate from the read-only install directory). The flag survives service restarts, OS reboots, and upgrades.

### 2.3 When the flag changes

- **Default → Secured** (turn it on): triggered by the Settings UI's initial-Admin-creation wizard. After the wizard commits the first Administrator to the database, the flag flips to `true` and the auth middleware reloads. The WPF and Web clients immediately show login screens.
- **Secured → Default** (turn it off): triggered by a confirmation modal in Settings. Requires the actor to be an Administrator and to type the literal string `DISABLE RBAC` into a confirmation field. On commit, all sessions are revoked, the flag flips to `false`, and the synthetic Default user takes over.

Both transitions go through a single `IRbacModeTransitionService` that owns the orchestration so the steps are atomic — no partial states where the flag flipped but sessions weren't revoked.

---

## 3. The Synthetic Default User

When `RBAC:Enabled = false`, every gRPC call and every SignalR connection that lacks a valid session token is hydrated with the **synthetic Default user**:

```csharp
public static class DefaultUser
{
    // Stable UUID so audit entries correlate across restarts.
    public static readonly Guid UserId = new("00000000-0000-0000-0000-000000000001");

    public static IUserContext ForClient(ClientKind kind) => new SyntheticUserContext(
        userId:      UserId.ToString(),
        displayName: "Default user",
        clientKind:  kind,
        roles:       Array.Empty<string>());     // role list intentionally empty
}
```

Key properties:
- **One ID, fixed forever.** Lets audit reports correlate "what did the system do in Default mode" across the entire lifetime of the install.
- **Role list is empty.** The authz short-circuit (§4) doesn't consult roles in Default mode; the empty list makes it obvious to any later RBAC code path that this isn't a real authenticated user.
- **`ClientKind` is set from the connecting transport.** WPF connections get `ClientKind.Wpf`; Web Client connections get `ClientKind.Web`. This is the only piece of context that drives the authz decision.

The synthetic user is **never written to the `Users` table**. It exists only in memory as a `SessionAuthInterceptor` synthetic injection. Switching to Secured mode and back leaves no Default-user row to clean up.

---

## 4. Authorization Short-Circuit

`AuthorizationService.CanAsync` checks the RBAC flag *before* any role or assignment evaluation:

```csharp
public Task<AuthDecision> CanAsync(IUserContext user, Permission permission, string? resourceId = null,
                                   CancellationToken ct = default)
{
    if (!_options.CurrentValue.RBAC.Enabled)
    {
        if (user.ClientKind == ClientKind.Wpf)
            return Task.FromResult(AuthDecision.Allow("default-mode-wpf"));

        if (user.ClientKind == ClientKind.Web && IsReadPermission(permission))
            return Task.FromResult(AuthDecision.Allow("default-mode-web-read"));

        return Task.FromResult(AuthDecision.Deny("default-mode-web-readonly"));
    }

    // Normal RBAC evaluation continues here (see 01_System_Design.md §3.2)
    return EvaluateRbacAsync(user, permission, resourceId, ct);
}

private static bool IsReadPermission(Permission p) => p is
    Permission.Pipeline_View or
    Permission.Report_View;
```

**Rules in plain language:**
- WPF in Default mode → everything allowed.
- Web in Default mode + read permission (`Pipeline_View`, `Report_View`) → allowed.
- Web in Default mode + write permission (any `Pipeline_Trigger`, `Pipeline_Cancel`, etc.) → denied with reason `default-mode-web-readonly`.

The deny reason is surfaced in the gRPC `PermissionDenied` status detail and in the audit entry, so debugging "why can't Web trigger?" is one query: filter audit for `ReasonCode = "default-mode-web-readonly"`.

### 4.1 Permission catalog is unchanged

The `Permission` enum from `01_System_Design.md` §3.1 has the same members in both modes. Only the *evaluation* changes. This means:
- Adding a new permission still requires only changes to the enum and `CanAsync` (per NFR-MNT-01).
- Phase 0 and beyond write their code as if Secured mode is the only mode; the short-circuit in `CanAsync` makes Default mode "just work" without any feature having to special-case it.

---

## 5. Lock Model in Default Mode

The `LockRegistry` from `Pipeline_Lock_Coordination_Spec.md` works **unchanged** in Default mode. When WPF triggers a pipeline:

1. `LockRegistry.TryAcquire(pipelineId, owner, LockKind.Trigger)` is called with `owner = DefaultUser.ForClient(ClientKind.Wpf)`.
2. A `PipelineLock` row is created with `Owner.DisplayName = "Default user"` and `Owner.ClientKind = Wpf`.
3. SignalR broadcasts `PipelineLockAcquired` to all connected clients (including the Web read-only observers).
4. Web Client renders the lock badge from the broadcast payload — no special-casing — so the badge reads `Locked by Default user (WPF)` as shown in Mockup 11.

When the run completes (or WPF user cancels), the lock releases normally and the badge disappears.

### 5.1 What changes about locks in Default mode

- **No conflict dialog ever appears in Default mode**, because WPF is the sole writer. There is nothing to conflict with.
- **No "Take over and revert" button on Web** — Web cannot trigger, so it cannot acquire a lock; therefore force-release is moot. The conflict dialog (Mockup 6) and reason-capture dialog (Mockup 9) are simply never shown in Default mode.
- **Multi-WPF-window cases** (the same machine running two WPF instances) still go through the registry. The second instance's Trigger acquires a fresh lock (same `UserId`, same `DisplayName`) — `TryAcquire` treats it as a re-acquisition by the same owner (per Lock spec §4.3 invariant 1 and the "same user, two devices" edge case in §10.5).

---

## 6. Audit Logging in Default Mode

Audit entries are written exactly as in Secured mode. The only difference is the actor:

```jsonc
{
  "auditId": 42137,
  "userId": "00000000-0000-0000-0000-000000000001",   // Default user UUID
  "guestId": null,
  "roleAtTime": null,                                  // no role in Default mode
  "actionName": "Pipeline_Trigger",
  "resourceId": "WarmSetup-Four-Nodes",
  "decision": "Allow",
  "reasonCode": "default-mode-wpf",
  "timestampUtc": "2026-06-09T08:23:11.123Z",
  "clientKind": "Wpf",
  "correlationId": "..."
}
```

Operations history is therefore continuous across mode switches. An organization that runs in Default for six months, switches to Secured, and then queries the audit log for the last year will see:
- Six months of `userId = 00000000-…-0001, displayName = "Default user"` entries
- Followed by entries with real user UUIDs after the switch

No data is lost, no fields are nulled out. The audit viewer (Mockup 8) handles both kinds of rows uniformly — the `User` column just shows "Default user" instead of a username.

### 6.1 Schema impact

The `AuditEntries.RoleAtTime` column already permits NULL in the schema (per `01_System_Design.md` §7.1). In Default mode all audit entries are written with `RoleAtTime = NULL`. No schema change needed.

---

## 7. UI Differences Between Modes

| Surface | Default mode | Secured mode |
|---|---|---|
| WPF top banner | Amber banner: "Default mode — switch to Secured in Settings →" | Hidden |
| WPF user badge | "Default user" with dashed border + user icon | Real username + role-colored label |
| WPF tabs | Controller, WatchList, Agents, Dashboard, Settings | Controller, WatchList, Agents, Dashboard, **Users**, **Audit log**, Settings (Admin) |
| WPF Trigger / Cancel / Retry buttons | Enabled | Enabled (when authorized) |
| WPF login screen | Skipped | Shown |
| Web user badge | "Observer · Read only" with dashed border + eye icon | Real username + role-colored label, or Guest badge |
| Web Trigger button | Visibly disabled with tooltip | Enabled when authorized; disabled with "Pipeline is locked by [user]" tooltip on locked pipelines |
| Web tabs | All pipelines, Dashboard, Live log | My pipelines (Engineer) / All pipelines (SrMgr) / All pipelines (Admin/Guest), Dashboard, Reports |
| Web login screen | Skipped (direct entry as Observer) | Shown (with "Continue as Guest" option) |
| User management screen | Not accessible (no Users tab) | Admin only, per Mockup 7 |
| Audit log viewer | Not accessible (no Audit log tab) — entries still written | Admin only, per Mockup 8 |
| Conflict dialog (Mockup 6) | Never shown | Shown when triggering a locked pipeline |
| Force-release reason capture (Mockup 9) | Never shown | Shown when Admin/SrMgr clicks "Take over and revert…" |
| Lock badges | Shown on Web for running pipelines as "Locked by Default user (WPF)" | Real user identity in the badge |

Tab visibility is driven by the `capabilities[]` list from `/api/me`. In Default mode `/api/me` returns:
- From WPF: capabilities = `["Pipeline_*", "Settings_*"]` (everything except Users / Audit which don't apply)
- From Web: capabilities = `["Pipeline_View", "Report_View"]` only

The same client-side capability gates that hide Admin-only tabs in Secured mode hide the Users / Audit tabs in Default mode. No new UI code branches.

---

## 8. Mode Switch: Default → Secured

The typical upgrade path. Triggered from the WPF Settings > Security mode panel (Mockup 10).

### 8.1 Wizard flow

1. **Click "Switch to Secured mode…"** on the Default mode card.
2. **Initial Administrator screen** opens (modal, cannot be dismissed without canceling the whole switch).
   - Username (required, unique)
   - Email (required)
   - Password (required, min 12 chars)
   - Confirm password
3. **Click "Create Administrator and enable Secured mode"**.
4. **Atomic server transaction** runs inside `IRbacModeTransitionService.SwitchToSecuredAsync`:
   - Hash the password (bcrypt cost 12).
   - Insert the new Administrator row into `Users`.
   - Set `RBAC:Enabled = true` in writable config.
   - Reload the auth middleware (live, no restart required for this direction).
   - Audit entry: `ActionName = "System_SwitchToSecured"`, actor = Default user.
5. **WPF shows the login screen** immediately. Admin signs in with the credentials just created.
6. **Web Clients currently connected** receive a SignalR `SystemModeChanged` event → reload the page → see the login screen.

### 8.2 What's not done by the wizard

- **No other users are created.** The new Admin must create Senior Managers and Engineers through the User Management screen (Mockup 7) after sign-in.
- **No pipeline assignments are created.** Until the Admin assigns pipelines to Engineers, the only people who can trigger are Admin and any Senior Managers created.
- **The Web Client lock behavior changes immediately.** Existing in-flight pipelines that were locked by Default user are now locked by the new Admin (the lock owner identity is rewritten as part of the transition transaction). Web users see the badge update from "Locked by Default user (WPF)" to "Locked by [admin username] (WPF)" via a SignalR `PipelineLockRewritten` event.

### 8.3 Idempotency

If the wizard is started but cancelled or the process crashes mid-way:
- If `RBAC:Enabled` is still `false` and no Administrator row was committed → no-op, system stays in Default mode.
- If the Administrator row was committed but `RBAC:Enabled` is still `false` → recovery path is to either re-attempt the switch (which will detect the existing Admin row and skip creation) or delete the orphaned Administrator row. The transition service's atomic transaction prevents this in normal operation.

---

## 9. Mode Switch: Secured → Default

The rare direction. Triggered from the same WPF Settings panel, only visible to Administrators.

### 9.1 Confirmation flow

1. **Click "Switch to Default mode…"** on the Secured mode card (visible only in Secured mode + only to Admins).
2. **Warning modal** opens with a strong message:

   > Switching to Default mode will:
   > - Sign out all currently active users on both WPF and Web
   > - Disable the login screens on both clients
   > - Archive (not delete) all user accounts and pipeline assignments
   > - Continue all in-flight pipelines without interruption — they will be re-attributed to Default user
   > - Continue writing the audit log (entries from after the switch show "Default user" as actor)
   > - This is intended for testing or rollback scenarios, not normal operation
   >
   > Type **DISABLE RBAC** below to confirm.

3. **Type the confirmation string.** Submit button stays disabled until the typed string matches exactly.
4. **Atomic server transaction** runs inside `IRbacModeTransitionService.SwitchToDefaultAsync`:
   - Revoke all sessions (`UPDATE Sessions SET RevokedUtc = strftime(...) WHERE RevokedUtc IS NULL`).
   - Set `Users.IsActive = 0` for all users (archive, don't delete — preserves audit references).
   - For each active `PipelineLock`, rewrite the owner identity to the Default user. Broadcast `PipelineLockRewritten` SignalR events.
   - Set `RBAC:Enabled = false` in writable config.
   - Reload the auth middleware.
   - Audit entry: `ActionName = "System_SwitchToDefault"`, actor = the Admin who initiated. This is the **last** audit entry that has a real user identity until the next mode switch.
5. **WPF immediately drops the login screen.** Next time the app starts (or current window is reloaded), it goes straight to the main view as Default user.
6. **Web Clients reload** and become Observer (read-only).

### 9.2 Archive semantics

User rows are **archived, not deleted**, so that:
- The audit log's foreign references to `UserId` remain valid.
- A subsequent Secured-mode switch can reuse the same usernames (the wizard offers "Restore archived users" as a setup option).
- Pipeline assignments are preserved for restoration.

The actual data preserved:
- `Users` rows with `IsActive = 0`
- `PipelineAssignments` rows untouched (the FK to `Users(UserId)` resolves to an inactive row)
- `Sessions` rows preserved with `RevokedUtc` set

---

## 10. Out-of-Box Experience

A fresh install of TestController starts in Default mode. The new operator:

1. Installs the API + WPF Control Node on the controller machine.
2. Opens WPF — no login screen, sees the main view with the Default-mode banner.
3. Can trigger pipelines immediately. Productivity in <30 seconds.
4. Web Client at `controller.aveva.local` works out of the box for everyone on the network — read-only.
5. When the team grows or compliance asks, the Admin clicks "Switch to Secured mode" in Settings and goes through the wizard. <5 minutes to full RBAC.

This is materially better than forcing Secured mode at install time, which would require the operator to:
- Create the first user before the system can do anything
- Decide on a password
- Configure user assignments before any Engineer can use the system
- All before they've verified the install even works

Default mode lets verification come first; security comes when it's needed.

---

## 11. Migration from Existing Deployments

Customers who already run TestController (pre-RBAC version) upgrade to the new build and find themselves in Default mode by default. Concretely:

- The new build's `appsettings.json` ships with `RBAC:Enabled = false`. Their existing config is preserved by the installer.
- All existing pipelines, WatchList items, and run history continue to work.
- WPF behaves exactly as before from the user's perspective: no login screen, full operator capability.
- Web Client gains read-only access (or already had read-only access in the prior build — either way no regression).
- The Settings > Security mode panel is the discovery point for upgrading to RBAC.

**Customers who want to stay in Default forever can.** Default mode is not deprecated and not removed. It is a first-class, supported operational state.

---

## 12. Restart Requirements

| Transition | Service restart required? | Why |
|---|---|---|
| Default → Secured (live) | No | Adding auth middleware can be done by reloading `IOptionsMonitor<RbacOptions>` and re-registering interceptors. The auth interceptor's hot path checks the flag on each request. |
| Secured → Default (live) | No | Same. The interceptor's hot path checks the flag on each request and skips token validation when the flag is off. |
| Install of new binaries | Yes | Standard service upgrade. Independent of mode. |

The auth interceptor is implemented as:

```csharp
public class SessionAuthInterceptor : Interceptor
{
    private readonly IOptionsMonitor<RbacOptions> _options;
    private readonly ISessionStore _sessions;

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        IUserContext user;
        if (!_options.CurrentValue.RBAC.Enabled)
        {
            // Default mode: synthetic identity, no token expected.
            var clientKind = DetectClientKind(context);
            user = DefaultUser.ForClient(clientKind);
        }
        else
        {
            // Secured mode: validate session token.
            user = await ValidateSessionAsync(context) ?? throw new RpcException(new Status(StatusCode.Unauthenticated, "no-session"));
        }
        context.UserState["user"] = user;
        return await continuation(request, context);
    }
}
```

The `_options.CurrentValue` check is one indexed dictionary lookup — negligible cost on the hot path.

---

## 13. Testing Matrix

**Every feature delivered in Phases 0–10 must work in both Default and Secured modes.** The CI test suite reflects this:

```
TestProjects/
├── ControlNode.Tests.Default/        ← runs every test with RBAC:Enabled = false
│   └── DefaultModeFixture.cs
├── ControlNode.Tests.Secured/        ← runs every test with RBAC:Enabled = true
│   └── SecuredModeFixture.cs
└── ControlNode.Tests.Shared/         ← test cases authored once, parameterized by fixture
    ├── PipelineTriggerTests.cs
    ├── LockBehaviorTests.cs
    └── ...
```

A shared test base class wires up the fixture-specific configuration. Per-test attributes (`[FactInDefaultMode]`, `[FactInSecuredMode]`, `[FactInBothModes]`) drive which fixtures execute the test. The vast majority of tests carry `[FactInBothModes]`.

**Mode-specific tests** (small set):
- Default mode: synthetic user identity, audit row has `UserId = 00000000-…-0001`, Web write denied with `default-mode-web-readonly`.
- Secured mode: login flow, role-based denials, force-release dialog flow.

**Mode-transition tests**:
- Default → Secured wizard: idempotent, creates Admin, flips flag, rewrites lock owners, broadcasts `SystemModeChanged`.
- Secured → Default confirmation: archives users, revokes sessions, rewrites locks, broadcasts.
- In-flight pipeline survives a mode switch.

---

## 14. Implementation Notes (Phase 0 deliverables)

The Default mode mechanics are simple enough to fit inside Phase 0 with minimal effort. Concrete additions to the Phase 0 file list:

```
ControlNode.Core/Identity/DefaultUser.cs              ← static DefaultUser class
ControlNode.Core/Identity/SyntheticUserContext.cs     ← IUserContext implementation
ControlNode.Core/Configuration/RbacOptions.cs         ← strongly-typed options
ControlNode.Infrastructure/Configuration/WritableOptions.cs   ← writes back to appsettings.json
ControlNode.Application/SystemMode/IRbacModeTransitionService.cs
ControlNode.Application/SystemMode/RbacModeTransitionService.cs
ControlNode.Api/Services/MeController.cs              ← extended to return Default user identity in Default mode
ControlNode.Api/Services/SystemModeGrpcService.cs     ← Get / SwitchToSecured / SwitchToDefault RPCs
ControlNode.Api/Hubs/SystemModeBroadcaster.cs         ← broadcasts SystemModeChanged
```

Plus the AuthZ short-circuit added to the existing `AuthorizationService.CanAsync`.

The Settings UI (Mockup 10) and the Default mode banner UI (Mockup 11) ship in **Phase 0.5 — Default Mode UX**, a one-week mini-phase that runs between Phase 0 and Phase 1. Phase 0.5 deliverables:

- WPF Settings page with Security mode panel (Mockup 10)
- WPF Default-mode banner at the top of the main window (Mockup 11 top half)
- Default-user badge in the WPF top chrome (Mockup 11)
- Web Client Observer badge + disabled Trigger UI (Mockup 11 bottom half)
- Initial-Admin-creation wizard for Default → Secured switch
- DISABLE RBAC confirmation modal for Secured → Default switch

Estimated effort: **5 engineer-days** for Phase 0.5, assuming the Phase 0 mechanics are already in place.

---

## 15. Risks & Open Items

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Operator forgets they're in Default mode and exposes the system on a public network | Medium | High | Persistent yellow banner across the top of WPF. Documented in operator handbook. Future: optional network-restriction warning that detects external access in Default mode. |
| Audit entries from Default mode are mistaken for entries from a missing user during a forensics investigation | Low | Medium | The `default-mode-*` reason codes make Default-mode entries obvious in queries. Audit viewer's User column shows "Default user" with the dashed-border styling to visually distinguish them. |
| Mode-switch wizard left in a half-state by a process crash | Low | Medium | All mode-switch operations are atomic inside `IRbacModeTransitionService`. The recovery path on startup checks consistency (flag matches Admin presence) and surfaces a warning if inconsistent. |
| Customer in Default mode demands a hardening audit and the auditor sees "no authentication" | Low | Low | Default mode is documented and supported as a deliberate operational state. Customers in regulated environments are expected to run Secured mode; this is messaged in the operator handbook. |
| In-flight pipeline behavior across mode switch is confusing | Low | Low | The transition service preserves in-flight pipelines by rewriting the lock owner identity. Document this in the operator handbook. |

---

## 16. What This Document Does Not Cover

- **External identity providers** (Azure AD, Okta) — out of scope per SRS §13.
- **Multi-tenancy** — out of scope per SRS §13.
- **Migration to multi-controller deployment** — out of scope per OD-03 resolution.
- **Per-deployment custom mode names** (e.g., "Lab" vs "Production") — naming is fixed as "Default" and "Secured" for clarity.
- **Granular per-tab Default mode capability tweaks** — Default mode capabilities are fixed (WPF: write everything, Web: read everything). Anything finer requires Secured mode.

---

## 17. Cross-References

- `00_Master_Plan.md` §3.7 (Operational Mode decision)
- `01_System_Design.md` §3.4 (AuthZ short-circuit)
- `02_Implementation_Roadmap.md` Phase 0 (mechanics) + Phase 0.5 (UX)
- `04_UI_Mockup_Catalog.md` Mockup 10 (Settings) + Mockup 11 (Default mode in action)
- `Pipeline_Lock_Coordination_Spec.md` §4.3 (lock model, unchanged in Default mode)
