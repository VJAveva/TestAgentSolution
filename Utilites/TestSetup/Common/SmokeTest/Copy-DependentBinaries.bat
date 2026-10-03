@echo off
setlocal EnableDelayedExpansion
REM ==============================================================
REM  Copy-DependentBinaries.bat  -  COMMON, one copy for all releases
REM  Location: C:\TestSetup\Common\SmokeTest\Copy-DependentBinaries.bat
REM  Called from the Controller as RunRemoteCommand.
REM
REM  Prepares a test agent for WAS Smoke Tests:
REM    Step 1: Environment setup (registry, COM registration)
REM    Step 2: License files
REM    Step 3: Test framework configs
REM    Step 4: OI-Server SIM config
REM    Step 5: ArchestrA framework DLLs
REM    Step 6: Alarm binaries
REM    Step 7: Warm Redundancy (GATEWAY + SIM OI-Server)  - OPT-IN, see -WarmRedundancy
REM
REM  DESIGN: Zero parenthesized if/else blocks anywhere in this
REM  file. cmd.exe misparses ')' in paths like "Program Files (x86)"
REM  as closing a parenthesized block, causing "\Common was unexpected"
REM  errors. All branching uses goto or single-line if...command.
REM
REM  Usage:
REM    Copy-DependentBinaries.bat <ExecBase> [-BinariesSource <path>]
REM                               [-WarmRedundancy] [-ListOnly]
REM
REM    <ExecBase>         execution base, e.g. C:\CISmokeTest
REM    -BinariesSource    root holding DEPENDENCIES\. Defaults to <ExecBase>.
REM                       Pipeline token: _BinariesSource
REM    -WarmRedundancy    run Step 7. Default OFF. Only the GATEWAY/SIM configs
REM                       under DEPENDENCIES\Gateway and \SIM make it meaningful,
REM                       and Step 7 costs ~80s of service stop/start waits.
REM                       Pipeline token: _WarmRedundancy
REM    -ListOnly          report every source as FOUND/MISSING and copy NOTHING.
REM                       Sources are classified NET (DevTransfer share),
REM                       DEP (binaries source) or LOCAL (resolved on the agent).
REM
REM  Environment variables of the same name are honoured when the switch is
REM  absent, so the pipeline can set _BinariesSource / _WarmRedundancy directly.
REM
REM  Exit codes: 0 ok  1 usage/precondition  2 one or more FAIL
REM ==============================================================

REM ---- Validate input ----
if "%~1"=="" echo [FAIL] Usage: Copy-DependentBinaries.bat ^<ExecBase^> [-BinariesSource path] [-WarmRedundancy] [-ListOnly]&exit /b 1

set "EXEC=%~1"
shift

REM ---- Defaults from environment (pipeline tokens), overridden by switches ----
set "BINSRC=%_BinariesSource%"
set "LISTONLY=0"
set "WARMRED=0"
if /I "%_WarmRedundancy%"=="true" set "WARMRED=1"
if "%_WarmRedundancy%"=="1" set "WARMRED=1"

:ParseArgs
if "%~1"=="" goto :ArgsDone
if /I "%~1"=="-BinariesSource" set "BINSRC=%~2"&shift&shift&goto :ParseArgs
if /I "%~1"=="-WarmRedundancy" set "WARMRED=1"&shift&goto :ParseArgs
if /I "%~1"=="-ListOnly" set "LISTONLY=1"&shift&goto :ParseArgs
echo [WARN] Ignoring unknown argument: %~1
shift
goto :ParseArgs
:ArgsDone

if not defined BINSRC set "BINSRC=%EXEC%"
if "%BINSRC%"=="" set "BINSRC=%EXEC%"

REM In -ListOnly the execution base is only a destination, so do not demand it exists.
if "!LISTONLY!"=="1" goto :SkipExecCheck
if not exist "%EXEC%\" echo [FAIL] Execution base not found: %EXEC%&exit /b 1
:SkipExecCheck

