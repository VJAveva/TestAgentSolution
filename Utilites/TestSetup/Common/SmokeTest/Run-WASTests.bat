@echo off
setlocal EnableDelayedExpansion
REM ==============================================================
REM  Run-WASTests.bat  (v4.2 - Resilient + Resume Mode)
REM  Location: C:\TestAgentService\Run-WASTests.bat
REM
REM  Data-driven test execution engine. Reads test case list from
REM  TestCaseList.json (parsed via ParseTestList.ps1) and executes
REM  each entry in the exact order specified in the JSON file.
REM
REM  CHANGELOG v4.2:
REM    - Added startup label validation: script verifies every
REM      required subroutine label exists before any execution.
REM      Prevents mid-run "cannot find batch label" failures from
REM      edit corruption (line ending issues, accidental deletion,
REM      indentation, etc.).
REM    - Added RESUME mode: -Resume flag re-runs only previously
REM      failed tests by reading FailedTests_<machine>.txt and
REM      filtering the parsed test list. Stale results are NOT
REM      deleted in resume mode -- existing passes are preserved.
REM    - Resume mode merges new results into the existing summary
REM      (cumulative pass/fail counts), so the final report
REM      reflects the full test history.
REM    - More explicit error messages around file operations to
REM      aid debugging at scale.
REM
REM  Results folder structure (mirrored to Controller):
REM    C:\TestResults\{Build}\
REM      Set1\  *.trx, TestSummary_{Machine}.txt, FailedTests.txt
REM      Set2\  *.trx, TestSummary_{Machine}.txt, FailedTests.txt
REM      Set3\  *.trx, TestSummary_{Machine}.txt, FailedTests.txt
REM      TestSummary_{Machine}.txt   (overall)
REM      FailedTests_{Machine}.txt   (overall)
REM
REM  Usage:
REM    Run-WASTests.bat BuildNumber TestSet [ControllerNode] [-Resume]
REM
REM  Examples:
REM    Run-WASTests.bat OAK_main_20260406.5 Set1
REM    Run-WASTests.bat OAK_main_20260406.5 All CONTROLLER01
REM    Run-WASTests.bat OAK_main_20260406.5 Set2 jvgr22
REM    Run-WASTests.bat OAK_main_20260406.5 All jvgr22 -Resume
REM ==============================================================

REM ==============================================================
REM  STARTUP VALIDATION: verify every required label exists
REM  This catches script corruption (line endings, deleted labels,
REM  indented labels, BOM bytes) BEFORE any test runs, so we fail
REM  fast and clearly instead of mid-run with a cryptic error.
REM ==============================================================
set "_LABEL_CHECK_FAILED=0"
for %%L in (DoHeader DoRun DoRun_Fail DoRun_SkipDLL DoSkip DoService DoService_NoSrc DoService_Unknown WriteSuiteSummary CleanSuiteDir CopyResults CopySuiteToCtrl MirrorToCtrl WriteOverallReport BuildResumeFilter ApplyResumeFilter) do (
    findstr /B /C:":%%L" "%~f0" >nul 2>&1
    if !ERRORLEVEL! NEQ 0 (
        echo [FAIL] Required label :%%L is missing or unreadable in %~nx0
        set "_LABEL_CHECK_FAILED=1"
    )
)
if "!_LABEL_CHECK_FAILED!"=="1" (
    echo.
    echo [FAIL] Script integrity check failed.
    echo        Possible causes:
    echo          - Script was saved with Unix LF-only line endings
    echo          - A required label was deleted during editing
    echo          - Script has UTF-8 BOM bytes ^(EF BB BF^) at the start
    echo          - A label is indented instead of starting at column 1
    echo.
    echo        Verify with:
    echo          findstr /B /C:":MirrorToCtrl" "%~f0"
    echo        Should output exactly one line: :MirrorToCtrl
    exit /b 90
)

