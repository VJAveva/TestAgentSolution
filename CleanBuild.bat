@echo off
echo ============================================
echo  TestControllerGrpc - Full Clean Build
echo ============================================
echo.

echo Closing any VS instances holding locks...
taskkill /f /im devenv.exe 2>nul
timeout /t 2 /nobreak >nul

echo Deleting bin and obj folders...
for /d /r "%~dp0" %%d in (bin obj) do (
    if exist "%%d" (
        echo   Removing: %%d
        rd /s /q "%%d"
    )
)

echo Deleting .vs folder...
if exist "%~dp0.vs" rd /s /q "%~dp0.vs"

echo Clearing NuGet cache for this solution...
dotnet nuget locals temp -c 2>nul

echo.
echo ============================================
echo  Clean complete. Now rebuild in VS:
echo  Build ^> Rebuild Solution  (Ctrl+Shift+B)
echo ============================================
pause
