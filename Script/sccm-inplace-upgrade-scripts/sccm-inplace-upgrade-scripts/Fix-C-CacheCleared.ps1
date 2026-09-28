#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Recovers a PC where ccmcache was already cleared (properly or manually).
.DESCRIPTION
    Clearing the cache does not by itself fix the stuck "Installing...", and a
    manual delete in Explorer leaves WMI records pointing at folders that no
    longer exist. This script clears the orphaned task sequence, recreates the
    cache root if it was removed, and deletes stale cache records.

    After this the rerun downloads install.wim again from 0%.
.EXAMPLE
    .\Fix-C-CacheCleared.ps1
#>
[CmdletBinding()]
param()

if (Get-Process TSManager, SetupHost, setupprep -ErrorAction SilentlyContinue) {
    Write-Warning 'Upgrade is still running. Stopping here - do not clean up a live task sequence.'
    return
}

'Stopping CcmExec...'
Stop-Service CcmExec -Force

# 1. Clear the orphaned task sequence
$req = Get-CimInstance -Namespace root\ccm\SoftMgmtAgent -ClassName CCM_TSExecutionRequest -ErrorAction SilentlyContinue
if ($req) { $req | Remove-CimInstance; "Removed $($req.Count) task sequence execution request(s)." }
else      { 'No task sequence execution request found.' }

Remove-Item 'C:\_SMSTaskSequence' -Recurse -Force -ErrorAction SilentlyContinue

# 2. Recreate the cache root if the whole folder was deleted
if (-not (Test-Path 'C:\Windows\ccmcache')) {
    New-Item -ItemType Directory -Path 'C:\Windows\ccmcache' | Out-Null
    'Recreated C:\Windows\ccmcache'
}

# 3. Remove cache records whose folder no longer exists
$stale = Get-CimInstance -Namespace root\ccm\SoftMgmtAgent -ClassName CacheInfoEx -ErrorAction SilentlyContinue |
         Where-Object { -not (Test-Path $_.Location) }
if ($stale) {
    foreach ($s in $stale) {
        "Removing stale record $($s.ContentId) -> $($s.Location)"
        $s | Remove-CimInstance
    }
} else {
    'No stale cache records found.'
}

'Starting CcmExec...'
Start-Service CcmExec
Start-Sleep -Seconds 60

# 4. Machine Policy Retrieval & Evaluation Cycle
Invoke-CimMethod -Namespace root\ccm -ClassName SMS_Client -MethodName TriggerSchedule `
    -Arguments @{ sScheduleID = '{00000000-0000-0000-0000-000000000021}' } | Out-Null

"Free space on C: (GB): $([math]::Round((Get-PSDrive C).Free / 1GB, 1))"
'Done. If free space is low, run Fix-D-SetupLeftovers.ps1 before clicking Install.'