REM ---- Validate arguments ----
if "%~1"=="" (
    echo [FAIL] Usage: Run-WASTests.bat BuildNumber TestSet [ControllerNode] [-Resume]
    echo        TestSet = Set1 ^| Set2 ^| Set3 ^| All
    echo        -Resume = re-run only previously failed tests
    exit /b 1
)

set "BUILD=%~1"
set "TESTSET=%~2"
set "CTRL=%~3"
set "RESUME_MODE=0"

REM Check for -Resume flag in any argument position 3 or 4
if /I "%~3"=="-Resume" (
    set "RESUME_MODE=1"
    set "CTRL="
)
if /I "%~4"=="-Resume" set "RESUME_MODE=1"

if "!TESTSET!"=="" set "TESTSET=All"

REM Resolve the Controller mirror root up front
set "CTRL_ROOT="
if not "!CTRL!"=="" set "CTRL_ROOT=\\!CTRL!\c$\TestResults\!BUILD!"

REM Validate TestSet
set "_VALID=0"
for %%S in (Set1 Set2 Set3 All) do if /I "!TESTSET!"=="%%S" set "_VALID=1"
if "!_VALID!"=="0" (
    echo [FAIL] Invalid TestSet: !TESTSET!. Valid: Set1, Set2, Set3, All
    exit /b 1
)

REM ---- Resolve paths ----
set "SCRIPT_DIR=%~dp0"
set "JSON=!SCRIPT_DIR!TestCaseList.json"
set "PS_HELPER=!SCRIPT_DIR!ParseTestList.ps1"
set "BIN=C:\TestBinaries"
set "DLL_PRIMARY=!BIN!\WASSmokeTest.dll"
set "DLL_SCRIPT=!BIN!\ScriptFunctions.dll"
set "RESULTS_ROOT=C:\TestResults\!BUILD!"
set "PARSED=!RESULTS_ROOT!\_parsed_tests.tmp"
set "PARSED_FILTERED=!RESULTS_ROOT!\_parsed_tests_resume.tmp"
set "RESUME_FILTER=!RESULTS_ROOT!\_resume_filter.tmp"
set "SIM_SRC=C:\CISmokeTest\TestData\WASSmokeTest\SIM.AAcfg"
set "SIM_DST=C:\ProgramData\Wonderware\OI-Server\$Operations Integration Supervisory Servers$\OI.SIM\OI.SIM"

REM Overall report files (in build root)
set "REPORT=!RESULTS_ROOT!\TestSummary_%COMPUTERNAME%.txt"
set "FAIL_LOG=!RESULTS_ROOT!\FailedTests_%COMPUTERNAME%.txt"
set "PREV_FAIL_LOG=!RESULTS_ROOT!\FailedTests_%COMPUTERNAME%.prev.txt"

REM ---- Validate required files ----
if not exist "!JSON!" (
    echo [FAIL] TestCaseList.json not found: !JSON!
    exit /b 1
)
if not exist "!PS_HELPER!" (
    echo [FAIL] ParseTestList.ps1 not found: !PS_HELPER!
    exit /b 1
)

REM ---- Resolve MSTest.exe ----
set "MSTEST="
set "_ms1=C:\Program Files (x86)\Microsoft Visual Studio\2017\Enterprise\Common7\IDE\MSTest.exe"
set "_ms2=C:\Program Files (x86)\Microsoft Visual Studio\2017\TestAgent\Common7\IDE\MSTest.exe"
if exist "!_ms1!" set "MSTEST=!_ms1!"
if not defined MSTEST if exist "!_ms2!" set "MSTEST=!_ms2!"
if not defined MSTEST (
    echo [FAIL] MSTest.exe not found
    exit /b 1
)

REM ---- Validate primary test DLL ----
if not exist "!DLL_PRIMARY!" (
    echo [FAIL] Test DLL not found: !DLL_PRIMARY!
    exit /b 1
)

REM ---- Check script DLL availability ----
set "HAS_SCRIPT_DLL=0"
if exist "!DLL_SCRIPT!" set "HAS_SCRIPT_DLL=1"

