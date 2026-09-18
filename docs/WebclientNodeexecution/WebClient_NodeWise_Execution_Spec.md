# Implementation Spec: WebClient Node-Wise Execution

| Field | Value |
|---|---|
| **Component** | TestController.WebClient (React) + TestController.WebApi + Api + Core |
| **Type** | New capability — run any node, not just the root |
| **Priority** | P1 (engineer debugging workflow) |
| **Mockup** | `WebClient_NodeWise_Execution_Mockup.html` |

---

## Instructions for Copilot

Add node-wise execution to the WebClient so an engineer can trigger any runnable
node in a pipeline — root, action group, single action, or template action — not
just the root. This matches what the WPF app already does. Read the actual WPF
node-run path first and REUSE its engine; do not build a parallel executor.

Replace every `PLUG IN` with the real names from the codebase. The scope is:
run the chosen node in isolation (a group runs its own actions; a single action
runs just itself), with the same RBAC as the root trigger.

---

## The gap (plain language)

Today the WebClient exposes only the root trigger. To test a one-line fix, an
engineer must re-run the entire pipeline (often 90+ minutes). The WPF app already
lets you run any node. This brings the WebClient to parity: a **Run** control on
every runnable node, so an engineer debugging runs just the node they're on.

## Confirmed behaviour

- **Runnable nodes:** root, action group (SEQ/PAR), single action, template action.
- **Scope:** run that node in isolation — a group runs its own child actions in
  order; a single action runs only itself. Nothing before or after it runs.
- **Optional Initialize:** because a node may need its parameters loaded, the run
  confirm offers "+ its Initialize" when the node sits under an Initialize.
- **RBAC:** identical to the root trigger — if the user is granted the pipeline
  (or is admin), they can run any node in it. No separate permission. A pipeline
  locked by another user still blocks (admin can override, as today).
- **Build:** uses the pipeline's effective build by default (shown in the confirm).
- **Live log:** a node-run streams into the Execution view exactly like a root
  run, just scoped to the node.

---

## Step 0 — Find the existing node-run path (do first, reuse it)

The WPF app already runs individual nodes. Locate and report:
1. How the WPF UI triggers a single node — the command and what it calls into.
   ===== PLUG IN: e.g. MainViewModel node-run + IActionPipelineExecutor. =====
2. The executor entry point that runs a subtree/single node rather than the whole
   pipeline. The pipeline executor walks Initialize/Ref/Group/Action — find the
   method that can execute starting at a given node.
   ===== PLUG IN: PipelineExecutorBase / ExecuteEventTrackedAsync and friends. =====
3. How a node is identified (an id/path within the WatchItem tree) so the API can
   name which node to run. ===== PLUG IN: the node id/path scheme. =====

**Reuse this engine.** The WebApi path must call the SAME executor the WPF app
uses, so behaviour is identical and there's one code path to maintain.

---

## Step 1 — Core: run a node by id

Ensure the executor can run a single node by id in isolation. If the WPF path
already does this, expose it; if it only runs whole events, add a scoped entry:

```csharp
// ===== PLUG IN: real types =====
Task<SessionResponse> ExecuteNodeAsync(
    string watchItemId,
    string nodeId,            // the group / action / template node to run
    NodeRunScope scope,       // OnlyThisNode | NodeWithInitialize
    string? buildOverride,    // null = pipeline effective build
    UserContext user,
    CancellationToken ct);

public enum NodeRunScope { OnlyThisNode, NodeWithInitialize }
```

- Resolve the node by id within the WatchItem tree.
- If it's a group → run its child actions per the group's SEQ/PAR semantics.
- If it's a single action/template → run just that.
- If scope = NodeWithInitialize → run the nearest owning Initialize first.
- Wrap in a session exactly like a root run, so progress/results/live log all work.
- Enforce the SAME locking as root: acquire the pipeline/agent locks; refuse if
  locked by another (admin override allowed).

---

## Step 2 — WebApi: the node-run endpoint

```
POST /api/execution/pipelines/{watchItemId}/nodes/{nodeId}/run
body: { scope: "OnlyThisNode" | "NodeWithInitialize", build?: string }
```

- **Authorize server-side, same as root:** admin OR pipeline granted → allowed;
  else 403. Never trust the client to gate this.