REM ---- Source paths ----
set "NET=\\dev\link\DevTransfer"
set "FW=C:\Program Files (x86)\ArchestrA\Framework\Bin"
set "ED=C:\Program Files (x86)\ArchestrA\SEditorsCommon"
set "LIC_POOL=C:\ProgramData\AVEVA\Licensing\LocalLicensePool"
set "ARCH_LIC=C:\Program Files (x86)\Common Files\ArchestrA\License"
set "OI_SIM=C:\ProgramData\Wonderware\OI-Server\$Operations Integration Supervisory Servers$\OI.SIM\OI.SIM"
set "DEP=%BINSRC%\DEPENDENCIES"

REM ---- Detect VS 2017 IDE path ----
set "IDE="
set "_chk1=C:\Program Files (x86)\Microsoft Visual Studio\2017\Enterprise\Common7\IDE\PublicAssemblies"
set "_chk2=C:\Program Files (x86)\Microsoft Visual Studio\2017\TestAgent\Common7\IDE\PublicAssemblies"
if exist "!_chk1!" set "IDE=C:\Program Files (x86)\Microsoft Visual Studio\2017\Enterprise\Common7\IDE"
if not defined IDE if exist "!_chk2!" set "IDE=C:\Program Files (x86)\Microsoft Visual Studio\2017\TestAgent\Common7\IDE"
REM -ListOnly is expected to run off-agent, where VS is absent; keep going and report LOCAL paths.
if not defined IDE if "!LISTONLY!"=="1" set "IDE=C:\Program Files (x86)\Microsoft Visual Studio\2017\Enterprise\Common7\IDE"
if not defined IDE echo [FAIL] VS 2017 Enterprise or TestAgent not found.&exit /b 1

set "TW=!IDE!\CommonExtensions\Microsoft\TestWindow"
set "PUB_ASM=!IDE!\PublicAssemblies"

REM ---- .NET Framework path for regasm ----
set "DOTNET=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"

REM ---- Log file ----
set "LOG=!EXEC!\CopyDependentBinaries.log"
if "!LISTONLY!"=="1" set "LOG=%TEMP%\CopyDependentBinaries-ListOnly.log"

REM ---- Counters ----
set /a PASS=0
set /a FAIL_COUNT=0
set /a SKIP=0
set /a DONE=0
REM ---- -ListOnly counters, split by source category ----
set /a LF_NET=0
set /a LM_NET=0
set /a LF_DEP=0
set /a LM_DEP=0
set /a LF_LOC=0
set /a LM_LOC=0

set "MODE=COPY"
if "!LISTONLY!"=="1" set "MODE=LIST ONLY (no files are copied)"
set "WRTXT=disabled"
if "!WARMRED!"=="1" set "WRTXT=ENABLED"

echo ==================================================
echo [INFO] COPY DEPENDENT BINARIES
echo [INFO] Mode       : !MODE!
echo [INFO] Machine    : %COMPUTERNAME%
echo [INFO] ExecBase   : %EXEC%
echo [INFO] Binaries   : %BINSRC%
echo [INFO] DEPENDENCIES: !DEP!
echo [INFO] Warm Redund: !WRTXT!
echo [INFO] VS IDE     : !IDE!
echo [INFO] Log        : !LOG!
echo ==================================================
echo.

echo ================================================== > "!LOG!"
echo  Copy-DependentBinaries - %DATE% %TIME%           >> "!LOG!"
echo  Machine: %COMPUTERNAME%   Mode: !MODE!            >> "!LOG!"
echo  Binaries: %BINSRC%                                >> "!LOG!"
echo ================================================== >> "!LOG!"
echo. >> "!LOG!"

goto :Main

REM ==============================================================
REM  SUBROUTINES - Zero parenthesized blocks
REM ==============================================================

:EnsureDir
if "!LISTONLY!"=="1" exit /b 0
set "_ed_path=%~1"
if exist "!_ed_path!\*" exit /b 0
mkdir "!_ed_path!" >nul 2>&1
if !ERRORLEVEL! NEQ 0 echo [WARN] Could not create folder: !_ed_path!&exit /b 0
echo [INFO] Created folder: !_ed_path!
exit /b 0

REM Classify a source path as NET / DEP / LOCAL. Result in _CAT.
REM Substring-replacement test: if removing the root changes the string, it contained it.
:Classify
set "_cl_src=%~1"
set "_CAT=LOCAL"
set "_cl_t=!_cl_src:%NET%=!"
if not "!_cl_t!"=="!_cl_src!" set "_CAT=NET"&exit /b 0
set "_cl_t=!_cl_src:%DEP%=!"
if not "!_cl_t!"=="!_cl_src!" set "_CAT=DEP"
exit /b 0

