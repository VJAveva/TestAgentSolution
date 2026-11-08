# Phase 3b Context Pack — WPF Pipeline Lock UI

> Attach this file to every Copilot session while working on Phase 3b. Detach when Phase 3b ships and switch to `phase-3c-context.md`.

> Companion reading: `docs/rbac/04_UI_Mockup_Catalog.md` Mockups 3, 4, 6, 9; `docs/rbac/05_Default_Mode_Design.md` §5 (Lock Model in Default Mode), §7 (UI Differences); `docs/rbac/03_Integration_With_Lock_Spec.md` §3, §6; `docs/architecture/CURRENT_STATE.md` "Executor Adapter" section; `docs/architecture/CONVENTIONS.md` WPF MVVM section.

---

## Goal

Ship the WPF client-side lock UI — badge, conflict dialog, force-release reason dialog, and lock-aware trigger gating — WPF ONLY. After Phase 3b:

- `LockStateService` subscribes to SignalR lock events and exposes a thread-safe lock map keyed by WatchItem `Tag`
- Every tree row shows the correct `LockBadge` (blue = your run + elapsed timer; amber = other owner)
- Trigger/Cancel buttons are disabled when the pipeline is locked by another user
- Conflict dialog (Mockup 6) opens from the `PipelineLockDto` payload on gRPC `Aborted` — no refetch
- Force-release reason dialog (Mockup 9) opens from the conflict dialog; submits `POST /api/locks/{tag}/force-release`
- Badge updates via `PipelineLockStolen` broadcast — NO optimistic update
- Default mode: badges COLLAPSED for own-user locks; conflict dialog NEVER appears

