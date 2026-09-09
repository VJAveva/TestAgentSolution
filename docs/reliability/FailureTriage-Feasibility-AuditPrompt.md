# Failure Triage ("Why did this fail?") — Feasibility Audit Prompt

**Purpose:** find out whether the blame-card feature can be built on what already exists, *before* writing a single implementation prompt.

**How to run it:**
1. Open the TestAgentSolution repo in VS Code / Visual Studio.
2. New Copilot Chat session (fresh — no prior context).
3. Paste everything below the scissors line as **one message**.
4. Expect a report, not code. If Copilot writes code, stop and re-paste the ground rules.

---

✂ — — — — — — — — — — COPY FROM HERE — — — — — — — — — —

## GROUND RULES — read these first, they override anything else

You are performing a **read-only feasibility audit**. You are not building anything.

1. **Do not write, modify, refactor, or generate any code.** No suggestions, no "here's how you'd do it", no sample classes. This pass produces a report only.
2. **Every claim must carry evidence**: `path/to/File.cs:120-145`. A claim with no file reference is not allowed.
3. **If you cannot find something, say so explicitly**: `NOT FOUND — searched for: <terms you actually searched>`. Never assume something exists because a well-designed system would have it. Never infer a class exists from its name being mentioned in a comment or doc.
4. **Report what the repository contains, not what it ought to contain.** No architecture opinions in this pass.
5. If a file is too large to read fully, state which regions you read and which you skipped.
6. Distinguish clearly between: *code that exists and runs*, *code that exists but is unreferenced/dead*, and *something described only in a markdown doc or comment*.

## CONTEXT — the feature being assessed

I want to add a **Failure Triage** capability to the controller: when a test fails, show the top 3 most likely culprit code changes, with a confidence score, plus Confirmed/Wrong feedback buttons.

The intended approach is to **reuse the existing Code Churn & Impact Mapping engine in reverse** — instead of "change → which tests to run", it becomes "failure → which change caused it".

Your job is to tell me what of this already exists, what is partially there, and what is missing entirely.

## PART 1 — Capability inventory

Answer each numbered question with: **verdict + evidence + one-line explanation.**
Verdicts: `EXISTS` / `PARTIAL` / `MISSING`.

### Group A — Failure signal (the input)

A1. When a test fails on an agent, is the failure captured as **structured data** (test name, assert message, stack trace) or only as raw stdout text? Where exactly is it parsed?
A2. Is there a **result model / DTO** for a single test result? Name it and show its fields.
A3. Are run results **persisted** (SQLite/EF Core/files), or do they only live in UI state and vanish on restart? Show the DbContext or writer.
A4. Is there a **run identifier** that ties a set of results to one execution across all agents?
A5. Does the system record **which build / product binaries** were under test in a given run? (Look at build number handling, install steps, dependent-binary copy steps.)

*Search hints:* `TestResult`, `.trx`, `OutputReceived`, `NodeProgress`, `ActionPipelineExecutor`, `DbContext`, `Run-WASTests`, `Install-Build`, `Copy-DependentBinaries`.

### Group B — Change data (the suspect list)

B1. Is there existing code that fetches **commits/changesets for a build** from Azure DevOps? PowerShell, C#, or both? Show the entry point.
B2. What is its **output shape** — file paths only, or file paths + author + commit id + timestamp?
B3. Is that output **consumed by C# anywhere**, or is it a standalone script a human runs?
B4. Is the build → commit mapping **stored anywhere**, or recomputed each time?

*Search hints:* `GetBuildChanges`, `GetBuildChanges_OMI`, ADO REST calls, `_apis/build`, `changes`.

### Group C — Impact engine reuse (the core question)

C1. Is the retrieval cascade exposed behind an **interface in TestControllerGrpc.Core**, or is it embedded inside a command handler / UI path? Name the interface and its methods.
C2. Can retrieval be invoked with an **arbitrary free-text query**, or does its public entry point require a changeset/diff as input? This is the single most important answer in the audit — be precise.
C3. What is the **schema of `impact-index.db`**? List tables and columns. Does it store enough to score a text query against code units?
C4. Is the **reranker** a separately callable component with a stable input contract, or is it inlined in the pipeline?
C5. What is the **schema of `impact-outcomes.db`**? Does the outcome record assume a test-selection shape, or is it general enough to hold a new outcome type (triage verdict)?
C6. How is the **LLM reranker actually called** — endpoint, auth, timeout, retry? Is it reachable from the controller VM unattended (e.g. an overnight run at 3am with no user logged in)?

*Search hints:* `impact-index`, `impact-outcomes`, `BM25`, `Rerank`, `HyDE`, `RRF`, `MMR`, `IImpact*`, `Embedding`.

### Group D — Host wiring (where it would live)

D1. Show the **DI registration blocks** for both hosts (WPF and WebApi) where a new Core service would be registered.
D2. What is the existing pattern for adding a **REST endpoint + SignalR event** pair? Point at one concrete example end-to-end.
D3. In WPF, where is the **failed-test row rendered**, and is there an existing details/properties pane that could host a card without new window plumbing?
D4. Does the WebClient have an equivalent detail surface?

### Group E — Constraints and risks

E1. Any **SQLite/SMB locking** handling already in place for the shared stores? What is it?
E2. Is there existing **credential handling** the triage service could reuse for ADO calls, or is it currently hardcoded/plaintext? State what you find, without reproducing any secret value in your report.
E3. What is the typical **size of a run's output** (rough order of magnitude, from any logs or code limits you can see)? Relevant to fingerprint extraction cost.

## PART 2 — Summary table

Produce exactly this table, one row per question above:

| ID | Capability | Verdict | Evidence (file:lines) | Est. effort to close gap |
|----|-----------|---------|----------------------|--------------------------|

Effort scale: `NONE` (already there) / `S` (< 1 day) / `M` (1–3 days) / `L` (> 3 days) / `UNKNOWN`.

## PART 3 — The go/no-go answer

Answer this one question directly, in plain language, in under 150 words:

> **For a single failed test, can I today obtain (a) a stable text fingerprint of the failure and (b) the list of code changes in that build — using existing code, without building new infrastructure?**

Then list:
- **The 3 biggest unknowns** that this audit could not resolve from source alone.
- **The single cheapest spike** (half a day or less) that would resolve the most important unknown.
- **Any assumption I appear to be making that the code contradicts.** Be blunt here; I would rather find out now.

✂ — — — — — — — — — — COPY TO HERE — — — — — — — — — —

---

## Reading the result

The audit passes if **C2 comes back `EXISTS` or `PARTIAL` with a small effort**. If retrieval can only be entered with a changeset as input, the reverse-direction idea needs a new entry point in Core first — still feasible, but that becomes Phase 0 of the build pack rather than a free ride on existing code.

If **A1 or A3 comes back `MISSING`**, stop and fix the failure-capture path first. No amount of ranking intelligence helps if the failure itself is only an unparsed stdout line that isn't kept anywhere.
