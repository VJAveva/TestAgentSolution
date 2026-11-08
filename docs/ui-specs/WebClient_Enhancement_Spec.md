# WebClient Enhancement Specification

> **Purpose**: Enhance the existing TestAgentSolution WebClient to (a) match the WPF ControllerService Execution Dashboard visually and functionally, and (b) add a hierarchical Test Plan tree view for the WatchList. Real-time updates via SignalR with REST polling fallback. Pure SPA (React + TypeScript) served by the API.

> **How to use this document**: Paste this entire document into GitHub Copilot Chat (or any coding assistant). Each section is self-contained. Generate files in the order listed in **Section 11 — File Generation Order**.

---

## 1. Context

The current `TestAgentSolution.WebClient` is a single-page application served by `TestAgentSolution.Api` (ASP.NET Core). It has three top-level tabs visible in the toolbar:

- **TestController** — issue commands to agents
- **WatchList** — flat list of triggers and templates (7 items + 2 templates)
- **Agents** — live agent status

This enhancement adds two capabilities:

1. **WatchList tree view** — drill into any WatchList item and see its full hierarchy: WatchListItem → Events → Sequences → Actions (Initialize, References, Parallel groups, Remote commands). The tree shows what the test plan will execute, with action-type badges (`EVT`, `SEQ`, `INIT`, `REF`, `PAR`, `RMT`, `cmd`).

2. **Execution Dashboard tab** — new top-level tab that mirrors the WPF ControllerService's Execution Dashboard. Three sub-views: Pipeline (collapsible session cards with agent action chips), Timeline (per-session Gantt charts), Unified Log (filterable virtualized log stream). Header metric strip showing Active sessions / Agents locked / Actions passed / Actions failed / Overall progress.

Both views consume the same `ControllerService` SignalR hub the WPF dashboard uses, with REST polling as a fallback when SignalR is unavailable.

---

## 2. Goals

1. **Visual parity with WPF dashboard.** Same dark palette, same component patterns, same accent colors per status.
2. **Functional parity with WPF.** Anything the WPF dashboard can show, the WebClient can show.
3. **Hierarchical test plan visibility.** Operators can see exactly what will run, in what order, on which agents, before clicking Trigger.
4. **Real-time without WebSocket dependency.** SignalR when reachable; degrades gracefully to polling.
5. **No breaking changes to existing API or storage.** Only additive endpoints and DTOs.

---

## 3. Tech Stack

| Concern | Choice | Notes |
|---|---|---|
| Framework | React 18 + TypeScript 5 | Functional components, hooks |
| Build tool | Vite 5 | Fast dev server with HMR; static build for production |
| Routing | React Router 6 | `/dashboard`, `/watchlist`, `/agents`, `/controller` |
| Server state | TanStack Query 5 | Caching, polling, refetch coordination |
| Real-time | `@microsoft/signalr` 8 | Same hub URL as WPF dashboard |
| Styling | Tailwind CSS 3 + CSS variables | Theme tokens match WPF colors |
| Icons | Lucide React | Lightweight, tree-shakeable |
| Virtualized lists | `@tanstack/react-virtual` 3 | For the Unified Log view (thousands of rows) |
| Date formatting | `date-fns` 3 | Lightweight, immutable |
| Hosting | ASP.NET Core static files | API serves the `dist/` build at root |

**Why React over Vue here:** TypeScript story is more mature in the .NET ecosystem, TanStack Query + SignalR have first-class React bindings, and the team most likely already has React fluency given the WPF MVVM background (component+state model maps cleanly).

---

## 4. Final UI Specification

The window is organized as a fixed top chrome (logo + tabs + connection indicator), a body that changes per route, and a status footer.

### 4.1 Top chrome (always visible, ~56px tall)

A horizontal bar with:

- **Left**: TestAgent logo + product name `TestController` in 16px medium weight.
- **Center**: Tab strip — `Controller`, `WatchList`, `Agents`, `Dashboard`. Each tab is an underline-style button:
  - Inactive: secondary text color, no underline, hover changes text to primary
  - Active: primary text color, 2px accent-blue underline along the bottom edge of the bar
- **Right**: Connection indicator (small dot + label) and a user/settings dropdown
  - Connection states: `Live` (green dot, SignalR connected), `Polling` (yellow dot, falling back to REST), `Offline` (red dot, neither working)

### 4.2 Status footer (always visible, ~28px tall)

A thin strip with:

- Left: current SignalR/polling status (matching the top chrome indicator) and the controller node name (e.g., `Controller: jvgr1`)
- Right: build version, link to API health endpoint

### 4.3 View — WatchList

This is the existing view, **enhanced** with a tree-mode toggle. The current flat list stays; a new tree mode is the second display option.

#### Header strip
- Left: title `WATCHLIST` in 12px uppercase letter-spaced
- Right: view-mode toggle — two icons, list and tree. Default = list. Selection persists in localStorage.

#### Action bar (unchanged from current)
Buttons in this exact order: `Trigger`, `Cancel`, `Import`, `Export`, `Refresh`. Same visual treatment as the screenshot.

#### Body — list mode (current behavior)
The existing flat list with `WatchList (N items)` and `Templates (M)` collapsible sections. No changes.

#### Body — tree mode (new — matches screenshot 2)

Renders the same data as a hierarchical tree. Each row has a chevron, a name, an action-type badge, and optional metadata (action count, target hostname).

**Tree-row schema, top to bottom:**

```
▾ [icon] Test Plan (WatchList.xml — 7 items)            7 tests · 55 steps
  ▾ ⊙ Revert Sanity Machines                            1 item
    ▾ ⊙ Renamed                          EVT            2 actions
      ▾ ⊙ RevertingAgents                SEQ            2 actions
          ⊙ Initialize                   INIT
          ↺ RevertSanityTestAgents       REF
      ▸ ⊙ NewActionGroup                 SEQ
  ▾ ⊙ Revert Warm Machines                              1 item
    ▾ ⊙ Renamed                          EVT            2 actions
      ▾ ⊙ RevertingAgents                SEQ            2 actions
          ⊙ Initialize                   INIT
          ↺ RevertWarmAgents             REF
      ▸ ⊙ NewActionGroup                 SEQ
  ▾ ⊙ Sanity Tests on Five nodes                        1 item
    ▾ ⊙ Renamed                          EVT            1 action
      ▾ ⊙ Install Build on Multi node and Execution   SEQ   4 actions
        ▸ ⊙ Initialize                   INIT
        ▾ ⊙ Setup Pre-Requiste on Nodes  SEQ            2 actions
          ▾ ⊙ Copy Agent Prep batch      PAR            5 actions
              ▸ Copy Prepare-Agent.bat on jvgr1   RMT   cmd
              ▸ Copy Prepare-Agent.bat on jvgr2   RMT   cmd
              ▸ Copy Prepare-Agent.bat on jvhist  RMT   cmd
              ▸ Copy Prepare-Agent.bat on jvkpri  RMT   cmd
              ▸ Copy Prepare-Agent.bat on jvkbak  RMT   cmd
          ▸ ⊙ Run Agent Prep batch       PAR            5 actions
```

