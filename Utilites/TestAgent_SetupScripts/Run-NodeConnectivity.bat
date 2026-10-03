@echo off
REM =====================================================================
REM  TestAgent Node Connectivity Diagnostic - Batch Wrapper
REM  Replaces Run-NetworkDiagnostic.bat (archived).
REM  Right-click -> "Run as administrator" for the firewall checks.
REM  Read-only: changes nothing on any node.
REM  Target framework: .NET 10 (runtime 10.0.12 or newer)
REM =====================================================================
setlocal EnableExtensions
set "RC=1"

REM ============ EDIT THESE VALUES FOR YOUR ENVIRONMENT ============
set "CONTROLLER=JVGR22"
REM Leave NODES empty to use deploy\fleet-inventory.json, or list names:
REM   set "NODES=JVGR1,WARMGR"
set "NODES="
set "REPORT=%~dp0Test-NodeConnectivity.json"
REM Set TCSETUP_NOPAUSE=1 in the environment to skip the final pause.
REM ================================================================

set "PS=%~dp0Test-NodeConnectivity.ps1"

echo.
echo  Running node connectivity diagnostic...
echo.
pushd "%~dp0"
if not defined NODES goto :run_all

powershell -NoProfile -ExecutionPolicy Bypass -File "%PS%" -Controller "%CONTROLLER%" -ReportPath "%REPORT%" -Nodes %NODES%
set "RC=%ERRORLEVEL%"
goto :after_ps

:run_all
powershell -NoProfile -ExecutionPolicy Bypass -File "%PS%" -Controller "%CONTROLLER%" -ReportPath "%REPORT%"
set "RC=%ERRORLEVEL%"

:after_ps
popd

if "%RC%"=="0" goto :ok
goto :err

:ok
echo.
echo  [SUCCESS] All checks passed.
goto :end

:err
echo.
echo  [ATTENTION] One or more checks FAILED - see the table above.
echo  Report: %REPORT%
goto :end

:end
echo.
if not "%TCSETUP_NOPAUSE%"=="1" pause
endlocal & exit /b %RC%
