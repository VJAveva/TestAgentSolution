# 04 — UI Mockup Catalog

> **Purpose**: pair each visual mockup from the design conversation with the implementation phase that delivers it, and capture the design rationale so engineers picking up a phase can see the target visual and the reasoning behind it without re-litigating decisions.

> **Prerequisite**: read `00_Master_Plan.md`, `01_System_Design.md`, `02_Implementation_Roadmap.md`, and `03_Integration_With_Lock_Spec.md` first. This catalog references their components, permissions, and phase definitions.

---

## How to Use This Catalog

The nine mockups in this catalog were rendered as interactive HTML widgets during the design conversation. They show **layout and information architecture**, using the design-system color palette (which auto-adapts to light/dark mode). They are **not pixel-perfect color specs** — the actual implementation uses your established dark-theme tokens (`#14161B` canvas, `#74ACEA` accent blue, `#C39ADF` purple, `#F0B070` amber, `#5DD0A8` teal, etc.) which map 1:1 to the semantic colors used in the mockups.

For each mockup this catalog records:

1. **Which SRS requirements / spec sections it satisfies**
2. **Which phase from the roadmap delivers it** (so the team building Phase N knows which mockups apply)
3. **The design rationale** — why specific layout, color, and interaction choices were made
4. **Implementation details** — server-side and client-side specifics that need to align with the visual
5. **Cross-references** to related mockups and other design docs

When planning sprint work for any phase, look up the relevant mockups here, read the rationale once, and treat the design decisions as settled unless something changes in the underlying requirements.

---

## Phase ↔ Mockup Index

A quick lookup so engineers can find the visuals for their phase without reading the whole catalog.

| Phase | Phase name | Mockups |
|---|---|---|
| 0 | Identity & AuthZ foundations | — (no user-visible UI) |
| 0.5 | Default Mode UX | Mockup 10 (Security mode switch), Mockup 11 (Default mode in action) |
| 1 | User management | Mockup 1 (Login), Mockup 2 (Post-login identity), Mockup 7 (User management) |
| 2 | Pipeline trigger + cancel + AuthZ | — (reuses existing pipeline list UI, applies role filtering) |
| 3 | Lock coordination integration | Mockup 3 (Web triggers, WPF observes), Mockup 4 (WPF triggers, Web observes), Mockup 6 (Conflict dialog), Mockup 9 (Force-release reason capture) |
| 4 | Retry hierarchy | — (extends existing tree UI; no new mockup needed) |
| 5 | Bulk operations | Mockup 5 (Trigger All Idle confirm + result) |
| 6 | Enable / disable pipelines | — (small affordance added to existing Admin pipeline list) |
| 7 | Read paths + Guest | Mockup 2 (shows Guest variant) |
| 8 | Notifications + flaky detection | — (email content and Admin/Manager dashboard sections are standard patterns) |
| 9 | Reports | — (download dialog and report viewer are standard patterns) |
| 10 | Audit viewer + hardening | Mockup 8 (Audit log viewer) |

---

## Mockup Catalog

### Mockup 1 — Login Screens (WPF and Web)

**Delivered by Phase 1.** Server-side bits land in Phase 0.

**Satisfies**: `FR-AUTH-01`, `FR-AUTH-02`, `FR-AUTH-04` (Guest path).

**Visual summary**: two stacked frames — WPF window with native chrome on top, browser-style window with URL bar below. Both show the same form (username, password, sign in button) with the same shield-check brand mark. Web variant adds a divider with "or" and a "Continue as Guest" link.

**Key design decisions**:

| Decision | Why |
|---|---|
| Identical form layout in both clients | Reduces cognitive load when users switch between clients. Same brand mark, same field order, same primary action color. |
| Shield-check icon as the brand mark | Communicates security/authority. Reused throughout — top chrome, login screens, conflict dialogs. |
| Sentence case throughout ("Sign in" not "Sign In") | Per design system, applied everywhere. |
| WPF has no Guest path | Per SRS §2.2, Administrator role is WPF-only. WebClient is the access path for Engineers, Managers, and Guests. |
| Hint text below the button names available roles | Sets expectations before the user tries a credential. WPF: "Administrator, Senior Manager, or Engineer". Web: "Senior Manager, Engineer, or Guest". |
| "Continue as Guest" is a secondary button (transparent bg), not a link | Larger tap target, accessibility, parity with the primary action. Per design system: buttons over links for actions. |

**Implementation details**:

- The same `LoginRequest` gRPC message backs both clients (`username`, `password`).
- The `ContinueAsGuestAsync` RPC issues a session with `UserId = NULL` and a freshly generated `GuestId` (UUID), returns a short-lived token (default 60-minute inactivity expiry per `FR-AUTH-07`).
- Web client first-login flow: response to `LoginAsync` includes `mustChangePassword: true` flag → frontend routes to first-login password change screen before any other navigation.
- Both clients hit `POST /api/auth/login`. Token is returned in the body and stashed (WPF: `IConfiguration`-backed token store; Web: in-memory + sessionStorage with secure flags).

