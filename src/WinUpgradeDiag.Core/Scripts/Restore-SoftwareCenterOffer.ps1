#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Brings the Windows 11 upgrade back to Software Center after a failed upgrade rolled back.
.DESCRIPTION
    An in-place upgrade task sequence changes the ConfigMgr client before Windows Setup runs:
    it switches on provisioning mode (the client stops processing new policy) and records that a
    task sequence is running. After a rollback, Setup runs a ConfigMgr command that undoes both.
    When the rollback was forced by a crash or a power cut, that command never runs, and the PC
    is left on Windows 10 with a client that offers nothing - the upgrade never reappears.

    This script undoes exactly those leftovers, and nothing else:

      1. switches provisioning mode off;
      2. removes the "task sequence running" record, if one survived;
      3. clears the run history of the task sequence that failed, so it can run again;
      4. restarts the client and refreshes policy;
      5. reports which task sequences Software Center can now offer.

    If step 5 finds none, the deployment is not reaching this PC and the cause is on the
    ConfigMgr server (the target collection), which no change on the PC fixes properly.

    It does not delete the WMI repository, uninstall the client or touch the client cache.
.EXAMPLE
    .\Restore-SoftwareCenterOffer.ps1
#>
[CmdletBinding()]
param()

if (Get-Process TSManager, SetupHost, setupprep -ErrorAction SilentlyContinue) {
    Write-Warning 'An upgrade is running right now. Stopping here - do not change a live task sequence.'
    return
}
if (-not (Get-Service CcmExec -ErrorAction SilentlyContinue)) {
    Write-Warning 'The ConfigMgr client is not installed on this PC, so there is nothing to restore.'
    return
}

$ccmKey   = 'HKLM:\SOFTWARE\Microsoft\CCM\CcmExec'
$history  = 'HKLM:\SOFTWARE\Microsoft\SMS\Mobile Client\Software Distribution\Execution History\System'
$changed  = 0

# ---------------------------------------------------------------- what the rollback left behind

Write-Host "`n== What the failed upgrade left behind" -ForegroundColor Cyan

$reg = Get-ItemProperty -LiteralPath $ccmKey -ErrorAction SilentlyContinue
$provisioning = $reg -and "$($reg.ProvisioningMode)" -eq 'true'
Write-Host ("   Provisioning mode        : " + $(if ($provisioning) { 'ON  <- the client is ignoring new policy' } else { 'off' }))
if ($reg.SystemTaskExcludes) { Write-Host "   Components switched off  : $($reg.SystemTaskExcludes -join '; ')" }

$requests = @(Get-CimInstance -Namespace root\ccm\SoftMgmtAgent -ClassName CCM_TSExecutionRequest -ErrorAction SilentlyContinue)
Write-Host ("   'Task sequence running'  : " + $(if ($requests.Count) { "YES, $($requests.Count) record(s) <- nothing is running" } else { 'no' }))

# The task sequence whose last recorded run failed.
$names = @{}
Get-CimInstance -Namespace root\ccm\policy\machine\actualconfig -ClassName CCM_TaskSequence -ErrorAction SilentlyContinue |
    ForEach-Object { if ($_.PKG_PackageID) { $names[$_.PKG_PackageID] = $_.PKG_Name } }

$failed = $null
foreach ($pkg in Get-ChildItem -LiteralPath $history -ErrorAction SilentlyContinue) {
    $last = Get-ChildItem -LiteralPath $pkg.PSPath -ErrorAction SilentlyContinue |
        ForEach-Object { Get-ItemProperty -LiteralPath $_.PSPath } |
        Sort-Object { try { [datetime]$_._RunStartTime } catch { [datetime]::MinValue } } -Descending |
        Select-Object -First 1
    if (-not $last) { continue }
    $isTs = $names.ContainsKey($pkg.PSChildName) -or $last._ProgramID -eq '*'
    if ($isTs -and $last._State -ne 'Success') {
        $when = try { [datetime]$last._RunStartTime } catch { [datetime]::MinValue }
        if (-not $failed -or $when -gt $failed.When) {
            $failed = [PSCustomObject]@{ Id = $pkg.PSChildName; When = $when; Name = $names[$pkg.PSChildName] }
        }
    }
}
Write-Host ("   Failed task sequence     : " + $(if ($failed) { "$(if ($failed.Name) { $failed.Name } else { 'task sequence' }) ($($failed.Id))" } else { 'none recorded' }))