REM ==============================================================
REM  RESUME MODE: Validate prerequisites for resume
REM ==============================================================
if "!RESUME_MODE!"=="1" (
    if not exist "!RESULTS_ROOT!" (
        echo [FAIL] Cannot resume: results folder does not exist
        echo        Expected: !RESULTS_ROOT!
        echo        Run without -Resume to do a fresh run.
        exit /b 1
    )
    if not exist "!FAIL_LOG!" (
        echo [INFO] Resume requested but no previous FailedTests log found.
        echo [INFO] Either all previous tests passed, or no previous run exists.
        echo [INFO] Expected: !FAIL_LOG!
        echo [INFO] Nothing to resume. Exiting cleanly.
        exit /b 0
    )

    REM Count failed tests
    set /a _FAIL_COUNT=0
    for /f %%I in ('type "!FAIL_LOG!" 2^>nul ^| find /c /v ""') do set /a _FAIL_COUNT=%%I

    if !_FAIL_COUNT! EQU 0 (
        echo [INFO] FailedTests log exists but is empty. Nothing to resume.
        exit /b 0
    )

    echo ==================================================
    echo [INFO] RESUME MODE
    echo [INFO] Previously failed tests: !_FAIL_COUNT!
    echo [INFO] Stale results will be PRESERVED
    echo [INFO] Only failed tests will be re-executed
    echo ==================================================
    echo.
)

REM ==============================================================
REM  CLEAN STALE DATA (only in fresh-run mode, NOT in resume)
REM ==============================================================
if "!RESUME_MODE!"=="0" (
    if not exist "!RESULTS_ROOT!" mkdir "!RESULTS_ROOT!" >nul 2>&1
    if /I "!TESTSET!"=="Set1" call :CleanSuiteDir Set1
    if /I "!TESTSET!"=="Set2" call :CleanSuiteDir Set2
    if /I "!TESTSET!"=="Set3" call :CleanSuiteDir Set3
    if /I "!TESTSET!"=="All"  call :CleanSuiteDir Set1&call :CleanSuiteDir Set2&call :CleanSuiteDir Set3
    del /Q "!RESULTS_ROOT!\TestSummary_*.txt" >nul 2>&1
    del /Q "!RESULTS_ROOT!\FailedTests_*.txt" >nul 2>&1
    del /Q "!RESULTS_ROOT!\*.tmp" >nul 2>&1
    echo [INFO] Stale results cleaned.
) else (
    REM In resume mode: archive current FailedTests as .prev for diff
    if exist "!FAIL_LOG!" (
        copy /Y "!FAIL_LOG!" "!PREV_FAIL_LOG!" >nul 2>&1
        del /Q "!FAIL_LOG!" >nul 2>&1
    )
    REM Clear stale .trx files ONLY for failed tests (preserve passes)
    REM This is handled later, after we know which tests to re-run.
    echo [INFO] Previous results preserved. Failed tests will be cleared individually.
)

REM ---- Parse JSON into flat file ----
echo [INFO] Parsing TestCaseList.json for !TESTSET! ...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "!PS_HELPER!" -JsonPath "!JSON!" -SetName "!TESTSET!" -OutPath "!PARSED!"
if !ERRORLEVEL! NEQ 0 (
    echo [FAIL] Failed to parse TestCaseList.json
    exit /b 1
)
if not exist "!PARSED!" (
    echo [FAIL] Parsed test list not generated
    exit /b 1
)

REM ==============================================================
REM  RESUME MODE: Filter parsed list to only failed tests
REM ==============================================================
if "!RESUME_MODE!"=="1" (
    call :BuildResumeFilter
    call :ApplyResumeFilter
    if !ERRORLEVEL! NEQ 0 (
        echo [FAIL] Could not build resume filter
        exit /b 1
    )
    REM Replace PARSED with the filtered list for the rest of execution
    move /Y "!PARSED_FILTERED!" "!PARSED!" >nul 2>&1
)