:CountFound
if "%~1"=="NET" set /a LF_NET+=1&exit /b 0
if "%~1"=="DEP" set /a LF_DEP+=1&exit /b 0
set /a LF_LOC+=1
exit /b 0

:CountMissing
if "%~1"=="NET" set /a LM_NET+=1&exit /b 0
if "%~1"=="DEP" set /a LM_DEP+=1&exit /b 0
set /a LM_LOC+=1
exit /b 0

:CopySingle
set /a DONE+=1
set "_cs_src=%~1"
set "_cs_dst=%~2"
set "_cs_lbl=%~3"
if "!LISTONLY!"=="1" goto :CopySingle_List
if not exist "!_cs_src!" goto :CopySingle_Skip
call :EnsureDir "!_cs_dst!"
echo F | xcopy "!_cs_src!" "!_cs_dst!" /Y /R >nul 2>&1
if !ERRORLEVEL! NEQ 0 goto :CopySingle_Fail
set /a PASS+=1
echo [PASS] !_cs_lbl!
exit /b 0
:CopySingle_List
call :Classify "!_cs_src!"
if not exist "!_cs_src!" goto :CopySingle_ListMissing
call :CountFound "!_CAT!"
echo [FOUND  ] !_CAT!  !_cs_lbl!
echo [FOUND  ] !_CAT!  !_cs_src! >> "!LOG!"
exit /b 0
:CopySingle_ListMissing
call :CountMissing "!_CAT!"
echo [MISSING] !_CAT!  !_cs_lbl!  --  !_cs_src!
echo [MISSING] !_CAT!  !_cs_src! >> "!LOG!"
exit /b 0
:CopySingle_Skip
set /a SKIP+=1
echo [SKIP] !_cs_lbl! -- source not found: !_cs_src!
exit /b 0
:CopySingle_Fail
set /a FAIL_COUNT+=1
echo [FAIL] !_cs_lbl! -- xcopy error copying !_cs_src!
exit /b 0

:CopyWild
set /a DONE+=1
set "_cw_src=%~1"
set "_cw_dst=%~2"
set "_cw_lbl=%~3"
for %%F in ("!_cw_src!") do set "_cw_srcdir=%%~dpF"
if "!LISTONLY!"=="1" goto :CopyWild_List
if not exist "!_cw_srcdir!" goto :CopyWild_Skip
call :EnsureDir "!_cw_dst!"
xcopy "!_cw_src!" "!_cw_dst!" /Y /R >nul 2>&1
if !ERRORLEVEL! NEQ 0 goto :CopyWild_Fail
set /a PASS+=1
if defined _cw_lbl if not "!_cw_lbl!"=="" echo [PASS] !_cw_lbl!
exit /b 0
:CopyWild_List
call :Classify "!_cw_src!"
if not exist "!_cw_srcdir!" goto :CopyWild_ListMissing
REM Folder exists - confirm the wildcard actually matches something.
set "_cw_hit="
for %%F in ("!_cw_src!") do if not defined _cw_hit set "_cw_hit=%%F"
if not defined _cw_hit goto :CopyWild_ListEmpty
call :CountFound "!_CAT!"
echo [FOUND  ] !_CAT!  !_cw_lbl!
echo [FOUND  ] !_CAT!  !_cw_src! >> "!LOG!"
exit /b 0
:CopyWild_ListEmpty
call :CountMissing "!_CAT!"
echo [MISSING] !_CAT!  !_cw_lbl!  --  no match: !_cw_src!
echo [MISSING] !_CAT!  no match: !_cw_src! >> "!LOG!"
exit /b 0
:CopyWild_ListMissing
call :CountMissing "!_CAT!"
echo [MISSING] !_CAT!  !_cw_lbl!  --  folder: !_cw_srcdir!
echo [MISSING] !_CAT!  folder: !_cw_srcdir! >> "!LOG!"
exit /b 0
:CopyWild_Skip
set /a SKIP+=1
if defined _cw_lbl if not "!_cw_lbl!"=="" echo [SKIP] !_cw_lbl! -- source folder not found: !_cw_srcdir!
exit /b 0
:CopyWild_Fail
set /a FAIL_COUNT+=1
if defined _cw_lbl if not "!_cw_lbl!"=="" echo [FAIL] !_cw_lbl! -- xcopy error from !_cw_src!
exit /b 0


