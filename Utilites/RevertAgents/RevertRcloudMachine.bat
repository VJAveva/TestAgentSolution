@echo off
setlocal EnableExtensions
REM ==============================================================
REM  RevertRcloudMachine.bat  (launcher, v1.1)
REM  Put this in C:\TestSetup\RevertAgents\ next to the .ps1 and
REM  point the Controller pipeline at THIS file instead of the ps1.
REM
REM  Usage:   RevertRcloudMachine.bat OrgName Machines [User Password]
REM    OrgName   vCloud organisation, e.g. AppServerPool2
REM    Machines  one VM, or several as "VM1,VM2" (quoted)
REM    User / Password  optional; omit them and set RCLOUD_USER / RCLOUD_PASSWORD
REM                     on the controller instead (never put the password in a pipeline)
REM
REM  Examples:
REM    cmd /c C:\TestSetup\RevertAgents\RevertRcloudMachine.bat AppServerPool2 warmhist
REM    cmd /c C:\TestSetup\RevertAgents\RevertRcloudMachine.bat AppServerPool2 "warmhist,warmgr"
REM
REM  Why: calling "powershell script.ps1 args" without -File makes
REM  PowerShell parse the path as a COMMAND. If the file is missing
REM  or renamed you get a cryptic CommandNotFoundException. This
REM  launcher self-locates the script, verifies it exists, prints a
REM  clear diagnostic if not, and runs it with -File / Bypass so the
REM  real script output and exit code come through.
REM
REM  Exit codes: 5 = script file not found, otherwise the ps1's code
REM ==============================================================

set "HERE=%~dp0"
set "PS1="

if "%~2"=="" (
    echo [FAIL] Usage: RevertRcloudMachine.bat OrgName Machines [User Password]
    echo        Example: RevertRcloudMachine.bat AppServerPool2 "warmhist,warmgr"
    exit /b 1
)

REM Accept either the old or the new script name (first match wins)
for %%N in ("Revert-RcloudMachines.ps1" "RevertRcloudMachine.ps1" "RevertRcloudMachines.ps1") do (
    if not defined PS1 if exist "%HERE%%%~N" set "PS1=%HERE%%%~N"
)

if not defined PS1 (
    echo [FAIL] No revert script found in %HERE%
    echo        Expected one of: Revert-RcloudMachines.ps1 / RevertRcloudMachine.ps1
    echo        Folder contents:
    dir /b "%HERE%" 2>nul
    exit /b 5
)

echo [INFO] Launcher : %~f0
echo [INFO] Script   : %PS1%
REM NEVER echo %* here. Args 3 and 4 are the vCloud user and password, and this stdout is
REM relayed by the agent into the controller's execution log, where it persists on disk.
echo [INFO] Args     : %1 %2 (user and password redacted)

powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%PS1%" %*
set "RC=%ERRORLEVEL%"

echo [INFO] Script exit code: %RC%
exit /b %RC%