**Visual rules:**
- Indentation: 18px per level
- Chevron: 12px, rotates 90° when expanded; uses `▸` collapsed, `▾` expanded
- Row icon: small filled circle (`⊙`) for groups, return-arrow (`↺`) for references, no icon for plain commands
- Badge order: action-type badge (EVT/SEQ/INIT/REF/PAR/RMT) first, then `cmd` if it's a shell command
- Action count: appears right-aligned for any node containing children
- Hover: row background lightens by ~6%
- Click on row or chevron: toggle expand/collapse
- Double-click on a leaf (e.g., a `cmd` row): open a side panel showing the full command line, target agent, expected exit codes, and any environment vars
- Drag-and-drop is **not** in scope for v1; tree is read-only for editing (use Import/Export with XML files for now)

**Badge palette** (background tint at ~20% alpha, border at full opacity, text at full opacity):

| Badge | Color | Meaning |
|---|---|---|
| `EVT` | purple `#A78BFA` | Event handler — triggered by an outer condition |
| `SEQ` | sky-blue `#38BDF8` | Sequence — actions run in order, stop on first failure |
| `PAR` | indigo `#818CF8` | Parallel — actions run concurrently across targets |
| `INIT` | teal `#2DD4BF` | Initialization step |
| `REF` | amber `#FBBF24` | Reference to a template (expand inline or jump to template) |
| `RMT` | rose `#FB7185` | Remote action — runs on a named target host |
| `cmd` | slate `#94A3B8` | Plain shell command |

#### Side panel — leaf action details
Slides in from the right at 360px wide. Sections:
- Header: command name + close (X)
- Target: agent name with status dot
- Command line (monospace, full path)
- Working directory
- Expected exit codes
- Environment overrides (key=value list)
- Estimated duration (from history)
- "Show in Pipeline" button — switches to the Dashboard tab and scrolls to the matching action in any active session

### 4.4 View — Execution Dashboard (new tab)

This is the new top-level tab. It is **layout-identical to the WPF Execution Dashboard** so operators can switch between WPF desktop and web browser without retraining.

#### Sub-navigation strip
Three pill-style buttons stacked horizontally just under the top chrome: `Pipeline`, `Timeline`, `Unified log`. Selected pill has the elevated card background + 0.5px hairline border.

#### Metric strip (always visible under the sub-nav)
Five equal-width cards in a horizontal row with a 12px gap:

| Card | Value | Color |
|---|---|---|
| Active sessions | integer | accent blue |
| Agents locked | integer | primary text |
| Actions passed | integer | accent green |
| Actions failed | integer | accent red |
| Overall progress | `NN%` | primary text |

Each card: card background, rounded-md, 16px padding, 20px medium-weight number on top, 11px secondary label below.

#### Sub-view 1 — Pipeline

Filter strip at top:
- Text input with placeholder `Filter by session, agent, or action...`
- Segmented control: `All` / `Running` / `Failed`

Below the filter, a scrollable list of session cards. Each card:
- Header row (clickable to expand/collapse):
  - Caret chevron, status pill (`Running` blue / `Completed` green / `Failed` red), session name
  - Right-aligned: owner badge, agent count, elapsed time (mono), progress percentage
- Body (visible when expanded): one row per agent
  - Left column (120px, fixed): agent name (mono), state label below in status color, 3px progress bar at bottom filled to overall progress %
  - Right column (flex): WrapPanel of action chips with `→` between them

Action chip variants (same vocabulary as WPF):

| Variant | Glyph | Background | Border | Text | Notes |
|---|---|---|---|---|---|
| Done | `✓` | green tint | green | green | |
| Running | `●` | blue tint | blue | blue | pulse opacity 1 → 0.4 → 1 on 1.5s loop |
| Failed | `✕` | red tint | red | red | shows reason in parens after name |
| Pending | `○` | transparent | gray | tertiary text | |
| Rebooting | `↻` | yellow tint | yellow | yellow | |

If an agent finishes its work but the session is still waiting, show italic muted text after the last chip: `Waiting for jvgr1 to finish...`

**Special collapsed-summary cases** inside the body row:
- `All N actions passed` — single green chip filling the row
- Banner row above agents: `2 agents failed installation. 3 agents completed successfully.` — red tint background, full width

#### Sub-view 2 — Timeline

Centered explanatory paragraph at top (4 lines, secondary color):

> Timeline view renders a horizontal Gantt chart per session.
> Each agent is a row, actions are bars positioned by start time and duration.
> Color-coded: green = done, blue = running, red = failed, gray = pending.
> Overlapping parallel actions shown on stacked lanes within the agent row.

Below, scrollable list of Gantt cards. Each card:
- Session name header
- Hour ruler row at top (mono `0:00`, `0:30`, `1:00`, `1:30`)
- Per-agent rows: 60px-wide agent name on left, flex-grow timeline strip on right
- Inside the timeline strip, action bars sit side-by-side with 2px gap; each bar 18px tall, 3px corner radius
- Bar widths proportional to duration as a percentage of the visible window
- Bar colors match action status; running bars show a `●` pulse glyph at the right edge
- Inscribed monospace label inside the bar if width > 60px, ellipsis otherwise

#### Sub-view 3 — Unified log

Top filter row:
- Text input `Search logs...` (flex-grows)
- Two ComboBoxes side by side:
  - Session filter: `All sessions` / one entry per active session name
  - Level filter: `All levels` / `Errors only`

Second row: filter pills `All` / one per agent name. Multi-select OR semantics within the agent dimension.

Below: card-bordered container holding a virtualized list. Each log row, left to right:
- Timestamp (mono, 60px, tertiary color, `HH:mm:ss`)
- Session tag badge (rounded 3px, 9px text, colored by session status)
- Agent name (mono, 60px, accent-blue)
- Message (flex-grow, ellipsis on overflow, color-coded by level)

Levels:
- `Error` → red
- `Warning` → yellow
- `Success` → green
- `Info` → secondary text

