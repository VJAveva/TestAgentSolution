@echo off
REM =====================================================================
REM  TestAgent AGENT Node Setup - Batch Wrapper
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
set "AGENT_PORT=5200"
set "AGENT_NAME=%COMPUTERNAME%"
set "CONTROLLER_ADDRESS=http://JVGR22:5100"
set "INSTALL_DIR=C:\TestAgentSolution\Agent"
REM Set INSTALL_AS_SERVICE=1 only after the binaries are copied to INSTALL_DIR.
set "INSTALL_AS_SERVICE="
REM ================================================================

set "PS=%~dp0Setup-AgentNode.ps1"
set "LOG=%~dp0Setup-AgentNode.log"

set "SVC_ARG="
if defined INSTALL_AS_SERVICE set "SVC_ARG=-InstallAsService"

echo  Starting Agent setup...
echo.
pushd "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%PS%" -AgentPort %AGENT_PORT% -AgentName "%AGENT_NAME%" -ControllerAddress "%CONTROLLER_ADDRESS%" -InstallDir "%INSTALL_DIR%" -LogFile "%LOG%" %SVC_ARG%
set "RC=%ERRORLEVEL%"
popd

if "%RC%"=="0" goto :ok
if "%RC%"=="2" goto :warn
goto :err

:ok
echo.
echo  [SUCCESS] Agent setup completed with no warnings.
goto :end

:warn
echo.
echo  [WARNING] Agent setup completed with warnings - review the output above.
goto :end

:err
echo.
echo  [ERROR] Agent setup failed (exit code %RC%). See above and "%LOG%".
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
