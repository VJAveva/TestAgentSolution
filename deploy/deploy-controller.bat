@echo off
REM ============================================================================
REM  deploy-controller.bat
REM  Builds and deploys the TestControllerGrpc (WPF + gRPC server) to a
REM  target node.  Run from the solution root directory.
REM
REM  Usage:
REM    deploy-controller.bat <TargetNode> [GrpcPort] [PublishDir]
REM
REM  Examples:
REM    deploy-controller.bat CONTROLLER01
REM    deploy-controller.bat CONTROLLER01 5100
REM    deploy-controller.bat CONTROLLER01 5100 C:\Deploy\Controller
REM ============================================================================
setlocal enabledelayedexpansion

set "TARGET_NODE=%~1"
set "GRPC_PORT=%~2"
set "PUBLISH_DIR=%~3"

if "%TARGET_NODE%"=="" (
    echo ERROR: Target node name is required.
    echo.
    echo Usage: deploy-controller.bat ^<TargetNode^> [GrpcPort] [PublishDir]
    echo   TargetNode  - Machine name or IP where the Controller will run
    echo   GrpcPort    - gRPC listening port (default: 5100)
    echo   PublishDir  - Local publish output folder (default: publish\controller)
    exit /b 1
)

if "%GRPC_PORT%"=="" set "GRPC_PORT=5100"
if "%PUBLISH_DIR%"=="" set "PUBLISH_DIR=%~dp0..\publish\controller"

set "PROJECT_DIR=%~dp0..\TestControllerGrpc"
set "PROJECT_FILE=%PROJECT_DIR%\TestControllerGrpc.csproj"
set "REMOTE_DEPLOY=\\%TARGET_NODE%\C$\TestControllerService"

echo ============================================================================
echo  TestControllerGrpc Deployment
echo ============================================================================
echo  Target Node   : %TARGET_NODE%
echo  gRPC Port     : %GRPC_PORT%
echo  Publish Dir   : %PUBLISH_DIR%
echo  Remote Deploy : %REMOTE_DEPLOY%
echo ============================================================================
echo.

REM ?? Step 1: Publish ????????????????????????????????????????????????????????
echo [1/4] Publishing TestControllerGrpc...
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

REM ?? Step 2: Patch appsettings.json with target port ????????????????????????
echo [2/4] Patching appsettings.json (ControllerGrpcPort=%GRPC_PORT%)...
set "APPSETTINGS=%PUBLISH_DIR%\appsettings.json"
if exist "%APPSETTINGS%" (
    powershell -NoProfile -Command ^
        "$json = Get-Content '%APPSETTINGS%' -Raw | ConvertFrom-Json; " ^
        "$json.ControllerGrpcPort = %GRPC_PORT%; " ^
        "$json | ConvertTo-Json -Depth 10 | Set-Content '%APPSETTINGS%' -Encoding UTF8"
    echo       appsettings.json patched.
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
echo [4/4] Launching Controller on %TARGET_NODE%...
echo       NOTE: The Controller is a WPF application and must run in an
echo             interactive desktop session.  If you are deploying to a
echo             remote server, start it manually or via a scheduled task
echo             that runs in an interactive session.
echo.

REM Attempt local launch if deploying to the current machine
set "LOCAL_NAME=%COMPUTERNAME%"
if /I "%TARGET_NODE%"=="%LOCAL_NAME%" (
    echo       Detected local deployment - starting Controller...
    start "" "%REMOTE_DEPLOY%\TestControllerGrpc.exe"
    echo       Controller started.
) else (
    echo       Remote deployment detected.
    echo       To start on %TARGET_NODE%, run:
    echo         \\%TARGET_NODE%\C$\TestControllerService\TestControllerGrpc.exe
)

echo.
echo ============================================================================
echo  Deployment complete.
echo  Controller endpoint: http://%TARGET_NODE%:%GRPC_PORT%
echo ============================================================================
endlocal