Cap at 5000 rows client-side; drop oldest as new arrive.

### 4.5 View — Agents (existing, light enhancements only)

No structural change. Apply the same dark theme tokens for visual consistency with the new views.

---

## 5. Color Tokens (CSS variables)

Defined once in `src/styles/theme.css` and referenced via Tailwind's `theme.extend.colors` and direct `var()` references. Values match WPF exactly so screenshots compare 1:1.

```css
:root {
  /* Backgrounds */
  --bg-canvas:        #111418;  /* page background */
  --bg-primary:       #1A1F26;  /* cards, panels */
  --bg-secondary:     #222831;  /* elevated surfaces (selected, hover) */
  --bg-chip:          #2A313C;  /* small badges */

  /* Borders */
  --border-tertiary:  #2D343F;  /* hairlines, default chip borders */
  --border-info:      #4A9EFF;
  --border-success:   #3DDC84;
  --border-danger:    #FF5C6C;
  --border-warning:   #E0B341;

  /* Tinted backgrounds (20% alpha of accent) */
  --bg-info:          #334A9EFF;
  --bg-success:       #333DDC84;
  --bg-danger:        #33FF5C6C;
  --bg-warning:       #33E0B341;

  /* Text */
  --text-primary:     #E6E9EE;
  --text-secondary:   #9098A4;
  --text-tertiary:    #6B7280;
  --text-info:        #4A9EFF;
  --text-success:     #3DDC84;
  --text-danger:      #FF5C6C;
  --text-warning:     #E0B341;

  /* Badge accents for tree view action-type badges */
  --badge-evt:        #A78BFA;
  --badge-seq:        #38BDF8;
  --badge-par:        #818CF8;
  --badge-init:       #2DD4BF;
  --badge-ref:        #FBBF24;
  --badge-rmt:        #FB7185;
  --badge-cmd:        #94A3B8;

  /* Radii */
  --radius-sm: 3px;
  --radius-md: 4px;
  --radius-lg: 8px;
}
```

**Tailwind config integration** (`tailwind.config.ts`):

```ts
export default {
  content: ["./index.html", "./src/**/*.{ts,tsx}"],
  theme: {
    extend: {
      colors: {
        canvas:       "var(--bg-canvas)",
        "bg-primary": "var(--bg-primary)",
        "bg-secondary": "var(--bg-secondary)",
        chip:         "var(--bg-chip)",
        border:       "var(--border-tertiary)",
        info:         "var(--text-info)",
        success:      "var(--text-success)",
        danger:       "var(--text-danger)",
        warning:      "var(--text-warning)",
        muted:        "var(--text-secondary)",
        subtle:       "var(--text-tertiary)",
        // badges
        "badge-evt":  "var(--badge-evt)",
        "badge-seq":  "var(--badge-seq)",
        "badge-par":  "var(--badge-par)",
        "badge-init": "var(--badge-init)",
        "badge-ref":  "var(--badge-ref)",
        "badge-rmt":  "var(--badge-rmt)",
        "badge-cmd":  "var(--badge-cmd)",
      },
      fontFamily: {
        sans: ["'Segoe UI'", "system-ui", "sans-serif"],
        mono: ["Consolas", "'Cascadia Mono'", "monospace"],
      },
    },
  },
};
```

---

## 6. TypeScript Data Models

Defined in `src/types/orchestration.ts`. These mirror the C# `Models/ExecutionModels.cs` types from the WPF dashboard so the SignalR payloads decode without translation.

```ts
export type ActionStatus = "Pending" | "Running" | "Done" | "Failed" | "Rebooting" | "Idle";
export type SessionStatus = "Idle" | "Running" | "Completed" | "Failed" | "Cancelled";
export type AgentState = "Idle" | "Executing" | "Failed" | "Rebooting" | "Done" | "Waiting";
export type LogLevel = "Info" | "Warning" | "Error" | "Success";

/** Tree-view action types (drives the badge colors) */
export type ActionType = "EVT" | "SEQ" | "PAR" | "INIT" | "REF" | "RMT" | "cmd";

export interface ActionItem {
  id: string;
  name: string;
  status: ActionStatus;
  progress: number;          // 0-100
  startTime?: string;         // ISO-8601
  endTime?: string;           // ISO-8601
  failReason?: string;
  displayLabel: string;       // computed server-side: "Name (XX%)" when running etc.
}

export interface AgentItem {
  id: string;
  name: string;
  state: AgentState;
  overallProgress: number;    // 0-100
  waitingFor?: string;
  actions: ActionItem[];
  stateLabel: string;         // server-computed display string
}

export interface SessionItem {
  id: string;
  name: string;
  owner: string;
  status: SessionStatus;
  elapsedSeconds: number;
  progress: number;
  isExpanded?: boolean;       // client-side UI state, not persisted
  summaryMessage?: string;
  hasErrorBanner?: boolean;
  agents: AgentItem[];
  elapsedDisplay: string;     // server-computed "HH:mm:ss"
}

export interface LogEntry {
  timestamp: string;          // ISO-8601
  sessionTag: string;
  sessionStatus: SessionStatus;
  agentName: string;
  message: string;
  level: LogLevel;
}

export interface DashboardMetrics {
  activeSessions: number;
  agentsLocked: number;
  actionsPassed: number;
  actionsFailed: number;
  overallProgress: number;    // 0-100
}

/** Tree-view test plan models */
export interface PlanNode {
  id: string;
  name: string;
  actionType: ActionType;
  childCount?: number;        // for non-leaf nodes
  actionCount?: number;       // server-computed aggregate
  isLeaf: boolean;
  children?: PlanNode[];
  /** Only populated for cmd/RMT leaf nodes */
  leaf?: PlanLeafDetail;
}

export interface PlanLeafDetail {
  targetAgent: string;
  commandLine: string;
  workingDirectory?: string;
  expectedExitCodes: number[];
  environment?: Record<string, string>;
  estimatedDurationSeconds?: number;
}

export interface WatchListTreeRoot {
  fileName: string;            // "WatchList.xml"
  itemCount: number;
  testCount: number;           // for the summary "7 tests · 55 steps"
  stepCount: number;
  items: PlanNode[];
}
```

---

## 7. SignalR Contract + Polling Fallback

### 7.1 Hub

The hub URL is the same one the WPF dashboard uses: `{apiBaseUrl}/hubs/execution`. The web client connects with `WithAutomaticReconnect()`. Server-emitted messages:

