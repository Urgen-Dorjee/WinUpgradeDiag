#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Repairs the ConfigMgr client, then pulls machine policy.
.DESCRIPTION
    Last resort before escalating - still far cheaper than replacing the PC.
    ccmrepair.exe hands off to ccmsetup, so watch ccmsetup.log for the result.
    After the repair completes, run Fix-B-SetupInterrupted.ps1 and rerun the
    deployment from Software Center.
.EXAMPLE
    .\Repair-CcmClient.ps1
#>
[CmdletBinding()]
param()

$repair = 'C:\Windows\CCM\ccmrepair.exe'
if (-not (Test-Path $repair)) {
    Write-Warning "$repair not found. The client may need a full reinstall from the site server."
    return
}

Start-Process $repair -Wait
'ccmrepair started. It hands off to ccmsetup, which can take several minutes.'

$log = 'C:\Windows\ccmsetup\Logs\ccmsetup.log'
if (Test-Path $log) {
    ''
    "--- last 20 lines of $log ---"
    Get-Content $log -Tail 20
    ''
    "Watch it live with: Get-Content '$log' -Tail 20 -Wait"
}
