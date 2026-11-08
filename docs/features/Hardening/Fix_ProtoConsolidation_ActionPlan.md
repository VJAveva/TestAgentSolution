# Action Plan: Consolidate the 4 Proto Copies into 1 Shared File

| Field | Value |
|---|---|
| **Goal** | Replace 4 byte-identical copies of `test_agent.proto` with ONE shared source, linked into the other projects |
| **Risk** | Near-zero — the copies are identical today, so generated code must not change |
| **Effort** | ~half a day including verification |
| **Rule** | The generated C# must be byte-identical before and after. If it isn't, stop and investigate. |

---

## Why this matters (plain language)

`test_agent.proto` is the contract between the controller and every agent. It
exists in **four separate copies** today. They're identical right now, so nothing
is broken. But the moment someone edits one copy and not the others, the two
sides disagree about the message format — and the symptom is a **runtime failure
where both components compiled perfectly**. That's the worst kind of bug: invisible
until an agent silently can't talk to the controller.

This change removes that landmine by making one file the single source of truth.
Because the copies are identical, done correctly this changes **nothing** at
runtime — it's pure risk removal.

---

## The four copies (confirmed)

```
TestControllerGrpc.Core/Protos/test_agent.proto   <-- KEEP THIS ONE (the shared kernel)
TestControllerGrpc/Protos/test_agent.proto        <-- replace with a link
TestAgentGrpc/Protos/test_agent.proto             <-- replace with a link
TestAgentDisplay/Protos/test_agent.proto          <-- replace with a link (see Q1 note)
```

`TestControllerGrpc.Core` is the right home: it's the shared kernel that
everything already references, and it depends on nothing itself.

---

## Instructions for Copilot

Do this as a careful, verified refactor. The success criterion is NOT "it
compiles" — it's "the generated protobuf C# is identical before and after."
Follow the steps in order. Do not skip the before/after comparison. Read the
actual `.csproj` files before editing.

---

## Step 0 — Capture the "before" state (do NOT skip)

Before changing anything, record what the build produces today, so you can prove
you didn't change it.

1. Confirm all four copies are actually identical:
   ```
   Get-FileHash TestControllerGrpc.Core/Protos/test_agent.proto,
                TestControllerGrpc/Protos/test_agent.proto,
                TestAgentGrpc/Protos/test_agent.proto,
                TestAgentDisplay/Protos/test_agent.proto -Algorithm SHA256
   ```
   All four hashes MUST match. If any differs, STOP — the copies have already
   drifted, and consolidating blindly would pick a "winner" and silently change a
   contract. Report the difference instead of proceeding.

2. Build the solution clean and locate the generated proto C# for each project.
   With `Grpc.Tools`, generated files land under each project's
   `obj/<Config>/<TargetFramework>/Protos/` (e.g. `TestAgent.cs`,
   `TestAgentGrpc.cs`). List them and record their hashes:
   ```
   dotnet build TestAgentSolution.sln -c Debug
   # then hash every generated *.cs under each obj/.../Protos/ folder
   ```
   Save these hashes. This is your "before" fingerprint. The whole point of the
   change is that these do not move.

3. Note in each `.csproj` HOW the proto is currently included — find the
   `<Protobuf ... />` item and its attributes (`GrpcServices`, `Access`, etc.).
   They may differ per project (e.g. client-only vs server). **These attributes
   must be preserved exactly** on each project's link — they control what code is
   generated. Record them per project before editing.

---

## Step 1 — Pick the canonical file

Keep `TestControllerGrpc.Core/Protos/test_agent.proto` as the single source of
truth. Do not move or rename it — moving it would churn the project that everything
depends on. The other three projects will point AT it.

---

## Step 2 — Relink the three consumers, one at a time

For EACH of the three consumer projects (`TestControllerGrpc`, `TestAgentGrpc`,
`TestAgentDisplay`), do this individually and rebuild after each — so if one
breaks you know exactly which:

1. Delete that project's local copy of `Protos/test_agent.proto`.
2. In that project's `.csproj`, change its `<Protobuf>` item to point at the
   shared file via a relative path, using `Link` so it still appears tidily under
   `Protos/` in the IDE. Preserve the EXACT attributes you recorded in Step 0.3
   for THAT project:
   ```xml
   <ItemGroup>
     <Protobuf Include="..\TestControllerGrpc.Core\Protos\test_agent.proto"
               Link="Protos\test_agent.proto"
               GrpcServices="..."   <!-- keep this project's original value -->
               Access="..." />       <!-- keep this project's original value -->
   </ItemGroup>
   ```
   The relative path depends on each project's location — verify it resolves.
3. Rebuild THAT project alone.
4. **Compare the generated proto C# to the "before" fingerprint for that project.**
   It must be identical. If it changed, the most likely cause is a mismatched
   `GrpcServices`/`Access` attribute — fix the attribute to match the original,
   don't accept the diff.

Do all three. Rebuild the full solution after the third.

---

## Step 3 — Prove nothing changed

This is the verification that makes the change trustworthy:

1. **Generated code identical:** re-hash every generated proto `.cs` under every
   `obj/.../Protos/`. Every hash must match the Step 0 "before" fingerprint. A
   match proves the wire contract is unchanged for all four projects.
2. **Full solution builds clean** — no new warnings about proto generation.
3. **Run the existing test suite** — all ~1,745 tests must still pass, especially
   anything touching gRPC / agent registration / command dispatch. Green here
   confirms controller↔agent serialization is unaffected.
4. **One live smoke test:** start the controller and one agent; confirm the agent
   registers, heartbeats succeed, and a trivial command round-trips. This is the
   real proof that the contract still works end to end.

If all four pass, the consolidation is safe and done.

---

## Step 4 — Prevent it from ever splitting again

Add a one-line guard so a future edit can't silently recreate the problem:

- Leave a short comment at the top of the canonical
  `TestControllerGrpc.Core/Protos/test_agent.proto`:
  ```
  // SINGLE SOURCE OF TRUTH for the controller<->agent contract.
  // Do NOT copy this file into other projects. The WPF host, agent, and
  // display projects link to THIS file via <Protobuf Include> in their .csproj.
  // Editing a copy elsewhere causes runtime serialization failures that compile
  // cleanly. If you need it elsewhere, add a <Protobuf> link, never a copy.
  ```
- Optional, higher-effort: a tiny CI check that fails the build if more than one
  `test_agent.proto` file exists in the repo. Only add this if CI edits are cheap.

---

## The Q1 caveat: TestAgentDisplay

`TestAgentDisplay` is flagged in the audit as possibly unused (published, but zero
project references). Two clean options:

- **Preferred:** relink it like the others (Step 2). It stays proto-compatible
  with one source of truth regardless of whether it's used. Lowest thinking cost.
- **If you already know it's dead:** don't bother relinking — it'll be deleted in
  the dead-code stage anyway. But until that decision is made, relinking is the
  safe default so it can't drift in the meantime.

Recommendation: **relink it now.** It's one more line and removes the question
from this change entirely.

---

## What "done" looks like

| Check | Pass condition |
|---|---|
| One proto file | Only `TestControllerGrpc.Core/Protos/test_agent.proto` exists |
| Three links | The other 3 `.csproj` files reference it via `<Protobuf Include ... Link>` |
| Generated code identical | Every generated `.cs` hash matches the Step 0 fingerprint |
| Solution builds | Clean, no new proto warnings |
| Tests green | All existing tests pass |
| Live smoke test | Agent registers + heartbeats + a command round-trips |
| Guard in place | Comment (and optional CI check) preventing re-copying |

---

## If something goes wrong

- **Generated code differs after relinking** → a `<Protobuf>` attribute
  (`GrpcServices`/`Access`) doesn't match the original for that project. Fix the
  attribute to match Step 0.3; don't accept the diff.
- **A project can't find the shared file** → the relative `Include` path is wrong
  for that project's folder depth. Correct the path; it's not a design problem.
- **Hashes differed in Step 0** (copies already drifted) → STOP. This is no longer
  a safe zero-risk change. Report which copy differs and how, and treat it as a
  real contract-reconciliation decision, not a mechanical consolidation.

---

## One-line summary

Keep the Core copy, point the other three projects' `.csproj` at it via
`<Protobuf Include ... Link>`, and prove the generated C# is byte-identical
before and after. Same runtime behaviour, one source of truth, landmine removed.