| Method | Args | Server emits when |
|---|---|---|
| `SessionUpserted` | `SessionItem` | Session created OR status/progress/elapsed changed |
| `AgentUpserted` | `sessionId: string, agent: AgentItem` | Agent joined OR state/progress changed |
| `ActionUpserted` | `sessionId: string, agentId: string, action: ActionItem` | Action started OR status/progress changed |
| `LogAppended` | `LogEntry` | Any surfaced log line |
| `MetricsUpdated` | `DashboardMetrics` | Header counters changed |
| `WatchListChanged` | `WatchListTreeRoot` | Tree structure modified (import, edit) — triggers tree refetch |

### 7.2 Polling fallback

When the SignalR connection state is anything other than `Connected` for more than 5 seconds, the client switches to polling. TanStack Query handles this with `refetchInterval`:

| Endpoint | Path | Method | Interval when polling |
|---|---|---|---|
| Sessions list | `/api/sessions` | GET | 2000 ms |
| Single session detail | `/api/sessions/{id}` | GET | 2000 ms (only when one is open) |
| Metrics | `/api/dashboard/metrics` | GET | 3000 ms |
| Recent logs | `/api/logs?since={iso}` | GET | 1000 ms (cursor-paginated) |
| WatchList tree | `/api/watchlist/tree` | GET | 30000 ms (rarely changes) |

When SignalR reconnects, polling is paused and an immediate refetch of all queries kicks off to backfill anything missed during the gap.

### 7.3 Connection-state state machine

```
              SignalR start succeeds
   Connecting ───────────────────────────► Live
       │                                     │
       │ 5s of failed reconnect              │ Connection drops
       ▼                                     ▼
   Polling ◄──────────────────────────── Reconnecting
       │      Reconnect succeeds              ▲
       │                                     │
       │ 5s of failed REST polling           │ Connection recovers
       ▼                                     │
   Offline ─────────────────────────────────┘
```

The status indicator in the top chrome reflects this state directly: green / yellow / red.

---

## 8. Architecture

### 8.1 Component hierarchy

```
<App>
  <QueryClientProvider>
    <SignalRProvider>
      <BrowserRouter>
        <Layout>
          <TopChrome />                    ← logo, tabs, connection indicator
          <Routes>
            /controller     → <ControllerView />
            /watchlist      → <WatchListView />   ← list ⇄ tree toggle
            /agents         → <AgentsView />
            /dashboard      → <DashboardView>
                                 /pipeline   → <PipelinePanel />
                                 /timeline   → <TimelinePanel />
                                 /logs       → <UnifiedLogPanel />
                               </DashboardView>
          </Routes>
          <StatusFooter />
        </Layout>
      </BrowserRouter>
    </SignalRProvider>
  </QueryClientProvider>
</App>
```

### 8.2 Hooks (the layer that hides SignalR vs polling)

Components never talk to SignalR directly; they call hooks that abstract the source.

| Hook | Returns | Internally |
|---|---|---|
| `useSessions()` | `SessionItem[]` | Subscribes to `SessionUpserted/AgentUpserted/ActionUpserted` when SignalR is live; falls back to TanStack Query `/api/sessions` polling |
| `useSession(id)` | `SessionItem \| undefined` | Single-session variant |
| `useMetrics()` | `DashboardMetrics` | `MetricsUpdated` or `/api/dashboard/metrics` |
| `useLogs(filters)` | `LogEntry[]` (last 5000) | `LogAppended` stream merged with REST backfill |
| `useWatchListTree()` | `WatchListTreeRoot` | One-shot fetch + invalidation on `WatchListChanged` |
| `useConnectionStatus()` | `"Live" \| "Polling" \| "Offline"` | Source of the connection indicator |

Each hook returns an object with `data`, `isLoading`, `error`, and `source: "signalr" \| "polling"` so views can optionally show the source in tooltips.

### 8.3 SignalR service

A singleton class wraps `HubConnectionBuilder`:

```ts
class ExecutionHubClient {
  private connection: HubConnection | null = null;
  private state: BehaviorSubject<ConnectionState> = ...;

  on<T>(method: string, handler: (payload: T) => void): () => void;   // returns unsubscribe
  start(): Promise<void>;
  stop(): Promise<void>;
  get state$(): Observable<ConnectionState>;
}
```

The `SignalRProvider` instantiates one client, attaches reconnect/state listeners, and exposes both the client and the current state via React context.

---

## 9. File Structure

```
TestAgentSolution.WebClient/
├── index.html
├── package.json
├── vite.config.ts
├── tailwind.config.ts
├── tsconfig.json
├── postcss.config.js
└── src/
    ├── main.tsx                          ← entry point
    ├── App.tsx                           ← QueryClientProvider, SignalRProvider, router
    ├── styles/
    │   ├── theme.css                     ← CSS variables (section 5)
    │   └── globals.css                   ← Tailwind directives, body styles
    ├── types/
    │   ├── orchestration.ts              ← all DTOs from section 6
    │   └── api.ts                        ← REST request/response shapes
    ├── api/
    │   ├── client.ts                     ← fetch wrapper with base URL + auth
    │   ├── sessions.ts                   ← GET /api/sessions, etc.
    │   ├── logs.ts                       ← GET /api/logs?since=…
    │   ├── metrics.ts                    ← GET /api/dashboard/metrics
    │   └── watchlist.ts                  ← GET /api/watchlist, GET /api/watchlist/tree
    ├── signalr/
    │   ├── ExecutionHubClient.ts         ← singleton wrapper
    │   ├── SignalRProvider.tsx           ← React context provider
    │   └── useSignalRConnection.ts       ← exposes status + raw client
    ├── hooks/
    │   ├── useSessions.ts
    │   ├── useSession.ts
    │   ├── useMetrics.ts
    │   ├── useLogs.ts
    │   ├── useWatchListTree.ts
    │   └── useConnectionStatus.ts
    ├── components/
    │   ├── layout/
    │   │   ├── Layout.tsx
    │   │   ├── TopChrome.tsx
    │   │   ├── TabStrip.tsx
    │   │   ├── ConnectionIndicator.tsx
    │   │   └── StatusFooter.tsx
    │   ├── common/
    │   │   ├── Badge.tsx                 ← action-type and status badges
    │   │   ├── MetricCard.tsx
    │   │   ├── SegmentedControl.tsx
    │   │   ├── Chip.tsx                   ← action chips for Pipeline view
    │   │   ├── StatusPill.tsx
    │   │   ├── ProgressBar.tsx
    │   │   └── SidePanel.tsx              ← slide-in right panel for tree leaf details
    │   ├── tree/
    │   │   ├── TreeView.tsx               ← generic recursive tree component
    │   │   ├── TreeNode.tsx               ← single row
    │   │   └── TreeRowBadges.tsx          ← action-type badge cluster
    │   ├── pipeline/
    │   │   ├── PipelinePanel.tsx
    │   │   ├── SessionCard.tsx
    │   │   ├── AgentRow.tsx
    │   │   └── ActionChipRow.tsx
    │   ├── timeline/
    │   │   ├── TimelinePanel.tsx
    │   │   ├── GanttCard.tsx
    │   │   └── GanttRow.tsx                ← SVG-based bar renderer
    │   └── logs/
    │       ├── UnifiedLogPanel.tsx
    │       ├── LogFilterBar.tsx
    │       └── VirtualizedLogList.tsx
    └── views/
        ├── ControllerView.tsx              ← existing, theme-updated
        ├── WatchListView.tsx               ← list/tree toggle
        ├── AgentsView.tsx                  ← existing, theme-updated
        └── DashboardView.tsx               ← sub-router + metric strip
```

