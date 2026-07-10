@echo off
setlocal EnableExtensions
REM ==============================================================
REM  RevertHyperV.bat  (launcher, v1.0)
REM  Put this in C:\TestSetup\RevertAgents\ next to the .ps1 and
REM  point the Controller pipeline at THIS file instead of the ps1:
REM
REM    cmd /c C:\TestSetup\RevertAgents\RevertHyperV.bat HCPCIAGENT7
REM    cmd /c C:\TestSetup\RevertAgents\RevertHyperV.bat HCPCIAGENT7,HCPCIAGENT8,HCPCIAGENT9
REM    cmd /c C:\TestSetup\RevertAgents\RevertHyperV.bat AG1,AG2 -Checkpoint SP2026_BASELINE
REM
REM  NOTE: pass multiple VM names as ONE comma-separated argument
REM  with NO spaces around the commas, so cmd keeps it as a single
REM  token and PowerShell binds it to the -VMs string array.
REM
REM  Self-locates the script, verifies it exists, prints a clear
REM  diagnostic if not, and runs it with -File / Bypass so the real
REM  script output and exit code come through.
REM
REM  Exit codes: 5 = script file not found, otherwise the ps1's code
REM ==============================================================

set "HERE=%~dp0"
set "PS1="

REM Accept either the old or the new script name (first match wins)
for %%N in ("Revert-HyperVMachines.ps1" "RevertHyperV.ps1" "RevertHyperVMachines.ps1") do (
    if not defined PS1 if exist "%HERE%%%~N" set "PS1=%HERE%%%~N"
)

if not defined PS1 (
    echo [FAIL] No Hyper-V revert script found in %HERE%
    echo        Expected one of: Revert-HyperVMachines.ps1 / RevertHyperV.ps1
    echo        Folder contents:
    dir /b "%HERE%" 2>nul
    exit /b 5
)

echo [INFO] Launcher : %~f0
echo [INFO] Script   : %PS1%
echo [INFO] Args     : %*

powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%PS1%" %*
set "RC=%ERRORLEVEL%"

echo [INFO] Script exit code: %RC%
exit /b %RC%
