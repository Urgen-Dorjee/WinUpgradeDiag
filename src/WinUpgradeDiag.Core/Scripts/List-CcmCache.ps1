#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Lists ConfigMgr client cache (ccmcache) contents.
.DESCRIPTION
    Use this to find the ContentId of the OS upgrade package - it is the item
    several GB in size. That ContentId is the -ContentId parameter of
    Fix-A-DownloadInterrupted.ps1.

    FolderExists = False means the folder was deleted manually and the WMI record
    is now stale. Clean those up with Fix-C-CacheCleared.ps1.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\List-CcmCache.ps1
.EXAMPLE
    .\List-CcmCache.ps1 -MinimumSizeGB 1
#>
[CmdletBinding()]
param(
    [double]$MinimumSizeGB = 0
)

$items = Get-CimInstance -Namespace root\ccm\SoftMgmtAgent -ClassName CacheInfoEx -ErrorAction Stop |
    Select-Object ContentId, ContentVer,
        @{n = 'SizeGB';       e = { [math]::Round($_.ContentSize / 1MB, 2) }},   # ContentSize is in KB
        @{n = 'Persist';      e = { $_.PersistInCache }},
        LastReferenced, Location,
        @{n = 'FolderExists'; e = { Test-Path $_.Location }}

$items |
    Where-Object { $_.SizeGB -ge $MinimumSizeGB } |
    Sort-Object SizeGB -Descending |
    Format-Table -AutoSize

$totalGB = [math]::Round(($items | Measure-Object SizeGB -Sum).Sum, 2)
"Total cached: $totalGB GB in $($items.Count) items"

$stale = @($items | Where-Object { -not $_.FolderExists })
if ($stale.Count) {
    Write-Warning "$($stale.Count) cache record(s) point to folders that no longer exist. Run Fix-C-CacheCleared.ps1."
}