### 9.1 Backend file additions

```
src/
├── TestAgentSolution.Api/
│   ├── Controllers/
│   │   ├── WatchListController.cs           ← add tree endpoint
│   │   └── DashboardController.cs           ← new: /api/dashboard/metrics
│   ├── Hubs/
│   │   └── ExecutionHub.cs                  ← already exists for WPF; reuse
│   ├── Services/
│   │   └── WatchListTreeService.cs          ← XML → PlanNode[]
│   └── wwwroot/                              ← Vite build output goes here
│       └── (dist files)
```

---

## 10. File-by-File Specifications

### 10.1 `package.json`

```json
{
  "name": "testagent-webclient",
  "private": true,
  "version": "1.0.0",
  "type": "module",
  "scripts": {
    "dev": "vite",
    "build": "tsc && vite build",
    "preview": "vite preview"
  },
  "dependencies": {
    "@microsoft/signalr": "^8.0.0",
    "@tanstack/react-query": "^5.0.0",
    "@tanstack/react-virtual": "^3.0.0",
    "date-fns": "^3.0.0",
    "lucide-react": "^0.300.0",
    "react": "^18.2.0",
    "react-dom": "^18.2.0",
    "react-router-dom": "^6.20.0"
  },
  "devDependencies": {
    "@types/react": "^18.2.0",
    "@types/react-dom": "^18.2.0",
    "@vitejs/plugin-react": "^4.2.0",
    "autoprefixer": "^10.4.16",
    "postcss": "^8.4.32",
    "tailwindcss": "^3.4.0",
    "typescript": "^5.3.0",
    "vite": "^5.0.0"
  }
}
```

### 10.2 `vite.config.ts`

```ts
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      "/api":  { target: "http://localhost:5000", changeOrigin: true },
      "/hubs": { target: "http://localhost:5000", ws: true, changeOrigin: true },
    },
  },
  build: {
    outDir: "../TestAgentSolution.Api/wwwroot",
    emptyOutDir: true,
  },
});
```

### 10.3 `src/signalr/ExecutionHubClient.ts`

A thin wrapper around `HubConnection`. Listed in full because handler bookkeeping is fiddly.

```ts
import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from "@microsoft/signalr";

export type ConnectionState = "Connecting" | "Connected" | "Reconnecting" | "Disconnected";

export class ExecutionHubClient {
  private connection: HubConnection;
  private listeners = new Map<string, Set<(arg: unknown) => void>>();
  private stateListeners = new Set<(s: ConnectionState) => void>();
  private currentState: ConnectionState = "Disconnected";

  constructor(hubUrl: string) {
    this.connection = new HubConnectionBuilder()
      .withUrl(hubUrl)
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build();

    this.connection.onreconnecting(() => this.setState("Reconnecting"));
    this.connection.onreconnected(() => this.setState("Connected"));
    this.connection.onclose(() => this.setState("Disconnected"));
  }

  on<T = unknown>(method: string, handler: (payload: T) => void): () => void {
    if (!this.listeners.has(method)) {
      this.listeners.set(method, new Set());
      this.connection.on(method, (payload: T) => {
        this.listeners.get(method)?.forEach((h) => h(payload));
      });
    }
    this.listeners.get(method)!.add(handler as (a: unknown) => void);
    return () => this.listeners.get(method)?.delete(handler as (a: unknown) => void);
  }

  onState(handler: (s: ConnectionState) => void): () => void {
    this.stateListeners.add(handler);
    handler(this.currentState);
    return () => this.stateListeners.delete(handler);
  }

  async start(): Promise<void> {
    if (this.connection.state !== HubConnectionState.Disconnected) return;
    this.setState("Connecting");
    try {
      await this.connection.start();
      this.setState("Connected");
    } catch (e) {
      this.setState("Disconnected");
      throw e;
    }
  }

  async stop(): Promise<void> {
    await this.connection.stop();
  }

  get isConnected(): boolean {
    return this.connection.state === HubConnectionState.Connected;
  }

  private setState(s: ConnectionState) {
    this.currentState = s;
    this.stateListeners.forEach((h) => h(s));
  }
}
```

### 10.4 `src/hooks/useSessions.ts`

The hook that hides SignalR vs polling. This is the pattern; replicate it for the other hooks.