REM ==============================================================
REM  MAIN LOGIC
REM ==============================================================
:Main

REM ==============================================================
REM  SECTION 1/7 : Environment Setup
REM ==============================================================
echo [STEP] 1/7 Environment setup ^(Registry + COM registration^)
if "!LISTONLY!"=="1" goto :Step1_ListNote

REM ---- Registry: TestAutomation LibraryPath (PASS/FAIL accounted) ----
set /a DONE+=1
echo [INFO] Creating registry key: HKCU\Software\Wonderware\TestAutomation >> "!LOG!"
REG ADD "HKCU\Software\Wonderware\TestAutomation" /v LibraryPath /t REG_SZ /d "!PUB_ASM!" /f >nul 2>&1
if !ERRORLEVEL! NEQ 0 goto :Step1_RegFail
set /a PASS+=1
echo [PASS] Registry: TestAutomation\LibraryPath
echo [PASS] Value: !PUB_ASM! >> "!LOG!"
goto :Step1_RegAsm

:Step1_RegFail
set /a FAIL_COUNT+=1
echo [FAIL] Registry: TestAutomation\LibraryPath
echo [FAIL] Registry key creation failed >> "!LOG!"
goto :Step1_RegAsm

:Step1_ListNote
echo [INFO ] LOCAL  registry + regasm steps are agent-local, not listed
goto :Step1_Done

:Step1_RegAsm
set /a DONE+=1
set "_ap_dll=!PUB_ASM!\AutomationProvider.dll"
set "_ap_tlb=!PUB_ASM!\AutomationProvider.tlb"

echo. >> "!LOG!"
echo [INFO] Locating AutomationProvider.dll >> "!LOG!"
echo ---------------------------------------- >> "!LOG!"

if exist "!_ap_dll!" goto :Step1_HaveDll

REM ---- Not in PublicAssemblies; recursively search ExecBase ----
echo [INFO] Not in PublicAssemblies - searching !EXEC! recursively
echo [INFO] Recursive search under !EXEC! for AutomationProvider.dll >> "!LOG!"

set "_found_dll="
for /f "delims=" %%F in ('dir /s /b /a:-d "!EXEC!\AutomationProvider.dll" 2^>nul') do if not defined _found_dll set "_found_dll=%%F"

if not defined _found_dll goto :Step1_NoDll

echo [INFO] Found: !_found_dll!
echo [INFO] Found DLL: !_found_dll! >> "!LOG!"

echo F | xcopy "!_found_dll!" "!_ap_dll!" /Y /R >nul 2>&1
if !ERRORLEVEL! NEQ 0 goto :Step1_CopyFail
echo [PASS] Copied AutomationProvider.dll -^> PublicAssemblies
echo [PASS] Copied !_found_dll! -^> !_ap_dll! >> "!LOG!"

REM ---- Sibling .tlb (optional; regasm will regen if absent) ----
for %%D in ("!_found_dll!") do set "_found_dir=%%~dpD"
set "_found_tlb=!_found_dir!AutomationProvider.tlb"
if not exist "!_found_tlb!" goto :Step1_HaveDll
echo F | xcopy "!_found_tlb!" "!_ap_tlb!" /Y /R >nul 2>&1
if !ERRORLEVEL! NEQ 0 goto :Step1_HaveDll
echo [PASS] Copied AutomationProvider.tlb -^> PublicAssemblies
echo [PASS] Copied !_found_tlb! -^> !_ap_tlb! >> "!LOG!"
goto :Step1_HaveDll

:Step1_CopyFail
set /a FAIL_COUNT+=1
echo [FAIL] Could not copy AutomationProvider.dll
echo [FAIL] xcopy failed: !_found_dll! -^> !_ap_dll! >> "!LOG!"
goto :Step1_Done

