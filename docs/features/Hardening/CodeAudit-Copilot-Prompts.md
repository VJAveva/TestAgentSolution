# TestAgentSolution — Code Audit & Hardening Prompt Pack

Prompts for GitHub Copilot Chat. Run them **one file (or one folder) at a time**, not on the whole solution.
Copilot degrades badly when given too much scope — it starts inventing rewrites instead of finding real problems.

**Order:** Pass 1 → 2 → 3 → 4 → 5. Fix and commit between passes. Never run two passes in one prompt.

---

## 0. The one-prompt version

Use this when you just want a fast read on a single file.

```
You are reviewing production C# in a distributed test-orchestration system
(.NET 10, WPF + ASP.NET Core sharing a Core library, gRPC, EF Core/SQLite).

Review #file: as a senior architect. Do NOT rewrite it. Report only.

Find, in this order:
1. Secrets, credentials, connection strings, or tokens in source.
2. Correctness bugs: async void, .Result/.Wait(), unobserved tasks,
   missing CancellationToken, undisposed IDisposable, thread-affinity violations.
3. Architecture smells: host-specific types leaking into shared code,
   wrong DI lifetimes, hidden static state.
4. Performance: N+1 queries, work on the UI thread, per-call object
   construction that should be cached, unbounded collections, sync-over-async.
5. Missing hardening: no timeout, no retry, no input validation, swallowed exceptions.

Output a markdown table: | Severity | Line | Issue | Why it bites in production | Minimal fix |
Severity = Critical / High / Medium / Low.
Order rows by severity. Max 15 rows — give me the ones that matter.
If you are unsure whether something is a bug, say "UNVERIFIED" in the Issue column
instead of guessing.
```

---

## Pass 1 — Sanitize (secrets & leakage)

**Run this first, on the whole repo, before anything else.**

### Prompt 1.1 — Secret sweep

```
Scan #codebase for hardcoded secrets. Look for:
- Azure DevOps PATs, API keys, bearer tokens
- Passwords in PowerShell (.ps1), batch (.bat), XML, JSON, .config
- Domain/service account credentials
- Connection strings with embedded passwords
- Base64 blobs that decode to credentials

For each hit report: file path, line number, what kind of secret, and whether
it is still likely live.

Then produce a table mapping each secret to its replacement strategy:
Windows Credential Manager | environment variable | ADO variable group | user secrets.

Do not print the secret values back to me in full — mask the middle.
```

> **Known before you start:** live ADO PAT in plaintext in `GetBuildChanges.ps1`,
> `GetBuildChanges_OMI.ps1`, `test1.ps1`. Also `_VCloudPassword` appears both in
> the parameter files and hardcoded inside WatchList template commands.
> **Revoke the PAT first, then audit.** A rotated secret in git history is far
> cheaper than a live one.

### Prompt 1.2 — Log leakage

```
Review #folder: for logging and exception handling that leaks sensitive data.
Flag any log statement, exception message, or gRPC status detail that could emit:
credentials, full connection strings, machine account names, internal file paths,
or raw request payloads.

Also flag any catch block that logs and continues without a comment explaining
why swallowing is correct.

Report as a table. Suggest structured-logging replacements using named
placeholders, not string interpolation.
```

### Prompt 1.3 — Config hygiene

```
Review all appsettings*.json, *.config, and launchSettings.json in #codebase.
Flag: secrets committed, environment-specific values baked into the default file,
missing values that will throw at startup instead of failing fast with a clear message.

Propose an IOptions<T> + validation-on-startup pattern for each config section
that currently reads raw IConfiguration keys inline.
```

---

## Pass 2 — Pitfalls (correctness)

This is where most of the real bugs are. Run per-project.

### Prompt 2.1 — Async & threading

```
Audit #folder: for async/concurrency defects. Specifically:

- async void methods that are not event handlers
- .Result, .Wait(), .GetAwaiter().GetResult() — sync-over-async deadlock risk
- Tasks started and never awaited or observed (fire-and-forget without a
  continuation that logs faults)
- Missing CancellationToken parameters, or tokens accepted but never passed down
- ConfigureAwait usage that is wrong for the host (WPF has a sync context,
  ASP.NET Core does not — flag code in the SHARED Core library that assumes either)
- Shared mutable state accessed from multiple threads without a lock,
  Interlocked, or a concurrent collection
- CancellationTokenSource instances never disposed
- Timers / EventLogWatcher / file watchers with no unsubscribe path

For each: line number, the failure mode it produces at runtime, and the minimal fix.
Rank by how likely it is to appear only under load.
```

### Prompt 2.2 — WPF-specific

