# Phase 1b Context Pack — User Management (WPF Admin)

> Attach this file to every Copilot session while working on Phase 1b. Detach when Phase 1b ships and switch to `phase-2-context.md`.

> Companion reading (already in Copilot context via `.github/copilot-instructions.md`): `docs/rbac/01_System_Design.md` §7.1, `docs/rbac/02_Implementation_Roadmap.md` §Phase 1, `docs/rbac/04_UI_Mockup_Catalog.md` Mockup 7, `docs/architecture/CURRENT_STATE.md`, `docs/architecture/CONVENTIONS.md`.

---

## Goal

Ship the WPF-only Admin user management screen: list, create, edit, delete users, assign/revoke pipelines in bulk, and admin-initiated password reset. Exit when an Admin can fully manage the user roster from the WPF Controller, all operations are audit-logged, and the "Users" tab is capability-gated.

---

## Where things land in the existing solution

| Concern | Project |
|---|---|
| UserManagementPage, UserListView, UserDetailView, modal dialogs | **`TestControllerGrpc`** (`Views/Admin/`) |
| UserManagementViewModel, UserDetailViewModel, dialog VMs | **`TestControllerGrpc`** (`ViewModels/Admin/`) |
| UserManagementClient (HTTP calls to user endpoints) | **`TestControllerGrpc`** (`Services/UserManagementClient.cs`) |
| REST endpoints: CRUD, assign, revoke, reset-password | **`TestController.Api`** (`Controllers/UserController.cs`) |
| User service logic (validation, last-admin rule, cascading delete) | **`TestController.Api`** (`Services/UserService.cs`) |
| MainWindow "Users" tab (capability-gated) | **`TestControllerGrpc`** (`Views/MainWindow.xaml` — modify) |
| Tests | **`TestControllerGrpc.Tests/Rbac/`** + **`TestController.WebApi.Tests/Rbac/`** |

No new projects. All work lands in existing projects.

---

## File targets (18 tasks, 3 blocks)

### Block A — Server endpoints + service logic (no dependencies beyond Phase 0)

| # | Path | What |
|---|---|---|
| 1 | `TestController.Api/Controllers/UserController.cs` | REST endpoints: `GET /api/users`, `GET /api/users/{id}`, `POST /api/users`, `PUT /api/users/{id}`, `DELETE /api/users/{id}`, `POST /api/users/{id}/assign-pipelines`, `POST /api/users/{id}/revoke-pipelines`, `POST /api/users/{id}/reset-password`. All require `User_*` permissions. |
| 2 | `TestController.Api/Services/UserService.cs` | Business logic: create (hash password, set `MustChangePassword=true`), update, delete (last-admin check + cascade), list, assign/revoke bulk, reset-password (generate random, set flag). Singleton using `IDbContextFactory`. |

### Block B — WPF UI (depends on Phase 1a completion + Block A)

| # | Path | What |
|---|---|---|
| 3 | `TestControllerGrpc/Services/UserManagementClient.cs` | HTTP client wrapping UserController endpoints. Singleton, uses `IHttpClientFactory`. |
| 4 | `TestControllerGrpc/ViewModels/Admin/UserManagementViewModel.cs` | Master VM — `[ObservableProperty]` for `Users`, `SelectedUser`, `SearchText`, `RoleFilter`. `[RelayCommand]` for Load, Add, Delete, ResetPassword. Hosts sub-VMs. |
| 5 | `TestControllerGrpc/ViewModels/Admin/UserDetailViewModel.cs` | Detail pane VM for the selected user — displays metadata, assigned pipelines, quick actions. |
| 6 | `TestControllerGrpc/ViewModels/Admin/AddUserDialogViewModel.cs` | Dialog VM: username, email, role (default Engineer), auto-generated password, pipeline checkboxes. Validates username uniqueness before submit. |
| 7 | `TestControllerGrpc/ViewModels/Admin/AssignPipelinesDialogViewModel.cs` | Dialog VM: loads all WatchItems with checkboxes, pre-checks current assignments, submits diff (assign new + revoke removed) in one call. |
| 8 | `TestControllerGrpc/ViewModels/Admin/ResetPasswordDialogViewModel.cs` | Dialog VM: calls reset endpoint, displays generated password once, tracks `HasCopied` state. Modal cannot be dismissed until copied or explicitly cancelled. |
| 9 | `TestControllerGrpc/ViewModels/Admin/DeleteUserConfirmDialogViewModel.cs` | Dialog VM: shows user info + assignment count + audit-retention warning. Confirm button. |
| 10 | `TestControllerGrpc/Views/Admin/UserManagementPage.xaml(.cs)` | Master/detail layout per Mockup 7: left pane = searchable user list, right pane = detail. |
| 11 | `TestControllerGrpc/Views/Admin/UserListView.xaml(.cs)` | Left pane: search box, role filter pills, user rows (avatar + name + role + pipeline count). |
| 12 | `TestControllerGrpc/Views/Admin/UserDetailView.xaml(.cs)` | Right pane: header, metadata grid, assigned pipelines, quick actions. |
| 13 | `TestControllerGrpc/Views/Admin/AddUserDialog.xaml(.cs)` | Modal dialog for creating a user. |
| 14 | `TestControllerGrpc/Views/Admin/AssignPipelinesDialog.xaml(.cs)` | Modal dialog with WatchItem checkboxes. |
| 15 | `TestControllerGrpc/Views/Admin/ResetPasswordDialog.xaml(.cs)` | Modal showing generated password + copy button. |
| 16 | `TestControllerGrpc/Views/Admin/DeleteUserConfirmDialog.xaml(.cs)` | Confirmation modal with assignment count and audit warning. |
| 17 | `TestControllerGrpc/Views/MainWindow.xaml` (modify) | Add "Users" tab, visible only when `AuthClient.CurrentUser.Capabilities` contains `User_Create`. |

