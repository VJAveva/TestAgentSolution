---
name: add-rbac-permission
description: 'Add a new RBAC Permission to TestAgentSolution: append-only enum member, mirrored in all four catalogs (C# enum, AuthorizationService buckets, PermissionCatalog, TS capabilities.ts) with the role counts in capabilities.test.ts updated. Use for new permission, gate a feature by role, RBAC capability.'
argument-hint: 'Permission name and which roles get it'
---

# Add an RBAC Permission

## Never
- **Never insert, reorder, renumber or delete** a `Permission` member. Members have implicit values and are persisted in SQLite and audit rows, so a removal or insertion silently rewrites history. **Append at the end only.**
- Never change only one catalog. A mismatch makes the UI contradict the API.

## The four places (all in one change)
1. **`TestControllerGrpc.Core/Authorization/Permission.cs`**: append the member with a one-line summary.
2. **`TestController.Persistence/Authorization/AuthorizationService.cs`**: put it in exactly the right bucket:
   - `IsAdminOnlyPermission`: Administrator only.
   - `IsSeniorManagerOnlyPermission`: Admin + SeniorManager. **Not** also in `IsReadPermission`.
   - `IsPipelineScopedPermission`: checked against a pipeline Tag (Engineer needs an assignment).
   - `IsReadPermission`: also grants **Guests and Default-mode web**. Only use it for genuinely read-only views.
   Administrator is an unconditional allow.
3. **`TestControllerGrpc.Core/Authorization/PermissionCatalog.cs`**: feeds `/api/auth/me` and WPF `CapabilityChecker`. Admin is `Enum.GetValues` (automatic). **SeniorManager / Engineer / Guest are explicit lists**, so add it to each role that gets it.
4. **`TestController.WebClient/src/lib/capabilities.ts`** `PERMISSION_CATALOG`: the TS mirror, a hand-maintained array per role.
   Then update **`src/lib/capabilities.test.ts`**: the `toHaveLength(n)` per role (Administrator / SeniorManager / Engineer / Guest) by +1 for each role that gained it.

## Check that the layers agree
For each role, write down the expected (role, permission, resourceId) results:
- `CapabilityChecker.Can(...)` (UX) must equal `AuthorizationService.CanAsync(...)` (authority) for both a **null** and a **real** resourceId.
- Known trap: a pipeline-scoped permission with null resourceId is allowed by `CapabilityChecker` for Engineers but denied by `CanAsync`.
- Known pre-existing drift: the TS Administrator list lacks some C# members (see `docs/hardening-plan/SECURITY-GAPS.md`). Don't "fix" the counts to hide it; add your +1 on top and mention the drift.

## Wire it (if asked)
- REST: `[RequirePermission(Permission.X)]` (skill `add-rest-endpoint`).
- WPF: `[RelayCommand(CanExecute = nameof(CanX))]` using `_capabilityChecker.Can(Permission.X, ...)`. Refresh it from `MainViewModel.OnCapabilitiesChanged` (both dispatcher branches). Disable rather than hide, and set `ToolTipService.ShowOnDisabled="True"`.
- WebClient: gate on `isSecured` as well, because the API fails open in Default mode.

## Verify
- C#: `dotnet build` shows 0 errors. Add or extend tests in `TestControllerGrpc.Tests` / `TestController.WebApi.Tests`: each role gets the expected allow/deny from `CanAsync`, and `PermissionCatalog` contains it for the right roles.
- TS: in `TestController.WebClient`, `npx vitest run src/lib/capabilities.test.ts` passes with the new counts.
- Red-proof: drop the member from one role's list and confirm a test fails.
- Report the member and its implied ordinal, the buckets, the roles, the four files and the new counts.
