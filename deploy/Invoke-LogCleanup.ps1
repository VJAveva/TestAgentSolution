<#
.SYNOPSIS
    Cleans up old log and audit files based on retention policy.

.DESCRIPTION
    Centralized log retention and cleanup for TestAgentSolution components:
    - WebApi logs (crash dumps, request logs)
    - Agent audit logs
    - Execution session persistence files
    - Test result archives

    Retention policy defaults:
    - Application logs: 30 days
    - Audit logs: 90 days
    - Crash dumps: 60 days
    - Test results: 90 days

.PARAMETER LogDirectories
    Array of log directory paths to clean. Default: common deployment paths.

.PARAMETER LogRetentionDays
    Days to retain application log files. Default: 30

.PARAMETER AuditRetentionDays
    Days to retain audit log files. Default: 90

.PARAMETER CrashRetentionDays
    Days to retain crash dump files. Default: 60

.PARAMETER ResultsRetentionDays
    Days to retain test result archives. Default: 90

.PARAMETER WhatIf
    Show what would be deleted without actually deleting.

.EXAMPLE
    .\Invoke-LogCleanup.ps1
    .\Invoke-LogCleanup.ps1 -WhatIf
    .\Invoke-LogCleanup.ps1 -LogRetentionDays 14 -AuditRetentionDays 60
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [string[]]$LogDirectories = @(
        "C:\TestAgentSolution\Logs",
        "C:\TestControllerService\Logs",
        "C:\TestAgent\Logs"
    ),
    [int]$LogRetentionDays = 30,
    [int]$AuditRetentionDays = 90,
    [int]$CrashRetentionDays = 60,
    [int]$ResultsRetentionDays = 90,
    [string]$ResultsRootPath = "C:\TestResults"
)

$ErrorActionPreference = "Continue"
$startTime = Get-Date
$totalDeleted = 0
$totalFreedBytes = 0
$deletionLog = @()

function Write-Status {
    param([string]$Message, [string]$Level = "INFO")
    $ts = Get-Date -Format "HH:mm:ss"
    $color = switch ($Level) {
        "OK"   { "Green" }
        "WARN" { "Yellow" }
        "FAIL" { "Red" }
        "STEP" { "Cyan" }
        default { "White" }
    }
    Write-Host "[$ts] [$Level] $Message" -ForegroundColor $color
}

function Remove-OldFiles {
    param(
        [string]$Path,
        [string]$FilePattern,
        [int]$RetentionDays,
        [string]$Category
    )

    if (-not (Test-Path $Path)) {
        Write-Status "$Category`: Directory not found: $Path" "WARN"
        return
    }

    $cutoff = (Get-Date).AddDays(-$RetentionDays)
    $files = Get-ChildItem -Path $Path -Filter $FilePattern -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -lt $cutoff }

    if ($files.Count -eq 0) {
        Write-Status "$Category`: No files older than $RetentionDays days in $Path" "OK"
        return
    }

    $totalSize = ($files | Measure-Object -Property Length -Sum).Sum
    $sizeMB = [math]::Round($totalSize / 1MB, 2)

    if ($WhatIf -or $PSCmdlet.ShouldProcess("$($files.Count) files ($sizeMB MB) in $Path", "Delete")) {
        if ($WhatIf) {
            Write-Status "$Category`: Would delete $($files.Count) files ($sizeMB MB) from $Path" "WARN"
        } else {
            $deletedCount = 0
            foreach ($file in $files) {
                try {
                    Remove-Item $file.FullName -Force -ErrorAction Stop
                    $deletedCount++
                    $script:totalFreedBytes += $file.Length
                }
                catch {
                    Write-Status "  Cannot delete: $($file.Name) - $($_.Exception.Message)" "WARN"
                }
            }
            $script:totalDeleted += $deletedCount
            $script:deletionLog += [PSCustomObject]@{
                Category = $Category
                Path     = $Path
                Files    = $deletedCount
                SizeMB   = $sizeMB
            }
            Write-Status "$Category`: Deleted $deletedCount files ($sizeMB MB) from $Path" "OK"
        }
    }
}

Write-Host ""
Write-Host "  ============================================================" -ForegroundColor Cyan
Write-Host "    Log Retention & Cleanup" -ForegroundColor Cyan
Write-Host "  ============================================================" -ForegroundColor Cyan
Write-Host ""
Write-Status "Retention policy:"
Write-Host "    Application logs: $LogRetentionDays days" -ForegroundColor DarkGray
Write-Host "    Audit logs:       $AuditRetentionDays days" -ForegroundColor DarkGray
Write-Host "    Crash dumps:      $CrashRetentionDays days" -ForegroundColor DarkGray
Write-Host "    Test results:     $ResultsRetentionDays days" -ForegroundColor DarkGray
if ($WhatIf) {
    Write-Host ""
    Write-Host "    *** DRY RUN MODE - no files will be deleted ***" -ForegroundColor Yellow
}
Write-Host ""

