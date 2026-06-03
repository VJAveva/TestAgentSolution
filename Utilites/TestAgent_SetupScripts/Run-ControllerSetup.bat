@echo off
REM =====================================================================
REM  TestAgent CONTROLLER Node Setup - Batch Wrapper
REM  Right-click -> "Run as administrator"
REM  Target framework: .NET 10
REM =====================================================================
setlocal EnableExtensions
set "RC=1"

echo.
echo  Checking for Administrator privileges...
net session >nul 2>&1
if %errorlevel% neq 0 goto :no_admin

REM ============ EDIT THESE VALUES FOR YOUR ENVIRONMENT ============
set "CONTROLLER_PORT=5100"
set "INSTALL_DIR=C:\TestAgentSolution\Controller"
set "VOCABULARY_DIR=C:\TestControllerService"
REM To pre-register agents, list quoted IPs separated by commas, e.g.:
REM   set "AGENT_IPS="10.228.117.101","10.228.117.102""
set "AGENT_IPS="
REM ================================================================

set "PS=%~dp0Setup-ControllerNode.ps1"
set "LOG=%~dp0Setup-ControllerNode.log"

echo  Starting Controller setup...
echo.
pushd "%~dp0"
if not defined AGENT_IPS goto :run_no_agents

powershell -NoProfile -ExecutionPolicy Bypass -File "%PS%" -ControllerPort %CONTROLLER_PORT% -InstallDir "%INSTALL_DIR%" -VocabularyDir "%VOCABULARY_DIR%" -LogFile "%LOG%" -AgentIPs %AGENT_IPS%
set "RC=%ERRORLEVEL%"
goto :after_ps

:run_no_agents
powershell -NoProfile -ExecutionPolicy Bypass -File "%PS%" -ControllerPort %CONTROLLER_PORT% -InstallDir "%INSTALL_DIR%" -VocabularyDir "%VOCABULARY_DIR%" -LogFile "%LOG%"
set "RC=%ERRORLEVEL%"

:after_ps
popd

if "%RC%"=="0" goto :ok
if "%RC%"=="2" goto :warn
goto :err

:ok
echo.
echo  [SUCCESS] Controller setup completed with no warnings.
goto :end

:warn
echo.
echo  [WARNING] Controller setup completed with warnings - review the output above.
goto :end

:err
echo.
echo  [ERROR] Controller setup failed (exit code %RC%). See above and "%LOG%".
goto :end

:no_admin
echo.
echo  [ERROR] This script must be run as Administrator.
echo  Right-click this file and choose "Run as administrator".
goto :end

:end
echo.
pause
endlocal & exit /b %RC%