No Web client work in this phase (that's Phase 3c).

---

## Where things land

| Concern | Path | Project |
|---|---|---|
| Lock state service | `Services/LockStateService.cs` | `TestControllerGrpc` |
| Lock badge VM | `ViewModels/LockBadgeViewModel.cs` | `TestControllerGrpc` |
| Lock badge control | `Views/Controls/LockBadge.xaml` + `.cs` | `TestControllerGrpc` |
| MainWindow ItemTemplate diff | `Views/MainWindow.xaml` (modify) | `TestControllerGrpc` |
| Conflict dialog | `Views/Dialogs/LockConflictDialog.xaml` + `.cs` | `TestControllerGrpc` |
| Conflict dialog VM | `ViewModels/LockConflictDialogViewModel.cs` | `TestControllerGrpc` |
| Force-release reason dialog | `Views/Dialogs/ForceReleaseReasonDialog.xaml` + `.cs` | `TestControllerGrpc` |
| Force-release reason dialog VM | `ViewModels/ForceReleaseReasonDialogViewModel.cs` | `TestControllerGrpc` |
| MainViewModel.Execution.cs diff | `ViewModels/MainViewModel.Execution.cs` (modify) | `TestControllerGrpc` |
| Unit tests | `Rbac/LockStateServiceTests.cs` | `TestControllerGrpc.Tests` |
| Unit tests | `Rbac/LockBadgeViewModelTests.cs` | `TestControllerGrpc.Tests` |
| Unit tests | `Rbac/ForceReleaseReasonDialogVmTests.cs` | `TestControllerGrpc.Tests` |

---

## File targets (8 tasks, 3 blocks)

### Block A — Service + badge VM (no UI dependencies)

| # | Path | What |
|---|---|---|
| 1 | `TestControllerGrpc/Services/LockStateService.cs` | Singleton; injects the existing SignalR `HubConnection`; subscribes to `PipelineLockAcquired`, `PipelineLockReleased`, `PipelineLockExpired`, `PipelineLockStolen`, `PipelineLockRewritten`; calls `GET /api/locks` on initial connect AND on every reconnect for full resync; exposes `ConcurrentDictionary<string, PipelineLockDto> Locks` keyed by `PipelineId` (= WatchItem `Tag`); exposes `bool IsLockedByOther(string tag)` and `PipelineLockDto? GetLock(string tag)`; raises `event Action? LocksChanged` **marshaled to Dispatcher** via `Application.Current.Dispatcher.InvokeAsync`. |
| 2 | `TestControllerGrpc/ViewModels/LockBadgeViewModel.cs` | Per-row state, resolved from `LockStateService` lock map. Properties: `bool IsLockedByMe`, `bool IsLockedByOther`, `string OwnerLabel` (format: `"Locked by {displayName} ({clientKind})"` for other; `"Your run · mm:ss"` for self). Elapsed timer: `DispatcherTimer` at 1-second tick, active ONLY when `IsLockedByMe`; stopped and disposed when the lock releases. Subscribes to `LockStateService.LocksChanged` and re-evaluates from the map. |

### Block B — Controls + dialogs (depends on Block A)

| # | Path | What |
|---|---|---|
| 3 | `TestControllerGrpc/Views/Controls/LockBadge.xaml` + `.cs` | Two visual states: **amber** (other-owner badge: lock icon + `OwnerLabel` text), **blue** (your-run badge: play icon + elapsed `mm:ss`). Visibility bindings use `FallbackValue=Collapsed`. DataContext = `LockBadgeViewModel`. Does NOT trigger tree rebuild — bound per-row. **MainWindow.xaml ItemTemplate diff** is part of this task: insert `<controls:LockBadge />` into the existing WatchItem tree row template, bound to a `LockBadgeViewModel` property on the row item. |
| 4 | `TestControllerGrpc/Views/Dialogs/LockConflictDialog.xaml` + `.cs` + `ViewModels/LockConflictDialogViewModel.cs` | Mockup 6. Receives `PipelineLockDto` in constructor → populates owner card (avatar initials from `OwnerDisplayName`, client-kind chip, `AcquiredUtc` formatted, progress placeholder, expiry countdown recomputed client-side per second via DispatcherTimer from `ExpiresUtc`). Actions: Close, View dashboard. Force-release strip `Visibility` bound to `CapabilityChecker.Can(Permission.Pipeline_ForceRelease)` + re-evaluated on `CapabilitiesChanged`. DataContext via `App.Services` in `.cs` constructor. |
| 5 | `TestControllerGrpc/Views/Dialogs/ForceReleaseReasonDialog.xaml` + `.cs` + `ViewModels/ForceReleaseReasonDialogViewModel.cs` | Mockup 9. Properties: `string Reason`, `bool IsAffirmationChecked`, `bool CanSubmit` (computed: `Reason.Length >= 10 && IsAffirmationChecked`), `string CharacterCounter` (`"{len} / 10 minimum"`), `string AffirmationLabel` (`"I confirm that {ownerDisplayName}'s run will be cancelled"`). Amber submit button. Submit calls `POST /api/locks/{tag}/force-release` with `{ reason }`. Client validates `>= 10 chars` for UX; server re-validates. On success, dialog closes — badge update comes from `PipelineLockStolen` broadcast, NOT optimistic. DataContext via `App.Services` in `.cs` constructor. |

### Block C — Trigger gating + tests (depends on A + B)

| # | Path | What |
|---|---|---|
| 6 | `TestControllerGrpc/ViewModels/MainViewModel.Execution.cs` (modify) | `CanTriggerEvent` / `CanTriggerWatchItem` gain `&& !_lockStateService.IsLockedByOther(tag)`. On gRPC `Aborted` (status code check) with lock payload OR REST 409 with `PipelineLockDto` body: deserialize the DTO, open `LockConflictDialog` with the DTO — no refetch. |
| 7 | `TestControllerGrpc.Tests/Rbac/LockStateServiceTests.cs` | Tests: event handling updates the map correctly; reconnect triggers full resync via `GET /api/locks`; all event handlers marshal to Dispatcher before raising `LocksChanged`; Default-mode lock with same userId → `IsLockedByOther` returns `false`. |
| 8 | `TestControllerGrpc.Tests/Rbac/LockBadgeViewModelTests.cs` + `ForceReleaseReasonDialogVmTests.cs` | Badge VM: timer starts on `IsLockedByMe`, stops on release, elapsed formats correctly. Force-release VM: counter shows correct text; `CanSubmit = false` when reason < 10 chars; `CanSubmit = false` when checkbox unchecked; `CanSubmit = true` only when both conditions met. Default mode assertion: conflict dialog never shown (test that `IsLockedByOther` is always false when owner UserId matches current user). |

---

## Payload shapes (from Phase 3a — do NOT invent field names)

```csharp
// TestController.Api/Contracts/PipelineLockDto.cs
public sealed record PipelineLockDto
{
    public required string PipelineId { get; init; }       // = WatchItem Tag
    public required string OwnerDisplayName { get; init; } // e.g. "ravi.kumar"
    public required string OwnerClientKind { get; init; }  // "Wpf" | "Web"
    public required DateTime AcquiredUtc { get; init; }
    public required DateTime ExpiresUtc { get; init; }
}

// TestControllerGrpc.Core/Locking/LockEvent.cs
public sealed record LockEvent(LockEventKind EventKind, PipelineLock Lock, OwnerIdentity? PriorOwner = null);

// TestControllerGrpc.Core/Locking/OwnerIdentity.cs
public sealed record OwnerIdentity(string UserId, string DisplayName, ClientKind ClientKind);
// Equality on UserId ONLY.

// SignalR hub events (server → client):
//   PipelineLockAcquired(PipelineLockDto dto)
//   PipelineLockReleased(PipelineLockDto dto)
//   PipelineLockExpired(PipelineLockDto dto)
//   PipelineLockStolen(PipelineLockDto dto, string priorOwnerDisplayName)
//   PipelineLockRewritten(PipelineLockDto dto, string priorOwnerDisplayName)
```

---

## Sequence rules

- **Phase 3a must be complete** — `LockRegistry`, `LockBroadcaster`, `PipelineLockDto`, SignalR events, gRPC conflict payload, REST 409.
- **Block A first** — service + VM, no XAML.
- **Block B depends on A** — controls bind to the VM.
- **Block C depends on A + B** — trigger gating uses service; tests verify full flow.
- **Task 3 includes the MainWindow ItemTemplate diff** — that's part of the badge's exit criteria, not a separate later step.

---

## Watch out for

1. **Every dialog assigns `DataContext` via `App.Services` in its constructor**, per the popup-window convention in `CONVENTIONS.md`. No exceptions.

2. **Every `Visibility` binding uses `FallbackValue=Collapsed`** — fail closed. If the binding path is null or broken, the element must not appear.

3. **SignalR events arrive off the UI thread.** Marshal via `Application.Current.Dispatcher.InvokeAsync` BEFORE touching any `ObservableCollection` or raising `LocksChanged` to VMs. The `LockStateService` owns this marshaling; VMs receive events already on the UI thread.

4. **LockBadge updates must NOT rebuild the tree (expansion state).** Bind per-row to a `LockBadgeViewModel` resolved from the lock map; membership of the tree doesn't change, only badge state. Use property change notification on existing row items — never re-create the `ObservableCollection`.

5. **Default mode: WPF is the sole writer.** Badge is COLLAPSED when the lock owner is the current Default user (`UserId = "00000000-0000-0000-0000-000000000001"`). No conflict dialog can ever appear in Default mode — `IsLockedByOther` always returns `false` because the owner's `UserId` matches the current user. Assert this in a test.

6. **Elapsed timer lifecycle.** The `DispatcherTimer` (1-second tick) runs ONLY for `IsLockedByMe` rows. Stop and dispose the timer when the lock releases. Leak risk with many concurrent runs — one timer per locked-by-me row, not a global timer.

7. **Expiry countdown in the conflict dialog** comes from `ExpiresUtc` in the DTO. Recompute client-side per second with a local `DispatcherTimer`. Do NOT poll the server for expiry updates.

8. **Force-release submit calls `POST /api/locks/{tag}/force-release`** with `{ "reason": "..." }`. Client validates `reason.Length >= 10` for UX; server re-validates as source of truth. On success the dialog closes and the badge updates via the `PipelineLockStolen` broadcast — do NOT optimistically update the lock map or badge.

9. **`CapabilityChecker.Can(Permission.Pipeline_ForceRelease)` gates the force-release strip** in the conflict dialog. Re-evaluate visibility on `CapabilitiesChanged` event (edge case: mode switch while dialog is open).

10. **gRPC `Aborted` payload extraction:** the `PipelineLockDto` is serialized in trailing metadata under key `lock-conflict-bin`. Deserialize from that entry. The REST path uses HTTP 409 with a JSON body `{ "error": "pipeline-locked", "lock": { ... } }` — extract `.lock` as `PipelineLockDto`.

11. **No refetch on conflict.** The DTO in the error response IS the conflict context. Open the `LockConflictDialog` directly with it. No `GET /api/locks/{tag}` call.

12. **`AgentLockManager` is completely untouched.** This phase adds UI for pipeline locks, not agent locks. Do not import, modify, or reference `AgentLockManager`.

---

## How to start a task in this phase

```
[SPEC]
- docs/rbac/04_UI_Mockup_Catalog.md Mockups 3, 4, 6, 9
- docs/rbac/05_Default_Mode_Design.md §5, §7
- docs/rbac/03_Integration_With_Lock_Spec.md §3, §6
- docs/architecture/CONVENTIONS.md — WPF MVVM, DI
- docs/architecture/CURRENT_STATE.md — Executor Adapter, RBAC Phase status

[CURRENT STATE]
- Phase 3a complete: LockRegistry, LockBroadcaster, PipelineLockDto, SignalR events, gRPC Aborted payload, REST 409
- MainViewModel.Execution.cs has TriggerEvent [RelayCommand]
- App.Services static accessor available for dialog DataContext
- CapabilityChecker service exists (from Phase 1)
- Current user identity available via IUserContext

[TASK]
Generate <path>

[CONSTRAINTS]
- DataContext via App.Services in dialog .cs constructor
- FallbackValue=Collapsed on all Visibility bindings
- Dispatcher marshal in LockStateService before LocksChanged
- No tree rebuild — bind per-row LockBadgeViewModel
- Default mode: badge collapsed for own user; no conflict dialog
- Elapsed timer: per-row DispatcherTimer, stop on release
- Expiry countdown: client-side DispatcherTimer from ExpiresUtc
- Force-release: POST, no optimistic update, wait for PipelineLockStolen
- CapabilityChecker.Can(Pipeline_ForceRelease) gates strip
- IAppLogger for operational logs
- CommunityToolkit.Mvvm: [ObservableProperty], [RelayCommand]
- Singleton lifetime for LockStateService
- Field names from PipelineLockDto — do not invent

[OUTPUT]
- The file content
- Nothing else
```

---

## Exit checklist (don't move to Phase 3c until all green)

- [ ] `LockStateService` subscribes to all 5 SignalR lock events
- [ ] `LockStateService` calls `GET /api/locks` on connect AND reconnect
- [ ] All event handlers marshal to Dispatcher before mutating map or raising `LocksChanged`
- [ ] `LockBadgeViewModel` shows blue "Your run · mm:ss" for `IsLockedByMe`
- [ ] `LockBadgeViewModel` shows amber "Locked by {name} ({client})" for `IsLockedByOther`
- [ ] Elapsed timer starts on lock acquire, stops on release (no leak)
- [ ] `LockBadge` inserted into MainWindow WatchItem tree row without breaking expansion
- [ ] Trigger button disabled when `IsLockedByOther(tag)` is true
- [ ] gRPC `Aborted` with lock payload → `LockConflictDialog` opens with DTO, no refetch
- [ ] REST 409 with lock body → same behavior
- [ ] Conflict dialog shows owner card with initials, role, client chip, expiry countdown
- [ ] Force-release strip visible ONLY when `CapabilityChecker.Can(Pipeline_ForceRelease)`
- [ ] Force-release strip re-evaluates on `CapabilitiesChanged`
- [ ] `ForceReleaseReasonDialog` enforces reason >= 10 chars + affirmation checkbox
- [ ] Submit button disabled until BOTH conditions met (amber when enabled)
- [ ] Submit calls `POST /api/locks/{tag}/force-release`; dialog closes on success
- [ ] Badge updates via `PipelineLockStolen` broadcast — no optimistic update
- [ ] Default mode: `IsLockedByOther` always `false` (same UserId)
- [ ] Default mode: badge COLLAPSED for Default user's own locks
- [ ] Default mode: conflict dialog NEVER shown (test asserts this)
- [ ] All `Visibility` bindings use `FallbackValue=Collapsed`
- [ ] All dialog DataContexts assigned via `App.Services`
- [ ] `AgentLockManager` completely untouched
- [ ] `LockStateServiceTests` pass (events, resync, thread marshaling, Default mode)
- [ ] `LockBadgeViewModelTests` pass (timer lifecycle, format, release cleanup)
- [ ] `ForceReleaseReasonDialogVmTests` pass (counter, checkbox, gating matrix)
- [ ] All Phase 0–3a tests still pass (regression)
