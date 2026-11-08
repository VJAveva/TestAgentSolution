# Phase 2b Context Pack — WPF Capability Gating & Filtered Pipeline View

> Attach this file to every Copilot session while working on Phase 2b. Detach when Phase 2b ships and switch to `phase-2c-context.md`.

> Companion reading (already in Copilot context via `.github/copilot-instructions.md`): `docs/rbac/01_System_Design.md` §3 (authorization), `docs/rbac/02_Implementation_Roadmap.md` §Phase 2, `docs/rbac/04_UI_Mockup_Catalog.md` Mockup 2 + Mockup 4 (Engineer "My pipelines" header), `docs/architecture/CURRENT_STATE.md` "WPF View Structure" + "MainViewModel partials".

---

## Goal

Ship the WPF-side capability layer so Engineers only see assigned pipelines and UI buttons disable/enable based on permission state. After Phase 2b:

- Engineers in Secured mode see a filtered TreeView ("My pipelines — 3 of 7 visible")
- Trigger/Cancel/TriggerAll buttons respect role-based `CanExecute` predicates
- If the server denies a request that the UI allowed (stale assignments), a friendly dialog shows and triggers a refresh
- In Default mode, **zero visible change** vs the pre-RBAC build — all pipelines shown, all buttons enabled

No WebClient work in this phase (that's Phase 2c).

---

## Where things land in the existing solution

| Concern | Project / File |
|---|---|
| `CapabilityChecker` (synchronous permission checks for UI bindings) | **`TestControllerGrpc`** (`Services/CapabilityChecker.cs`) |
| `CurrentUserHolder` (active user context for WPF session) | **`TestControllerGrpc`** (`Services/CurrentUserHolder.cs`) |
| `FilteredWatchItems` property + "N of M" label | **`TestControllerGrpc`** (`ViewModels/MainViewModel.cs` — same partial that owns `TreeRoots` and `_config`) |
| `CanExecute` predicate changes | **`TestControllerGrpc`** (`ViewModels/MainViewModel.Execution.cs` — modify existing predicates) |
| `AuthorizationDeniedDialog` | **`TestControllerGrpc`** (`Views/Dialogs/AuthorizationDeniedDialog.xaml` + `.xaml.cs`) |
| DI registrations | **`TestControllerGrpc`** (`App.xaml.cs` — modify) |
| Tests | **`TestControllerGrpc.Tests/Rbac/`** |

No new projects. All work lands in existing projects.

---

## File targets (8 tasks, 3 blocks)

### Block A — CapabilityChecker + CurrentUserHolder (no dependencies beyond Phase 2a)

| # | Path | What |
|---|---|---|
| 1 | `TestControllerGrpc/Services/CurrentUserHolder.cs` | Singleton. Holds the active `IUserContext`. Properties: `User` (get), `IsSecuredMode` (get). Methods: `SetUser(AuthUserInfo)`, `Clear()`, `SetDefaultUser()`. Raises `UserChanged` event. Wired by `AuthClient.AuthStateChanged` (login → `SetUser`, logout → `Clear`). In Default mode, `SetDefaultUser()` sets a `SyntheticUserContext` with `DefaultUser` values + all pipeline tags as `AssignedPipelineIds`. |
| 2 | `TestControllerGrpc/Services/CapabilityChecker.cs` | Singleton. Synchronous API: `bool Can(Permission permission, string? resourceId = null)`. Logic: (a) if Default mode → `true` for WPF; (b) if user is Admin → `true`; (c) check `PermissionCatalog.GetPermissionsForRole(role)` contains the permission; (d) if `resourceId` != null and permission is resource-scoped → check `User.AssignedPipelineIds.Contains(resourceId)`. Raises `CapabilitiesChanged` event on `CurrentUserHolder.UserChanged`. Does NOT hit the network or DB — purely cached state. |

### Block B — FilteredWatchItems + UI integration (depends on Block A)

| # | Path | What |
|---|---|---|
| 3 | `TestControllerGrpc/ViewModels/MainViewModel.cs` (modify) | Add: `ReadOnlyObservableCollection<TreeNodeViewModel> FilteredWatchItemNodes` backed by a `CollectionViewSource` or manual membership list on `WatchListRoot.Children`. Add `[ObservableProperty] private string _pipelineFilterLabel = ""` (shows "Showing 3 of 7 pipelines" for Engineers; empty otherwise). Subscribe to `CapabilityChecker.CapabilitiesChanged` → call `RefreshFilteredPipelines()`. |
| 4 | `TestControllerGrpc/ViewModels/MainViewModel.cs` (modify) | `RefreshFilteredPipelines()` method: if Default mode or Admin/SrMgr → all children visible, label empty. If Engineer → hide children whose `Tag` is not in `CurrentUserHolder.User.AssignedPipelineIds`. Use `TreeNodeViewModel.IsVisible` property to toggle visibility rather than rebuilding the collection (preserves expansion state). |
| 5 | `TestControllerGrpc/ViewModels/MainViewModel.Execution.cs` (modify) | Change `CanTriggerEvent`, `CanTriggerWatchItem`, `CanTriggerAll`, `CanCancel` predicates to include `_capabilityChecker.Can(Permission.Pipeline_Trigger, tag)` / `_capabilityChecker.Can(Permission.Pipeline_Cancel)`. Subscribe to `CapabilityChecker.CapabilitiesChanged` → marshal to UI thread → `NotifyExecutionCanExecuteChanged()`. |

### Block C — AuthorizationDeniedDialog + Refresh (depends on Block A + B)

| # | Path | What |
|---|---|---|
| 6 | `TestControllerGrpc/Views/Dialogs/AuthorizationDeniedDialog.xaml(.cs)` | Simple modal: shield icon, "Permission Denied" title, message body (from `PipelineAuthorizationDeniedException.HumanReadable`), "Refreshing your permissions…" progress hint, OK button. On open, fires a background `/api/auth/me` refresh via `AuthClient.FetchMeAsync()` which cascades through `CurrentUserHolder` → `CapabilityChecker.CapabilitiesChanged` → UI rebind. |
| 7 | `TestControllerGrpc/ViewModels/MainViewModel.Execution.cs` (modify) | In `TriggerEvent()` / `TriggerWatchItem()` / `CancelExecution()` catch block for `PipelineAuthorizationDeniedException`: instead of just `AddLog`, also show `AuthorizationDeniedDialog`. After the dialog closes the UI is already updated (refresh happened in background). |
| 8 | `TestControllerGrpc.Tests/Rbac/CapabilityCheckerTests.cs` | Unit tests: Default-mode always returns true, Admin always true, Engineer+assigned returns true, Engineer+unassigned returns false, CapabilitiesChanged fires on user change, SrMgr sees all pipeline permissions without assignment check. |

---

## Sequence rules

- **Phase 2a must be complete** — `PipelineAuthorizationGuard` and `PipelineAuthorizationDeniedException` must exist.
- **Block A first** — `CurrentUserHolder` and `CapabilityChecker` before any UI integration.
- **Block B depends on Block A** — `MainViewModel` uses `CapabilityChecker`.
- **Block C depends on A + B** — the dialog shows on denial, and the refresh re-evaluates filtering.
- **Task 5 (CanExecute changes) and Task 3/4 (filtering) are independent** within Block B — can be done in parallel.

---

## Watch out for

1. **CapabilityChecker is a UX hint, NOT security.** The server-side guard from Phase 2a (`PipelineAuthorizationGuard` → `IAuthorizationService.CanAsync`) remains the source of truth. NEVER remove a server check because the UI already gates it. Client-side gating prevents wasted round-trips; server-side gating prevents unauthorized operations.

2. **Default mode: FilteredWatchItems == all items, all buttons enabled.** Zero visible change vs the pre-RBAC build. The regression test must load a WatchList with 7 items, set `RBAC:Enabled = false`, and assert all 7 are visible and all Trigger commands report `CanExecute = true`.

3. **CanExecute predicates must be synchronous — no async, no DB calls.** `CapabilityChecker.Can(...)` works entirely off the cached `IUserContext` held by `CurrentUserHolder`. The set of `AssignedPipelineIds` is fetched once via `/api/auth/me` and cached until next refresh.

4. **Assignment staleness window.** When an admin edits assignments (Phase 1b Users tab), the Engineer's WPF session won't know until the next refresh. Strategy: (a) subscribe to a SignalR `AssignmentsChanged` event on `/hubs/controller` if it exists (check first); (b) if not, add a 60-second timer that calls `AuthClient.FetchMeAsync()` to re-pull assignments; (c) always refresh on `PipelineAuthorizationDeniedException` (the dialog does this already). The timer is cheap — `/api/auth/me` is a single DB read cached on the session.

5. **MainViewModel is split into 12+ partials.** `FilteredWatchItemNodes` and `_pipelineFilterLabel` go in `MainViewModel.cs` (the partial that owns `TreeRoots`, `_config`, and `WatchListRoot`). Do NOT create a new partial file. The `RefreshFilteredPipelines()` method also belongs there, next to `RebuildAllTrees()` which is in `MainViewModel.Helpers.cs` — but since it references `_config` and `TreeRoots` directly, it's cleaner in the main partial.

6. **ObservableCollection filtering: don't rebuild the collection on every change.** Rebuilding `TreeRoots` collapses expanded nodes. Instead, toggle `TreeNodeViewModel.IsVisible` (or an equivalent `Visibility` property) on the WatchItem-level children of `WatchListRoot`. The TreeView XAML already binds `Visibility` or should use a `BoolToVisibilityConverter`. Only call `OnPropertyChanged(nameof(FilteredWatchItemNodes))` to notify the count label; the tree itself observes individual node visibility changes.

7. **"Showing N of M pipelines" count label appears only for Engineers in Secured mode.** Set `PipelineFilterLabel` to `""` for Admin/SrMgr/Default. Set it to `$"Showing {visible} of {total} pipelines"` for Engineers. Bind label `Visibility` to `string.IsNullOrEmpty(PipelineFilterLabel)` via converter.

8. **Dispatcher: `CapabilitiesChanged` may fire from a background thread** (SignalR callback, timer callback, or `FetchMeAsync` continuation). Before calling `NotifyCanExecuteChanged()` or modifying `TreeNodeViewModel.IsVisible`, marshal to the UI thread via `Application.Current.Dispatcher.Invoke(...)`. Use `Dispatcher.CheckAccess()` to skip marshaling when already on UI thread.

9. **`CurrentUserHolder.SetUser(AuthUserInfo)` must map `AuthUserInfo` to `IUserContext`.** Build a `SyntheticUserContext` from the `AuthUserInfo` fields. `AssignedPipelineIds` must come from an expanded `/api/auth/me` response (add `AssignedPipelineIds: List<string>` to `MeResponse` and `AuthUserInfo` if not already present). If the current `/api/auth/me` endpoint doesn't return assignments, extend it in this phase — it's a one-line change to `AuthController.Me()`.

10. **`CapabilityChecker` is Singleton DI** (per `CONVENTIONS.md`). Constructor injects `CurrentUserHolder` and `IOptionsMonitor<RbacOptions>`. It does NOT inject `IAuthorizationService` — it replicates the rules client-side using `PermissionCatalog` (which is a static class, no DI needed).

11. **The existing `NotifyExecutionCanExecuteChanged()` in `MainViewModel.cs` already notifies all execution commands.** Reuse it from the `CapabilitiesChanged` handler. Don't duplicate the command list.

12. **`AuthClient.AuthStateChanged` already exists** (fires on login/logout). Wire `CurrentUserHolder` to subscribe to it in DI setup or in the `CurrentUserHolder` constructor. The flow is: `AuthClient.AuthStateChanged` → `CurrentUserHolder.SetUser/Clear` → `CurrentUserHolder.UserChanged` → `CapabilityChecker.CapabilitiesChanged` → `MainViewModel` rebinds.

---

## `/api/auth/me` response extension

If `MeResponse` / `AuthUserInfo` do not already include `AssignedPipelineIds`, add them in this phase:

```csharp
// AuthController.Me() — after building capabilities:
var assignedPipelineIds = user.AssignedPipelineIds.ToList();

return Ok(new MeResponse(
    // ...existing fields...
    AssignedPipelineIds: assignedPipelineIds));
```

```csharp
// MeResponse record — add field:
public sealed record MeResponse(
    // ...existing fields...
    List<string> AssignedPipelineIds);

// AuthUserInfo record — add field:
public sealed record AuthUserInfo(
    // ...existing fields...
    List<string> AssignedPipelineIds);
```

This is the only change outside `TestControllerGrpc/` in this phase.

---

## How to start a task in this phase

Copy this skeleton when opening a Copilot session for any task in Block A–C:

```
[SPEC]
- docs/rbac/01_System_Design.md §3 — Authorization Design
- docs/rbac/04_UI_Mockup_Catalog.md Mockup 2 + Mockup 4 — Engineer filtered view
- docs/rbac/02_Implementation_Roadmap.md §Phase 2 — Pipeline Trigger + Cancel
- docs/architecture/CURRENT_STATE.md — MainViewModel partials, WPF MVVM

[CURRENT STATE]
- Phase 0 + 0.5 + 1a + 1b + 2a complete
- Tasks 1..N of Phase 2b already done (paths)
- Pending: this task (path)
- Existing patterns: MainViewModel.cs owns TreeRoots + _config, .Execution.cs owns trigger/cancel

[TASK]
Generate <path>

[CONSTRAINTS]
- CapabilityChecker is Singleton, synchronous Can(), UX-only (not security)
- CurrentUserHolder is Singleton, holds IUserContext, raises UserChanged
- FilteredWatchItems in MainViewModel.cs (same partial as TreeRoots)
- Do NOT rebuild TreeRoots — toggle node visibility instead
- CanExecute must be synchronous — no async, no DB
- Marshal CapabilitiesChanged to UI thread before NotifyCanExecuteChanged
- Default mode = all visible, all enabled, zero visible change
- CommunityToolkit.Mvvm [ObservableProperty] + [RelayCommand]
- IAppLogger for logging, not Serilog
- Singleton DI unless Scoped required

[OUTPUT]
- The file content (or surgical diff for modifications)
- One line: DI registration (if applicable)
- Nothing else
```

---

## Exit checklist (don't move to Phase 2c until all green)

- [ ] Default mode: 7 pipelines loaded, all 7 visible in TreeView, Trigger/Cancel/TriggerAll `CanExecute == true`
- [ ] Secured mode + Admin: same as Default — all 7 visible, all buttons enabled
- [ ] Secured mode + SeniorManager: all 7 visible, Trigger/Cancel enabled, TriggerAll enabled
- [ ] Secured mode + Engineer (assigned to 3 of 7): only 3 WatchItem nodes visible, label shows "Showing 3 of 7 pipelines"
- [ ] Engineer Trigger button: enabled for visible (assigned) items, disabled for unassigned (if somehow selected via code)
- [ ] Engineer TriggerAll: triggers only assigned pipelines (guard prevents unassigned anyway)
- [ ] Server denies trigger (assignment revoked mid-session): `AuthorizationDeniedDialog` shows, permissions auto-refresh, button disables after refresh
- [ ] After denial + refresh: Engineer sees updated filter (now 2 of 7 if one was revoked)
- [ ] TreeView expansion state preserved after `RefreshFilteredPipelines()`
- [ ] `CapabilitiesChanged` fires from background thread: no crash, UI updates correctly (dispatcher marshal)
- [ ] `CapabilityChecker.Can()` never hits network or DB — purely cached state
- [ ] 60-second refresh timer operational (or SignalR `AssignmentsChanged` subscription if available)
- [ ] All Phase 0 + 0.5 + 1a + 1b + 2a tests still pass (regression)
- [ ] No changes to `IAuthorizationService`, `PipelineAuthorizationGuard`, or `IActionPipelineExecutor`
