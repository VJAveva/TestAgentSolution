# Phase 1a Context Pack — Login & Identity

> Attach this file to every Copilot session while working on Phase 1a. Detach when Phase 1a ships and switch to `phase-1b-context.md`.

> Companion reading (already in Copilot context via `.github/copilot-instructions.md`): `docs/rbac/01_System_Design.md`, `docs/rbac/02_Implementation_Roadmap.md`, `docs/rbac/04_UI_Mockup_Catalog.md` (Mockups 1 & 2), `docs/architecture/CURRENT_STATE.md`, `docs/architecture/CONVENTIONS.md`.

---

## Goal

Ship login screens for both WPF and Web, post-login routing (skip in Default mode, enforce in Secured mode), first-login forced password change, and wire the `UserIdentityBadge` (created in Phase 0.5) to real authenticated user data via `GET /api/auth/me`. Exit when a user can log in from either client, see their real identity in the badge, and be forced to change password on first login.

---

## Where things land in the existing solution

| Concern | Project |
|---|---|
| WPF LoginPage, ChangePasswordPage, LoginViewModel, ChangePasswordViewModel | **`TestControllerGrpc`** (`Views/Login/`, `ViewModels/Login/`) |
| WPF auth service (token store, login/logout orchestration) | **`TestControllerGrpc`** (`Services/AuthService.cs`) |
| Web Client LoginView, ChangePasswordView, authStore | **`TestController.WebClient`** (`src/views/`, `src/stores/`) |
| `GET /api/auth/me` REST endpoint | **`TestController.Api`** (`Controllers/AuthController.cs`) |
| `ChangePasswordAsync` gRPC extension | **`TestController.Api`** (`Services/AuthGrpcService.cs` — extend existing) |
| Tests | **`TestControllerGrpc.Tests/Rbac/`** + **`TestController.WebApi.Tests/Rbac/`** |

No new projects are added in Phase 1a. All work lands in existing projects from Phase 0/0.5.

---

## File targets (14 tasks, 3 blocks)

### Block A — WPF Login UI (depends on Phase 0.5 completion)

| # | Path | What |
|---|---|---|
| 1 | `TestControllerGrpc/Views/Login/LoginPage.xaml(.cs)` | Login screen per Mockup 1 — username, password, Sign In button. Shield-check brand mark. |
| 2 | `TestControllerGrpc/ViewModels/Login/LoginViewModel.cs` | CommunityToolkit.Mvvm — `[ObservableProperty]` for fields, `[RelayCommand]` for `SignInAsync`. Calls `IAuthService.LoginAsync`. |
| 3 | `TestControllerGrpc/Views/Login/ChangePasswordPage.xaml(.cs)` | First-login forced password change — current password + new + confirm. |
| 4 | `TestControllerGrpc/ViewModels/Login/ChangePasswordViewModel.cs` | Validates matching new passwords, min-length 12, mixed case + digit. Calls `IAuthService.ChangePasswordAsync`. |
| 5 | `TestControllerGrpc/Services/AuthService.cs` | Wraps `AuthGrpcService` RPCs (`LoginAsync`, `LogoutAsync`, `ChangePasswordAsync`). Stores session token in-memory only. Exposes `CurrentUser` observable. |
| 6 | `TestControllerGrpc/App.xaml.cs` (modify) | Post-login routing: if `RBAC:Enabled == false` → navigate directly to MainWindow. If `true` → show LoginPage. After login, if `MustChangePassword` → show ChangePasswordPage before MainWindow. |

### Block B — Web Client Login (independent of Block A; can parallelize)

| # | Path | What |
|---|---|---|
| 7 | `TestController.WebClient/src/stores/useAuthStore.ts` | Zustand store: `user`, `token`, `isAuthenticated`, `mustChangePassword`, `login()`, `logout()`, `changePassword()`, `fetchMe()`. Token persisted in `sessionStorage`. |
| 8 | `TestController.WebClient/src/views/LoginView.tsx` | Login form per Mockup 1 — username, password, Sign In. "Continue as Guest" secondary button. |
| 9 | `TestController.WebClient/src/views/ChangePasswordView.tsx` | First-login forced change — current + new + confirm. Client-side validation mirrors server rules. |
| 10 | `TestController.WebClient/src/components/header/UserIdentityBadge.tsx` (modify) | Replace synthetic "Default user" / "Observer" data with real user from `useAuthStore`. Falls back to Default-mode display when `useSystemModeStore.isDefault`. |
| 11 | `TestController.WebClient/src/App.tsx` (modify) | Route guard: if mode is Secured and not authenticated → redirect to `/login`. If `mustChangePassword` → redirect to `/change-password`. Default mode skips login entirely. |

### Block C — Server endpoint + Tests (depends on Phase 0 services)

| # | Path | What |
|---|---|---|
| 12 | `TestController.Api/Controllers/AuthController.cs` | `GET /api/auth/me` — returns `{ userId, username, displayName, role, clientKind, capabilities[], mustChangePassword }`. `POST /api/auth/change-password` — requires current password verification. |
| 13 | `TestControllerGrpc.Tests/Rbac/LoginViewModelTests.cs` | ViewModel tests: login success → navigates, login failure → shows error, rate-limit hit → shows retry message. |
| 14 | `TestController.WebApi.Tests/Rbac/AuthMeEndpointTests.cs` | E2E: login → call `/api/auth/me` → correct user returned. Unauthenticated call → 401. First-login flag → `mustChangePassword: true`. |

---

## Sequence rules