### Block C — Tests

| # | Path | What |
|---|---|---|
| 18 | `TestControllerGrpc.Tests/Rbac/UserManagementViewModelTests.cs` | VM tests: load users, create user → list refreshes, delete last admin → error, assign/revoke diff logic. |
| 19 | `TestController.WebApi.Tests/Rbac/UserCrudIntegrationTests.cs` | E2E: create user, list includes new user, update role → sessions revoked, delete cascades assignments, last-admin rule returns 409. |

---

## Sequence rules

- **Phase 1a must be complete** — login, `AuthClient`, `AuthController`, and the capability-gated badge must all work.
- **Block A first** — server endpoints before WPF UI (UI calls the endpoints).
- **Block B tasks 3–9 (service + VMs) before 10–17 (XAML)** — views bind to VMs.
- **Task 17 (MainWindow tab) depends on task 4** — the tab hosts the `UserManagementPage` whose DataContext is `UserManagementViewModel`.
- **Block C depends on A and B** being functional.
- **Within Block B**: task 3 (client) first, then 4 (master VM), then 5–9 (detail + dialog VMs), then 10–16 (views), then 17 (wire into MainWindow).

---

## Watch out for

1. **"Cannot delete last Administrator" (FR-USR-05) — enforced in `UserService.DeleteAsync`.** Before deleting, query `Users.Count(u => u.Role == Role.Administrator && u.IsActive && u.UserId != targetId)`. If zero, return error `FailedPrecondition`. This is the authoritative check — the UI also disables the Delete button when the selected user is the only active Admin, but the server rule is canonical.

2. **Delete cascades to PipelineAssignments (FR-USR-04) — single transaction.** `UserService.DeleteAsync` opens a transaction, deletes all `PipelineAssignments` for the user, then deletes the `User` row. If either fails, the whole transaction rolls back. Do NOT rely on EF cascade-delete configuration — do it explicitly for auditability (each removed assignment gets its own `User_Revoke` audit row before the `User_Delete` row).

3. **Reset password generates a strong random password, shows it ONCE with copy-to-clipboard, never stores plaintext, sets `MustChangePassword=true`.** The server generates a 16-char random password (mixed case + digit + one special char), hashes it, stores only the hash, returns the plaintext in the response ONCE. The `ResetPasswordDialog` displays it in a monospace read-only field and a "Copy to clipboard" button. The dialog cannot be closed via the X button or Escape until the user clicks "Copy" or "I've noted it" (explicit dismiss). Next login by that user forces password change.

4. **Assign/Revoke bulk operations happen in a single DB transaction.** The `POST /api/users/{id}/assign-pipelines` endpoint accepts `{ pipelineIds: [...] }` representing the DESIRED final set. The server computes the diff against current assignments and executes adds + removes in one transaction. Each individual add/remove gets its own audit row (`User_Assign` / `User_Revoke`).

5. **Username uniqueness enforced by DB unique index AND validated client-side before submit.** The `AddUserDialog` debounces username input and calls `GET /api/users?username={value}` to check existence. If taken, show inline error immediately (red border + "Username already exists"). The DB `UNIQUE` constraint is the backstop — if the race condition hits, the server returns `Conflict(409)` and the dialog shows a generic error.

6. **`AddUserDialog` requires Role selection — cannot create another Administrator via UI.** The role dropdown offers only `SeniorManager` and `Engineer`. Administrators are created only via the Default→Secured wizard (Phase 0.5) or the DB seed migration. This is a deliberate UI constraint per SRS §2.2; the server endpoint also rejects `Role.Administrator` in `CreateAsync` with `InvalidArgument`.

