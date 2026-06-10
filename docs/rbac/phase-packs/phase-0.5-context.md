# Phase 0.5 Context Pack — Default Mode UX

> Attach this file to every Copilot session while working on Phase 0.5. Detach when Phase 0.5 ships and switch to `phase-1-context.md`.

> Companion reading (already in Copilot context via `.github/copilot-instructions.md`): `docs/rbac/01_System_Design.md`, `docs/rbac/05_Default_Mode_Design.md`, `docs/rbac/04_UI_Mockup_Catalog.md`, `docs/architecture/CURRENT_STATE.md`, `docs/architecture/CONVENTIONS.md`.

---

## Goal

Ship the Default-mode user experience so a fresh install is usable end-to-end without RBAC setup, and provide the visible UI for switching between Default and Secured modes. Exit when both WPF and Web Client render mode-appropriate chrome, the Initial Admin wizard works, and `SystemModeChanged` triggers live reloads in both clients.

---

## Where things land in the existing solution

The design docs use `ControlNode.*` project names. The actual codebase maps as follows:

| Concern | Project |
|---|---|
| WPF Settings page, Security mode panel, banner, badge, wizard, confirm dialog | **`TestControllerGrpc`** (WPF host — add `Views/Settings/`, `Controls/`, `ViewModels/Settings/` folders) |
| WPF SystemMode client service | **`TestControllerGrpc`** (`Services/`) |
| Web Client banner, badge, disabled trigger, SignalR handler | **`TestController.WebClient`** (`src/components/`, `src/hooks/`, `src/stores/`, `src/signalr/`) |
| SystemMode SignalR hub event (server-side broadcast) | **`TestController.Api`** (existing `Hubs/` — extend the hub with `SystemModeChanged` event) |
| Tests | **`TestControllerGrpc.Tests/Rbac/`** + **`TestController.WebApi.Tests/Rbac/`** |

No new projects are added in Phase 0.5. All work lands in existing projects from Phase 0.

---

## File targets (15 tasks, 3 blocks)

### Block A — WPF UI (depends on Phase 0 completion)

| # | Path | What |
|---|---|---|
| 1 | `TestControllerGrpc/Views/Settings/SettingsView.xaml(.cs)` | Settings page host with Security mode panel (Mockup 10 per `04_UI_Mockup_Catalog.md`) |
| 2 | `TestControllerGrpc/Views/Settings/SecurityModePanel.xaml(.cs)` | Side-by-side mode cards showing Default ACTIVE vs Secured; switch buttons |
| 3 | `TestControllerGrpc/ViewModels/Settings/SecurityModeViewModel.cs` | CommunityToolkit.Mvvm — `[ObservableProperty]` for mode state, `[RelayCommand]` for switch actions |
| 4 | `TestControllerGrpc/Views/Settings/InitialAdminWizard.xaml(.cs)` | Multi-step wizard: username + password + confirm → creates first user AND flips flag in one transaction |
| 5 | `TestControllerGrpc/Views/Settings/DisableRbacConfirmDialog.xaml(.cs)` | Modal requiring exact-string typing to confirm Secured → Default transition |
| 6 | `TestControllerGrpc/Controls/DefaultModeBanner.xaml(.cs)` | Fixed (non-dismissible) banner at main window top; visible only in Default mode |
| 7 | `TestControllerGrpc/Controls/UserIdentityBadge.xaml(.cs)` | Handles all role variants: Admin, SrMgr, Engineer, Guest, Observer, Default user — dashed border for Default |
| 8 | `TestControllerGrpc/Services/SystemModeClient.cs` | Wraps `SystemModeGrpcService` RPCs; subscribes to `SystemModeChanged` SignalR for live reload |

### Block B — Web Client (independent of Block A; can parallelize)

| # | Path | What |
|---|---|---|
| 9 | `TestController.WebClient/src/stores/useSystemModeStore.ts` | Zustand store: `mode`, `isDefault`, `isSecured`, `fetchMode()`, `onModeChanged()` |
| 10 | `TestController.WebClient/src/components/header/DefaultModeBanner.tsx` | Fixed banner — rendered when `mode === 'default'` |
| 11 | `TestController.WebClient/src/components/header/UserIdentityBadge.tsx` | All variants: Admin/Engineer/SrMgr/Guest/Observer/Default user; Observer badge in Default mode |
| 12 | `TestController.WebClient/src/components/common/DisabledTriggerButton.tsx` | Disabled trigger button with tooltip explaining Default-mode restriction |
| 13 | `TestController.WebClient/src/signalr/SystemModeEvents.ts` | Subscribes to `SystemModeChanged`; calls `useSystemModeStore.getState().fetchMode()` then triggers reload |

### Block C — Tests (depends on A and B)

| # | Path | What |
|---|---|---|
| 14 | `TestControllerGrpc.Tests/Rbac/SecurityModeViewModelTests.cs` | ViewModel tests: switch commands, wizard completion, disable-confirm validation |
| 15 | `TestController.WebApi.Tests/Rbac/ModeSwitchE2ETests.cs` | E2E: mode switch → SignalR event fires → `/api/system/mode` returns new state |

---

## Sequence rules

- **Phase 0 must be fully complete** before starting Phase 0.5 — all mechanics (flag, synthetic user, authz, interceptor, mode-transition service, SignalR hub) must exist.
- **Block A and Block B are independent** — WPF and Web Client can be developed in parallel by separate engineers.
- **Block C depends on both A and B** — integration tests exercise the full stack.
- **Within Block A**: tasks 1–3 (Settings page shell) before 4–5 (wizard/dialog); task 6–7 (banner/badge) are independent of Settings and can start immediately.
- **Within Block B**: task 9 (store) first, then 10–13 in any order (all consume the store).