REM ---- Read total count from first line ----
set /a TOTAL=0
for /f "tokens=2 delims=|" %%A in ('findstr /B "TOTAL" "!PARSED!"') do set /a TOTAL=%%A

REM ---- Count runnable tests (run + skip only) ----
set /a RUNNABLE=0
for /f "usebackq skip=1 tokens=2 delims=|" %%B in ("!PARSED!") do (
    if "%%B"=="run"  set /a RUNNABLE+=1
    if "%%B"=="skip" set /a RUNNABLE+=1
)

if !RUNNABLE! EQU 0 (
    if "!RESUME_MODE!"=="1" (
        echo [INFO] No failed tests to re-run after filter. Nothing to do.
        exit /b 0
    ) else (
        echo [FAIL] No runnable tests found in parsed list
        exit /b 1
    )
)

REM ---- Global counters ----
set /a DONE=0
set /a G_PASS=0
set /a G_FAIL=0
set /a G_SKIP=0

REM ---- Per-suite counters (reset on each header) ----
set /a S_PASS=0
set /a S_FAIL=0
set /a S_SKIP=0
set /a S_DONE=0
set "CUR_SET="
set "CUR_SET_NAME="
set "CUR_SET_DIR="

REM ---- Capture start time ----
set "START_TIME=!time!"
set "_tts=!START_TIME: =0!"
for /F "tokens=1-3 delims=:." %%a in ("!_tts!") do set /a "START_SECS=(%%a*3600)+(%%b*60)+%%c"

REM ---- Banner ----
echo.
echo ==================================================
echo [INFO] WAS SMOKE TEST SUITE  ^(v4.2 Resilient^)
if "!RESUME_MODE!"=="1" echo [INFO] MODE     : RESUME ^(failed tests only^)
if "!RESUME_MODE!"=="0" echo [INFO] MODE     : FRESH RUN
echo [INFO] Machine  : %COMPUTERNAME%
echo [INFO] Build    : !BUILD!
echo [INFO] TestSet  : !TESTSET!
echo [INFO] TestList : !JSON!
echo [INFO] Tests    : !RUNNABLE!
echo [INFO] Results  : !RESULTS_ROOT!\{Set1,Set2,Set3}
echo [INFO] MSTest   : !MSTEST!
echo [INFO] Start    : !START_TIME!
echo ==================================================
echo.

REM ==============================================================
REM  MAIN EXECUTION LOOP
REM ==============================================================
for /f "usebackq skip=1 tokens=1-6 delims=|" %%A in ("!PARSED!") do (
    set "_SEQ=%%A"
    set "_ACTION=%%B"
    set "_SET=%%C"
    set "_DLL_KEY=%%D"
    set "_TEST=%%E"
    set "_EXTRA=%%F"
    if "!_ACTION!"=="header" call :DoHeader
    if "!_ACTION!"=="wait" echo [INFO] Waiting !_EXTRA! seconds...&timeout /T !_EXTRA! /NOBREAK >nul
    if "!_ACTION!"=="service" call :DoService
    if "!_ACTION!"=="skip" call :DoSkip
    if "!_ACTION!"=="run" call :DoRun
)

REM Write final suite summary (for the last set processed)
call :WriteSuiteSummary

goto :CopyResults

REM ==============================================================
REM  SUBROUTINES
REM  ZERO parenthesized if/else blocks within subroutines.
REM  All branching via goto. Each subroutine ends with exit /b.
REM ==============================================================

REM --------------------------------------------------------------
REM :BuildResumeFilter
REM   Reads FailedTests_<machine>.prev.txt and creates a hash map
REM   in a temp file. Each line of the filter file is a test name.
REM --------------------------------------------------------------
:BuildResumeFilter
del /Q "!RESUME_FILTER!" >nul 2>&1
if not exist "!PREV_FAIL_LOG!" (
    echo [FAIL] Cannot build resume filter: !PREV_FAIL_LOG! missing
    exit /b 1
)
copy /Y "!PREV_FAIL_LOG!" "!RESUME_FILTER!" >nul 2>&1
echo [INFO] Resume filter built from !PREV_FAIL_LOG!
exit /b 0

