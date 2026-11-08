# Requirement & Recommended Approach: On-Demand Agent Update Button

| Field | Value |
|---|---|
| **Feature** | Per-node "Check for updates / Install updates" button in the Fleet Maintenance grid |
| **Component** | TestControllerGrpc (Fleet UI) + Core (installer) + TestAgentGrpc (agent-side) |
| **Type** | New feature |
| **Priority** | P1 |
| **Relationship to golden-image refresh** | A simpler, safer subset — reuses the same installer, skips the risky baseline-replace |

---

## 1. The requirement (what and why)

### 1.1 What
Add a single button to each agent's card in the Fleet Maintenance grid that:
- On first click, **checks online** for available Windows updates on that agent.
- If updates are found, the **same button** changes to offer **install**.
- On install, it installs the updates on that agent and reports progress.
- When done, it shows the node is up to date (and flags reboot-required if so).

One button, changing state — no separate dialogs for the simple case.

### 1.2 Why
Agents need to be patched, but the current system has no way to do it on demand.
The full golden-image refresh feature is unreachable and carries the risk of
corrupting the baseline every agent reverts to. This button delivers the useful
half — patch a node when an operator chooses — without the dangerous half
(automatic baseline replacement). It is operator-driven, per-node, and reversible.

### 1.3 Explicit scope boundary (important)
This button **patches the live running agent, not its golden snapshot.** If the
agent later reverts to its snapshot, the updates are gone. This is intentional and
acceptable for the target use — patching a specific node on demand — but it is NOT
a substitute for keeping the baseline patched. Baseline patching remains a separate,
future decision (golden-image refresh). This boundary must be clear in the UI so an
operator never assumes a patched node stays patched across a revert.

---

## 2. Functional requirements

| # | Requirement |
|---|---|
| FR-1 | Each agent card in the Maintenance grid shows an update button, default label "Check for updates". |
| FR-2 | Clicking it triggers an **online** Windows-Update search on that agent; button shows a checking/spinner state. |
| FR-3 | If no updates: button shows "Up to date" and returns to idle after a moment. |
| FR-4 | If updates found: button shows the count (e.g. "3 updates available") and becomes an install action. |
| FR-5 | Clicking install installs the updates on that agent; button shows progress (e.g. "Installing 1/3"). |
| FR-6 | On completion: button shows "Up to date"; if a reboot is required, the card shows a reboot-required badge and the existing Reboot button applies. |
| FR-7 | On failure: button shows a clear error state; the failure is logged; the node is NOT left in an unknown state. |
| FR-8 | The action is **per node** and **manual** — nothing happens automatically, and each agent is handled independently. |
| FR-9 | An agent that cannot install updates (capability-probe fails) shows the button disabled with a reason ("not install-capable"), never a silent no-op. |
| FR-10 | Same RBAC as other maintenance actions — only users permitted to run maintenance can check/install. |

---

## 3. Recommended approach

### 3.1 Reuse the installer, skip the operation
Build (or reuse, once built) `INodeUpdateInstaller` — the same search+install
component the golden-image spec needs. But wire it to a **simple button state
machine**, NOT the 12-phase golden-image operation. This is deliberately less
machinery: no revert, no snapshot, no baseline replace.

### 3.2 Where the work runs
- **Search + install run agent-side, elevated** (the agent is LocalSystem, so it
  has the rights), dispatched over the existing gRPC command path.
- The controller-side installer just dispatches and streams progress back.
- Reuse the WUApi COM surface the existing `WindowsUpdateDetector` already uses —
  but with an ONLINE search (the detector uses cache-only), since we need to find
  installable updates, then `IUpdateInstaller` to install.

### 3.3 The button state machine (the UX)
```
Idle:        [Check for updates]
  click → search online
Checking:    [Checking…]  (spinner, button disabled)
  → no updates  → Idle:      [Up to date ✓]  (then back to Check)
  → updates     → Available: [N updates available]  (now an install action)
  click → install
Installing:  [Installing k/N…]  (progress, button disabled)
  → success  → Done:  [Up to date ✓]  + reboot-required badge if applicable
  → failure  → Error: [Update failed — retry]  (logged; node not left unknown)
Unsupported: [Updates not supported]  (disabled, with reason)
```
One control, self-explanatory. Progress streams through the existing maintenance
progress channel — no new plumbing.

