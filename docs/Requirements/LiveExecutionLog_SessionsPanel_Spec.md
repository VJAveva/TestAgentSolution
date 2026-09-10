# Feature Spec: Populate Sessions Panel + Agent-Filtered Live Log

| Field | Value |
|---|---|
| **Component** | TestController.WebClient (React + TypeScript + Vite + Zustand) |
| **Area** | Execution view - left Sessions panel + Live Execution Log |
| **Type** | Bug fix + enhancement |
| **Priority** | P1 |

---

## Instructions for Copilot

Two connected problems on the Execution page:

1. **BUG**: The left SESSIONS panel shows "No active sessions" even when agents
   are actively running (visible in the live log). It must show the running
   pipeline as a Root node with its agent nodes underneath.
2. **ENHANCEMENT**: Selecting an agent node (or its filter tab) must filter the
   Live Execution Log to show ONLY that agent's lines. "All" shows everything.

Read the actual code before editing. Replace every `PLUG IN` with the real
store/hook/type names from this codebase. Match the existing visual style
(the app already has the tabs, the monospace log, and the blue accent).

An HTML mockup accompanies this spec (`LiveExecutionLog_Mockup.html`) - use it
as the visual + behavioral target.

---

## The Structure (confirmed)

- **Root pipeline** node (e.g. "SmokeTest"), shown as a header with overall
  status + a count of agents.
- **Four agent nodes FLAT under the root** (JVGR1, JVGR2, JVHIST, WARMGR). No
  phase nesting - just root -> agents.
- Each agent node shows one of two statuses: **Running** or **Idle**.
  - Running = currently executing, has live log lines
  - Idle = not part of this run, or finished (not scheduled this run)
- Some agents run in parallel (multiple Running at once); some may never run
  this session (Idle).

---

## Problem 1: Why the Sessions Panel Is Empty (investigate first)

The panel says "No active sessions" while the log streams live events. That
means the panel and the log are reading DIFFERENT sources, or the panel's
data source is not being populated/subscribed.

Investigate:
1. Find the Sessions panel component (left column of the Execution view).
2. Find where it gets its session list (a Zustand store slice? a hook? a
   SignalR subscription?).
3. Find where the Live Execution Log gets ITS data (almost certainly a SignalR
   stream of execution events, since it updates live).
4. Determine WHY the log has data but the panel doesn't. Likely causes:
   - The panel reads a REST snapshot taken once at load (before the session
     started) and never updates from the live stream.
   - The panel subscribes to a "sessions" event the server never emits (server
     emits execution events but no session-created/updated event).
   - The session IS in the store but the panel's "is there an active session?"
     check is wrong (e.g. filtering by a status that never matches).

Report which it is before fixing. The fix differs per cause:
   - If the panel just needs to derive from the same live event stream, build
     the session/agent view model FROM the execution events the log already
     receives.
   - If the server should emit session lifecycle events, note that as a
     server-side follow-up, and in the meantime derive the panel state from the
     execution-event stream client-side.

### Recommended approach (client-side derivation)

The log already receives a live stream of execution events, each tagged with an
agent name (`[JVGR1]`, `[JVGR2]`, etc.). Derive the Sessions panel from that
same stream so the two can never disagree:

```
For each incoming execution event:
  - ensure a Root session exists (create on first event of a session id)
  - ensure an agent node exists for event.agentName under that root
  - mark that agent node "Running" (it just produced an event)
  - update its counters (ok/fail counts, elapsed) from the event
An agent node is "Idle" if it is a known fleet member for this pipeline but has
produced no events (not scheduled), or if the session ended.
```

===== PLUG IN: the real execution-event shape (agent name field, session id
field, status/level field) and the real store actions. =====

---

## Problem 2: Agent-Filtered Log

### Behavior
- The filter tabs at the top of the log currently show labels like `[Revert]`,
  `[PHASE]`, `[WARM P]` that don't clearly identify the AGENT. Replace/augment
  so there is a clear tab per agent: **All | JVGR1 | JVGR2 | JVHIST | WARMGR**,
  each with a status dot (green=running, grey=idle).
- **Default = All** (combined stream, current behavior).
- Selecting an agent (via the tab OR clicking the agent node in the left panel)
  filters the log to ONLY that agent's lines.
- The two selectors stay in sync: clicking the node highlights the tab and vice
  versa (single source of truth for "selected agent").

### State
Add one piece of state: `selectedAgent: string | 'all'` (default `'all'`).
Put it wherever the Execution view's UI state lives.