REM --------------------------------------------------------------
REM :ApplyResumeFilter
REM   Reads PARSED, keeps:
REM     - TOTAL header (always)
REM     - header rows (always - needed for suite tracking)
REM     - service rows (always - pre-test config)
REM     - wait rows (always - timing)
REM     - run rows ONLY if the test name appears in RESUME_FILTER
REM     - skip rows are dropped (no point re-skipping)
REM   Writes to PARSED_FILTERED.
REM --------------------------------------------------------------
:ApplyResumeFilter
del /Q "!PARSED_FILTERED!" >nul 2>&1

REM Write TOTAL header first (it will be wrong but kept for compat)
set /a _NEW_TOTAL=0
echo TOTAL^|0> "!PARSED_FILTERED!"

REM Process each line
for /f "usebackq skip=1 tokens=1-6 delims=|" %%A in ("!PARSED!") do (
    set "_F_SEQ=%%A"
    set "_F_ACTION=%%B"
    set "_F_SET=%%C"
    set "_F_DLL=%%D"
    set "_F_TEST=%%E"
    set "_F_EXTRA=%%F"

    if "!_F_ACTION!"=="header" (
        echo !_F_SEQ!^|!_F_ACTION!^|!_F_SET!^|!_F_DLL!^|!_F_TEST!^|!_F_EXTRA!>> "!PARSED_FILTERED!"
    )
    if "!_F_ACTION!"=="service" (
        echo !_F_SEQ!^|!_F_ACTION!^|!_F_SET!^|!_F_DLL!^|!_F_TEST!^|!_F_EXTRA!>> "!PARSED_FILTERED!"
    )
    if "!_F_ACTION!"=="wait" (
        echo !_F_SEQ!^|!_F_ACTION!^|!_F_SET!^|!_F_DLL!^|!_F_TEST!^|!_F_EXTRA!>> "!PARSED_FILTERED!"
    )
    if "!_F_ACTION!"=="run" (
        findstr /X /C:"!_F_TEST!" "!RESUME_FILTER!" >nul 2>&1
        if !ERRORLEVEL! EQU 0 (
            echo !_F_SEQ!^|!_F_ACTION!^|!_F_SET!^|!_F_DLL!^|!_F_TEST!^|!_F_EXTRA!>> "!PARSED_FILTERED!"
            set /a _NEW_TOTAL+=1
            REM Delete the stale .trx for this failed test so re-run is clean
            if exist "!RESULTS_ROOT!\!_F_SET!\!_F_TEST!.trx" del /Q "!RESULTS_ROOT!\!_F_SET!\!_F_TEST!.trx" >nul 2>&1
        )
    )
)

echo [INFO] Resume filter applied: !_NEW_TOTAL! test(s) will be re-run
exit /b 0

REM --------------------------------------------------------------
REM :CleanSuiteDir  SetKey
REM   Removes stale files from a suite subfolder.
REM --------------------------------------------------------------
:CleanSuiteDir
set "_csd=!RESULTS_ROOT!\%~1"
if not exist "!_csd!" exit /b 0
del /Q "!_csd!\*.trx" >nul 2>&1
del /Q "!_csd!\*.txt" >nul 2>&1
exit /b 0

REM --------------------------------------------------------------
REM :DoHeader  - Start a new suite section
REM --------------------------------------------------------------
:DoHeader
if not "!CUR_SET!"=="" call :WriteSuiteSummary
set "CUR_SET=!_SET!"
set "CUR_SET_NAME=!_TEST!"
set "CUR_SET_DIR=!RESULTS_ROOT!\!_SET!"
if not exist "!CUR_SET_DIR!" mkdir "!CUR_SET_DIR!" >nul 2>&1
set /a S_PASS=0
set /a S_FAIL=0
set /a S_SKIP=0
set /a S_DONE=0
echo ==================================================
echo [STEP] !CUR_SET!: !CUR_SET_NAME!
echo [STEP] Results: !CUR_SET_DIR!
echo ==================================================
echo.
exit /b 0