- Acquire the lock (or 409 if locked by another, unless admin override).
- Call `ExecuteNodeAsync`. Return the session id, same shape as the root trigger.
- The node-run session flows through the existing SignalR live-event channel so
  the Execution view streams it like any run.

===== PLUG IN: match the real route conventions + auth attributes. =====

---

## Step 3 — WebClient: a Run control on every runnable node

In the pipeline tree (WatchList view), add a **Run** affordance per node, matching
the mockup:

- **Root:** a always-visible primary "Run Pipeline" button (as today).
- **Group (SEQ/PAR):** a "Run group" button, revealed on row hover/focus.
- **Single action:** "Run action".
- **Template action:** "Run template".
- **Initialize / Ref-only structural nodes:** no standalone run (they're setup);
  they appear as the "+ Initialize" option on their siblings instead.
- A node already running shows a RUNNING badge instead of the button.

Keep it keyboard-accessible: the Run control is reachable by tab/enter on the
focused node, not hover-only. ===== PLUG IN: the real tree component + node model. =====

## Step 4 — WebClient: the run-scope confirm

Clicking Run opens a small confirm (mockup), NOT an immediate fire:

- Title = the node name + its type (single action / group / template).
- **"What to run"** radio: *Only this node* (default) vs *This node + its
  Initialize* (shown only when the node sits under an Initialize).
- **Build** shown (the pipeline's effective build) so there's no surprise; allow
  override if your build rules permit per-run builds.
- A note that it runs on the live agent(s) and that node-run is allowed because the
  pipeline is assigned.
- Confirm → POST to the node-run endpoint → switch to Execution view, which streams
  the scoped run live.

For a group, optionally list the child actions that will run (mockup shows this) so
the engineer sees exactly what they're about to trigger.

## Step 5 — Execution view: show it's a scoped run

- The session created by a node-run appears in the Execution sessions panel like
  any run, labelled so it's clear it's a scoped node-run (e.g. "Node: Copy
  Prepare-Agent.bat" rather than the whole pipeline name).
- Live log streams the scoped actions only. Everything else (progress, cancel,
  results) works as for a root run — because it IS the same engine.

---

## Acceptance criteria

| ID | Criterion |
|---|---|
| AC-1 | Every runnable node (root, group, single action, template) has a Run control in the WebClient tree |
| AC-2 | Running a group runs its child actions (SEQ/PAR) in isolation; nothing outside runs |
| AC-3 | Running a single action runs only that action |
| AC-4 | The "+ Initialize" option appears only when the node sits under an Initialize, and runs setup first when chosen |
| AC-5 | Node-run uses the SAME executor as the WPF app — one code path, identical behaviour |
| AC-6 | Server authorizes node-run with the SAME RBAC as root (granted pipeline or admin); 403 otherwise |
| AC-7 | Node-run respects locking (409 if locked by another; admin override works) |
| AC-8 | Node-run uses the pipeline's effective build by default, shown in the confirm |
| AC-9 | The scoped run streams into the Execution view live, like a root run |
| AC-10 | The Run control is keyboard-accessible, not hover-only |
| AC-11 | Existing root-trigger behaviour is unchanged; all existing tests pass |

---

## Build order

1. Step 0 — find and confirm the WPF node-run engine; report it. Do NOT build a
   new executor.
2. Step 1 — Core `ExecuteNodeAsync` (reusing that engine), with locking + session.
3. Step 2 — WebApi endpoint with server-side RBAC + lock check.
4. Step 3 — tree Run controls per node type.
5. Step 4 — the run-scope confirm.
6. Step 5 — Execution view labels the scoped run.
7. Verify every AC; run the full existing suite.

Start with Step 0: show me how the WPF app runs a single node today and the
executor method it calls, so the WebApi reuses the exact same path.

---

## Why this is the right path

- **Reuse the WPF engine, don't rebuild it.** The WPF app already runs nodes; the
  only thing missing is a WebApi door into that same engine plus the React UI. One
  code path means the web and desktop behave identically and there's nothing to
  drift.
- **Same RBAC, enforced server-side.** No new permission model — granted the
  pipeline = run any node. The server decides; the client only shows/hides.
- **Isolation keeps it safe.** A node runs just itself (or its group), so an
  engineer debugging can't accidentally kick off the whole 90-minute pipeline.
- **The confirm prevents mistakes** — it names the node, the scope, the build, and
  the live agent before anything fires.