:Step1_HaveDll
if not exist "!DOTNET!\regasm.exe" goto :Step1_NoRegasm
"!DOTNET!\regasm.exe" "!_ap_dll!" /tlb:"!_ap_tlb!" /codebase >> "!LOG!" 2>&1
if !ERRORLEVEL! NEQ 0 goto :Step1_RegAsmFail
set /a PASS+=1
echo [PASS] Registered AutomationProvider.dll
goto :Step1_Done

:Step1_NoDll
set /a SKIP+=1
echo [SKIP] AutomationProvider.dll not in PublicAssemblies and no copy found under !EXEC!
echo [SKIP] Recursive search of !EXEC! yielded no AutomationProvider.dll >> "!LOG!"
goto :Step1_Done

:Step1_NoRegasm
set /a SKIP+=1
echo [SKIP] regasm.exe not found at !DOTNET!
goto :Step1_Done

:Step1_RegAsmFail
set /a FAIL_COUNT+=1
echo [FAIL] regasm registration failed
echo [FAIL] regasm returned error >> "!LOG!"

:Step1_Done
echo ---------------------------------------- >> "!LOG!"
echo.


REM ==============================================================
REM  SECTION 2/7 : License Files
REM ==============================================================
echo [STEP] 2/7 License files
call :EnsureDir "!LIC_POOL!"
call :EnsureDir "!ARCH_LIC!"

call :CopyWild "!NET!\AutomationConfig\License\*.*"  "!ARCH_LIC!\"  "Customer galaxy licenses"
call :CopySingle "!NET!\LegacyLicenses\Loc\NonFlex.loc"          "!LIC_POOL!\"  "NonFlex.loc"
call :CopySingle "!NET!\LegacyLicenses\Loc\Flex.loc"             "!LIC_POOL!\"  "Flex.loc"
echo.


REM ==============================================================
REM  SECTION 3/7 : Test Framework Configs (IDE + TestWindow)
REM ==============================================================
echo [STEP] 3/7 Test framework configs
call :EnsureDir "!TW!"

call :CopySingle "!NET!\AutomationConfig\MSTest.exe.config"       "!IDE!\"  "MSTest.exe.config -> IDE"
call :CopySingle "!NET!\AutomationConfig\MSTest.exe.config"       "!TW!\"   "MSTest.exe.config -> TestWindow"
call :CopySingle "!NET!\AutomationConfig\QTAgent32_40.exe.config"  "!IDE!\"  "QTAgent32_40.exe.config -> IDE"
call :CopySingle "!NET!\AutomationConfig\QTAgent32_40.exe.config"  "!TW!\"   "QTAgent32_40.exe.config -> TestWindow"
call :CopyWild   "!NET!\AutomationConfig\SecurityDrivenInferno\*.*" "!IDE!\"  "SecurityDrivenInferno -> IDE"
call :CopyWild   "!NET!\AutomationConfig\SecurityDrivenInferno\*.*" "!TW!\"   "SecurityDrivenInferno -> TestWindow"
echo.


REM ==============================================================
REM  SECTION 4/7 : OI-Server SIM Config
REM ==============================================================
echo [STEP] 4/7 OI-Server SIM config
call :EnsureDir "!OI_SIM!"
call :CopySingle "!DEP!\OISERVER\SIM.AAcfg"  "!OI_SIM!\"  "SIM.AAcfg"
echo.


REM ==============================================================
REM  SECTION 5/7 : ArchestrA Framework DLLs (IDE + TestWindow + ExecBase)
REM ==============================================================
echo [STEP] 5/7 ArchestrA framework DLLs
call :EnsureDir "!TW!"

call :CopySingle "!FW!\ScriptPackage.dll"       "!IDE!\"  "ScriptPackage.dll -> IDE"
call :CopySingle "!FW!\ScriptPackage.Net.dll"    "!IDE!\"  "ScriptPackage.Net.dll -> IDE"
call :CopySingle "!FW!\ScriptRuntime.dll"        "!IDE!\"  "ScriptRuntime.dll -> IDE"
call :CopySingle "!FW!\ScriptRuntime.Net.dll"    "!IDE!\"  "ScriptRuntime.Net.dll -> IDE"
call :CopyWild   "!FW!\ArchestrA.QuickScript*.dll"  "!IDE!\"  "QuickScript DLLs -> IDE"

