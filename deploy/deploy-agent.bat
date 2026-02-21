@echo off
REM ============================================================================
REM  deploy-agent.bat
REM  Builds and deploys the TestAgentGrpc (WinForms tray + gRPC server/client)
REM  to one or more agent nodes.  Run from the solution root directory.
REM
REM  Usage:
REM    deploy-agent.bat <AgentNode> <ControllerNode> [AgentPort] [ControllerPort] [PublishDir]
REM
REM  Examples:
REM    deploy-agent.bat AGENT01 CONTROLLER01
REM    deploy-agent.bat AGENT01 CONTROLLER01 5200 5100
REM    deploy-agent.bat AGENT01 CONTROLLER01 5200 5100 C:\Deploy\Agent
REM ============================================================================
setlocal enabledelayedexpansion

set "AGENT_NODE=%~1"
set "CONTROLLER_NODE=%~2"
set "AGENT_PORT=%~3"
set "CONTROLLER_PORT=%~4"
set "PUBLISH_DIR=%~5"

if "%AGENT_NODE%"=="" (
    echo ERROR: Agent node name is required.
    echo.
    echo Usage: deploy-agent.bat ^<AgentNode^> ^<ControllerNode^> [AgentPort] [ControllerPort] [PublishDir]
    echo   AgentNode       - Machine name or IP where the Agent will run
    echo   ControllerNode  - Machine name or IP of the Controller
    echo   AgentPort       - Agent gRPC listening port (default: 5200)
    echo   ControllerPort  - Controller gRPC port (default: 5100)
    echo   PublishDir      - Local publish output folder (default: publish\agent)
    exit /b 1
)
if "%CONTROLLER_NODE%"=="" (
    echo ERROR: Controller node name is required.
    echo.
    echo Usage: deploy-agent.bat ^<AgentNode^> ^<ControllerNode^> [AgentPort] [ControllerPort] [PublishDir]
    exit /b 1
)

if "%AGENT_PORT%"=="" set "AGENT_PORT=5200"
if "%CONTROLLER_PORT%"=="" set "CONTROLLER_PORT=5100"
if "%PUBLISH_DIR%"=="" set "PUBLISH_DIR=%~dp0..\publish\agent"

set "PROJECT_DIR=%~dp0..\TestAgentGrpc"
set "PROJECT_FILE=%PROJECT_DIR%\TestAgentGrpc.csproj"
set "REMOTE_DEPLOY=\\%AGENT_NODE%\C$\TestAgentService"

echo ============================================================================
echo  TestAgentGrpc Deployment
echo ============================================================================
echo  Agent Node        : %AGENT_NODE%
echo  Agent gRPC Port   : %AGENT_PORT%
echo  Controller Node   : %CONTROLLER_NODE%
echo  Controller Port   : %CONTROLLER_PORT%
echo  Publish Dir       : %PUBLISH_DIR%
echo  Remote Deploy     : %REMOTE_DEPLOY%
echo ============================================================================
echo.

REM ?? Step 1: Publish ????????????????????????????????????????????????????????
echo [1/4] Publishing TestAgentGrpc...
dotnet publish "%PROJECT_FILE%" ^
    -c Release ^
    -r win-x64 ^
    --self-contained false ^
    -o "%PUBLISH_DIR%" ^
    /p:PublishSingleFile=false
if errorlevel 1 (
    echo ERROR: dotnet publish failed.
    exit /b 1
)
echo       Published successfully.
echo.

REM ?? Step 2: Patch appsettings.json with agent/controller config ????????????
echo [2/4] Patching appsettings.json...
set "APPSETTINGS=%PUBLISH_DIR%\appsettings.json"
if exist "%APPSETTINGS%" (
    powershell -NoProfile -Command ^
        "$json = Get-Content '%APPSETTINGS%' -Raw | ConvertFrom-Json; " ^
        "$json.AgentSettings.AgentName = '%AGENT_NODE%'; " ^
        "$json.AgentSettings.GrpcPort = %AGENT_PORT%; " ^
        "$json.AgentSettings.ControllerAddress = 'http://%CONTROLLER_NODE%:%CONTROLLER_PORT%'; " ^
        "$json.AgentSettings.AgentEndpoint = 'http://%AGENT_NODE%:%AGENT_PORT%'; " ^
        "$json | ConvertTo-Json -Depth 10 | Set-Content '%APPSETTINGS%' -Encoding UTF8"
    echo       appsettings.json patched:
    echo         AgentName         = %AGENT_NODE%
    echo         GrpcPort          = %AGENT_PORT%
    echo         ControllerAddress = http://%CONTROLLER_NODE%:%CONTROLLER_PORT%
    echo         AgentEndpoint     = http://%AGENT_NODE%:%AGENT_PORT%
) else (
    echo       WARNING: appsettings.json not found in publish output.
)
echo.

REM ?? Step 3: Copy to remote node ????????????????????????????????????????????
echo [3/4] Deploying to %REMOTE_DEPLOY%...
if not exist "%REMOTE_DEPLOY%" (
    mkdir "%REMOTE_DEPLOY%" 2>nul
    if errorlevel 1 (
        echo ERROR: Cannot create remote directory %REMOTE_DEPLOY%.
        echo        Verify network access and admin share availability.
        exit /b 1
    )
)
robocopy "%PUBLISH_DIR%" "%REMOTE_DEPLOY%" /MIR /NJH /NJS /NDL /NP /NFL
if %errorlevel% GEQ 8 (
    echo ERROR: Robocopy failed with exit code %errorlevel%.
    exit /b 1
)
echo       Files deployed.
echo.

REM ?? Step 4: Optionally launch on the remote node ???????????????????????????
echo [4/4] Launching Agent on %AGENT_NODE%...
echo       NOTE: The Agent runs a WinForms system-tray app and must run in
echo             an interactive desktop session.

set "LOCAL_NAME=%COMPUTERNAME%"
if /I "%AGENT_NODE%"=="%LOCAL_NAME%" (
    echo       Detected local deployment - starting Agent...
    start "" "%REMOTE_DEPLOY%\TestAgentGrpc.exe"
    echo       Agent started.
) else (
    echo       Remote deployment detected.
    echo       To start on %AGENT_NODE%, run:
    echo         \\%AGENT_NODE%\C$\TestAgentService\TestAgentGrpc.exe
)

echo.
echo ============================================================================
echo  Deployment complete.
echo  Agent endpoint      : http://%AGENT_NODE%:%AGENT_PORT%
echo  Controller endpoint : http://%CONTROLLER_NODE%:%CONTROLLER_PORT%
echo ============================================================================
endlocal
