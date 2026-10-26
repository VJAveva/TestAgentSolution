---
name: deploy-controller
description: 'Safely deploy the current TestAgentSolution build to the WPF controller on JVGR22 via deploy/Invoke-Patch.ps1. Use when asked to deploy, patch, ship or push the controller. Enforces a clean git tree, a Release rebuild, green test suites, a mandatory dry run with user approval, a SHA256 check of the deployed files and a DEPLOYED_COMMIT.txt record.'
argument-hint: 'Optional: target node (default JVGR22)'
---

# Deploy Controller (JVGR22)

Ships the committed HEAD to `\\<Node>\C$\TestControllerService` with `deploy\Invoke-Patch.ps1 -Component controller`.
That script copies only files whose content differs. It never mirrors, so data that exists only on the target (WatchList.xml, Parameters\, Logs\, orchestrator.db, impact-*.db) is never touched. `appsettings.json` is protected.

## Hard rules (never break, never work around)
- **No dirty tree.** Never pass `-AllowDirty`. If `git status --porcelain` shows anything, stop and list it.
- **No apply without a dry run** in this same session, followed by the user's explicit OK.
- **Never kill the controller.** No `Stop-Process`/`taskkill`: CrashDumpHelper writes a dump of about 600 MB+ and has filled C: before. The X button only hides it to the tray. Ask the user to exit via **tray icon → Exit** (or File → Exit).
- **Never touch preserveFiles.** Do not use `Invoke-FleetDeployment.ps1` (robocopy `/MIR`) for this. Agent-side `*.bat` files (e.g. `C:\TestAgentService\Run-WASTests.bat`) are in preserveFiles and need a **manual per-agent copy**. Report them; never copy them.
- Never edit the target's `appsettings.json`. If the dry run reports missing keys, tell the user they must add them by hand.

## Procedure

### 1. Preflight (refuse on failure)
```powershell
Set-Location <repo root>
git status --porcelain            # must be EMPTY -> otherwise STOP and list the files
git log --oneline -1               # record HEAD for the report
(Get-PSDrive C).Free / 1GB         # warn if < 10 GB (disk-full errors look like MSB3021 file-lock errors)
```

### 2. Rebuild in Release
```powershell
dotnet build TestAgentSolution.sln -c Release --no-incremental
```
- Check that the exit code is 0 **and** the output shows `0 Error(s)`. Stop on any `error CS`/`error MC`/`error MSB`.
- Release output is separate from the Debug output, so a locally running controller does not block the build.
- MSB3021/MSB3027 copy locks (e.g. a leftover `testhost.exe`) come from the environment, not the code. Name the locking PID and ask the user. Do not kill devenv or the controller.
- Never use `--no-restore`. It breaks Grpc.Tools proto codegen.

### 2b. Test gate (refuse on real failures)
Run one suite at a time. Do not edit files or build while a suite runs: the Ui guards read XAML off disk, and testhost locks bin\.
```powershell
dotnet test TestControllerGrpc.Tests\TestControllerGrpc.Tests.csproj -c Release --no-build   # ~6.5 min
dotnet test TestController.WebApi.Tests\TestController.WebApi.Tests.csproj -c Release --no-build   # ~2 min
```
`--no-build` is safe here only because step 2 just rebuilt Release from the clean tree.
If anything fails, check whether it belongs to the **known flaky cluster**. These are wall-clock/concurrency tests that fail under parallel load:
`LockBroadcastIntegrationTests`, `LockPersistenceMigrationTests`, `AgentLockManagerTests.Persistence_*`,
`VocabularyMonitorTests`, `PipelineParallelismTests.Parallel_CancellationRespected_*`,
`PowerShellScriptRunnerTests.RunAsync_Should_KillTheScript_*`, `CommandExecutionE2ETests.RunCommandStreamed_AcceptsNewCommand_*`.
- Flaky-cluster failure: re-run those classes in isolation (`--filter "FullyQualifiedName~<Class>"`). If they pass in isolation, report them as flaky and continue.
- Any other failure, or a cluster test that also fails in isolation: **STOP**. Report the failures and do not dry-run.
- Report the pass/fail totals for both suites. A total that is lower than usual (controller ~2190, WebApi ~758) means the run is not trustworthy.