```
Review #folder: (WPF project) for UI-layer defects:

- Work on the Dispatcher thread that should be on a background thread
- Dispatcher.Invoke where InvokeAsync/BeginInvoke is correct (deadlock risk)
- INotifyPropertyChanged / event subscriptions that create memory leaks
  (long-lived publisher, short-lived subscriber, no unsubscribe)
- ObservableCollection mutated from a non-UI thread
- Commands with no CanExecute guard that allow double-invocation
- ViewModels holding IDisposable services without implementing IDisposable

Report as a table with severity and the smallest safe change.
```

### Prompt 2.3 — gRPC & network resilience

```
Review #folder: for gRPC client/server defects:

- GrpcChannel created per call instead of cached/reused
- Missing deadlines on unary calls (a hung agent will hang the controller forever)
- Streaming calls without backpressure handling or a cancellation path
- No retry/transient-fault policy on calls that cross the network
- Exceptions from RpcException not mapped to a domain-level result
- Reconnect logic that busy-loops instead of backing off
- Assumption that a disconnect is always an error

Note: agent disconnect DURING a VM revert is expected and must be suppressed,
not surfaced as a fault. Flag anywhere that assumption is missing.

Report table: line, issue, production symptom, fix.
```

### Prompt 2.4 — SQLite / EF Core

```
Review #folder: for data-access defects against SQLite via EF Core:

- N+1 query patterns (lazy loading inside a loop)
- Missing AsNoTracking() on read-only queries
- DbContext captured in a singleton, or shared across threads
- Long-running write transactions that will block other writers
  (SQLite allows one writer at a time)
- Missing busy_timeout / WAL configuration
- Queries with no index backing the WHERE or ORDER BY
- Client-side evaluation of what should be a server-side filter
- Bulk inserts done row-by-row with SaveChanges inside the loop

For each, give the fix AND the index or PRAGMA it needs, if any.
```

---

## Pass 3 — Architecture boundary check

The keystone is **two front doors, one engine**. This pass verifies it is still true.

### Prompt 3.1 — Leakage across the seam

```
The architecture rule for this solution: TestControllerGrpc.Core is the shared
engine. TestControllerGrpc (WPF) and TestController.WebApi are two independent
hosts. NO host-specific type may appear in Core. Notification is host-local.

Audit TestControllerGrpc.Core for violations:
- References to WPF types (Dispatcher, ObservableCollection, ICommand,
  System.Windows.*)
- References to ASP.NET Core types (HttpContext, IHttpContextAccessor,
  ControllerBase, SignalR hub types)
- Direct use of a concrete notification/UI mechanism instead of an abstraction
- Static/singleton state that assumes a single host process
- Anything that reads configuration from a host-specific source

For each violation: what it is, which host it couples Core to, and the interface
that should replace it. List the interface + the two host-side implementations.
```

### Prompt 3.2 — DI lifetime audit

```
Review the DI registrations in both hosts (WPF composition root and WebApi
Program.cs / ServiceCollection extensions).

Find:
- Captive dependencies (a singleton holding a scoped or transient service)
- Services registered with different lifetimes in the two hosts where they
  should match
- DbContext registered as singleton
- Services that hold state but are registered transient (state silently lost)
- Missing registrations that will throw only at runtime on a specific code path

Output a table: Service | WPF lifetime | WebApi lifetime | Correct? | Why.
Flag every row where the two hosts disagree and explain whether the difference
is intentional (host-local by design) or a bug.
```

### Prompt 3.3 — Failure modes & state machines

```
Review #file: which implements a multi-phase operation state machine.

For each state transition ask:
1. What happens if the process crashes here? Is the state recoverable on restart,
   or is a node left stranded in an intermediate state?
2. Is there a timeout on this phase, or can it hang forever?
3. Is the transition idempotent if retried?
4. Is there a cancellation path, and does it leave consistent state?

Produce a table: Phase | Crash consequence | Timeout? | Idempotent? | Gap.
Then list the top 3 gaps by blast radius.
```

---

## Pass 4 — Performance & workflow

### Prompt 4.1 — Hot path profiling targets

```
Review #folder: and identify the code paths most likely to be performance
bottlenecks under a fleet of 10+ agents reporting concurrently.

For each candidate:
- What makes it expensive (allocation, I/O, lock contention, serialization)
- Whether the cost is per-request, per-agent, or per-item
- Big-O behaviour as the fleet grows
- Whether it blocks a thread or is truly async

Rank by expected impact. Do NOT optimize yet — I want the target list first.
Explicitly mark anything you cannot assess without a profiler as
"NEEDS MEASUREMENT".
```

### Prompt 4.2 — Allocation & collection review

