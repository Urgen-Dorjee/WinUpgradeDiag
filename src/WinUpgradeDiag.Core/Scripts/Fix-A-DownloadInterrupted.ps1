#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Recovers a PC interrupted while the task sequence was downloading install.wim.
.DESCRIPTION
    Symptom: Software Center shows "Installing..." forever after a restart during
    "Downloading install.wim (xx% complete)". The task sequence execution request
    was never closed, so reruns queue behind a task sequence that is not running.

    This script clears the orphaned task sequence FIRST (so the cache item is not
    locked), then deletes the partial download through the client, then pulls
    machine policy.

    Get -ContentId from List-CcmCache.ps1 (the multi-GB item).
.EXAMPLE
    .\Fix-A-DownloadInterrupted.ps1 -ContentId ABC00123
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ContentId
)

if (Get-Process TSManager, SetupHost, setupprep -ErrorAction SilentlyContinue) {
    Write-Warning 'Upgrade is still running. Stopping here - do not clean up a live task sequence.'
    return
}

# 1. Clear the orphaned task sequence
'Stopping CcmExec...'
Stop-Service CcmExec -Force

$req = Get-CimInstance -Namespace root\ccm\SoftMgmtAgent -ClassName CCM_TSExecutionRequest -ErrorAction SilentlyContinue
if ($req) { $req | Remove-CimInstance; "Removed $($req.Count) task sequence execution request(s)." }
else      { 'No task sequence execution request found.' }

Remove-Item 'C:\_SMSTaskSequence' -Recurse -Force -ErrorAction SilentlyContinue

'Starting CcmExec...'
Start-Service CcmExec
Start-Sleep -Seconds 60     # let the client finish starting

# 2. Delete the partial content through the client (keeps WMI and disk in sync)
$cache = (New-Object -ComObject UIResource.UIResourceMgr).GetCacheInfo()
$found = $false
foreach ($e in $cache.GetCacheElements()) {
    if ($e.ContentId -eq $ContentId) {
        $found = $true
        $cache.DeleteCacheElementEx($e.CacheElementId, $true)   # $true = delete even if persisted
        "Deleted cache element $($e.ContentId) at $($e.Location)"
    }
}
if (-not $found) { Write-Warning "No cache element with ContentId '$ContentId' found." }

# 3. Machine Policy Retrieval & Evaluation Cycle
Invoke-CimMethod -Namespace root\ccm -ClassName SMS_Client -MethodName TriggerSchedule `
    -Arguments @{ sScheduleID = '{00000000-0000-0000-0000-000000000021}' } | Out-Null

"Free space on C: (GB): $([math]::Round((Get-PSDrive C).Free / 1GB, 1))"
'Done. Close and reopen Software Center, then click Install.'