### 3. Dry run (mandatory)
```powershell
.\deploy\Invoke-Patch.ps1 -Component controller
```
Output can exceed 20 KB. Grep it for `[ OK ]`, `[WARN]`, `[FAIL]`, `REAL content`, `NEW,`, `MVID churn`, `appsettings`, and the commit/file list since the previous `DEPLOYED_COMMIT.txt`.
Present to the user:
- commits and source files since the last deployed commit
- the list of files with REAL changes, plus the count of MVID-churn files (same size, new hash)
- any appsettings keys the target is missing (manual step)
- any changed agent-side `*.bat` / preserveFiles that need a manual per-agent copy

Do **not** trust the script's size buckets to decide what matters. A real XAML/C# change can keep the same byte length, so it lands in "MVID churn". Everything in the payload ships as one set.

### 4. Wait for the user's OK
Stop here. Do not continue without an explicit approval in chat.

### 5. Apply
1. Check that the controller is stopped:
   ```powershell
   foreach ($p in 5100,5200) { "$p : " + (Test-NetConnection <Node> -Port $p -WarningAction SilentlyContinue).TcpTestSucceeded }
   ```
   If either port is open, ask the user to exit it from the **tray icon**. Optionally check `GET http://<Node>:81/api/execution/sessions` for active runs first. Never kill it.
2. Apply the **already-staged** payload, so what ships is exactly what the user approved and the window between check and copy is seconds:
   ```powershell
   .\deploy\Invoke-Patch.ps1 -Component controller -SkipBuild -Apply
   ```
   The script refuses if the controller is running. It backs up every file it overwrites, aborts if the backup count does not match, and SHA256-checks every copied file.
3. Capture the `Rollback stamp: yyyyMMdd-HHmmss` line.

### 6. Verify independently
- Hash check: for the first-party assemblies (`TestControllerGrpc.dll`, `TestControllerGrpc.Core.dll`, `TestController.Api.dll`, `TestController.Persistence.dll`, `TestController.Reporting.dll`, `TestController.Impact.dll`), compare `Get-FileHash` of `$env:TEMP\tcpatch\controller\<f>` against `\\<Node>\C$\TestControllerService\<f>`. All must be equal.
- `DEPLOYED_COMMIT.txt`: `Invoke-Patch` writes `\\<Node>\C$\TestControllerService\_patchbackup\DEPLOYED_COMMIT.txt` (line 1 = full HEAD SHA, line 2 = stamp). Read it back and check that line 1 equals `git rev-parse HEAD` and there is **no** `DIRTY` note. If it is missing or wrong, write it with `Set-Content -Encoding ASCII "<sha>`n<stamp>"`.
- Check that protected state is untouched: `appsettings.json` and `WatchList.xml` LastWriteTime are earlier than the patch time.
- If a patch file fails verification, report it and give the rollback command. Do not retry blindly.

### 7. Report
Keep it short:
- deployed commit (short SHA + subject), rollback stamp, file count (real vs churn)
- hash check result and test totals (plus any flaky tests that passed when re-run on their own)
- manual follow-ups: **start `TestControllerGrpc.exe` on the target** (launchMode Manual, so the ports stay closed until someone does), missing appsettings keys, per-agent `*.bat` copies
- rollback command:
  ```powershell
  .\deploy\Invoke-Patch.ps1 -Component controller -Rollback -BackupStamp <stamp> -Apply
  ```

## Notes
- The patch ships the whole tree at HEAD, not just the task you were working on. Name every feature in the commit range in the report.
- Stage directory: `$env:TEMP\tcpatch\controller`. `-SkipBuild` reuses it. Only use `-SkipBuild` right after this session's dry run.