**Cross-references**: Mockup 2 (what they see after sign-in); `01_System_Design.md` §4 (auth flow); `02_Implementation_Roadmap.md` Phase 1 (UI files); SRS §8 UC-01, UC-05.

---

### Mockup 2 — Post-Login User Identity Headers

**Delivered by Phase 1** (Admin and Engineer variants), **revisited in Phase 7** (Guest variant once read paths land).

**Satisfies**: `FR-AUTH-04`, `FR-UI-01`, `FR-UI-02`, plus role-conditional tab visibility from the SRS permission matrix.

**Visual summary**: three stacked headers showing the top chrome after login. Each header has the brand mark on the left, role-appropriate tabs in the middle, and a user identity badge on the right. Three variants: Admin (WPF, 6 tabs including Users + Audit log), Engineer (Web, 3 tabs, "My pipelines" focus), Guest (Web, 2 tabs, dashed badge border).

**Key design decisions**:

| Decision | Why |
|---|---|
| User badge in top-right corner of every screen | Universal convention. Always visible so the user knows who they're acting as. |
| Avatar circle uses initials, not generic icons | Identifiable at a glance in scrolling lists (User management, audit log) — same component reused there. |
| Role label color-coded under the username | Blue = Admin, purple = Senior Manager, teal = Engineer, gray-dashed = Guest. Same palette used everywhere a role appears (badges, filter chips, etc.). |
| Tabs hidden if role lacks permission | Engineer doesn't see "Users" or "Audit log" tabs at all — they're not in the navigation, not just disabled. Avoids dead affordances. |
| Connection indicator (green dot "Live") only on the Web client | WPF runs in-process on the controller node; "live" status is implicit. Web depends on SignalR/polling and benefits from the explicit indicator. |
| Guest badge uses dashed border + eye icon | Visual signal that this is an unauthenticated session. Differs from authenticated user badges enough that it's never mistaken for a logged-in user. |
| Guest badge shows expiry hint ("Read only · expires in 60 min") | Manages expectations. Guest knows the session is temporary. |

**Implementation details**:

- Both clients call `GET /api/me` on startup to populate the badge. The response includes `username`, `displayName`, `role`, `clientKind`, and `capabilities[]`.
- Capabilities list is what UI components use to show/hide buttons — e.g., "Take over and revert" appears only if `capabilities.includes("Pipeline_ForceRelease")`.
- Role label color comes from a small client-side mapping: `{Administrator: blue, SeniorManager: purple, Engineer: teal, Guest: gray}`. Same mapping used in user management and audit log.
- Dropdown chevron on the badge opens a menu with "Change password" and "Sign out". Both call corresponding gRPC RPCs.

**Cross-references**: Mockup 7 (where the same avatar component reappears in the user list); SRS §8 UC-02, UC-05; `01_System_Design.md` §3.1 (permission catalog drives which tabs each role sees).

---

### Mockup 3 — Lock Scenario A: Web Triggers, WPF Observes

**Delivered by Phase 3** (Lock Coordination Integration).

**Satisfies**: Lock spec §8.2 (WPF lock badge), §9.3 (Web client behavior); SRS implicit requirement that one client can see the state changes from another.

**Visual summary**: side-by-side view of the same pipeline from both perspectives. Top half: ravi.kumar's WebClient view showing his pipeline running with a blue accent and "Your run · 05:23" badge. Bottom half: vinod.kumar's WPF view showing the same pipeline with an amber accent and "Locked by ravi.kumar (Web)" badge, disabled Trigger button, and an amber "Take over and revert" button (because Admin has the force-release permission).

**Key design decisions**:

| Decision | Why |
|---|---|
| Owner sees blue accent + "Your run" badge | Communicates "this is your work in flight" — distinct from "someone else is doing this". |
| Observer sees amber accent + "Locked by [user]" badge | Amber, not red — this isn't an error, it's coordination information. |
| Tree expansion identical on both sides | Read access is always shared. Both users see the same action progress in real time via SignalR. |
| Owner's badge shows elapsed time ("05:23") | Owner cares about their own progress. |
| Observer's badge shows start time ("since 14:23") | Observer cares about when the work started — helps them decide whether to wait or take over. |
| WPF Trigger button visibly disabled (40% opacity) | The user knows the button exists but understands they can't use it right now. Hover tooltip reads "Pipeline is locked by ravi.kumar". |
| "Take over and revert" is amber-tinted, not red | Same reason as the observer's lock badge — it's not destructive in an alarming way, it's an elevated coordination action. |
| Footer hint references the specific permission name | "Take over and revert appears because Administrator has `Pipeline_ForceRelease`." Helps engineers map UI to authorization rules during debugging. |

**Implementation details**:

