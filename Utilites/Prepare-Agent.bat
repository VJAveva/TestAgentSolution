@echo off
setlocal EnableExtensions
REM ==============================================================
REM  Prepare-Agent.bat
REM  Runs ON each Agent (invoked FROM the Controller as RunRemoteCommand).
REM  Pulls the build payload AND the test binaries from the Controller's
REM  C$ admin share down to fixed local folders, unblocks them, verifies.
REM
REM  Usage:   Prepare-Agent.bat ControllerName [SourceFolder] [BinariesFolder]
REM  Example: Prepare-Agent.bat CONTROLLER01 TestSetup\SP2023R2Train TestBinaries
REM
REM  Arguments:
REM    ControllerName  - host name of the Controller (its C$ share is used)
REM    SourceFolder    - folder under \\Controller\c$ to copy (default: TestSetup)
REM    BinariesFolder  - folder under \\Controller\c$ to copy (default: TestBinaries)
REM
REM  Exit codes:
REM    0  success            2  payload copy failed     20/21 share unreachable
REM    1  usage / verify     3  binaries copy failed     10   directory create failed
REM ==============================================================

REM --- Arguments ---
if "%~1"=="" (
    echo [FAIL] Usage: Prepare-Agent.bat ControllerName [SourceFolder] [BinariesFolder]
    exit /b 1
)
set "CTRL=%~1"
set "SRC=%~2"
set "BIN_SRC=%~3"
if "%SRC%"==""     set "SRC=TestSetup"
if "%BIN_SRC%"=="" set "BIN_SRC=TestBinaries"

REM --- Paths (remote sources on the Controller's C$ ; fixed local destinations) ---
set "REMOTE_SRC=\\%CTRL%\c$\%SRC%"
set "REMOTE_BIN=\\%CTRL%\c$\%BIN_SRC%"
set "LOCAL_DEST=C:\TestSetup"
set "BINARIES=C:\TestBinaries"
set "RESULTS=C:\TestResults"
set "LOGDIR=C:\TestSetup\Logs"
set "LOG=%LOGDIR%\Prepare-Agent_%COMPUTERNAME%.log"

REM --- Robocopy options: recurse, 3 retries/5s, 16 threads, summary-only, tee to log ---
set "RC_OPTS=/E /R:3 /W:5 /MT:16 /NP /NFL /NDL /TEE"

echo ==================================================
echo [INFO] Agent      : %COMPUTERNAME%
echo [INFO] Controller : %CTRL%
echo [INFO] Payload    : %REMOTE_SRC%  -^>  %LOCAL_DEST%
echo [INFO] Binaries   : %REMOTE_BIN%  -^>  %BINARIES%
echo [INFO] Log        : %LOG%
echo ==================================================
echo.

REM -- Step 1: Create local directories --------------------------
echo [STEP] Creating local directories...
if not exist "%LOCAL_DEST%" ( mkdir "%LOCAL_DEST%" && echo [PASS] Created %LOCAL_DEST% || ( echo [FAIL] Could not create %LOCAL_DEST% & exit /b 10 ) ) else ( echo [PASS] Exists  %LOCAL_DEST% )
if not exist "%BINARIES%"   ( mkdir "%BINARIES%"   && echo [PASS] Created %BINARIES%   || ( echo [FAIL] Could not create %BINARIES%   & exit /b 10 ) ) else ( echo [PASS] Exists  %BINARIES% )
if not exist "%RESULTS%"    ( mkdir "%RESULTS%"    && echo [PASS] Created %RESULTS%    || ( echo [FAIL] Could not create %RESULTS%    & exit /b 10 ) ) else ( echo [PASS] Exists  %RESULTS% )
if not exist "%LOGDIR%"     ( mkdir "%LOGDIR%"     && echo [PASS] Created %LOGDIR%     || ( echo [FAIL] Could not create %LOGDIR%     & exit /b 10 ) ) else ( echo [PASS] Exists  %LOGDIR% )
if exist "%LOG%" del /q "%LOG%" >nul 2>&1
echo.

REM -- Step 2: Verify the Controller shares are reachable --------
echo [STEP] Checking Controller shares...
if not exist "%REMOTE_SRC%\" (
    echo [FAIL] Cannot reach payload share: %REMOTE_SRC%
    echo        Check the Controller name, that C$ is accessible, and credentials.
    exit /b 20
)
if not exist "%REMOTE_BIN%\" (
    echo [FAIL] Cannot reach binaries share: %REMOTE_BIN%
    exit /b 21
)
echo [PASS] Shares reachable
echo.

REM -- Step 3: Copy build payload (TestSetup) -------------------
echo [STEP] Copying payload from %REMOTE_SRC% ...
robocopy "%REMOTE_SRC%" "%LOCAL_DEST%" %RC_OPTS% /LOG+:"%LOG%"
set "RC=%ERRORLEVEL%"
if %RC% GEQ 8 ( echo [FAIL] Payload copy failed ^(robocopy code %RC%^) - see %LOG% & exit /b 2 )
echo [PASS] Payload copy complete ^(robocopy code %RC%^)
echo.

REM -- Step 4: Copy test binaries (~1200 files) ----------------
echo [STEP] Copying test binaries from %REMOTE_BIN% (this can take a while)...
robocopy "%REMOTE_BIN%" "%BINARIES%" %RC_OPTS% /LOG+:"%LOG%"
set "RC=%ERRORLEVEL%"
if %RC% GEQ 8 ( echo [FAIL] Binaries copy failed ^(robocopy code %RC%^) - see %LOG% & exit /b 3 )
echo [PASS] Binaries copy complete ^(robocopy code %RC%^)
echo.

REM -- Step 5: Unblock files (strip Mark-of-the-Web) -----------
echo [STEP] Unblocking files...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem 'C:\TestSetup','C:\TestBinaries','C:\TestResults' -Recurse -File -EA SilentlyContinue | ForEach-Object { Remove-Item $_.FullName -Stream Zone.Identifier -EA SilentlyContinue }"
echo [PASS] Files unblocked
echo.

REM -- Step 6: Verify -------------------------------------------
echo [STEP] Verifying...
set "OK=1"
if not exist "%LOCAL_DEST%" ( echo [FAIL] Missing %LOCAL_DEST% & set "OK=0" )
if not exist "%BINARIES%"   ( echo [FAIL] Missing %BINARIES%   & set "OK=0" )
if not exist "%RESULTS%"    ( echo [FAIL] Missing %RESULTS%    & set "OK=0" )

REM Confirm the binaries folder actually received files
set "BIN_COUNT=0"
for /f %%C in ('dir /a:-d /b /s "%BINARIES%" 2^>nul ^| find /c /v ""') do set "BIN_COUNT=%%C"
if "%BIN_COUNT%"=="0" ( echo [FAIL] No files found in %BINARIES% after copy & set "OK=0" ) else ( echo [PASS] %BIN_COUNT% file^(s^) present in %BINARIES% )

if "%OK%"=="1" (
    echo ==================================================
    echo [PASS] Agent %COMPUTERNAME% READY
    echo ==================================================
    exit /b 0
) else (
    echo ==================================================
    echo [FAIL] Agent %COMPUTERNAME% SETUP FAILED
    echo ==================================================
    exit /b 1
)