---

## Watch out for

1. **Use CommunityToolkit.Mvvm `[ObservableProperty]` and `[RelayCommand]`** for `SecurityModeViewModel` and any other new WPF view models. NO manual `INotifyPropertyChanged`. Per `CONVENTIONS.md`.

2. **Use Zustand stores in React, not TanStack Query.** The system-mode state goes in `useSystemModeStore` — a plain Zustand store with `fetchMode()` action calling `apiFetch<T>()`. Do NOT introduce TanStack Query, React Query, or SWR.

3. **The Default mode banner is fixed (non-dismissible) by design.** Per `02_Implementation_Roadmap.md` Phase 0.5 Risks table: "Banner is fixed (not dismissible) by design — it is the discoverability anchor for the upgrade path." Do not add a close button or localStorage dismiss state.

4. **`UserIdentityBadge` handles ALL role variants** — Admin, SrMgr, Engineer, Guest, Observer, and Default user. WPF uses a `DataTemplate` selector or converter; Web uses a role-to-icon/color map. The same component renders in both Default mode (showing "Default user" or "Observer") and Secured mode (showing the authenticated user's role). Do not split this into multiple badge components.

5. **`SystemModeChanged` SignalR event triggers both clients to reload.** WPF subscribes via `SystemModeClient`; Web subscribes via `SystemModeEvents.ts`. On receiving the event: WPF reloads its shell (shows login screen if now Secured, removes it if now Default); Web Client reloads the page or navigates to login. The Web Client also polls `/api/system/mode` on SignalR reconnect as a fallback (per Risks table in `02_Implementation_Roadmap.md`).

6. **Initial Admin wizard creates the first user AND flips the flag in one transaction.** This is a single call to `IRbacModeTransitionService.SwitchToSecuredAsync(username, password)` — it creates the User row, hashes the password, inserts it, then flips `RBAC:Enabled = true` atomically. If the user creation fails, the flag must NOT flip. The wizard UI just collects inputs and calls one service method.

7. **DISABLE RBAC confirmation requires typing the exact string** (e.g., `DISABLE RBAC`). This prevents accidental mode switches. The confirm button stays disabled until the input matches exactly. This is a simple string comparison — not a regex, not case-insensitive.

8. **Test the live mode switch (no service restart needed).** The `IOptionsMonitor<RbacOptions>` pattern established in Phase 0 means mode changes take effect immediately. Phase 0.5 tests MUST verify: flip mode via the UI → next RPC uses the new mode's rules → no process restart. If your test restarts the host between mode switches, you're testing the wrong thing.

9. **Web Client trigger button is disabled, not hidden.** Per `05_Default_Mode_Design.md` — the button is visibly rendered but greyed out with a tooltip explaining why. This maintains UI discoverability. Do not `display: none` or conditionally omit the button.

10. **In-flight pipelines continue running after switching to Secured mode.** Per exit criteria: "in-flight pipelines continue running (now owned by the new Admin)". The mode switch does NOT cancel or interrupt running executions — it only affects future authorization checks.

---

## How to start a task in this phase

Copy this skeleton when opening a Copilot session for any task in Block A–C:

```
[SPEC]
- docs/rbac/02_Implementation_Roadmap.md §Phase 0.5 — <one line>
- docs/rbac/05_Default_Mode_Design.md §<Y> — <one line if relevant>
- docs/rbac/04_UI_Mockup_Catalog.md — Mockup <N>

[CURRENT STATE]
- Phase 0 complete (all 24 files shipped)
- Tasks 1..N of Phase 0.5 already done (paths)
- Pending: this task (path)
- Existing patterns: see docs/architecture/CONVENTIONS.md §<section>

[TASK]
Generate <path>

[CONSTRAINTS]
- CommunityToolkit.Mvvm for WPF view models ([ObservableProperty], [RelayCommand])
- Zustand store for React state (useSystemModeStore)
- apiFetch<T>() for API calls in Web Client
- IAppLogger for logging, not Serilog
- Default Singleton DI lifetime
- Do not modify any Phase 0 file unless adding a DI registration

[OUTPUT]
- The file content
- One line: DI registration or route wiring (if applicable)
- Nothing else
```

---

## Exit checklist (don't move to Phase 1 until all green)

- [ ] Fresh install opens WPF directly to the main view with the Default-mode banner visible
- [ ] WPF can trigger, cancel, retry without restriction; audit entries show Default user as actor
- [ ] Web Client visits the controller URL and lands on the All pipelines view with Observer badge
- [ ] Web Client Trigger button visibly disabled with tooltip; clicking it has no effect
- [ ] When WPF triggers a pipeline, Web Client receives `PipelineLockAcquired` and renders "Locked by Default user (WPF)" badge within 2 seconds
- [ ] Settings > Security mode shows both modes side by side with Default mode marked ACTIVE
- [ ] Clicking "Switch to Secured mode…" opens the Initial Admin wizard
- [ ] Completing the wizard: flag flips, login screen appears in WPF, Web Client reloads to login screen, in-flight pipelines continue running (now owned by the new Admin)
- [ ] From Secured mode, Admin can navigate to Settings > Security mode and switch back via DISABLE RBAC confirmation
- [ ] All Phase 0 tests still pass with `RBAC:Enabled = false` fixture