REM --------------------------------------------------------------
REM :DoRun  - Execute one MSTest test case
REM --------------------------------------------------------------
:DoRun
set /a DONE+=1
set /a S_DONE+=1
set /a _PCT=DONE*100/RUNNABLE
set "_RESOLVED_DLL=!DLL_PRIMARY!"
if "!_DLL_KEY!"=="script" set "_RESOLVED_DLL=!DLL_SCRIPT!"
if "!_DLL_KEY!"=="script" if "!HAS_SCRIPT_DLL!"=="0" goto :DoRun_SkipDLL
echo [!_PCT!%%] [!DONE!/!RUNNABLE!] Running: !_TEST!
"!MSTEST!" /testcontainer:"!_RESOLVED_DLL!" /test:!_TEST! /resultsfile:"!CUR_SET_DIR!\!_TEST!.trx" >nul 2>&1
if !ERRORLEVEL! NEQ 0 goto :DoRun_Fail
set /a G_PASS+=1
set /a S_PASS+=1
echo [!_PCT!%%] [PASS] !_TEST!
call :MirrorToCtrl "!CUR_SET_DIR!\!_TEST!.trx" "!CUR_SET!"
exit /b 0

:DoRun_Fail
set /a G_FAIL+=1
set /a S_FAIL+=1
echo [!_PCT!%%] [FAIL] !_TEST! ^(exit !ERRORLEVEL!^)
echo !_TEST!>> "!CUR_SET_DIR!\FailedTests_%COMPUTERNAME%.txt"
echo !_TEST!>> "!FAIL_LOG!"
call :MirrorToCtrl "!CUR_SET_DIR!\!_TEST!.trx" "!CUR_SET!"
call :MirrorToCtrl "!CUR_SET_DIR!\FailedTests_%COMPUTERNAME%.txt" "!CUR_SET!"
call :MirrorToCtrl "!FAIL_LOG!" ""
exit /b 0

:DoRun_SkipDLL
set /a G_SKIP+=1
set /a S_SKIP+=1
echo [!_PCT!%%] [!DONE!/!RUNNABLE!] [SKIP] !_TEST! - DLL not found
exit /b 0

REM --------------------------------------------------------------
REM :DoSkip  - Log a skipped test
REM --------------------------------------------------------------
:DoSkip
set /a DONE+=1
set /a S_DONE+=1
set /a G_SKIP+=1
set /a S_SKIP+=1
set /a _PCT=DONE*100/RUNNABLE
echo [!_PCT!%%] [!DONE!/!RUNNABLE!] [SKIP] !_TEST! - !_EXTRA!
exit /b 0

REM --------------------------------------------------------------
REM :DoService  - Handle service actions
REM --------------------------------------------------------------
:DoService
if not "!_EXTRA!"=="configure_sim" goto :DoService_Unknown
echo [STEP] Configuring SIM service for DeadBand tests...
if not exist "!SIM_SRC!" goto :DoService_NoSrc
if not exist "!SIM_DST!" mkdir "!SIM_DST!" >nul 2>&1
echo F | xcopy "!SIM_SRC!" "!SIM_DST!\" /Y /R >nul 2>&1
echo [INFO] Starting service: SIM
net start SIM >nul 2>&1
echo [INFO] Waiting 50 seconds for SIM initialization...
timeout /T 50 /NOBREAK >nul
echo [PASS] SIM service configured
exit /b 0

:DoService_NoSrc
echo [WARN] SIM config not found: !SIM_SRC! -- DeadBand tests may fail
exit /b 0

