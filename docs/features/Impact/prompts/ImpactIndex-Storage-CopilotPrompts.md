# Impact Index Storage Relocation — Copilot Prompt Pack

## Problem context (paste this first, before Prompt 1)

> Context for the changes that follow.
>
> The Code Churn / Impact Mapping engine in `TestControllerGrpc.Core` builds a SQLite
> index (BM25 + dense vectors) currently written to `TestController.WebApi/impact-index.db`.
> That path is inside the source tree, so the file got staged and committed. It reached
> 1.29 GB and GitHub's pre-receive hook rejected the push (100 MB per-file hard limit).
> Git LFS is not an acceptable fix: the free tier is 1 GB, and the file is a regenerable
> build artifact, not source.
>
> The index must move to a persistent location outside the repository. Two failure modes
> must both be prevented:
>
> 1. The index is committed to git again. (Caused the current breakage.)
> 2. The index is silently dropped from deliverables, so a fresh deploy starts with no
>    index and no clear signal that one must be built. This is the more dangerous
>    failure — it is invisible until someone runs an impact query and gets empty results.
>
> The solution is: the index file is never in git and never in the publish payload, but
> its **location is configured**, its **directory is provisioned**, its **absence is
> detected and reported explicitly**, and there is a **documented, scriptable rebuild path**.
>
> Architectural constraint: TestAgentSolution has two front doors (WPF `TestControllerGrpc`
> and `TestController.WebApi`) over one shared engine (`TestControllerGrpc.Core`). Path
> resolution must live in Core and behave identically for both hosts. No host-specific
> types cross into Core.
>
> Target framework is .NET 10. PowerShell scripts must be PowerShell 5.1 compatible and
> ASCII-only in console output. JSON/config files are UTF-8 with no BOM.

---

## Phase 1 — Get the repository clean

**Prompt 1**
```
Show me a PowerShell 5.1 command sequence that reports every commit in this repository
that touches a path matching *impact-index.db*, including the blob size at each commit.
I need to confirm the file exists only in unpushed local commits before I rewrite them.
Output must be ASCII-only.
```

**Prompt 2**
```
Add these entries to the repository root .gitignore, grouped under a comment explaining
that the impact index is a regenerable artifact and must never be committed:

impact-index.db
impact-index.db-wal
impact-index.db-shm

Explain why the -wal and -shm sidecar files must also be ignored given SQLite's
write-ahead logging mode.
```

**Prompt 3**
```
Search the entire solution for any remaining hardcoded reference to "impact-index.db"
or to a path under TestController.WebApi that resolves to the index. Include .csproj
files, appsettings*.json, launchSettings.json, PowerShell scripts, and batch files.
List each hit with file path and line number. Do not change anything yet.
```

---

## Phase 2 — Core: path resolution

**Prompt 4**
```
In TestControllerGrpc.Core, create an interface IImpactIndexPathProvider with:

  string IndexRoot { get; }
  string IndexFilePath { get; }
  bool IndexExists { get; }
  void EnsureIndexRootExists();

This is the single source of truth for where the impact index lives. Both the WPF host
and the WebApi host will consume it through DI. It must not reference any WPF or
ASP.NET Core type.
```

**Prompt 5**
```
Implement ImpactIndexPathProvider in TestControllerGrpc.Core with this resolution order,
first match wins:

  1. Environment variable IMPACT_INDEX_ROOT
  2. Configuration key ImpactMapping:IndexRoot (via IConfiguration)
  3. Default: Path.Combine(Environment.GetFolderPath(
       Environment.SpecialFolder.CommonApplicationData), "TestAgentSolution", "ImpactIndex")

The index file name is a constant, "impact-index.db". EnsureIndexRootExists must be
idempotent and must not throw if the directory already exists. Log which of the three
sources supplied the root at Information level on first resolution, and cache the
resolved value so the log line appears once per process.
```

**Prompt 6**
```
Add an ImpactMappingOptions class in Core bound to the ImpactMapping configuration
section, with an IndexRoot property (nullable string) and an XML doc comment stating
that a null value means "use the ProgramData default". Register it with the options
pattern. Show the appsettings.json snippet for both hosts, written as UTF-8 without BOM,
with IndexRoot left null and a comment-adjacent property explaining the override.
```

---

## Phase 3 — Absence must be loud, not silent

This is the part that prevents the index quietly vanishing from deliverables.

**Prompt 7**
```
Add an enum ImpactIndexStatus in Core with members: Ready, Missing, Empty, Corrupt,
Stale. Add a record ImpactIndexHealth carrying Status, IndexFilePath, SizeBytes,
DocumentCount, LastBuiltUtc, and a human-readable Message.
```

**Prompt 8**
```
Add IImpactIndexHealthCheck and its implementation in Core. It must:

  - Return Missing when the file does not exist at IImpactIndexPathProvider.IndexFilePath
  - Return Empty when the file exists but the document count is zero
  - Return Corrupt when opening the SQLite connection or running PRAGMA integrity_check fails
  - Return Stale when LastBuiltUtc is older than a configurable threshold (default 14 days)
  - Return Ready otherwise

Every non-Ready Message must state the resolved index path, the reason, and the exact
command to rebuild. Never return Ready as a fallback on an unexpected exception.
```