- Both clients subscribe to the same SignalR hub at `/hubs/execution`. The events `PipelineLockAcquired`, `PipelineLockReleased`, `PipelineLockExpired`, `PipelineLockStolen` drive the badge state.
- Web's "Your run" badge is rendered when `currentLock.owner.userId === currentUser.userId`.
- WPF's "Locked by [user]" badge uses the converter `OwnerToBadgeTextConverter` documented in `03_Integration_With_Lock_Spec.md`.
- The Trigger button's `IsEnabled` in WPF binds to `CanTrigger(pipelineId)` which returns false when `currentLock != null && currentLock.owner.userId != currentUser.userId`.
- The "Take over and revert" button's `Visibility` binds to `currentUser.capabilities.includes("Pipeline_ForceRelease")`. Hidden, not disabled, when not applicable (per Mockup 4 contrast).

**Cross-references**: Mockup 4 (the symmetric scenario), Mockup 6 (the conflict dialog that opens if WPF user clicks the disabled-looking Trigger anyway), Mockup 9 (what happens when they click "Take over and revert"); `Pipeline_Lock_Coordination_Spec.md` §4, §10.1, §10.2.

---

### Mockup 4 — Lock Scenario B: WPF Triggers, Web Engineer Observes

**Delivered by Phase 3.**

**Satisfies**: Same as Mockup 3, plus the role-conditional UI rule for `Pipeline_ForceRelease`.

**Visual summary**: mirror of Mockup 3 with the perspectives swapped. WPF user vinod.kumar triggered "Sanity Tests on Five nodes" (shows "Your run · 02:11"). Web user ravi.kumar (Engineer) sees the same pipeline with the amber lock badge "Locked by vinod.kumar (WPF)", but **does not** see a "Take over and revert" button because Engineer role doesn't have `Pipeline_ForceRelease`.

**Key design decisions**:

| Decision | Why |
|---|---|
| "Take over and revert" is hidden, not disabled | Per `01_System_Design.md` §3 — don't show affordances the user can never use. Disabled buttons make users think "why doesn't this work? did I do something wrong?" Hiding is cleaner. |
| Inline tooltip explains the locked state ("You can view dashboard and logs. Trigger and Cancel are unavailable…") | Replaces the missing force-release button with an explanation, so the user understands their available actions without confusion. |
| Engineer view header shows "My pipelines (3 assigned)" | Per `FR-AUTHZ-05`, Engineer sees only assigned pipelines. The "3 of 7 visible" count reinforces that this is a filtered view. |
| Other assigned pipelines remain triggerable | The lock only affects the one locked pipeline. Engineer can still work on their other pipelines normally. |

**Implementation details**:

- Engineer's pipeline list comes from `GET /api/pipelines?scope=mine` which server-side filters by `PipelineAssignments` to the current user. Per-pipeline lock state arrives via the same SignalR hub.
- "Take over and revert" button visibility check happens client-side (capabilities list from `/api/me`) AND server-side (the `Pipeline_ForceRelease` RPC handler does its own authz check). Client-side hide is for UX; server-side check is authoritative.

**Cross-references**: Mockup 3 (the inverted scenario); `01_System_Design.md` §3.1 (permission matrix); `02_Implementation_Roadmap.md` Phase 3 exit criteria.

---

### Mockup 5 — Trigger All Idle (Confirmation + Result)

**Delivered by Phase 5** (Bulk Operations).

**Satisfies**: `FR-PIPE-04` (Trigger All Idle), `FR-PIPE-06` (atomic transitions), `FR-UI-05` (explicit confirmation for bulk actions), `NFR-PRF-03` (3-second dispatch for 50 pipelines).

**Visual summary**: two stacked modals showing the before/after of a bulk trigger. **Step 1** confirmation modal shows the planned breakdown: "5 idle (will trigger) · 2 already running (skip) · 1 disabled (skip)". **Step 2** result modal shows the post-execution breakdown: green panel listing 5 triggered pipelines with their Run IDs, amber panel listing 3 skipped pipelines with reason badges.

**Key design decisions**:

| Decision | Why |
|---|---|
| Confirmation shows a preview, not just a count | Per `FR-UI-05` (confirmation requirement) but goes further — Admin sees *exactly* what will happen before committing. Prevents "oops, I forgot Production was disabled" surprises. |
| Preview uses a dry-run endpoint (`GET /api/pipelines/bulk-trigger/preview`) | Same query the server runs to find triggerable pipelines, returned without mutation. Idempotent and cheap. |
| Result modal separates "triggered" (green) from "skipped" (amber) | Visual scanning — Admin can immediately see successes vs needs-attention. |
| Each triggered row shows a Run ID (`run_a3f2b1`) | Lets Admin click through to a specific run if they want to investigate. Monospace font signals "this is a code identifier". |
| Each skipped row has a reason badge (`Already running`, `Disabled`) | Reasons map directly to the `OUTPUT`-clause results from the atomic SQL transition. Same vocabulary engineers see in the database. |
| Result modal shows total processed count in footer | "8 pipelines processed · 5 triggered · 3 skipped" — quick scanability for the Admin documenting the action. |
| "Open dashboard" action on the result | Natural next step after triggering 5 pipelines — go watch them run. |