call :CopySingle "!FW!\ScriptPackage.dll"       "!TW!\"  "ScriptPackage.dll -> TestWindow"
call :CopySingle "!FW!\ScriptPackage.Net.dll"    "!TW!\"  "ScriptPackage.Net.dll -> TestWindow"
call :CopySingle "!FW!\ScriptRuntime.dll"        "!TW!\"  "ScriptRuntime.dll -> TestWindow"
call :CopySingle "!FW!\ScriptRuntime.Net.dll"    "!TW!\"  "ScriptRuntime.Net.dll -> TestWindow"
call :CopyWild   "!FW!\ArchestrA.QuickScript*.dll"  "!TW!\"  "QuickScript DLLs -> TestWindow"

call :CopySingle "!FW!\ScriptPackage.dll"       "!EXEC!\"  "ScriptPackage.dll -> ExecBase"
call :CopySingle "!FW!\ScriptPackage.Net.dll"    "!EXEC!\"  "ScriptPackage.Net.dll -> ExecBase"
call :CopySingle "!FW!\ScriptRuntime.dll"        "!EXEC!\"  "ScriptRuntime.dll -> ExecBase"
call :CopySingle "!FW!\ScriptRuntime.Net.dll"    "!EXEC!\"  "ScriptRuntime.Net.dll -> ExecBase"
call :CopyWild   "!FW!\ArchestrA.QuickScript*.dll"  "!EXEC!\"  "QuickScript DLLs -> ExecBase"
echo.


REM ==============================================================
REM  SECTION 6/7 : Alarm Binaries
REM ==============================================================
echo [STEP] 6/7 Alarm binaries
call :EnsureDir "!TW!"
call :CopyWild   "!DEP!\ALARMS\*.dll"    "!TW!\"    "Alarm DLLs -> TestWindow"
call :CopySingle "!DEP!\ALARMS\wnal.dll"  "!EXEC!\"  "wnal.dll -> ExecBase"
echo.


REM ==============================================================
REM  SECTION 7/7 : Warm Redundancy (GATEWAY + SIM OI-Server)
REM    Opt-in. Only meaningful where DEPENDENCIES\Gateway and \SIM exist.
REM    Costs ~80s of service stop/start waits, so it is OFF by default.
REM ==============================================================
if "!WARMRED!"=="1" goto :Step7
echo [STEP] 7/7 Warm Redundancy -- SKIPPED ^(-WarmRedundancy not set^)
set /a SKIP+=1
goto :Step7_Done

:Step7
echo [STEP] 7/7 Warm Redundancy ^(GATEWAY + SIM OI-Server^)

set "OIS_ROOT=C:\ProgramData\Wonderware\OI-Server\$Operations Integration Supervisory Servers$"
set "GW_DST=!OIS_ROOT!\OI.GATEWAY\OI.GATEWAY"
set "SIM_DST=!OIS_ROOT!\OI.SIM\OI.SIM"
set "GW_SRC=!DEP!\Gateway"
set "SIM_SRC_DIR=!DEP!\SIM"
set "_LIC_BAT=!NET!\LegacyLicenses\Loc\CopyLicenseFile.bat"

if "!LISTONLY!"=="1" goto :Step7_List

REM ---- GATEWAY: stop, deploy, start ----
set /a DONE+=1
echo [INFO] Stopping GATEWAY service...
net stop GATEWAY >nul 2>&1
echo [INFO] Waiting 20 seconds...
timeout /T 20 /NOBREAK >nul

if not exist "!GW_SRC!" goto :Step7_GW_NoSrc
call :EnsureDir "!GW_DST!"
xcopy "!GW_SRC!\*.*" "!GW_DST!\" /Y /R >nul 2>&1
if !ERRORLEVEL! NEQ 0 goto :Step7_GW_Fail
set /a PASS+=1
echo [PASS] GATEWAY config deployed
goto :Step7_GW_Start

:Step7_GW_NoSrc
set /a SKIP+=1
echo [SKIP] GATEWAY source not found: !GW_SRC!
goto :Step7_GW_Start

:Step7_GW_Fail
set /a FAIL_COUNT+=1
echo [FAIL] GATEWAY config deploy -- xcopy error