7. **Every user management action writes an audit row with the actor's identity and the target user as `ResourceId`.** Actions: `User_Create`, `User_Update`, `User_Delete`, `User_Assign`, `User_Revoke`, `User_ResetPassword`. The audit `ResourceId` is the target user's `UserId`. Fire-and-forget via `IAuditWriter` — do NOT await in the request path.

8. **The "Users" tab in MainWindow appears only when `AuthClient.CurrentUser` has `User_Create` capability.** Use the same capability-check pattern from Phase 1a: `AuthClient.CurrentUser.Capabilities.Contains("User_Create")`. Bind the tab's `Visibility` to a converter or computed property on `MainViewModel`. In Default mode (`RBAC:Enabled = false`) the tab is hidden — Default user has no `User_*` capabilities.

9. **AssignPipelines dialog: show all WatchItems with checkboxes; current assignments pre-checked; submit diffs.** Load the full WatchItem list from the existing `IWatchListXmlParser` or the pipelines endpoint. Pre-check items that are already assigned to the selected user. On submit, compute `toAssign = checked - alreadyAssigned` and `toRevoke = alreadyAssigned - checked`. Call the assign-pipelines endpoint with the new desired set. Disabled WatchItems are shown but grayed out and uncheckable (per Mockup 7).

10. **`ResetPasswordDialog`: shows new password inline ONCE; "Copy to clipboard" button; modal cannot be dismissed until copied or explicitly cancelled.** Set `Window.Closing` handler to cancel close if `!HasCopied && !ExplicitlyCancelled`. The "Copy to clipboard" button calls `Clipboard.SetText(password)` and sets `HasCopied = true`, which enables the "Done" button. Escape key is captured and shows a warning ("Password will be lost — are you sure?").

11. **`DeleteUserConfirmDialog`: shows the user's assignment count and warns about audit log preservation.** Body text: "{username} has {N} pipeline assignments that will be removed." + "Audit history for this user will be retained." The confirm button text is "Delete user" (not generic "OK"). Disabled for 2 seconds after dialog opens (prevents accidental rapid-click confirmation).

---

## Excluded from this pack (lands in later phases)

- Audit log viewer (Phase 10)
- WatchList filtering for engineers (Phase 2)
- Web Client user management (Admin uses WPF only)
- Bulk operations across multiple users
- Self-service password change (already in Phase 1a)

---

## How to start a task in this phase

Copy this skeleton when opening a Copilot session for any task in Block A–C:

```
[SPEC]
- docs/rbac/02_Implementation_Roadmap.md §Phase 1 — User Management
- docs/rbac/04_UI_Mockup_Catalog.md — Mockup 7 (User Management)
- docs/rbac/01_System_Design.md §7.1 — Users, PipelineAssignments tables

[CURRENT STATE]
- Phase 0 + 0.5 + 1a complete
- Tasks 1..N of Phase 1b already done (paths)
- Pending: this task (path)
- Existing patterns: see docs/architecture/CONVENTIONS.md §<section>

[TASK]
Generate <path>

[CONSTRAINTS]
- CommunityToolkit.Mvvm for WPF view models ([ObservableProperty], [RelayCommand])
- IAppLogger for logging, not Serilog
- Default Singleton DI lifetime (except DbContext)
- IDbContextFactory<OrchestratorDbContext> in service classes
- IAuditWriter fire-and-forget (never await in request path)
- Admin-only: all User_* endpoints require User_* permissions
- Do not modify Phase 0/0.5/1a files unless adding a DI registration

[OUTPUT]
- The file content
- One line: DI registration or route wiring (if applicable)
- Nothing else
```

---

## Exit checklist (don't move to Phase 2 until all green)

- [ ] Admin sees "Users" tab in MainWindow; non-Admin users do not
- [ ] Default mode: "Users" tab hidden (Default user has no User_* capabilities)
- [ ] Admin creates an Engineer via AddUserDialog; new user appears in list
- [ ] Username uniqueness validated inline during typing (debounced)
- [ ] Creating a user with existing username shows inline error + server returns 409
- [ ] Role dropdown offers only SeniorManager and Engineer (no Administrator option)
- [ ] Created user has `MustChangePassword = true`; first login forces change
- [ ] Admin assigns 3 pipelines to an Engineer; pipeline count badge updates
- [ ] Admin revokes 1 pipeline; count decreases; revoked pipeline unchecked in dialog
- [ ] Admin resets a user's password; dialog shows new password; copy works
- [ ] After reset, user's next login forces password change
- [ ] Admin deletes an Engineer; user vanishes from list; assignments cascade-deleted
- [ ] Deleting the only active Administrator returns error and button stays disabled
- [ ] Audit log contains rows for every CRUD + assign + revoke + reset action
- [ ] Non-admin calling any `/api/users/*` endpoint receives 403
- [ ] All Phase 0 + 0.5 + 1a tests still pass