:DoService_Unknown
echo [WARN] Unknown service action: !_EXTRA!
exit /b 0

REM --------------------------------------------------------------
REM :WriteSuiteSummary  - Write per-suite summary to suite folder
REM --------------------------------------------------------------
:WriteSuiteSummary
if "!CUR_SET!"=="" exit /b 0
set "_S_RESULT=ALL PASSED"
if !S_FAIL! GTR 0 set "_S_RESULT=FAILURES DETECTED"
set "_S_TAG=[PASS]"
if !S_FAIL! GTR 0 set "_S_TAG=[WARN]"
echo.
echo !_S_TAG! !CUR_SET! done: Pass=!S_PASS! Fail=!S_FAIL! Skip=!S_SKIP! Total=!S_DONE!
set "_S_RPT=!CUR_SET_DIR!\TestSummary_%COMPUTERNAME%.txt"
(
    echo ============================================
    echo  !CUR_SET!: !CUR_SET_NAME!
    echo ============================================
    echo  Machine  : %COMPUTERNAME%
    echo  Build    : !BUILD!
    if "!RESUME_MODE!"=="1" echo  Mode     : RESUME ^(failed tests only^)
    if "!RESUME_MODE!"=="0" echo  Mode     : FRESH RUN
    echo  Passed   : !S_PASS!
    echo  Failed   : !S_FAIL!
    echo  Skipped  : !S_SKIP!
    echo  Total    : !S_DONE!
    echo  Result   : !_S_RESULT!
    echo ============================================
) > "!_S_RPT!"
call :MirrorToCtrl "!_S_RPT!" "!CUR_SET!"
echo.
exit /b 0

REM ==============================================================
REM  COPY RESULTS TO CONTROLLER
REM ==============================================================
:CopyResults
if "!CTRL!"=="" goto :WriteOverallReport
set "CTRL_ROOT=\\!CTRL!\c$\TestResults\!BUILD!"
echo.
echo [STEP] Copying results to !CTRL_ROOT! ...
if not exist "!CTRL_ROOT!" mkdir "!CTRL_ROOT!" >nul 2>&1
if /I "!TESTSET!"=="Set1" call :CopySuiteToCtrl Set1
if /I "!TESTSET!"=="Set2" call :CopySuiteToCtrl Set2
if /I "!TESTSET!"=="Set3" call :CopySuiteToCtrl Set3
if /I "!TESTSET!"=="All" call :CopySuiteToCtrl Set1&call :CopySuiteToCtrl Set2&call :CopySuiteToCtrl Set3
if exist "!REPORT!" xcopy "!REPORT!" "!CTRL_ROOT!\" /Y /Q >nul 2>&1
if exist "!FAIL_LOG!" xcopy "!FAIL_LOG!" "!CTRL_ROOT!\" /Y /Q >nul 2>&1
echo [PASS] Results copied to Controller
goto :WriteOverallReport

REM --------------------------------------------------------------
REM :CopySuiteToCtrl  SetKey
REM --------------------------------------------------------------
:CopySuiteToCtrl
set "_cs_src=!RESULTS_ROOT!\%~1"
set "_cs_dst=!CTRL_ROOT!\%~1"
if not exist "!_cs_src!" exit /b 0
if not exist "!_cs_dst!" mkdir "!_cs_dst!" >nul 2>&1
xcopy "!_cs_src!\*.*" "!_cs_dst!\" /Y /Q >nul 2>&1
if !ERRORLEVEL! NEQ 0 echo [WARN] Failed to copy %~1 to controller
exit /b 0

REM --------------------------------------------------------------
REM :MirrorToCtrl  LocalFile  SubDir
REM   Best-effort copy of a single file to the Controller node.
REM --------------------------------------------------------------
:MirrorToCtrl
if "!CTRL!"=="" exit /b 0
if not exist "%~1" exit /b 0
set "_mc_dst=!CTRL_ROOT!"
if not "%~2"=="" set "_mc_dst=!CTRL_ROOT!\%~2"
if not exist "!_mc_dst!" mkdir "!_mc_dst!" >nul 2>&1
xcopy "%~1" "!_mc_dst!\" /Y /Q >nul 2>&1
if !ERRORLEVEL! NEQ 0 echo [WARN] Failed to mirror %~nx1 to controller
exit /b 0

