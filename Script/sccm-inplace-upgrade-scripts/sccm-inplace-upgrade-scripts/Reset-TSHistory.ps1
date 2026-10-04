#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Lets Software Center run a failed task sequence again, by clearing its run history.
.DESCRIPTION
    Use when the cause is fixed but Software Center still refuses to start the task sequence.

    The client records every run under Execution History in the registry, with its result. This
    script finds the task sequence whose last run FAILED - the one Software Center is refusing -
    names it, and clears only that one's history. You do not need its package id.

    Task sequences that last succeeded are never touched: clearing those would let a required
    deployment run a second time.
.PARAMETER TsPackageId
    Optional. Only to reset a task sequence other than the one found, e.g. when several failed.
.EXAMPLE
    .\Reset-TSHistory.ps1
#>
[CmdletBinding()]
param(
    [string]$TsPackageId
)

$root = 'HKLM:\SOFTWARE\Microsoft\SMS\Mobile Client\Software Distribution\Execution History\System'

# Names come from machine policy; the history alone still works if policy cannot be read.
$names = @{}
try {
    Get-CimInstance -Namespace root\ccm\policy\machine\actualconfig -ClassName CCM_TaskSequence -ErrorAction Stop |
        ForEach-Object { if ($_.PKG_PackageID) { $names[$_.PKG_PackageID] = $_.PKG_Name } }
} catch { }

function Get-LastRun([string] $packageId) {
    $runs = foreach ($run in Get-ChildItem -LiteralPath (Join-Path $root $packageId) -ErrorAction SilentlyContinue) {
        $v = Get-ItemProperty -LiteralPath $run.PSPath -ErrorAction SilentlyContinue
        $started = $null
        if ($v._RunStartTime) { try { $started = [datetime]$v._RunStartTime } catch { } }
        [PSCustomObject]@{
            PackageId = $packageId
            Name      = $names[$packageId]
            Program   = $v._ProgramID
            State     = $v._State
            Started   = $started
            Code      = $v.SuccessOrFailureCode
        }
    }
    $runs | Sort-Object { if ($_.Started) { $_.Started } else { [datetime]::MinValue } } -Descending | Select-Object -First 1
}

function Format-Run($r) {
    $label = if ($r.Name) { "`"$($r.Name)`"" } else { 'Task sequence' }
    $when  = if ($r.Started) { ", last run $($r.Started.ToString('yyyy-MM-dd HH:mm'))" } else { '' }
    $state = if ($r.State) { $r.State.ToLower() } else { 'result not recorded' }
    $code  = if ($r.Code -and "$($r.Code)" -ne '0') { " with code $($r.Code)" } else { '' }
    "$label ($($r.PackageId))$when, $state$code"
}

if (-not (Test-Path -LiteralPath $root)) {
    'This PC has no deployment run history, so there is nothing to clear.'
    return
}

# Task sequences only: they run program "*", and policy confirms it when it can be read.
$taskSequences = @(Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue |
    ForEach-Object { Get-LastRun $_.PSChildName } |
    Where-Object { $_ -and ($names.ContainsKey($_.PackageId) -or $_.Program -eq '*') })

if ($TsPackageId) {
    $target = Get-LastRun $TsPackageId.Trim()
    if (-not $target) { "No run history for $TsPackageId on this PC, so there is nothing to clear."; return }
} else {
    $failed = @($taskSequences | Where-Object { $_.State -ne 'Success' } |
        Sort-Object { if ($_.Started) { $_.Started } else { [datetime]::MinValue } } -Descending)

    if ($failed.Count -eq 0) {
        'No task sequence on this PC has a failed run recorded, so nothing is blocking a rerun here.'
        if ($taskSequences) {
            ''
            'Task sequences with history (all last succeeded, so left alone):'
            $taskSequences | ForEach-Object { '  ' + (Format-Run $_) }
        }
        'If Software Center still will not offer it, the deployment itself may not be available to'
        'this PC yet - refresh policy, or check the deployment in the ConfigMgr console.'
        return
    }

    $target = $failed[0]
    if ($failed.Count -gt 1) {
        'More than one task sequence failed here. Resetting the most recent; the others are left alone:'
        $failed | Select-Object -Skip 1 | ForEach-Object { '  ' + (Format-Run $_) }
        '(To reset one of those instead, run this script with -TsPackageId and its id.)'
        ''
    }
}

'Resetting: ' + (Format-Run $target)
Remove-Item -LiteralPath (Join-Path $root $target.PackageId) -Recurse -Force
"Removed its run history."

'Restarting the ConfigMgr client...'
Restart-Service CcmExec -Force
Start-Sleep -Seconds 60

Invoke-CimMethod -Namespace root\ccm -ClassName SMS_Client -MethodName TriggerSchedule `
    -Arguments @{ sScheduleID = '{00000000-0000-0000-0000-000000000021}' } | Out-Null

'Done. Close and reopen Software Center, then click Install.'