:Step7_GW_Start
echo [INFO] Waiting 20 seconds...
timeout /T 20 /NOBREAK >nul
echo [INFO] Starting GATEWAY service...
net start GATEWAY >nul 2>&1

REM ---- SIM: stop, deploy, license, start ----
set /a DONE+=1
echo [INFO] Stopping SIM service...
net stop SIM >nul 2>&1
echo [INFO] Waiting 20 seconds...
timeout /T 20 /NOBREAK >nul

if not exist "!SIM_SRC_DIR!" goto :Step7_SIM_NoSrc
call :EnsureDir "!SIM_DST!"
xcopy "!SIM_SRC_DIR!\*.*" "!SIM_DST!\" /Y /R >nul 2>&1
if !ERRORLEVEL! NEQ 0 goto :Step7_SIM_Fail
set /a PASS+=1
echo [PASS] SIM config deployed
goto :Step7_SIM_License

:Step7_SIM_NoSrc
set /a SKIP+=1
echo [SKIP] SIM source not found: !SIM_SRC_DIR!
goto :Step7_SIM_License

:Step7_SIM_Fail
set /a FAIL_COUNT+=1
echo [FAIL] SIM config deploy -- xcopy error

:Step7_SIM_License
set /a DONE+=1
if not exist "!_LIC_BAT!" goto :Step7_SIM_NoLic
call "!_LIC_BAT!" >nul 2>&1
set /a PASS+=1
echo [PASS] License refresh via CopyLicenseFile.bat
goto :Step7_SIM_Start

:Step7_SIM_NoLic
set /a SKIP+=1
echo [SKIP] CopyLicenseFile.bat not found -- license not refreshed

:Step7_SIM_Start
echo [INFO] Starting SIM service...
net start SIM >nul 2>&1
echo [PASS] Warm Redundancy setup complete

echo [INFO] Final cooldown 20 seconds...
timeout /T 20 /NOBREAK >nul
goto :Step7_Done

:Step7_List
call :CopyWild   "!GW_SRC!\*.*"       "!GW_DST!\"   "GATEWAY config"
call :CopyWild   "!SIM_SRC_DIR!\*.*"  "!SIM_DST!\"  "SIM config"
call :CopySingle "!_LIC_BAT!"         "!TEMP!\"     "CopyLicenseFile.bat"

:Step7_Done
echo.


REM ==============================================================
REM  SUMMARY
REM ==============================================================
if "!LISTONLY!"=="1" goto :Summary_List

echo ==================================================
echo [INFO] SUMMARY  Done=!DONE!  Pass=!PASS!  Skip=!SKIP!  Fail=!FAIL_COUNT!
echo ==================================================
echo. >> "!LOG!"
echo SUMMARY Done=!DONE! Pass=!PASS! Skip=!SKIP! Fail=!FAIL_COUNT! >> "!LOG!"
if !FAIL_COUNT! GTR 0 exit /b 2
exit /b 0

:Summary_List
set /a _tot_found=!LF_NET!+!LF_DEP!+!LF_LOC!
set /a _tot_miss=!LM_NET!+!LM_DEP!+!LM_LOC!
echo ==================================================
echo [INFO] LIST ONLY SUMMARY  ^(nothing was copied^)
echo [INFO]   NET   share sources : !LF_NET! found, !LM_NET! missing
echo [INFO]   DEP   binaries src  : !LF_DEP! found, !LM_DEP! missing
echo [INFO]   LOCAL agent-resolved: !LF_LOC! found, !LM_LOC! missing
echo [INFO]   TOTAL               : !_tot_found! found, !_tot_miss! missing
echo ==================================================
echo [INFO] LOCAL entries resolve on the agent ^(VS 2017, ArchestrA Framework^);
echo [INFO] they are expected to be missing when listing from the controller.
echo. >> "!LOG!"
echo LIST SUMMARY NET=!LF_NET!/!LM_NET! DEP=!LF_DEP!/!LM_DEP! LOCAL=!LF_LOC!/!LM_LOC! >> "!LOG!"
REM Only NET and DEP gaps are actionable here.
set /a _actionable=!LM_NET!+!LM_DEP!
if !_actionable! GTR 0 exit /b 2
exit /b 0