REM ==============================================================
REM  WRITE OVERALL SUMMARY REPORT
REM ==============================================================
:WriteOverallReport
set "END_TIME=!time!"
set "_tts=!END_TIME: =0!"
for /F "tokens=1-3 delims=:." %%a in ("!_tts!") do set /a "END_SECS=(%%a*3600)+(%%b*60)+%%c"
set /a ELAPSED=END_SECS-START_SECS
if !ELAPSED! LSS 0 set /a ELAPSED+=86400
set /a E_HH=ELAPSED/3600
set /a E_MM=(ELAPSED%%3600)/60
set /a E_SS=ELAPSED%%60
if !E_HH! LSS 10 set "E_HH=0!E_HH!"
if !E_MM! LSS 10 set "E_MM=0!E_MM!"
if !E_SS! LSS 10 set "E_SS=0!E_SS!"
set "ELAPSED_FMT=!E_HH!h !E_MM!m !E_SS!s"

set "_RESULT_STR=ALL PASSED"
if !G_FAIL! GTR 0 set "_RESULT_STR=FAILURES DETECTED"

set "_MODE_STR=FRESH RUN"
if "!RESUME_MODE!"=="1" set "_MODE_STR=RESUME ^(failed tests only^)"

(
    echo ============================================
    echo  WAS SMOKE TEST REPORT  ^(Overall^)
    echo ============================================
    echo  Machine  : %COMPUTERNAME%
    echo  Build    : !BUILD!
    echo  TestSet  : !TESTSET!
    echo  Mode     : !_MODE_STR!
    echo  Start    : !START_TIME!
    echo  End      : !END_TIME!
    echo  Elapsed  : !ELAPSED_FMT!
    echo  -------------------------------------------
    echo  Passed   : !G_PASS!
    echo  Failed   : !G_FAIL!
    echo  Skipped  : !G_SKIP!
    echo  Total    : !DONE! / !RUNNABLE!
    echo  -------------------------------------------
    echo  Result   : !_RESULT_STR!
    echo ============================================
) > "!REPORT!"

call :MirrorToCtrl "!REPORT!" ""
call :MirrorToCtrl "!FAIL_LOG!" ""

REM ---- Cleanup temp files ----
del /Q "!PARSED!" >nul 2>&1
del /Q "!PARSED_FILTERED!" >nul 2>&1
del /Q "!RESUME_FILTER!" >nul 2>&1
del /Q "!PREV_FAIL_LOG!" >nul 2>&1

REM ---- Console summary ----
echo.
echo ==================================================
set "_TAG=[PASS]"
if !G_FAIL! GTR 0 set "_TAG=[WARN]"
echo !_TAG! !TESTSET! COMPLETE on %COMPUTERNAME%
if "!RESUME_MODE!"=="1" echo [INFO] Mode     : RESUME ^(failed tests only^)
if "!RESUME_MODE!"=="0" echo [INFO] Mode     : FRESH RUN
echo [INFO] Passed   : !G_PASS!
echo [INFO] Failed   : !G_FAIL!
echo [INFO] Skipped  : !G_SKIP!
echo [INFO] Total    : !DONE! / !RUNNABLE!
echo [INFO] Build    : !BUILD!
echo [INFO] Results  : !RESULTS_ROOT!
echo [INFO] Elapsed  : !ELAPSED_FMT!
echo [INFO] Report   : !REPORT!
if !G_FAIL! GTR 0 echo [INFO] Failures : !FAIL_LOG!
echo ==================================================

if !G_FAIL! GTR 0 exit /b 1
exit /b 0