```typescript
// ===== PLUG IN: add to the execution UI store slice =====
interface ExecutionUiState {
  selectedAgent: string | 'all';
  setSelectedAgent: (agent: string | 'all') => void;
}
```

### Filtering the log
The log is a list of event lines each with an `agentName`. Filter derived, not
mutated - keep the full list, filter at render:

```typescript
// ===== PLUG IN: real event type + selector =====
const visibleLines = useMemo(() =>
  selectedAgent === 'all'
    ? allLines
    : allLines.filter(l => l.agentName === selectedAgent),
  [allLines, selectedAgent]
);
```

Do NOT drop the hidden lines from the store - other agents keep streaming and
must still be there when the user switches back to All or to that agent.

### Sync node <-> tab
Both the agent node (left) and the tab (top) call the same
`setSelectedAgent(name)`. Both read `selectedAgent` to show their active state.
One source of truth, no divergence.

---

## Visual Requirements (match the mockup)

Left panel, per the mockup:
- Root card: pulse dot + "SmokeTest" + "4 agents" pill + "Root pipeline .
  session <id> . running <elapsed>" + a thin progress bar.
- Agent nodes: status dot (green running / grey idle), agent name in the
  agent's color (monospace), a sub-line (ok/fail/elapsed OR "Not scheduled this
  run"), and a status badge (RUNNING / IDLE).
- An "All agents" node at the top of the list.
- Selected node has the blue highlight.
- A small "NODE STATUS" legend explaining Running vs Idle.

Top tabs:
- `All` + one tab per agent, each with a status dot.
- Active tab uses the blue fill.

Log body:
- When filtered, show a small note: "Filtered to JVGR1 - showing N of M lines".
- Keep the existing monospace formatting, agent-colored `[AGENT]` tags, and
  level colors (PASS green, INFO grey, EventLog purple).

### Agent color mapping (consistent across nodes, tabs, and log tags)
```
JVGR1  -> blue    (#2f6df6)
JVGR2  -> purple  (#7a4bc4)
JVHIST -> amber   (#b26a00)
WARMGR -> green   (#1a9c62)
```
===== PLUG IN: if agents are dynamic, generate a stable color per agent name
(hash -> palette) instead of hardcoding. =====

---

## Empty vs Active States

- **No active session**: keep a clear empty state ("No active sessions. Use
  Trigger All or trigger individual WatchItems.") - but make sure this only
  shows when there is GENUINELY no running session, not when the check is
  simply wrong (Problem 1).
- **Active session**: show the root + agent nodes as above.
- **Agent idle within an active session**: show the node greyed with "IDLE" /
  "Not scheduled this run" - do NOT hide it; seeing which agents did NOT run is
  useful information.

---

## Acceptance Criteria

| ID | Criterion |
|---|---|
| AC-1 | When a session is running, the left panel shows the Root pipeline node, not "No active sessions" |
| AC-2 | All participating agents appear as flat nodes under the root |
| AC-3 | Running agents show a green dot + RUNNING; idle/not-scheduled show grey + IDLE |
| AC-4 | The panel updates live as events stream (no stale snapshot) |
| AC-5 | Tabs show All + one per agent, each with a status dot |
| AC-6 | Default selection is All (combined stream) |
| AC-7 | Selecting an agent filters the log to only that agent's lines |
| AC-8 | Clicking a left-panel agent node selects the same agent as the tab (in sync) |
| AC-9 | Switching back to All restores the full combined stream (no lost lines) |
| AC-10 | Agent colors are consistent across node, tab, and log tag |

---

## Implementation Order

1. **Diagnose Problem 1** - find why the panel is empty; report the cause.
2. **Derive session/agent view model** from the live event stream (or fix the
   subscription) so the panel populates.
3. **Add `selectedAgent` state** + the filter selector for the log.
4. **Build the agent tabs** (All + per agent with status dots).
5. **Wire node <-> tab sync** through the single `selectedAgent` action.
6. **Style to match the mockup** (nodes, badges, legend, filter note).
7. **Verify** against all acceptance criteria with a live multi-agent run.

Start with step 1: show me the Sessions panel component, where it reads its
data, and where the Live Execution Log reads its data - then tell me why one
has data and the other doesn't.

---

## Design Principles

- One source of truth for "selected agent" - node and tab both read/write it.
- Filter at render, never mutate the event list - hidden agents keep streaming.
- Panel and log derive from the SAME live stream - they can't disagree.
- Idle agents stay visible - "which agents did NOT run" is useful QA signal.