- **Phase 0.5 must be complete** before starting Phase 1a — the `UserIdentityBadge`, `DefaultModeBanner`, `useSystemModeStore`, and `SystemModeClient` must all exist.
- **Block A and Block B are independent** — WPF and Web Client can be developed in parallel.
- **Block C task 12 (endpoint) can start immediately** — it only depends on Phase 0 services (`ISessionStore`, `IAuthorizationService`).
- **Block C tests (13, 14) depend on A and B** being functional.
- **Within Block A**: task 5 (AuthService) first, then 1–4 (UI consumes the service), then 6 (routing wires them together).
- **Within Block B**: task 7 (authStore) first, then 8–9 (views consume the store), then 10–11 (wiring).

---

## Watch out for

1. **Login is skipped entirely in Default mode (`RBAC:Enabled = false`).** The app navigates straight to MainWindow (WPF) or AppShell (Web). No login screen renders. The `DefaultModeBanner` from Phase 0.5 remains visible. Login flow only activates when mode is Secured.

2. **The Default-mode banner from Phase 0.5 still shows.** Do not remove or hide it in Phase 1a. In Default mode the banner stays; in Secured mode it was already hidden by Phase 0.5 logic. The two features coexist cleanly.

3. **First-login flag (`User.MustChangePassword`) forces redirect to ChangePassword before any other navigation.** Both clients must check this flag immediately after `LoginAsync` succeeds. If `true`, the user cannot navigate anywhere else — no back button, no URL manipulation. Only after `ChangePasswordAsync` succeeds does the flag clear and normal routing resume.

4. **Web Client: persist token in `sessionStorage` (not `localStorage`) for security.** `sessionStorage` is cleared when the tab closes, limiting token exposure. Never store tokens in cookies or `localStorage`. The `useAuthStore` hydrates from `sessionStorage` on page load to survive in-tab refreshes.

5. **WPF: persist nothing in app config — re-login required on app restart in Secured mode.** The session token lives only in the in-memory `AuthService.CurrentUser` property. On app close the token is gone. On next launch, the user sees the login screen again. This is intentional per `01_System_Design.md` §4.

6. **Login failures: rate-limit per username AND per source IP (NFR-SEC-03).** After 5 failed attempts within 60 seconds for the same username OR same IP, subsequent attempts return a throttle error. The client should display a clear message ("Too many attempts — try again in X seconds"). Do not expose which dimension triggered the limit.

7. **The `UserIdentityBadge` from Phase 0.5 is reused — only the data flow changes.** In Default mode it still shows "Default user" (WPF) or "Observer" (Web) with dashed border. In Secured mode after login, it is populated from `/api/auth/me` response data (username, role, initials). Same component, different data source. Do not create a new badge component.

8. **`ChangePassword` endpoint requires current password verification, even on first-login forced change.** The user must supply their current (initial) password plus the new one. This prevents a stolen session from being used to set a new password without knowing the old one. Server returns `InvalidArgument` if current password is wrong.

9. **Clear session on logout — server side AND client side.** `LogoutAsync` must: (a) delete the `Sessions` row in the DB, (b) clear the in-memory token on the client, (c) Web: clear `sessionStorage`, (d) navigate to login screen. Partial logout (client-only) leaves a dangling server session that could be replayed.

---

## Excluded from this pack (lands in phase-1b-context.md)

- User Management screen (Mockup 7)
- Add / Edit / Delete user flows
- Assign pipelines to users
- Admin-initiated password reset
- `UserGrpcService` CRUD RPCs
- "Last Administrator cannot be deleted" rule

---

## How to start a task in this phase

Copy this skeleton when opening a Copilot session for any task in Block A–C:

```
[SPEC]
- docs/rbac/02_Implementation_Roadmap.md §Phase 1 — <one line>
- docs/rbac/04_UI_Mockup_Catalog.md — Mockup 1 (Login) / Mockup 2 (Post-login identity)
- docs/rbac/01_System_Design.md §4 — auth flow

[CURRENT STATE]
- Phase 0 + Phase 0.5 complete
- Tasks 1..N of Phase 1a already done (paths)
- Pending: this task (path)
- Existing patterns: see docs/architecture/CONVENTIONS.md §<section>

[TASK]
Generate <path>

[CONSTRAINTS]
- CommunityToolkit.Mvvm for WPF view models ([ObservableProperty], [RelayCommand])
- Zustand store for React state (useAuthStore)
- apiFetch<T>() for API calls in Web Client
- IAppLogger for logging, not Serilog
- Default Singleton DI lifetime (except DbContext)
- Token in sessionStorage (Web), in-memory only (WPF)
- Do not modify Phase 0/0.5 files unless adding a DI registration

[OUTPUT]
- The file content
- One line: DI registration or route wiring (if applicable)
- Nothing else
```

---

## Exit checklist (don't move to Phase 1b until all green)

- [ ] WPF in Default mode (`RBAC:Enabled = false`) opens directly to MainWindow — no login screen
- [ ] WPF in Secured mode shows LoginPage on startup
- [ ] WPF login with valid credentials navigates to MainWindow; badge shows real username + role
- [ ] WPF login with `MustChangePassword = true` forces ChangePasswordPage before MainWindow
- [ ] WPF logout clears in-memory token and returns to LoginPage
- [ ] Web Client in Default mode loads AppShell directly — no login screen, Observer badge shows
- [ ] Web Client in Secured mode redirects to `/login`
- [ ] Web Client login with valid credentials → badge shows real username + role
- [ ] Web Client "Continue as Guest" → badge shows Guest variant with dashed border and expiry hint
- [ ] Web Client first-login forced change → `/change-password` → success → normal navigation
- [ ] `GET /api/auth/me` returns correct user data for authenticated session
- [ ] `GET /api/auth/me` returns 401 for unauthenticated request
- [ ] Rate-limit triggers after 5 failed attempts within 60s (same username or same IP)
- [ ] Logout clears session server-side (session row deleted) and client-side (token gone)
- [ ] All Phase 0 + Phase 0.5 tests still pass
