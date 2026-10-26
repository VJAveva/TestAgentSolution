---
name: flaky-test-check
description: 'Decide whether a failing test is flaky, a regression, or simply broken by running the suite N times (default 10) on HEAD and on the previous commit and comparing per-test failure rates. Use for flaky test, intermittent failure, passes in isolation, is this my change, race in tests.'
argument-hint: 'Test project, optional test filter, optional N and base commit'
---

# Flaky Test Check

Don't call a test "flaky" from a single re-run. Measure the failure rate on **HEAD** and on the **previous commit** (or a named base), then compare.

## Setup
1. `git status --porcelain` must be clean (otherwise HEAD isn't what you're measuring). Commit or stash first, with the user's OK.
2. **Base commit**: default `HEAD~1`; the user may name another. Check out the base in a **worktree** so HEAD's bin/obj stay intact:
   ```powershell
   git worktree add "$env:TEMP\flaky-base" <base-sha>
   ```
   Needs about 10 GB free. Check `(Get-PSDrive C).Free` first, because disk-full looks like an MSB3021 file lock.
3. Build each tree **once** in Release (`dotnet build <tests.csproj> -c Release`), and gate on exit code 0. Never re-run with `--no-build` after a source change.

## Run
- Run **sequentially**, never two suites at once: CPU contention changes the outcome. Don't build or edit files while a suite runs (the Ui guards read XAML off disk; testhost locks bin\).
- Per tree, for i in 1..N:
  ```powershell
  dotnet test <tests.csproj> -c Release --no-build [--filter "<filter>"] --logger "trx;LogFileName=run$i.trx" --results-directory <out>\<tree>
  ```
- **Mode**: race-type flakiness often only shows under **full-suite parallel load**, and a test that is green in isolation proves little. Default to the full suite; use `--filter` only if the user asks, and say that isolation weakens the evidence.
  Cost: the controller suite is about 6.5 min, so 10 runs × 2 trees ≈ 2 h 10 min; the WebApi suite is about 2 min. State the estimate and get an OK before starting.
- Check each run's **total** test count. A run with fewer tests than the others (a crashed host, a blame collector truncating it) is invalid: exclude it and say so.

## Analyse
Parse the TRX files (`UnitTestResult[@outcome='Failed']/@testName`). For each test that failed at least once, build:

| Test | HEAD fails / N | Base fails / N | Verdict |
|---|---|---|---|

Verdicts:
- **Broken**: fails N/N on HEAD.
- **Regression (new flake)**: fails on HEAD (k > 0) and **0/N** on base. Your change introduced or exposed it, so investigate before merging.
- **Pre-existing flaky**: similar non-zero rates on both trees. Not caused by the change.
- **Improved**: base fails, HEAD doesn't.
- With N=10, 1/10 vs 0/10 is weak evidence. Say so, and offer more runs for that one test.

Cross-check against the known flaky cluster (wall-clock and concurrency tests): `LockBroadcastIntegrationTests`, `LockPersistenceMigrationTests`, `AgentLockManagerTests.Persistence_*`, `VocabularyMonitorTests`, `PipelineParallelismTests.Parallel_CancellationRespected_*`, `PowerShellScriptRunnerTests.RunAsync_Should_KillTheScript_*`, `CommandExecutionE2ETests.RunCommandStreamed_AcceptsNewCommand_*`. Being in the list does **not** excuse a rate that jumped on HEAD.

## Clean up
`git worktree remove "$env:TEMP\flaky-base" --force; git worktree prune`. If a cancelled run left `testhost.exe` orphans, name the PIDs and ask before stopping them.

## Report
The table, the run counts (valid/invalid), the total wall time, and per test the next step: fix the race (wait for an observable signal instead of a fixed delay), quarantine it with an issue link, or no action.