# -- Application logs --
Write-Status "Cleaning application logs..." "STEP"
foreach ($logDir in $LogDirectories) {
    Remove-OldFiles -Path $logDir -FilePattern "*.log" -RetentionDays $LogRetentionDays -Category "AppLog"
    Remove-OldFiles -Path $logDir -FilePattern "*.txt" -RetentionDays $LogRetentionDays -Category "AppLog"
}

# -- Crash dumps --
Write-Host ""
Write-Status "Cleaning crash dumps..." "STEP"
foreach ($logDir in $LogDirectories) {
    Remove-OldFiles -Path $logDir -FilePattern "*crash*" -RetentionDays $CrashRetentionDays -Category "CrashDump"
    Remove-OldFiles -Path $logDir -FilePattern "*.dmp" -RetentionDays $CrashRetentionDays -Category "CrashDump"
}

# -- Audit logs --
Write-Host ""
Write-Status "Cleaning audit logs..." "STEP"
foreach ($logDir in $LogDirectories) {
    $auditPath = Join-Path $logDir "Audit"
    Remove-OldFiles -Path $auditPath -FilePattern "*.json" -RetentionDays $AuditRetentionDays -Category "AuditLog"
    Remove-OldFiles -Path $auditPath -FilePattern "*.log" -RetentionDays $AuditRetentionDays -Category "AuditLog"
}

# -- Test results --
Write-Host ""
Write-Status "Cleaning old test results..." "STEP"
if (Test-Path $ResultsRootPath) {
    Remove-OldFiles -Path $ResultsRootPath -FilePattern "*.trx" -RetentionDays $ResultsRetentionDays -Category "TestResults"
    Remove-OldFiles -Path $ResultsRootPath -FilePattern "*.xml" -RetentionDays $ResultsRetentionDays -Category "TestResults"
    Remove-OldFiles -Path $ResultsRootPath -FilePattern "*.html" -RetentionDays $ResultsRetentionDays -Category "TestResults"

    # Remove empty subdirectories
    $emptyDirs = Get-ChildItem $ResultsRootPath -Directory -Recurse -ErrorAction SilentlyContinue |
        Where-Object { (Get-ChildItem $_.FullName -Recurse -File -ErrorAction SilentlyContinue).Count -eq 0 }
    foreach ($dir in $emptyDirs) {
        if (-not $WhatIf) {
            Remove-Item $dir.FullName -Force -Recurse -ErrorAction SilentlyContinue
        }
    }
    if ($emptyDirs.Count -gt 0) {
        Write-Status "Removed $($emptyDirs.Count) empty result directories" "OK"
    }
}

# -- Session persistence files --
Write-Host ""
Write-Status "Cleaning old session persistence files..." "STEP"
foreach ($logDir in $LogDirectories) {
    Remove-OldFiles -Path $logDir -FilePattern "sessions-*.json" -RetentionDays $LogRetentionDays -Category "SessionPersist"
}

# -- Summary --
$elapsed = (Get-Date) - $startTime
Write-Host ""
Write-Host "  ============================================================" -ForegroundColor DarkGray

if ($WhatIf) {
    Write-Host "  DRY RUN COMPLETE" -ForegroundColor Yellow
} else {
    $freedMB = [math]::Round($totalFreedBytes / 1MB, 2)
    Write-Host "  CLEANUP COMPLETE" -ForegroundColor Green
    Write-Host ""
    Write-Host "  Files deleted: $totalDeleted" -ForegroundColor White
    Write-Host "  Space freed:   $freedMB MB" -ForegroundColor White
    Write-Host "  Duration:      $($elapsed.TotalSeconds.ToString('F1'))s" -ForegroundColor DarkGray

    if ($deletionLog.Count -gt 0) {
        Write-Host ""
        Write-Host "  Breakdown:" -ForegroundColor DarkGray
        foreach ($entry in $deletionLog) {
            Write-Host "    $($entry.Category.PadRight(15)) $($entry.Files.ToString().PadLeft(5)) files  $($entry.SizeMB.ToString('F1').PadLeft(8)) MB  $($entry.Path)" -ForegroundColor White
        }
    }
}

Write-Host "  ============================================================" -ForegroundColor DarkGray
Write-Host ""