# ---------------------------------------------------------------- undo it

Write-Host "`n== Putting the client back" -ForegroundColor Cyan

if ($provisioning) {
    try {
        Invoke-CimMethod -Namespace root\ccm -ClassName SMS_Client -MethodName SetClientProvisioningMode `
            -Arguments @{ bEnable = $false } -ErrorAction Stop | Out-Null
        Write-Host '   Provisioning mode switched off through the client.'
    } catch {
        # The client's own method can fail on a damaged client; the registry is what it changes.
        Set-ItemProperty -LiteralPath $ccmKey -Name ProvisioningMode -Value 'false'
        Write-Host '   Provisioning mode switched off in the registry (the client method failed).'
    }
    Set-ItemProperty -LiteralPath $ccmKey -Name SystemTaskExcludes -Value '' -ErrorAction SilentlyContinue
    $changed++
}

Write-Host '   Stopping the ConfigMgr client...'
Stop-Service CcmExec -Force

if ($requests.Count) {
    Get-CimInstance -Namespace root\ccm\SoftMgmtAgent -ClassName CCM_TSExecutionRequest -ErrorAction SilentlyContinue |
        Remove-CimInstance
    Write-Host "   Removed the leftover 'task sequence running' record."
    $changed++
}
if (Test-Path -LiteralPath "$env:SystemDrive\_SMSTaskSequence") {
    Remove-Item -LiteralPath "$env:SystemDrive\_SMSTaskSequence" -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host '   Removed the leftover task sequence working folder.'
}
if ($failed) {
    Remove-Item -LiteralPath (Join-Path $history $failed.Id) -Recurse -Force
    Write-Host "   Cleared the failed run of $($failed.Id) so it can run again."
    $changed++
}

Write-Host '   Starting the ConfigMgr client...'
Start-Service CcmExec
Start-Sleep -Seconds 60

# Machine Policy Retrieval, then Evaluation.
foreach ($schedule in '{00000000-0000-0000-0000-000000000021}', '{00000000-0000-0000-0000-000000000022}') {
    Invoke-CimMethod -Namespace root\ccm -ClassName SMS_Client -MethodName TriggerSchedule `
        -Arguments @{ sScheduleID = $schedule } -ErrorAction SilentlyContinue | Out-Null
}
Write-Host '   Asked the client for fresh policy. Waiting for it to arrive (up to 3 minutes)...'

# ---------------------------------------------------------------- is it offered now?

$offered = @()
for ($i = 0; $i -lt 18 -and -not $offered; $i++) {
    Start-Sleep -Seconds 10
    $offered = @(Get-CimInstance -Namespace root\ccm\ClientSDK -ClassName CCM_Program -ErrorAction SilentlyContinue |
        Where-Object { $_.ProgramID -eq '*' -or $_.TaskSequence })
}

Write-Host "`n== Result" -ForegroundColor Cyan
if ($offered) {
    Write-Host '   Software Center can now offer:' -ForegroundColor Green
    $offered | ForEach-Object { Write-Host "     - $($_.Name)" -ForegroundColor Green }
    Write-Host '   Close and reopen Software Center, then click Install.'
} elseif ($changed -eq 0) {
    Write-Host '   Nothing on this PC was blocking the upgrade, and no task sequence is deployed to it.' -ForegroundColor Yellow
    Write-Host '   The deployment is not reaching this PC. That is decided on the ConfigMgr server: check that'
    Write-Host '   this PC is a member of the deployment''s target collection, and that no exclusion or'
    Write-Host '   "failed/in progress" query is keeping it out. Reinstalling the client only hides this,'
    Write-Host '   because it registers the PC as a new device with no history.'
} else {
    Write-Host '   The leftovers are cleared, but no task sequence has arrived in policy yet.' -ForegroundColor Yellow
    Write-Host '   Policy can take up to the client polling interval. Wait 15 minutes and open Software Center.'
    Write-Host '   If it is still missing, the cause is the target collection on the ConfigMgr server.'
}
