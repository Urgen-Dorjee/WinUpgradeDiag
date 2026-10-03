#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Clears a Software Center item stuck "Downloading" at a fixed percentage.

.DESCRIPTION
    For an application — not a task sequence — that sits at the same percentage forever and whose
    Cancel button does nothing.

    The percentage is held by a download job, not by the cache. ConfigMgr tracks a download in
    three separate places, and clearing only one leaves the other two pointing at work that can
    never finish:

      BITS                 the actual transfer, which survives a service restart and a reboot
      DataTransferService  the client's record of that BITS job
      ContentTransferMgr   the request that asked for the content in the first place

    Deleting C:\Windows\ccmcache in Explorer removes none of them. It also strands the WMI cache
    records, which still say the content is present at a folder that no longer exists — so the
    client believes it already has the content AND believes a download is in flight, and does
    neither. Cancel does nothing because the UI cancels the deployment, not the orphaned job
    underneath it. Removing the device from the collection does not help either: the stuck state is
    local, so re-targeting the same machine finds it exactly as it was.

    This stops the client, removes all three, clears stale cache records, then restarts and asks
    for policy again so the download starts from the beginning.

.PARAMETER ContentId
    Optional. Also delete this specific cached item. Use when one package is known bad; leave it
    out to clear only what is genuinely orphaned.

.PARAMETER KeepBits
    Leave BITS jobs alone. Only for a machine where another product is mid-transfer and you have
    confirmed the stuck job is not a BITS one.

.EXAMPLE
    .\Fix-E-StuckDownload.ps1 -WhatIf

.EXAMPLE
    .\Fix-E-StuckDownload.ps1

.NOTES
    Read the output. It names every job it removes, so an unexpected one is visible rather than
    silently cleaned up. Nothing here uninstalls software or touches the application itself.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [ValidatePattern('^[A-Za-z0-9_\-\.]{1,64}$')]
    [string] $ContentId,

    [switch] $KeepBits
)

$ErrorActionPreference = 'Continue'

function Write-Step { param([string] $Text) Write-Host "`n== $Text" -ForegroundColor Cyan }
function Write-Ok   { param([string] $Text) Write-Host "   $Text" -ForegroundColor Green }
function Write-Skip { param([string] $Text) Write-Host "   $Text" -ForegroundColor DarkGray }

# ---------------------------------------------------------------- safety

# Never clear content jobs underneath a running task sequence: that is how a machine ends up
# half-imaged with the content it still needs deleted.
$live = Get-Process TSManager, SetupHost, setupprep -ErrorAction SilentlyContinue
if ($live) {
    throw "A task sequence or Windows Setup is running ($($live.Name -join ', ')). Refusing to clear content jobs."
}

if (-not (Get-Service CcmExec -ErrorAction SilentlyContinue)) {
    throw 'The ConfigMgr client is not installed on this machine, so there is no download to clear.'
}

Write-Host 'Fix-E-StuckDownload' -ForegroundColor White

# ---------------------------------------------------------------- what is stuck

Write-Step 'What is in flight'

$bits = @(Get-BitsTransfer -AllUsers -ErrorAction SilentlyContinue)
if ($bits) {
    foreach ($job in $bits) {
        $pct = if ($job.BytesTotal -gt 0) { [math]::Round(($job.BytesTransferred / $job.BytesTotal) * 100, 1) } else { 0 }
        Write-Host ("   BITS  {0,-28} {1,-12} {2}%" -f $job.DisplayName, $job.JobState, $pct)
    }
} else {
    Write-Skip 'No BITS jobs.'
}

$dts = @(Get-CimInstance -Namespace root\ccm\DataTransferService -ClassName CCM_DTS_JobEx -ErrorAction SilentlyContinue)
Write-Skip ("DataTransferService jobs: " + $dts.Count)

$ctm = @(Get-CimInstance -Namespace root\ccm\SoftMgmtAgent -ClassName CCM_CTM_Job -ErrorAction SilentlyContinue)
Write-Skip ("ContentTransferManager jobs: " + $ctm.Count)

# ---------------------------------------------------------------- clear it

Write-Step 'Stopping the client'
if ($PSCmdlet.ShouldProcess('CcmExec', 'Stop service')) {
    Stop-Service CcmExec -Force -ErrorAction SilentlyContinue
    Write-Ok 'CcmExec stopped'
}

if (-not $KeepBits) {
    Write-Step 'Cancelling BITS transfers'
    foreach ($job in $bits) {
        if ($PSCmdlet.ShouldProcess($job.DisplayName, 'Remove BITS job')) {
            try {
                Remove-BitsTransfer -BitsJob $job -ErrorAction Stop
                Write-Ok "removed $($job.DisplayName)"
            } catch { Write-Warning "$($job.DisplayName) - $($_.Exception.Message)" }
        }
    }
    if (-not $bits) { Write-Skip 'nothing to cancel' }
} else {
    Write-Skip 'Leaving BITS jobs alone as requested.'
}