```ts
import { useEffect } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { fetchSessions } from "../api/sessions";
import { useSignalRConnection } from "../signalr/useSignalRConnection";
import type { SessionItem, AgentItem, ActionItem } from "../types/orchestration";

export function useSessions() {
  const { hub, status } = useSignalRConnection();
  const qc = useQueryClient();

  const query = useQuery({
    queryKey: ["sessions"],
    queryFn: fetchSessions,
    refetchInterval: status === "Live" ? false : 2000,
    staleTime: status === "Live" ? Infinity : 0,
  });

  // SignalR live updates → patch the cached query result.
  useEffect(() => {
    if (status !== "Live") return;

    const offSession = hub.on<SessionItem>("SessionUpserted", (incoming) => {
      qc.setQueryData<SessionItem[]>(["sessions"], (prev = []) => {
        const i = prev.findIndex((s) => s.id === incoming.id);
        if (i === -1) return [...prev, incoming];
        const next = [...prev];
        next[i] = { ...next[i], ...incoming, agents: next[i].agents };
        return next;
      });
    });

    const offAgent = hub.on<{ sessionId: string; agent: AgentItem }>(
      "AgentUpserted",
      ({ sessionId, agent }) => {
        qc.setQueryData<SessionItem[]>(["sessions"], (prev = []) =>
          prev.map((s) => {
            if (s.id !== sessionId) return s;
            const ai = s.agents.findIndex((a) => a.id === agent.id);
            const agents = ai === -1
              ? [...s.agents, agent]
              : s.agents.map((a, idx) => (idx === ai ? { ...a, ...agent, actions: a.actions } : a));
            return { ...s, agents };
          })
        );
      }
    );

    const offAction = hub.on<{ sessionId: string; agentId: string; action: ActionItem }>(
      "ActionUpserted",
      ({ sessionId, agentId, action }) => {
        qc.setQueryData<SessionItem[]>(["sessions"], (prev = []) =>
          prev.map((s) => {
            if (s.id !== sessionId) return s;
            return {
              ...s,
              agents: s.agents.map((a) => {
                if (a.id !== agentId) return a;
                const ai = a.actions.findIndex((x) => x.id === action.id);
                const actions = ai === -1
                  ? [...a.actions, action]
                  : a.actions.map((x, idx) => (idx === ai ? { ...x, ...action } : x));
                return { ...a, actions };
              }),
            };
          })
        );
      }
    );

    return () => {
      offSession();
      offAgent();
      offAction();
    };
  }, [hub, status, qc]);

  return {
    data: query.data ?? [],
    isLoading: query.isLoading,
    error: query.error,
    source: status === "Live" ? ("signalr" as const) : ("polling" as const),
  };
}
```

The same upsert + cache-merge pattern applies to `useMetrics`, `useLogs`, etc. — only the SignalR method names and cache keys change.

### 10.5 `src/components/tree/TreeView.tsx`

Recursive tree component. Single source of truth for layout, badge rendering, and expand/collapse state. The state lives in the parent so it survives data updates.

```tsx
import { useState, useCallback } from "react";
import { ChevronRight } from "lucide-react";
import type { PlanNode } from "../../types/orchestration";
import { TreeRowBadges } from "./TreeRowBadges";

interface TreeViewProps {
  nodes: PlanNode[];
  onLeafClick?: (node: PlanNode) => void;
  defaultExpandedDepth?: number;        // expand top N levels by default
}

export function TreeView({ nodes, onLeafClick, defaultExpandedDepth = 2 }: TreeViewProps) {
  const [expanded, setExpanded] = useState<Set<string>>(() => {
    const set = new Set<string>();
    const seed = (list: PlanNode[], depth: number) => {
      if (depth >= defaultExpandedDepth) return;
      list.forEach((n) => {
        if (!n.isLeaf) {
          set.add(n.id);
          if (n.children) seed(n.children, depth + 1);
        }
      });
    };
    seed(nodes, 0);
    return set;
  });

  const toggle = useCallback((id: string) => {
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }, []);

  const renderNode = (node: PlanNode, depth: number) => {
    const isOpen = expanded.has(node.id);
    return (
      <div key={node.id}>
        <button
          type="button"
          onClick={() => (node.isLeaf ? onLeafClick?.(node) : toggle(node.id))}
          onDoubleClick={() => node.isLeaf && onLeafClick?.(node)}
          className="group flex w-full items-center gap-2 py-1.5 pr-2 text-left text-sm hover:bg-bg-secondary"
          style={{ paddingLeft: `${depth * 18 + 8}px` }}
        >
          {!node.isLeaf ? (
            <ChevronRight
              size={12}
              className={`shrink-0 text-muted transition-transform ${isOpen ? "rotate-90" : ""}`}
            />
          ) : (
            <span className="w-3 shrink-0" />
          )}

          <span className="shrink-0 text-muted">
            {node.actionType === "REF" ? "↺" : "⊙"}
          </span>

          <span className="grow truncate text-[var(--text-primary)]">{node.name}</span>

          <TreeRowBadges node={node} />

          {node.actionCount !== undefined && (
            <span className="shrink-0 text-xs text-subtle">
              {node.actionCount} {node.actionCount === 1 ? "action" : "actions"}
            </span>
          )}
        </button>

        {isOpen && node.children && (
          <div>{node.children.map((c) => renderNode(c, depth + 1))}</div>
        )}
      </div>
    );
  };

  return <div className="font-sans">{nodes.map((n) => renderNode(n, 0))}</div>;
}
```

### 10.6 `src/components/tree/TreeRowBadges.tsx`

```tsx
import type { PlanNode, ActionType } from "../../types/orchestration";

const BADGE_STYLES: Record<ActionType, string> = {
  EVT:  "border-badge-evt  text-badge-evt  bg-badge-evt/20",
  SEQ:  "border-badge-seq  text-badge-seq  bg-badge-seq/20",
  PAR:  "border-badge-par  text-badge-par  bg-badge-par/20",
  INIT: "border-badge-init text-badge-init bg-badge-init/20",
  REF:  "border-badge-ref  text-badge-ref  bg-badge-ref/20",
  RMT:  "border-badge-rmt  text-badge-rmt  bg-badge-rmt/20",
  cmd:  "border-badge-cmd  text-badge-cmd  bg-badge-cmd/20",
};

export function TreeRowBadges({ node }: { node: PlanNode }) {
  return (
    <span className="flex shrink-0 items-center gap-1">
      <span
        className={`rounded-sm border px-1.5 py-0.5 font-mono text-[10px] uppercase ${BADGE_STYLES[node.actionType]}`}
      >
        {node.actionType}
      </span>
      {/* Cmd badge appears in addition to RMT for remote shell commands */}
      {node.actionType === "RMT" && node.leaf && (
        <span className={`rounded-sm border px-1.5 py-0.5 font-mono text-[10px] ${BADGE_STYLES.cmd}`}>
          cmd
        </span>
      )}
    </span>
  );
}
```

### 10.7 `src/views/WatchListView.tsx`

The view-mode toggle and the dual-rendering body.

