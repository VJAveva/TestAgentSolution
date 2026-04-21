@echo off
REM ============================================================================
REM  deploy-webapi.bat
REM  Builds the React frontend, publishes TestController.WebApi, and deploys
REM  the IIS-ready artifact to a target server node.
REM
REM  Usage:
REM    deploy-webapi.bat <TargetNode> [IISSiteName] [PublishDir]
REM
REM  Examples:
REM    deploy-webapi.bat WEBSERVER01
REM    deploy-webapi.bat WEBSERVER01 TestControllerWeb
REM    deploy-webapi.bat WEBSERVER01 TestControllerWeb C:\Deploy\webapi
REM ============================================================================
setlocal enabledelayedexpansion

set "TARGET_NODE=%~1"
set "IIS_SITE_NAME=%~2"
set "PUBLISH_DIR=%~3"

if "%TARGET_NODE%"=="" (
    echo ERROR: Target node name is required.
    echo.
    echo Usage: deploy-webapi.bat ^<TargetNode^> [IISSiteName] [PublishDir]
    echo   TargetNode   - Machine name or IP where IIS is running
    echo   IISSiteName  - IIS site name (default: TestControllerWeb)
    echo   PublishDir   - Local publish output folder (default: publish\webapi)
    exit /b 1
)

if "%IIS_SITE_NAME%"=="" set "IIS_SITE_NAME=TestControllerWeb"
if "%PUBLISH_DIR%"=="" set "PUBLISH_DIR=%~dp0..\publish\webapi"

set "SOLUTION_ROOT=%~dp0.."
set "WEBCLIENT_DIR=%SOLUTION_ROOT%\TestController.WebClient"
set "WEBAPI_DIR=%SOLUTION_ROOT%\TestController.WebApi"
set "WEBAPI_CSPROJ=%WEBAPI_DIR%\TestController.WebApi.csproj"
set "REMOTE_DEPLOY=\\%TARGET_NODE%\C$\inetpub\%IIS_SITE_NAME%"

echo ============================================================================
echo  TestController.WebApi IIS Deployment
echo ============================================================================
echo  Target Node   : %TARGET_NODE%
echo  IIS Site      : %IIS_SITE_NAME%
echo  Publish Dir   : %PUBLISH_DIR%
echo  Remote Deploy : %REMOTE_DEPLOY%
echo ============================================================================
echo.

REM -- Step 1: Build React frontend --------------------------------------------
echo [1/5] Building React frontend...
pushd "%WEBCLIENT_DIR%"
call npm install
if errorlevel 1 (
    echo ERROR: npm install failed.
    popd
    exit /b 1
)
call npm run build
if errorlevel 1 (
    echo ERROR: React build failed.
    popd
    exit /b 1
)
popd
echo       React frontend built successfully.
echo.

REM -- Step 2: Copy React dist to WebApi wwwroot --------------------------------
echo [2/5] Copying React build output to WebApi\wwwroot...
set "WWWROOT=%WEBAPI_DIR%\wwwroot"
if exist "%WWWROOT%" rmdir /s /q "%WWWROOT%"
mkdir "%WWWROOT%"
xcopy "%WEBCLIENT_DIR%\dist\*" "%WWWROOT%\" /E /I /Q /Y >nul
if errorlevel 1 (
    echo ERROR: Failed to copy React build output.
    exit /b 1
)
echo       Copied to wwwroot.
echo.

REM -- Step 3: Publish .NET WebApi ---------------------------------------------
echo [3/5] Publishing TestController.WebApi...
dotnet publish "%WEBAPI_CSPROJ%" ^
    -c Release ^
    --no-self-contained ^
    -o "%PUBLISH_DIR%"
if errorlevel 1 (
    echo ERROR: dotnet publish failed.
    exit /b 1
)
echo       Published successfully.
echo.

REM -- Step 4: Stop IIS site and deploy to remote node -------------------------
echo [4/5] Deploying to %REMOTE_DEPLOY%...

REM Stop the IIS site on the remote node to unlock files
echo       Stopping IIS site '%IIS_SITE_NAME%' on %TARGET_NODE%...
set "LOCAL_NAME=%COMPUTERNAME%"
if /I "%TARGET_NODE%"=="%LOCAL_NAME%" (
    powershell -NoProfile -Command "Import-Module WebAdministration -ErrorAction SilentlyContinue; Stop-WebSite -Name '%IIS_SITE_NAME%' -ErrorAction SilentlyContinue" 2>nul
) else (
    powershell -NoProfile -Command "Invoke-Command -ComputerName '%TARGET_NODE%' -ScriptBlock { Import-Module WebAdministration -ErrorAction SilentlyContinue; Stop-WebSite -Name '%IIS_SITE_NAME%' -ErrorAction SilentlyContinue }" 2>nul
)

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

REM -- Step 5: Start IIS site --------------------------------------------------
echo [5/5] Starting IIS site '%IIS_SITE_NAME%' on %TARGET_NODE%...
if /I "%TARGET_NODE%"=="%LOCAL_NAME%" (
    powershell -NoProfile -Command "Import-Module WebAdministration -ErrorAction SilentlyContinue; Start-WebSite -Name '%IIS_SITE_NAME%' -ErrorAction SilentlyContinue"
) else (
    powershell -NoProfile -Command "Invoke-Command -ComputerName '%TARGET_NODE%' -ScriptBlock { Import-Module WebAdministration -ErrorAction SilentlyContinue; Start-WebSite -Name '%IIS_SITE_NAME%' -ErrorAction SilentlyContinue }"
)
echo       Site started.
echo.

echo ============================================================================
echo  Deployment complete.
echo  Site URL: http://%TARGET_NODE%/
echo.
echo  IIS Setup (if first time):
echo    1. Install ASP.NET Core Hosting Bundle on %TARGET_NODE%
echo    2. Create IIS site '%IIS_SITE_NAME%' pointing to C:\inetpub\%IIS_SITE_NAME%
echo    3. Set App Pool to 'No Managed Code'
echo    4. Enable WebSockets in IIS (for SignalR)
echo    5. Edit appsettings.Production.json on the server for environment config
echo ============================================================================
endlocal
