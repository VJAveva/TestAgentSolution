@echo off
REM =====================================================================
REM  TestAgent Network Diagnostic - run from ANY machine
REM  Does NOT require Administrator.
REM    STEP 1: TCP / firewall / DNS reachability
REM    STEP 2: HTTP/2 self-diagnose (gRPC HTTP_1_1_REQUIRED / 0xd)
REM =====================================================================
setlocal EnableExtensions

REM ===================== EDIT THESE VALUES =====================
set "CONTROLLER_IP=10.48.190.248"
set "AGENT_IPS="10.48.190.213""

REM --- HTTP/2 self-diagnose target (address the Controller dials) ---
set "AGENT_HOST=localhost"
set "AGENT_PORT=5200"
set "AGENT_INSTALL_DIR=C:\TestAgentSolution\Agent"
REM =============================================================

set "NET_PS=%~dp0Test-TestAgentNetwork.ps1"
set "H2_PS=%~dp0Diagnose-Http2Protocol.ps1"
set "STEP1_RC=0"
set "STEP2_RC=0"

pushd "%~dp0"

echo.
echo ============================================================
echo  STEP 1 : Network reachability (TCP / firewall / DNS)
echo ============================================================
powershell -NoProfile -ExecutionPolicy Bypass -File "%NET_PS%" -ControllerIP %CONTROLLER_IP% -AgentIPs %AGENT_IPS% -ExportReport
set "STEP1_RC=%ERRORLEVEL%"
if not "%STEP1_RC%"=="0" echo  [WARN] Step 1 reported connectivity failures. Step 2 still runs.

echo.
echo ============================================================
echo  STEP 2 : HTTP/2 Protocol Self-Diagnose
echo           Detects the gRPC HTTP_1_1_REQUIRED condition (0xd)
echo ============================================================
powershell -NoProfile -ExecutionPolicy Bypass -File "%H2_PS%" -AgentHost %AGENT_HOST% -AgentPort %AGENT_PORT% -AgentInstallDir "%AGENT_INSTALL_DIR%"
set "STEP2_RC=%ERRORLEVEL%"

popd

echo.
echo ============================================================
echo  OVERALL RESULT
echo ============================================================
if "%STEP1_RC%"=="0" echo  Step 1 (reachability) : PASS
if not "%STEP1_RC%"=="0" echo  Step 1 (reachability) : FAIL
if "%STEP2_RC%"=="0" goto :h2_ok
if "%STEP2_RC%"=="3" goto :h2_required
if "%STEP2_RC%"=="4" goto :h2_dead
goto :h2_untested

:h2_ok
echo  Step 2 (HTTP/2)       : PASS - endpoint is gRPC-ready
goto :final

:h2_required
echo  Step 2 (HTTP/2)       : FAIL - HTTP_1_1_REQUIRED (Kestrel not on HTTP/2)
echo                          Fix: see Option A printed above.
goto :final

:h2_dead
echo  Step 2 (HTTP/2)       : FAIL - no HTTP response (agent not running / TLS / wrong port)
goto :final

:h2_untested
echo  Step 2 (HTTP/2)       : NOT TESTED - port unreachable or curl.exe missing
goto :final

:final
echo.
pause
endlocal & exit /b %STEP1_RC%