```tsx
import { useState } from "react";
import { List, Network } from "lucide-react";
import { TreeView } from "../components/tree/TreeView";
import { SidePanel } from "../components/common/SidePanel";
import { useWatchListTree } from "../hooks/useWatchListTree";
import type { PlanNode } from "../types/orchestration";

type ViewMode = "list" | "tree";

export function WatchListView() {
  const [mode, setMode] = useState<ViewMode>(() =>
    (localStorage.getItem("watchlist.mode") as ViewMode) ?? "list"
  );
  const [selectedLeaf, setSelectedLeaf] = useState<PlanNode | null>(null);
  const { data: tree, isLoading } = useWatchListTree();

  const changeMode = (m: ViewMode) => {
    setMode(m);
    localStorage.setItem("watchlist.mode", m);
  };

  return (
    <div className="flex h-full">
      <div className="flex-1 overflow-auto p-4">
        <div className="mb-3 flex items-center justify-between">
          <h2 className="text-xs uppercase tracking-wider text-muted">WatchList</h2>
          <div className="flex rounded-md border border-border bg-bg-primary p-0.5">
            <button
              type="button"
              onClick={() => changeMode("list")}
              className={`flex items-center gap-1 rounded px-2 py-1 text-xs ${
                mode === "list" ? "bg-bg-secondary text-[var(--text-primary)]" : "text-muted"
              }`}
            >
              <List size={12} /> List
            </button>
            <button
              type="button"
              onClick={() => changeMode("tree")}
              className={`flex items-center gap-1 rounded px-2 py-1 text-xs ${
                mode === "tree" ? "bg-bg-secondary text-[var(--text-primary)]" : "text-muted"
              }`}
            >
              <Network size={12} /> Tree
            </button>
          </div>
        </div>

        <ActionBar />

        <div className="mt-4 rounded-lg border border-border bg-bg-primary">
          {isLoading && <div className="p-4 text-sm text-muted">Loading…</div>}
          {!isLoading && tree && (
            <>
              <div className="border-b border-border px-3 py-2 text-xs text-muted">
                {tree.fileName} — {tree.itemCount} items · {tree.testCount} tests · {tree.stepCount} steps
              </div>
              {mode === "list" ? <FlatList nodes={tree.items} /> : (
                <TreeView nodes={tree.items} onLeafClick={setSelectedLeaf} />
              )}
            </>
          )}
        </div>
      </div>

      <SidePanel
        open={selectedLeaf !== null}
        onClose={() => setSelectedLeaf(null)}
        title={selectedLeaf?.name ?? ""}
      >
        {selectedLeaf?.leaf && <LeafDetail leaf={selectedLeaf.leaf} />}
      </SidePanel>
    </div>
  );
}

// ... ActionBar, FlatList, LeafDetail are small local components.
```

### 10.8 Server-side: `WatchListController.cs` (new endpoint)

```csharp
[ApiController]
[Route("api/watchlist")]
public class WatchListController : ControllerBase
{
    private readonly IWatchListTreeService _treeService;

    public WatchListController(IWatchListTreeService treeService)
    {
        _treeService = treeService;
    }

    /// <summary>
    /// Returns the WatchList as a hierarchical tree of PlanNodes.
    /// Reads the same WatchList.xml the WPF dashboard reads.
    /// </summary>
    [HttpGet("tree")]
    public ActionResult<WatchListTreeRoot> GetTree()
    {
        var tree = _treeService.LoadTree();
        return Ok(tree);
    }
}
```

### 10.9 Server-side: `WatchListTreeService.cs` (XML → PlanNode[])

```csharp
public interface IWatchListTreeService
{
    WatchListTreeRoot LoadTree();
}

public class WatchListTreeService : IWatchListTreeService
{
    private readonly IConfiguration _config;

    public WatchListTreeService(IConfiguration config)
    {
        _config = config;
    }

    public WatchListTreeRoot LoadTree()
    {
        var path = _config["WatchList:Path"] ?? "WatchList.xml";
        var doc = XDocument.Load(path);

        var items = doc.Root!
            .Elements("WatchListItem")
            .Select(BuildNode)
            .ToList();

        return new WatchListTreeRoot
        {
            FileName  = Path.GetFileName(path),
            ItemCount = items.Count,
            TestCount = items.Count,
            StepCount = items.Sum(CountSteps),
            Items     = items,
        };
    }

    private PlanNode BuildNode(XElement el)
    {
        var type     = el.Attribute("type")?.Value ?? "SEQ";
        var name     = el.Attribute("name")?.Value ?? el.Name.LocalName;
        var children = el.Elements()
                         .Where(c => c.Name.LocalName != "Parameter")
                         .Select(BuildNode)
                         .ToList();

        var isLeaf = children.Count == 0
                     && (type == "cmd" || type == "RMT" || type == "INIT");

        return new PlanNode
        {
            Id          = el.Attribute("id")?.Value ?? Guid.NewGuid().ToString("N"),
            Name        = name,
            ActionType  = ParseType(type),
            IsLeaf      = isLeaf,
            ChildCount  = children.Count,
            ActionCount = isLeaf ? null : CountActions(children),
            Children    = isLeaf ? null : children,
            Leaf        = isLeaf ? BuildLeaf(el) : null,
        };
    }

    private int CountSteps(PlanNode n) =>
        n.IsLeaf ? 1 : (n.Children?.Sum(CountSteps) ?? 0);

    private int CountActions(IEnumerable<PlanNode> children) =>
        children.Sum(c => c.IsLeaf ? 1 : (c.ActionCount ?? 0));

    // … ParseType, BuildLeaf elided
}
```

---

## 11. File Generation Order

Generate in this order so each file's dependencies are already defined:

```
 1. package.json
 2. vite.config.ts
 3. tailwind.config.ts
 4. tsconfig.json
 5. src/styles/theme.css
 6. src/styles/globals.css
 7. src/types/orchestration.ts
 8. src/types/api.ts
 9. src/api/client.ts
10. src/api/sessions.ts
11. src/api/logs.ts
12. src/api/metrics.ts
13. src/api/watchlist.ts
14. src/signalr/ExecutionHubClient.ts
15. src/signalr/SignalRProvider.tsx
16. src/signalr/useSignalRConnection.ts
17. src/hooks/useConnectionStatus.ts
18. src/hooks/useSessions.ts
19. src/hooks/useSession.ts
20. src/hooks/useMetrics.ts
21. src/hooks/useLogs.ts
22. src/hooks/useWatchListTree.ts
23. src/components/common/Badge.tsx
24. src/components/common/MetricCard.tsx
25. src/components/common/SegmentedControl.tsx
26. src/components/common/Chip.tsx
27. src/components/common/StatusPill.tsx
28. src/components/common/ProgressBar.tsx
29. src/components/common/SidePanel.tsx
30. src/components/tree/TreeRowBadges.tsx
31. src/components/tree/TreeView.tsx
32. src/components/tree/TreeNode.tsx
33. src/components/pipeline/ActionChipRow.tsx
34. src/components/pipeline/AgentRow.tsx
35. src/components/pipeline/SessionCard.tsx
36. src/components/pipeline/PipelinePanel.tsx
37. src/components/timeline/GanttRow.tsx
38. src/components/timeline/GanttCard.tsx
39. src/components/timeline/TimelinePanel.tsx
40. src/components/logs/LogFilterBar.tsx
41. src/components/logs/VirtualizedLogList.tsx
42. src/components/logs/UnifiedLogPanel.tsx
43. src/components/layout/ConnectionIndicator.tsx
44. src/components/layout/TabStrip.tsx
45. src/components/layout/StatusFooter.tsx
46. src/components/layout/TopChrome.tsx
47. src/components/layout/Layout.tsx
48. src/views/WatchListView.tsx
49. src/views/DashboardView.tsx
50. src/views/AgentsView.tsx (existing — re-theme only)
51. src/views/ControllerView.tsx (existing — re-theme only)
52. src/App.tsx
53. src/main.tsx
54. index.html
```