**Prompt 9**
```
Wire the health check into startup for both hosts so a non-Ready status is surfaced
immediately rather than at first query:

  - TestController.WebApi: register as an ASP.NET Core IHealthCheck at /health/impact-index,
    and log a Warning at startup if not Ready. Do not fail startup.
  - TestControllerGrpc (WPF): evaluate on shell load and show a non-blocking banner in the
    Code Churn surface with the status Message and a Rebuild Index action.

Keep the notification host-local per the existing architecture. Core exposes the health
result only; it does not raise UI notifications.
```

**Prompt 10**
```
Update every Core call site that opens the impact index so it consults
IImpactIndexHealthCheck first and throws ImpactIndexUnavailableException (new, in Core)
with the health Message when status is Missing, Empty, or Corrupt. An impact query must
never return an empty result set that is indistinguishable from "no impacted tests found".
Given the recall-bias principle for test selection, an unavailable index must fail loudly,
not degrade to selecting nothing.
```

---

## Phase 4 — Keep it out of the publish payload, on purpose

**Prompt 11**
```
In TestController.WebApi.csproj, add an explicit ItemGroup that removes impact-index.db
and its -wal/-shm sidecars from Content, None, and the publish output. Add an XML comment
above it explaining that the index is resolved at runtime via IImpactIndexPathProvider and
must not ship in the package. The exclusion should be deliberate and readable, so that
someone reviewing the csproj later understands the omission is intentional.
```

**Prompt 12**
```
Add an MSBuild target that runs after Build and emits a warning if a file named
impact-index.db is found anywhere under the solution directory tree, excluding bin and obj.
Warning code IMPACT001, message pointing at the .gitignore entry. This catches the case
where someone reintroduces the old path.
```

---

## Phase 5 — Provisioning and rebuild

**Prompt 13**
```
Update Prepare-ControllerNode.ps1 to create the impact index root directory. Requirements:

  - Default path C:\ProgramData\TestAgentSolution\ImpactIndex
  - Idempotent: re-running heals, does not duplicate or error
  - Grant modify permission to the wwAPPS service account on the directory
  - ASCII-only console output, PowerShell 5.1 compatible
  - Print the resolved path and whether it was created or already present

The controller VM is subject to snapshot reverts, so this must run as part of standard
provisioning rather than being applied manually.
```

**Prompt 14**
```
Create Rebuild-ImpactIndex.ps1 in the scripts folder. It must:

  - Accept an optional -IndexRoot parameter, defaulting to the same ProgramData path
  - Accept a -Force switch that deletes an existing index before rebuilding
  - Invoke the Core index build entry point
  - Report elapsed time, final file size, and indexed document count on completion
  - Exit non-zero on failure
  - ASCII-only output, PowerShell 5.1 compatible

This script is the answer that every non-Ready health Message points to, so its name and
invocation must match exactly what the health check text tells the operator to run.
```

**Prompt 15**
```
Add a section to FEATURE-ARCHITECTURE.md titled "Impact Index Storage" covering:

  - Why the index is not in source control (size, regenerable, GitHub 100 MB limit)
  - The three-tier path resolution order
  - The default ProgramData location and how to override it per machine
  - That the index does not survive a controller VM snapshot revert, and that
    Rebuild-ImpactIndex.ps1 must be run after a revert
  - The health check endpoint and what each ImpactIndexStatus value means
```

---

## Phase 6 — Optional: shared persistence

Only if a rebuild is expensive enough to justify the transport cost.

**Prompt 16**
```
Propose a design for optionally seeding the impact index from a network share rather than
rebuilding from scratch after a controller revert. Cover: a configurable
ImpactMapping:SeedSourcePath, integrity verification before the copy is trusted, staleness
comparison against the current build, and fallback to a full rebuild when the seed is
missing or fails verification. Do not implement yet — I want to see the trade-offs against
just running Rebuild-ImpactIndex.ps1 first.
```

---

## Verification checklist

Run after Phase 5, before considering this done.

- [ ] `git log --all -- "*impact-index.db"` returns nothing after the history rewrite
- [ ] Fresh clone plus build produces no `impact-index.db` anywhere in the tree
- [ ] `dotnet publish` output contains no `impact-index.db` or sidecar files
- [ ] Deleting the index and starting WebApi yields a Warning log and a Missing status at `/health/impact-index`
- [ ] Deleting the index and starting the WPF host shows the banner with a working Rebuild action
- [ ] An impact query against a missing index throws `ImpactIndexUnavailableException` rather than returning zero results
- [ ] `IMPACT_INDEX_ROOT` set to a temp path is honoured by both hosts, confirmed via the startup log line
- [ ] `Prepare-ControllerNode.ps1` run twice in a row succeeds both times
- [ ] `Rebuild-ImpactIndex.ps1` reproduces a working index from empty, and the health check flips to Ready
- [ ] Reintroducing a file at the old path triggers the IMPACT001 build warning
