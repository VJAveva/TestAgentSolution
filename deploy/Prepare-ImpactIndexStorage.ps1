<#
.SYNOPSIS
    Provisions the impact-index storage root on a controller node.

.DESCRIPTION
    The impact-mapping engine keeps two SQLite databases outside the source tree:

      <Root>\ImpactIndex\impact-index.db     rebuildable retrieval cache (can reach 1+ GB)
      <Root>\Learning\impact-outcomes.db     durable run outcomes that train the ranker

    Both directories must exist and be writable by the service account before the engine
    starts, otherwise the first write fails. This script is idempotent: re-running heals a
    partially provisioned node rather than erroring, which matters because the controller VM
    is subject to snapshot reverts.

    Note: reverting the VM destroys the index. Run Rebuild-ImpactIndex.ps1 afterwards.

.PARAMETER Root
    Parent folder for both databases. Default C:\ProgramData\TestAgentSolution.

.PARAMETER ServiceAccount
    Account granted Modify permission. Default MAGELLANDEV2000\wwAPPS.

.EXAMPLE
    .\Prepare-ImpactIndexStorage.ps1
.EXAMPLE
    .\Prepare-ImpactIndexStorage.ps1 -Root D:\TestAgentSolution -ServiceAccount CONTOSO\svc
#>
[CmdletBinding()]
param(
    [string]$Root = (Join-Path $env:ProgramData 'TestAgentSolution'),
    [string]$ServiceAccount = 'MAGELLANDEV2000\wwAPPS'
)

$ErrorActionPreference = 'Stop'

function Write-Step($m) { Write-Host "  [ .. ] $m" -ForegroundColor Cyan }
function Write-Ok($m)   { Write-Host "  [ OK ] $m" -ForegroundColor Green }
function Write-Warn2($m){ Write-Host "  [warn] $m" -ForegroundColor Yellow }

$indexRoot    = Join-Path $Root 'ImpactIndex'
$learningRoot = Join-Path $Root 'Learning'

Write-Host "Impact index storage provisioning" -ForegroundColor White
Write-Host "Root           : $Root"
Write-Host "Index root     : $indexRoot"
Write-Host "Learning root  : $learningRoot"
Write-Host "Service account: $ServiceAccount"
Write-Host ""

foreach ($dir in @($indexRoot, $learningRoot)) {
    if (Test-Path $dir) {
        Write-Ok "already present: $dir"
    }
    else {
        Write-Step "creating $dir"
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        Write-Ok "created: $dir"
    }
}

# Grant Modify to the service account. Re-running replaces the matching ACE rather than
# stacking duplicates, so the ACL stays clean across repeated provisioning.
foreach ($dir in @($indexRoot, $learningRoot)) {
    try {
        $acl  = Get-Acl -Path $dir
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            $ServiceAccount,
            'Modify',
            'ContainerInherit, ObjectInherit',
            'None',
            'Allow')
        $acl.SetAccessRule($rule)
        Set-Acl -Path $dir -AclObject $acl
        Write-Ok "granted Modify to $ServiceAccount on $dir"
    }
    catch {
        Write-Warn2 "could not set ACL on ${dir}: $($_.Exception.Message)"
        Write-Warn2 "grant Modify to $ServiceAccount manually, or the engine cannot write."
    }
}

Write-Host ""
Write-Host "Set IMPACT_INDEX_ROOT to override the location, or ImpactMapping:IndexRoot in appsettings.json." -ForegroundColor DarkGray
Write-Host "The index is NOT created by this script. Run Rebuild-ImpactIndex.ps1 to populate it." -ForegroundColor DarkGray
Write-Host "Provisioning complete." -ForegroundColor Green