Write-Step 'Removing DataTransferService jobs'
foreach ($job in $dts) {
    if ($PSCmdlet.ShouldProcess($job.ID, 'Remove DTS job')) {
        try { $job | Remove-CimInstance -ErrorAction Stop; Write-Ok "removed $($job.ID)" }
        catch { Write-Warning "$($job.ID) - $($_.Exception.Message)" }
    }
}
if (-not $dts) { Write-Skip 'none' }

Write-Step 'Removing ContentTransferManager jobs'
foreach ($job in $ctm) {
    if ($PSCmdlet.ShouldProcess($job.ContentID, 'Remove CTM job')) {
        try { $job | Remove-CimInstance -ErrorAction Stop; Write-Ok "removed $($job.ContentID)" }
        catch { Write-Warning "$($job.ContentID) - $($_.Exception.Message)" }
    }
}
if (-not $ctm) { Write-Skip 'none' }

Write-Step 'Clearing cache records that point at folders which are gone'
# This is what deleting ccmcache in Explorer leaves behind: the client still believes the content
# is present, so it will not fetch it again.
$cache = @(Get-CimInstance -Namespace root\ccm\SoftMgmtAgent -ClassName CacheInfoEx -ErrorAction SilentlyContinue)
$stale = @($cache | Where-Object { $_.Location -and -not (Test-Path -LiteralPath $_.Location) })

foreach ($record in $stale) {
    if ($PSCmdlet.ShouldProcess($record.ContentID, 'Remove stale cache record')) {
        try { $record | Remove-CimInstance -ErrorAction Stop; Write-Ok "removed $($record.ContentID) -> $($record.Location)" }
        catch { Write-Warning "$($record.ContentID) - $($_.Exception.Message)" }
    }
}
if (-not $stale) { Write-Skip 'none stale' }

if ($ContentId) {
    Write-Step "Removing the cached item $ContentId"
    $named = @($cache | Where-Object { $_.ContentID -eq $ContentId })
    foreach ($record in $named) {
        if ($PSCmdlet.ShouldProcess($ContentId, 'Remove cache record and folder')) {
            if ($record.Location -and (Test-Path -LiteralPath $record.Location)) {
                Remove-Item -LiteralPath $record.Location -Recurse -Force -ErrorAction SilentlyContinue
            }
            try { $record | Remove-CimInstance -ErrorAction Stop; Write-Ok "removed $ContentId" }
            catch { Write-Warning "$ContentId - $($_.Exception.Message)" }
        }
    }
    if (-not $named) { Write-Skip "$ContentId is not in the cache" }
}

if (-not (Test-Path 'C:\Windows\ccmcache')) {
    if ($PSCmdlet.ShouldProcess('C:\Windows\ccmcache', 'Recreate')) {
        New-Item -ItemType Directory -Path 'C:\Windows\ccmcache' | Out-Null
        Write-Ok 'recreated C:\Windows\ccmcache'
    }
}

# ---------------------------------------------------------------- restart and re-evaluate

Write-Step 'Starting the client'
if ($PSCmdlet.ShouldProcess('CcmExec', 'Start service')) {
    Start-Service CcmExec -ErrorAction SilentlyContinue
    Write-Ok 'CcmExec started - waiting for it to settle'
    Start-Sleep -Seconds 45
}

Write-Step 'Asking for policy again'
# Machine Policy Retrieval first, then Application Deployment Evaluation: the second is what
# re-examines an application deployment and starts the download from the beginning. Triggering
# only the first is why "it refreshed policy and nothing happened".
foreach ($schedule in @(
    @{ Id = '{00000000-0000-0000-0000-000000000021}'; Name = 'Machine Policy Retrieval & Evaluation' },
    @{ Id = '{00000000-0000-0000-0000-000000000121}'; Name = 'Application Deployment Evaluation' }
)) {
    if ($PSCmdlet.ShouldProcess($schedule.Name, 'Trigger')) {
        try {
            Invoke-CimMethod -Namespace root\ccm -ClassName SMS_Client -MethodName TriggerSchedule `
                -Arguments @{ sScheduleID = $schedule.Id } -ErrorAction Stop | Out-Null
            Write-Ok $schedule.Name
        } catch { Write-Warning "$($schedule.Name) - $($_.Exception.Message)" }
    }
}

Write-Host "`nDone. Software Center can take several minutes to re-evaluate." -ForegroundColor Yellow
Write-Host "Watch the download start again with:" -ForegroundColor DarkGray
Write-Host "  Get-Content C:\Windows\CCM\Logs\ContentTransferManager.log -Tail 20 -Wait" -ForegroundColor DarkGray
Write-Host "  Get-Content C:\Windows\CCM\Logs\CAS.log -Tail 20 -Wait" -ForegroundColor DarkGray
