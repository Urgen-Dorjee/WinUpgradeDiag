#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Clears the ConfigMgr execution history for a task sequence so it can rerun.
.DESCRIPTION
    Use when Software Center still refuses to start the task sequence after
    Fix-A / Fix-B / Fix-C.

    -TsPackageId is the TASK SEQUENCE package ID (visible in execmgr.log or the
    ConfigMgr console). It is not the same as the content ID of the OS upgrade
    package used by Fix-A.
.EXAMPLE
    .\Reset-TSHistory.ps1 -TsPackageId ABC00456
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$TsPackageId
)

$key = "HKLM:\SOFTWARE\Microsoft\SMS\Mobile Client\Software Distribution\Execution History\System\$TsPackageId"

if (Test-Path $key) {
    Remove-Item $key -Recurse -Force
    "Removed execution history for $TsPackageId"
} else {
    "No execution history found for $TsPackageId"
}

Restart-Service CcmExec -Force
Start-Sleep -Seconds 60

Invoke-CimMethod -Namespace root\ccm -ClassName SMS_Client -MethodName TriggerSchedule `
    -Arguments @{ sScheduleID = '{00000000-0000-0000-0000-000000000021}' } | Out-Null

'Done. Close and reopen Software Center, then click Install.'