Then the server-side files:

```
55. TestAgentSolution.Api/Services/IWatchListTreeService.cs
56. TestAgentSolution.Api/Services/WatchListTreeService.cs
57. TestAgentSolution.Api/Controllers/WatchListController.cs
58. TestAgentSolution.Api/Controllers/DashboardController.cs
59. TestAgentSolution.Api/Program.cs   (add UseStaticFiles + UseSpa)
```

---

## 12. API Endpoints Summary

| Method | Path | Returns | Used by |
|---|---|---|---|
| GET  | `/api/sessions` | `SessionItem[]` | useSessions (polling) |
| GET  | `/api/sessions/{id}` | `SessionItem` | useSession |
| POST | `/api/sessions/{id}/start` | `204` | TriggerButton |
| POST | `/api/sessions/{id}/cancel` | `204` | CancelButton |
| GET  | `/api/dashboard/metrics` | `DashboardMetrics` | useMetrics |
| GET  | `/api/logs?since={iso}&max={n}` | `LogEntry[]` (newest first) | useLogs |
| GET  | `/api/watchlist` | `WatchListItem[]` (flat — existing) | List mode |
| GET  | `/api/watchlist/tree` | `WatchListTreeRoot` | Tree mode |
| POST | `/api/watchlist/import` | `204` | Import button |
| GET  | `/api/watchlist/export` | XML stream | Export button |
| GET  | `/api/agents` | `AgentInfo[]` | AgentsView |
| GET  | `/api/health` | `200`/`503` | StatusFooter |

SignalR hub: `/hubs/execution` (reused from WPF).

---

## 13. Integration Steps

After Copilot generates the files:

1. **Install Node.js 20+** on the developer machine.
2. From the `TestAgentSolution.WebClient` folder: `npm install`, then `npm run dev`. The dev server starts on port 5173 with API requests proxied to `http://localhost:5000`.
3. **Add the static-file serving + SPA fallback to the API**:
   ```csharp
   // Program.cs
   app.UseStaticFiles();   // serves wwwroot/

   // SPA fallback: route any non-/api non-/hubs request to index.html
   app.MapFallbackToFile("index.html");
   ```
4. **Build for production**: `npm run build`. Output lands in `TestAgentSolution.Api/wwwroot/`. The API now serves the SPA at root.
5. **Configure CORS** (only if the WebClient runs on a different origin in dev — not needed when the API serves it):
   ```csharp
   builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
       p.WithOrigins("http://localhost:5173").AllowCredentials()
        .AllowAnyHeader().AllowAnyMethod()));
   ```
6. **Verify the SignalR connection** by opening the browser console — you should see `[SignalR] Connection started` and the top-chrome indicator should turn green.
7. **Test the polling fallback** by stopping the API momentarily — indicator should flip to yellow, REST polling continues against the closed port (failing), then to red after 5 seconds. Restarting the API should auto-recover to green within ~10 seconds.
8. **Test the tree view** with a representative `WatchList.xml`. If you have the file used in screenshot 2, drop it where `WatchListTreeService` reads from (configurable via `WatchList:Path`).

---

## 14. Implementation Rules — Read These Before Generating Code

1. **Every component is a function component**. No class components.
2. **TypeScript strict mode**: `"strict": true`, `"noUncheckedIndexedAccess": true` in tsconfig. No `any`; use `unknown` and narrow.
3. **Hooks are the only place async work happens**. Components do not call `fetch` directly; they call a hook.
4. **Cache merging is upsert-by-id**. Every SignalR handler must use `setQueryData` with the same merge pattern shown in `useSessions`.
5. **Connection state is the single source of truth** for SignalR vs polling. Components read `useConnectionStatus()`; they do not check `hub.isConnected` themselves.
6. **CSS through Tailwind + CSS variables**. No inline `style={{ color: ... }}` except for computed values (e.g., dynamic padding for tree depth).
7. **No localStorage writes outside of explicit user actions**. Don't persist transient UI state (expanded tree nodes) — keep it in component state.
8. **All async functions accept `AbortSignal`** where TanStack Query offers it, and propagate it to `fetch`.
9. **Action badges, status pills, and chips share their component implementations** — don't reimplement them per view.
10. **The Tree component is generic** — it works for any `PlanNode[]`. Don't tie it to WatchList specifically.
11. **The Pipeline / Timeline / UnifiedLog panels render even with empty data**. Show an empty state ("No active sessions"), never crash.
12. **Pulse animation on running chips** uses Tailwind's `animate-pulse` or a custom keyframe defined in `globals.css`. Stop animating when the status changes off `Running`.
13. **Virtualized list cap**: keep `useLogs` at 5000 entries client-side. Drop oldest as new arrive.
14. **Same dark theme everywhere**. Existing views (`ControllerView`, `AgentsView`) get re-themed using the same tokens; no light-mode toggle in v1.

---

## 15. Out of Scope for v1

These would be nice but are not required for this enhancement:

- Light theme
- Drag-and-drop reordering in the tree
- In-place editing of WatchList items (must go through Import/Export for now)
- User-saved tree-view filters
- Tree node search / find-in-tree
- Internationalization
- Mobile / tablet layouts (desktop browser only)
- Persistent UI state across browsers via the server
- The Agents view's deeper analytics (planned for a separate enhancement)

---

## 16. Generation Instruction (Copilot, read this last)

Generate **every file listed in Section 11, in that order**, with **complete, production-ready code**. No truncation, no placeholders, no `TODO` stubs. Every file must compile / lint as-is once `npm install` has run.

If you find any part of this spec ambiguous, make a reasonable choice and note it in a summary at the end. Do not ask clarifying questions mid-generation — generate the complete solution first.

Begin with file 1: `package.json`.
