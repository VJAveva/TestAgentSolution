# Task (parked): copy the test-data workbook back to the controller

> Owner: test-framework / agent side. Not FIR code. Raised by FIR Phase 4a.
> Status: **not applied.** Park until Phase 4 is finished.

## Why

`Run-WASTests.bat` copies results to the controller with, at L555:

```batch
xcopy "!_cs_src!\*.*" "!_cs_dst!\" /Y /Q
```

There is **no `/S` or `/E`**, so only files directly in `Set<N>\` are copied — never subdirectories. The
workbook the run used lives in MSTest's deployment folder:

```
C:\TestResults\<Build>\Set1\<user>_<machine> <timestamp>\Out\TestData\WASSmokeTest\WASSet1.xlsx
```

so it has **never** reached the controller. FIR therefore falls back to `Fir:TestDataRoots` plus the
staleness guard. Making the run bring its own copy turns the common case into an exact match, and the
report then reads `test data (… , copy from run)` instead of `controller copy, matched to log`.

## Change

In [Run-WASTests.bat](../../Utilites/TestSetup/Common/SmokeTest/Run-WASTests.bat):

1. One call at the end of `:CopySuiteToCtrl`:

   ```batch
   call :CopyTestDataToCtrl "%~1"
   ```

2. A new subroutine that copies **only the workbook named in the log's `ExcelPath` line** — not `*.xls*`.
   The name is in the debug trace as
   `(ReadValuesFromExcel) ExcelPath : "…\WASSet1.xlsx" and sheet name: "…"`, so parse the leaf name from
   the suite's `.trx`/console output (or accept it as a parameter the caller already knows) and copy that
   single file:

   ```batch
   :CopyTestDataToCtrl
   if "!CTRL!"=="" exit /b 0
   REM !_td_name! = workbook file name taken from the run's ExcelPath line
   if "!_td_name!"=="" exit /b 0
   for /d %%D in ("!RESULTS_ROOT!\%~1\*") do (
       if exist "%%~fD\Out\TestData" (
           if not exist "!CTRL_ROOT!\%~1\TestData" mkdir "!CTRL_ROOT!\%~1\TestData" >nul 2>&1
           for /r "%%~fD\Out\TestData" %%F in ("!_td_name!") do (
               if exist "%%~fF" xcopy "%%~fF" "!CTRL_ROOT!\%~1\TestData\" /Y /Q >nul 2>&1
           )
       )
   )
   exit /b 0
   ```

3. Add `CopyTestDataToCtrl` to the required-label whitelist at **L52** — the script validates its own
   subroutine labels at startup, and a label that is not listed is not protected against edit corruption.

4. A copy failure is **logged and ignored**. It must never fail the run:

   ```batch
   if !ERRORLEVEL! NEQ 0 echo [WARN] Could not copy test data for %~1 - continuing
   ```

## Rollout

`*.bat` is in `preserveFiles` in [fleet-inventory.json](../../deploy/fleet-inventory.json), so a fleet
deploy will **not** update `C:\TestAgentService\Run-WASTests.bat`. It must be copied by hand.

1. **Canary one agent first.** Copy to a single agent, run one suite, and verify:
   - `\\<controller>\c$\TestResults\<Build>\Set1\TestData\<workbook>` exists;
   - the test run's own pass/fail counts are unchanged;
   - a FIR for a failed test in that build reports `test data (… , copy from run)`.
2. Only then copy to the remaining agents.
3. Keep `Fir:TestDataRoots` configured regardless — old builds will never have the copy.

## Done when

- A new build's failed test shows `copy from run` in the FIR Sources line.
- Removing the workbook from the results folder still produces a full log-only report (no regression).