**Implementation details**:

- The atomic SQL from `01_System_Design.md` §5.2 (`UPDATE Pipelines SET State='Running' ... WHERE State='Idle' AND IsEnabled=1 RETURNING PipelineId, CurrentRunId`) drives the triggered list directly. The skipped list is the complement.
- Confirmation preview hits a no-side-effects version: `SELECT PipelineId, Name, State, IsEnabled FROM Pipelines WHERE State <> 'Running' OR IsEnabled = 0` and groups client-side by what would happen.
- Audit log captures the bulk action as a single row with `ActionName: "Pipeline_TriggerAll"`, `Payload: { triggered: [...], skipped: [...] }` — important for Admins reviewing "who ran everything on Friday at 5pm".
- Per `NFR-PRF-03`, the dispatch of all triggered pipelines to the executor is async; the response returns as soon as the DB transaction commits. Result modal shows the wall-clock duration ("Dispatched in 1.8 seconds") to confirm performance.

**Cross-references**: SRS §8 UC-04; `01_System_Design.md` §5.2 (atomic transition SQL); `02_Implementation_Roadmap.md` Phase 5.

---

### Mockup 6 — Conflict Dialog (Admin + Engineer Variants)

**Delivered by Phase 3.**

**Satisfies**: Lock spec §8.4 (WPF conflict dialog), §9.3 (Web conflict modal); `NFR-USE-01` (errors identify cause).

**Visual summary**: two variants of the same modal. **Admin variant** shows the lock conflict with full owner details, a "View live dashboard" action, plus a separator strip with "Take over and revert…" in an amber tint. **Engineer variant** is identical but omits the force-release section and adds a small explanatory line directing them to escalate.

**Key design decisions**:

| Decision | Why |
|---|---|
| Amber lock icon, not red error icon | This is coordination, not a system error. Red would signal "something broke" — wrong tone. |
| Body text reads "currently being run by [user] from the [client]" | Plain language. Names the person and the client kind so the actor knows whether to walk over to them or message remotely. |
| Owner details in a clean data grid | Avatar + role + client kind + start time + status + progress + lock-expiry. All the context the user needs to decide their next action. |
| Lock-expiry countdown ("in 28 seconds if no heartbeat") | Sets honest expectations. If the owning client has already disconnected, the user knows the lock will free itself momentarily without forcing anything. |
| Footer actions: "Close" + "View live dashboard" | Default actions for any role. View dashboard is the "wait and watch" path. |
| Admin's force-release strip is visually separated | Below the standard actions, in a tinted background, with a leading explanation. User has to make a deliberate jump to use it — not a routine action. |
| Admin's button has trailing ellipsis ("Take over and revert…") | UX convention: ellipsis = opens another dialog. Specifically, the reason-capture modal in Mockup 9. |
| Engineer variant has no Trigger or Cancel buttons mentioned | Hides irrelevant affordances. The inline hint redirects them to the right escalation path. |

**Implementation details**:

- Triggered when the Trigger endpoint returns gRPC `Aborted` with a `PipelineLockDto` payload.
- Client-side: catch the error, deserialize the payload, open the conflict modal pre-populated with the lock details.
- Admin variant's force-release section is wrapped in `<authz-gate permission="Pipeline_ForceRelease">` — same gating pattern as the top-chrome admin tabs and the user management screen access.
- "View live dashboard" navigates the user to the running pipeline's detail view in the Dashboard tab.

**Cross-references**: Mockup 9 (the reason-capture follow-up); Mockup 3 / 4 (the surface where the user clicked the disabled-looking Trigger to get here); `Pipeline_Lock_Coordination_Spec.md` §10.1, §10.2.

---

### Mockup 7 — User Management (WPF Admin)

**Delivered by Phase 1.**

**Satisfies**: `FR-USR-01` through `FR-USR-06`, `FR-UI-01` (unified Admin screen). Use case UC-01.

**Visual summary**: master/detail layout. Left pane (200px wide): searchable user list with filter pills (All / Admin / SrMgr / Engineer), each row showing avatar + name + role + assigned-pipeline count. Right pane: selected user's details — header with avatar/name/email/Edit/Delete buttons, metadata grid (role, status, created-by, first-login state), assigned pipelines list with "Manage" button, quick actions (Reset password, Deactivate, View audit history). Plus a separate "Add user" dialog mockup with username/email/role/initial password/pipeline assignments.

**Key design decisions**:

| Decision | Why |
|---|---|
| Master/detail layout (not modal-per-edit) | Admin frequently scans and edits across multiple users. Master/detail keeps context; modal-per-edit forces repeated open/close. |
| "(you)" suffix on the current user's row | Prevents Admin confusion (especially in larger teams). Also signals "Delete will be disabled when selected" per `FR-USR-05` (can't delete last Admin). |
| Role color coding everywhere it appears | Reused from Mockup 2's role labels. Visual consistency. |
| Pipeline count per user ("3 pipelines") in list rows | Per `FR-USR-06` — surfaces assignment density without opening every user. |
| Pipeline count per pipeline ("2 engineers") in assignment dialog | Symmetric — when assigning, Admin sees which pipelines are well-covered vs orphaned (0 engineers). Helps balance ownership. |
| "Status: Active · last seen 2 minutes ago" | Combines IsActive flag with most-recent session activity. Operationally useful for Admins debugging "is this user actually using the system?". |
| "First login: Password changed YYYY-MM-DD" | Per `FR-AUTH-06`. Before first login, this row reads "Pending — initial password set YYYY-MM-DD" and the Admin can see who hasn't logged in yet. |
| Assigned pipelines list shows assigned + "4 other pipelines available" hint | Most useful default view — what they have, with a CTA to add more. Full management happens in the "Manage" dialog. |
| Add User dialog defaults role to Engineer | Most common role created. Per SRS, only one Admin per system; Senior Managers are a handful; Engineers are the bulk. |
| Add User dialog auto-generates the initial password | Per `NFR-SEC-02`. Admin can regenerate or type their own. `MustChangePassword=true` is forced — not even an option in the UI. |
| Disabled pipelines shown but uncheckable in assignment list | Admin knows the pipeline exists but can't assign Engineers to a pipeline that can't run (per `FR-PIPE-07`). |
| "View audit history" quick action | Filters audit log to actions performed by *or against* this user. One-click correlation. |

**Implementation details**:

- All operations go through `UserGrpcService` (`Create`, `Update`, `Delete`, `List`, `AssignPipeline`, `RevokePipeline`). Each gated by the corresponding `User_*` permission (Admin only).
- "Last Admin can't be deleted" rule enforced in `DeleteUserHandler` — check `Users.Count(u => u.Role == Administrator && u.IsActive) > 1` before proceeding.
- Cascading delete of assignments per `FR-USR-04` happens in a single DB transaction with the user delete.
- Auto-generated password uses a cryptographically secure RNG with character class requirements (12 chars, mixed case + digit, ASCII-safe).
- Audit log writes per operation: `User_Create` (1) + `User_Assign` (one per pipeline) = N+1 audit rows for creating a user with N assignments.

**Cross-references**: SRS §8 UC-01; `01_System_Design.md` §7.1 (Users, PipelineAssignments tables); `02_Implementation_Roadmap.md` Phase 1.

---

### Mockup 8 — Audit Log Viewer (WPF Admin)

**Delivered by Phase 10** (Audit Viewer + Hardening).

**Satisfies**: `FR-AUD-01`, `FR-AUD-03`, `FR-AUD-04`. Use case UC-07.

**Visual summary**: Admin-only screen with summary header ("24,832 entries in last 30 days · 1,983 denied (8%)"), filter row (User, Role, Action, Resource, From/To, Decision segmented control), active-filter chip strip, dense table (Time UTC / User / Action / Resource / Decision / Reason), pagination footer. Deny rows have a red-tinted background. Export CSV button in the header.

**Key design decisions**:

| Decision | Why |
|---|---|
| "8% denied" summary stat in the header | Above industry norm (1-5%). Surfacing it invites the Admin to investigate. Click navigates to a pre-filtered "denies only" view. |
| Red-tinted background for deny rows | Visual scanning — anomalies stand out. Combined with the ✕ glyph + the bold "Deny" label + the reason code, per `NFR-USE-02` (don't rely on color alone). |
| Reason codes in monospace (`no-assignment`, `guest-readonly`, `admin-only`, `no-role`, `engineer-cannot`) | These come from `AuthDecision.ReasonCode` in code. Showing them in mono signals "this is a stable identifier you can grep for". Admin can scan a column and immediately understand the *pattern* of denials. |
| Guest user rows show eye icon instead of initials | Distinguishes anonymous sessions from named users. Guest IDs are short-lived random strings, not usernames. |
| Filter chip strip below the filter form | Once filters are applied, they collapse into removable chips. Active filters are visible at a glance; individual filters can be cleared with one click. |
| Pagination at 200/page | Audit can grow to millions of rows. 200 is small enough to render quickly, large enough to scroll without constant pagination. |
| Action filter supports wildcards (`Pipeline_*`) | Common Admin pattern: "show me all pipeline-related activity". Server maps to a `LIKE` query. |
| Click row to expand (implicit, not rendered) | Reveals full CorrelationId, ClientKind, IpAddress, raw payload. Engineers debugging an issue can follow the correlation ID across logs. |
| CSV export reflects the current filter scope | Not the whole table — that would be enormous. Filename includes filter summary: `audit_2026-05-08_to_2026-06-07_denies.csv`. |

**Implementation details**:

- Schema columns powering this view come from `01_System_Design.md` §7.1 `AuditEntries` table — `AuditId`, `UserId`/`GuestId`, `RoleAtTime`, `ActionName`, `ResourceId`, `Decision`, `ReasonCode`, `TimestampUtc`, `ClientKind`, `CorrelationId`.
- Three indexes specified in the design cover every common filter combination: `(UserId, TimestampUtc DESC)`, `(ActionName, TimestampUtc DESC)`, `(TimestampUtc DESC)`. No additional indexes needed.
- Append-only enforcement per `FR-AUD-02`: no `Update` or `Delete` operations exposed by the API.
- 12-month retention per `FR-AUD-04` via the `SessionHousekeepingWorker` (extended in Phase 10 to also prune audit entries older than 12 months).
- Export streams to CSV without buffering everything in memory — important for large filtered sets.
- Read access restricted to Administrator via `Audit_View` permission (`Audit_Export` for the CSV download).

**Cross-references**: SRS §8 UC-07; `01_System_Design.md` §7.1 (AuditEntries schema, indexes); `02_Implementation_Roadmap.md` Phase 10.

---

### Mockup 9 — Force-Release Reason Capture Dialog

**Delivered by Phase 3.**

**Satisfies**: Lock spec §10.3 (force-release with revert), `FR-AUD-01` (audit completeness for elevated actions).

**Visual summary**: a deliberately friction-heavy modal. Yellow warning triangle header, "This action cannot be undone" subtitle, owner card showing pipeline + current owner + progress, textarea for reason (min 10 chars, live counter), yellow info panel listing all four side-effects ("This action will: cancel / revert / notify / audit"), affirmation checkbox naming the affected user, amber-tinted submit button "Take over and revert".

**Key design decisions**:

| Decision | Why |
|---|---|
| Yellow warning triangle (not red error, not blue info) | Elevated but legitimate — distinguishes from system errors (red) and routine actions (blue). |
| "This action cannot be undone" pinned at the top | Read before any details. Frames the gravity of what follows. |
| Owner card includes progress ("2 of 4 agents complete (50%)") | Most empathy-inducing field. "They're 50% done, are you sure?" — designed to slow down the decision. |
| Reason textarea, min 10 characters | Per the Lock spec. Small enough to not feel punitive, large enough to force a real sentence. Counter live, turns green when threshold met. |
| "This action will:" yellow panel listing all side-effects | Eliminates ambiguity. Cancel + revert + notify + audit — all four spelled out. Notifying ravi.kumar is important; it prevents the "wait, who killed my run?" support ticket. |
| Affirmation checkbox names the user by name | Forces the actor to acknowledge whose work they're interrupting. Generic "I agree" is too easy to click without reading. |
| Submit button in amber (warning palette), not red | Red is reserved for errors and destructive deletes. This is elevated coordination, not destruction. |
| Submit button disabled until both conditions met | Reason length ≥ 10 AND checkbox checked. Client-side check is courtesy; server re-validates. |
| Trailing icon `lock-open` on the submit button | Pairs visually with the lock icon throughout the flow. Reinforces "this is the unlock action". |

**Implementation details**:

- Server-side flow in `ForceReleaseHandler`:
  1. `IAuthorizationService.CanAsync(user, Pipeline_ForceRelease, pipelineId)` — must allow
  2. Validate `reason.Length >= 10` — reject with `InvalidArgument` if not
  3. Mark existing lock `Status = Stolen`, capture prior owner
  4. Cancel the active session via the executor adapter
  5. Trigger the configured revert pipeline (if `revert: true` in the request)
  6. Acquire new lock for the actor with `Kind = Revert`
  7. Broadcast `PipelineLockStolen` SignalR event (with prior owner + actor + reason)
  8. Write audit entry: `{ ActionName: "Pipeline_ForceRelease", ResourceId: <pipelineId>, Decision: Allow, ReasonCode: "ok", Payload: { reason: "<text>", priorOwner: "ravi.kumar", priorOwnerSessionId: <...> } }`
- The reason text is part of the audit record forever — important for compliance and for the prior owner's after-the-fact understanding.
- Client receives `PipelineLockStolen` and shows a non-blocking toast to the prior owner: "Your session was taken over by vinod.kumar for revert. Reason: '[the text]'."

**Empty state behavior** (not separately rendered):

- Textarea is empty with placeholder text `Explain why you are taking over this pipeline...`
- Counter shows `0 / 10 minimum` in muted gray
- Affirmation checkbox label in muted color
- Submit button at 45% opacity, `disabled`, cursor `not-allowed`
- Transition to enabled state happens reactively when both validation conditions are satisfied

**Cross-references**: Mockup 6 (the conflict dialog the user came from); `Pipeline_Lock_Coordination_Spec.md` §10.3; `01_System_Design.md` §3.1 (`Pipeline_ForceRelease` permission); `02_Implementation_Roadmap.md` Phase 3.

---

### Mockup 10 — Security Mode Switch (Settings Panel)

**Delivered by Phase 0.5** (Default Mode UX).

**Satisfies**: the operational-mode decision from `00_Master_Plan.md` §3.7 and the full design in `05_Default_Mode_Design.md`.

**Visual summary**: WPF Settings page with a "Security mode" section showing two side-by-side cards. The currently active mode (Default in the rendered example) has a 2px info-blue border, blue-tinted background, and an "ACTIVE" pill in the top-right. Each card has a header icon (lock-open for Default, shield-lock for Secured), a one-sentence description, an "In this mode:" bulleted list of concrete behaviors, and a target-audience hint. Below the cards is a yellow "Switching to Secured mode will:" panel listing all five side-effects of the switch. The action buttons at the bottom are Cancel and "Switch to Secured mode…" (ellipsis = opens the initial-Admin wizard).

**Key design decisions**:

| Decision | Why |
|---|---|
| Side-by-side cards with explicit "ACTIVE" indicator | Self-documenting comparison. The colored border + background + pill make the active mode unmistakable. |
| Each card lists "In this mode:" bullets | Concrete behaviors, not marketing copy. The Admin reading this knows exactly what will change. |
| Audience hint at the bottom of each card ("Suitable for lab environments…" / "Suitable for production…") | Helps the Admin self-categorize before they commit. |
| Yellow side-effects panel below the cards | Surfaces *every* consequence of the switch before the click. Per UX principle: no surprises after destructive/elevated actions. |
| Ellipsis on the primary button | Convention — triggers the wizard, not an immediate flip. |
| WPF user badge shows "Default user" with dashed border + user icon | Same treatment as the Guest badge in Mockup 2 — visually distinct from any authenticated identity. |
| No Users / Audit log tabs in this header | Default mode has no user management to access. Audit entries are still written server-side; they just aren't viewable in the UI without an Admin role. |

**Implementation details**:

- The card layout is a CSS grid `grid-template-columns: 1fr 1fr; gap: 12px;`.
- The "ACTIVE" pill is positioned absolutely in the top-right of the card.
- The switch action calls `SystemModeGrpcService.SwitchToSecuredAsync` (after the wizard) or `SwitchToDefaultAsync` (after the DISABLE RBAC confirmation).
- The Secured → Default direction shows a different (more severe) warning panel with a confirmation field that requires typing the literal string `DISABLE RBAC` before the submit button enables.
- The transition itself is atomic on the server via `IRbacModeTransitionService` (see `05_Default_Mode_Design.md` §8 and §9).

**Cross-references**: Mockup 11 (what the system looks like in Default mode); `05_Default_Mode_Design.md` (full design, switch flows, audit semantics).

---

### Mockup 11 — Default Mode In Action (WPF + Web)

**Delivered by Phase 0.5.**

**Satisfies**: the "no auth, single operator, read-only Web" experience from `05_Default_Mode_Design.md`.

**Visual summary**: two stacked frames showing the same running pipeline from both clients in Default mode. **WPF top half**: persistent amber Default-mode banner across the top with a "Settings →" deep-link chip; standard app header with no Users / Audit log tabs and the "Default user" badge (dashed border + user icon); enabled Trigger / Cancel / Retry buttons; tree view showing a running pipeline with the normal blue "Running" badge (no lock UI because WPF is the only writer). **Web Client bottom half**: standard header with "Observer · Read only" badge (dashed border + eye icon); disabled Trigger button with no-cursor styling; "Live log" and "Dashboard" actions enabled; tree view showing the same running pipeline with the amber "Locked by Default user (WPF)" badge.

**Key design decisions**:

| Decision | Why |
|---|---|
| Persistent amber banner across the top of WPF | Constant visual reminder. The Admin always knows the system is in Default mode and there's a one-click escape via "Settings →". The banner is fixed (not dismissible) by design — it is the discoverability anchor. |
| "Default user" badge uses dashed border + user icon | Same visual treatment as the Guest badge in Mockup 2. Communicates "not a real authenticated identity" through the same vocabulary the rest of the app uses. |
| No lock badges on WPF running pipelines | WPF is the sole writer in Default mode; there is nothing to coordinate with. The blue "Running · 05:23" badge reflects "the system is running this" rather than "you've locked it from others". |
| Web Client tabs: "All pipelines" instead of "My pipelines" | Engineers don't exist in Default mode; everyone (every Observer) sees everything. |
| Observer badge with eye icon and dashed border | Visually distinct from any authenticated role badge (Admin blue, Engineer teal, Guest gray). The dashed border + eye icon == "not a real user". |
| Trigger button visibly disabled (45% opacity, no-cursor) | Stays present so users understand the feature exists; hover tooltip explains why it's unavailable ("Trigger is unavailable in Default mode…"). Hidden buttons make users wonder if they're missing a permission they don't know about; disabled-with-explanation is clearer. |
| "Locked by Default user (WPF)" badge on Web | Reuses the same amber lock badge component from Mockup 3/4 with the Default user identity. The Web Client renders it from the same SignalR `PipelineLockAcquired` payload as in Secured mode — no special-casing. |
| Live log + Dashboard buttons enabled | The whole point — Web is observational, not actionable. All read affordances stay fully functional. |
| Footer hints explain restrictions and the path to Secured | First-time Web users won't be confused about why Trigger is disabled; the hint also markets the upgrade path. |

**Implementation details**:

- The synthetic Default user has a stable `UserId = 00000000-0000-0000-0000-000000000001` so the audit log correlates Default-mode actions across the entire lifetime of the install.
- The `LockRegistry` works unchanged — when WPF triggers a pipeline, the lock is acquired with `Owner = { UserId: "00000000-…-0001", DisplayName: "Default user", ClientKind: Wpf }`. SignalR broadcasts the lock event normally; Web Client renders the badge from the payload.
- The `Pipeline_ForceRelease` permission is implicitly always allowed for the Default user from WPF, so opening a second WPF window and clicking Cancel "just works" with no conflict dialog (Mockup 6) or reason-capture dialog (Mockup 9).
- The Web Client's disabled Trigger button is gated client-side by the `capabilities[]` list returned from `/api/me` (which in Default mode is `["Pipeline_View", "Report_View"]` only). The server-side `AuthorizationService.CanAsync` independently enforces this — clicking the disabled button via dev tools still returns `PermissionDenied` with reason `default-mode-web-readonly`.
- The amber banner is implemented as a single component (`DefaultModeBanner.xaml`) that renders only when `RBAC:Enabled = false`. The "Settings →" chip is a navigation shortcut.

**Cross-references**: Mockup 10 (the Settings panel that controls this mode); Mockup 3 / 4 (Secured-mode equivalents that show what changes when RBAC is on); `05_Default_Mode_Design.md` §3 (synthetic user), §5 (lock model in Default mode), §7 (full UI difference table).

---

## What Is Not Mocked (and Why)

These UI surfaces from the SRS do not have dedicated mockups and don't need them:

| Surface | Why no mockup | Where to find the pattern |
|---|---|---|
| Individual run results / run history | Standard list + detail pattern; existing WebClient already implements close versions | WebClient_Enhancement_Spec.md §4 (the dashboard tabs already cover this) |
| Retry tree with per-level Retry buttons | Extends the existing pipeline tree from Mockup 3/4; adds retry buttons at Event / ActionGroup / Action rows when those nodes are in Failed state | `02_Implementation_Roadmap.md` Phase 4; `01_System_Design.md` §6 |
| Engineer "Retry Failed Action" button | Inline button on the Action row in the tree; same visual weight as other tree-row controls | Same as above |
| Email notification template | Backend template, not interactive UI; format follows standard transactional email patterns | `02_Implementation_Roadmap.md` Phase 8 |
| Manager dashboard "flaky pipelines" view | Standard list of pipelines flagged by the analyzer, with metric cards for trend; styled like the Dashboard tab cards | Phase 8 |
| Engineer mute notifications | Small toggle on the pipeline detail view with optional duration picker | `FR-NTF-04` |
| Weekly/monthly report download | Standard download dialog with date range, format radio (PDF/CSV), generate button | Phase 9 |
| Report generated content (PDF/CSV) | Server-rendered output, not interactive UI | Phase 9 |
| Enable/disable toggle on pipeline | Small switch added to the Admin pipeline list row, with a confirmation modal that reads "Disable [pipeline]? Bulk operations will skip it." | Phase 6 |
| Delete user confirmation | Standard destructive confirmation modal; lists impact ("Will revoke 3 assignments and end any active sessions") | Phase 1 |
| Reset password follow-up modal | Shows new password once with copy button, "email credentials to user" option, "user must change on next login" note | Phase 1 |

Each of these follows established patterns elsewhere in the design system or the existing WebClient. They're listed here so engineers don't go looking for mockups that don't exist.

---

## Viewing the Mockups

The mockups were rendered as inline HTML widgets during the design conversation. They are visible in the chat transcript. To rebuild any of them as static reference images:

1. Each mockup uses only the design-system CSS variables (`--color-background-*`, `--color-text-*`, `--color-border-*`) plus a few hardcoded ramp hex values for badges (e.g., `#E1F5EE` teal for Engineer, `#E6F1FB` blue for Administrator).
2. The dark-theme tokens used in the actual implementation (`#14161B` background, `#74ACEA` accent blue, etc.) map to the same semantic positions in the palette. The mockup rendered in light mode is the design system's auto light variant; render in dark mode and the colors invert correctly.
3. For pixel-perfect color reference matching the production WPF dashboard, use the existing palette from the earlier WPF dashboard work — the mockups intentionally use the design system's palette so they're theme-agnostic and don't need updating when the theme is tweaked.

---

## What Changes Next

If new UI surfaces are added during implementation, they should land in this catalog with the same structure: mockup → phase → SRS mapping → design rationale → implementation details → cross-references. The catalog is the persistent record of *why* the UI looks the way it does. Engineers six months from now who wonder "why is force-release amber and not red?" can find the answer here without re-running the design conversation.