### 3.4 Reboot handling
Windows updates usually need a reboot. After install:
- The node reports reboot-required via the **trustworthy** signal
  (`Auto Update\RebootRequired`) — which pairs with your reboot-detection narrowing
  (only that key is authoritative), so this produces a REAL reboot-required, not a
  false one.
- The operator uses the existing (working) Reboot button to complete the cycle.

### 3.5 Safety (much lighter than golden-image, but not zero)
- **Capability-probe per node** before enabling the button (FR-9) — some agents run
  as an interactive user and can't install; never assume.
- **Per-node, manual, one at a time** — no fleet-wide automatic action, so blast
  radius is one node the operator is watching.
- **Failure leaves a clear state** — error shown, logged, node not silently broken.
- **No baseline touched** — this is the key safety difference from golden-image:
  the worst case is "this one live node has a bad update," recoverable by reverting
  it to its (unpatched) snapshot. There is no way for this button to corrupt a
  baseline.

### 3.6 What this deliberately does NOT do
- Does not replace the golden snapshot (no baseline patching).
- Does not run automatically or on a schedule.
- Does not act on multiple nodes at once.
Each of those is a conscious exclusion to keep the feature safe and simple.

---

## 4. Recommended build order

1. **Installer first** — `INodeUpdateInstaller` (agent-side online search + install,
   controller-side dispatch). Test against a mock agent. *(This is the shared piece
   with the golden-image spec; build it once.)*
2. **The grid button + state machine** — wire the installer to the per-node button
   with the check→available→install→done flow.
3. **Capability probe** — disable the button with a reason where install isn't
   supported.
4. **Reboot pairing** — ensure a real post-update reboot-required shows and the
   existing Reboot button handles it.
5. **Prove on one spare agent** — check, install, reboot, confirm patched. Only a
   single live node is ever affected.

---

## 5. Acceptance criteria

| ID | Criterion |
|---|---|
| AC-1 | Every capable agent card shows a "Check for updates" button |
| AC-2 | Check performs an online WU search on that agent and reports the count |
| AC-3 | The same button transitions to install when updates are found |
| AC-4 | Install runs agent-side elevated, streams progress, reports success/failure |
| AC-5 | Post-install reboot-required is shown via the trustworthy signal and handled by the existing Reboot button |
| AC-6 | Non-capable agents show the button disabled with a reason, never a silent no-op |
| AC-7 | The action is per-node and manual; no automatic or fleet-wide behaviour |
| AC-8 | Failure is shown and logged; the node is never left in an unknown state |
| AC-9 | No baseline/snapshot is modified by this feature |
| AC-10 | Same RBAC as other maintenance actions |
| AC-11 | Existing reboot/revert features unchanged; existing tests pass; new tests cover the installer (mock agent) and the button states |

---

## 6. The decision this rests on (state it before building)

**Goal check:** this feature answers "patch a specific node on demand." If your
real goal is "all agents stay patched even after they revert," this helps but does
not fully solve it — because revert undoes the patch. In that case this is step one,
and baseline patching (golden-image refresh) is a later step two. Decide which you
need so the UI copy and expectations are set correctly. Recommendation: ship this
first regardless — it is the safe, useful, operator-controlled capability, and it
tells you whether baseline patching is even needed in practice.

---

## 7. Why this is the recommended approach (plain summary)

- It gives you real update capability **without the one dangerous operation**
  (baseline replacement) that made golden-image refresh risky.
- It **reuses the installer** you would build anyway, so it is not wasted work.
- It is **operator-driven, per-node, reversible** — the worst case is one live node
  with a bad update, fixed by a revert.
- It **pairs cleanly with the reboot-detection fix** you are already doing: updates
  produce a genuine reboot-required, which your narrowed detection flags correctly.
- It is **smaller and safer to ship** than golden-image refresh, and may turn out to
  be all you actually need.