```
Review #folder: for avoidable allocation and collection misuse:

- String concatenation in loops (should be StringBuilder or string.Create)
- LINQ chains enumerated multiple times (add ToList/ToArray once, or restructure)
- .Count() on an IEnumerable that is already a List/Array
- Repeated .ToList() materializations of the same query
- List<T> used where a Dictionary/HashSet lookup is needed (O(n) inside O(n))
- Unbounded collections that grow for the lifetime of the process
  (caches, event buffers, log accumulators) with no eviction
- Large objects allocated per-request that could be pooled or cached
- Regex compiled per call instead of static readonly / GeneratedRegex

Table: line | issue | measured-or-estimated cost | fix.
```

### Prompt 4.3 — Workflow-level optimization

```
Here is a workflow in this system: [DESCRIBE THE WORKFLOW — e.g. "operator clicks
Revert on a node; controller dispatches to agent; agent reverts VM snapshot;
controller polls readiness; node returns to Available"].

Trace the code path end to end across #codebase. Then:

1. Draw the sequence as a numbered list with the component owning each step.
2. Identify every point where the workflow waits, and what it is waiting on.
3. Identify steps that are serial but could be parallel.
4. Identify redundant work — anything computed, fetched, or serialized more
   than once in a single pass.
5. Identify chatty communication — many small calls where one batched call works.

Output: the trace, then a table of optimization opportunities ranked by
(time saved) / (risk of breaking it).
```

---

## Pass 5 — Harden

### Prompt 5.1 — Input & boundary validation

```
Review #folder: for missing validation at trust boundaries.
Trust boundaries here: gRPC service methods, WebApi controller actions,
XML/JSON config file parsing, file paths from user or config input,
and any value that reaches a shell/PowerShell invocation.

Flag:
- Parameters used without null/range/format checks
- Path values used without canonicalization (path traversal)
- Values interpolated into a command line or SQL without escaping
- Deserialization of untrusted input into permissive types
- Missing size limits on incoming payloads or collections

For each: the attack or failure it enables, and the guard clause to add.
Prefer failing fast with a clear exception over defensive silent defaults.
```

### Prompt 5.2 — Observability

```
Review #folder: and tell me what I would be unable to diagnose at 2am
from logs alone.

Specifically identify:
- Operations with no start/end log line
- Failure paths that log nothing
- Log statements with no correlation ID tying them to an operation or node
- Exceptions caught and rethrown, losing the stack trace (throw ex;)
- Missing structured properties (node name, build number, operation ID)
  that I would need to filter on

Propose the minimum set of log statements + structured properties to add,
using ILogger<T> with named placeholders. Do not add logging noise —
only what closes a real diagnostic gap.
```

### Prompt 5.3 — Test seams

```
Review #file: and tell me what makes it hard to unit test.

Look for: static calls to DateTime.Now / File.* / Process.Start / Environment.*,
new-ing dependencies inside methods, sealed types with no interface,
private methods holding the real logic, constructors doing work.

For each: the seam to introduce (interface, injected clock, factory) and the
smallest refactor that enables a test without changing behaviour.
Then write ONE example unit test for the highest-value case.
```

---

## Rules to give Copilot up front

Paste this once at the start of a session — it dramatically improves output quality:

```
Ground rules for this session:

1. You are auditing, not rewriting. Report findings before proposing code.
2. If you propose a change, give the MINIMAL diff, not a rewritten file.
3. If you are inferring behaviour you cannot see in the provided context,
   label it "ASSUMPTION:" explicitly.
4. Never invent an API. If you are unsure a method exists in .NET 10, say so.
5. Do not flag style preferences. I want defects, not opinions about var vs
   explicit types.
6. Rank everything by production impact, not by how easy it is to fix.
7. When in doubt about severity, ask me one clarifying question instead of
   guessing.
```

---

## Suggested run order

| Order | Pass | Scope | Why here |
|---|---|---|---|
| 1 | 1.1 Secret sweep | whole repo | Revoke first; everything else can wait |
| 2 | 2.1 Async/threading | Core, then each host | Highest bug density, cheapest fixes |
| 3 | 3.1 Boundary check | Core only | Confirms the keystone still holds before you build on it |
| 4 | 3.2 DI lifetimes | both hosts | Catches the bugs that only appear in one host |
| 5 | 2.3 / 2.4 | gRPC + data layer | The two places load actually hurts |
| 6 | 4.1 → 4.3 | targeted | Only after correctness is settled |
| 7 | 5.x | targeted | Hardening on a codebase you now trust |

Do not skip to Pass 4. Optimizing code that has an async deadlock in it just
makes the deadlock arrive faster.
