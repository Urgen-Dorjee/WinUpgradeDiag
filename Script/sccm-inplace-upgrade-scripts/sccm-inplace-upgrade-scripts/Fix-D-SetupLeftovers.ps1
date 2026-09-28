#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Saves Windows Setup logs and removes a half-built C:\$WINDOWS.~BT folder.
.DESCRIPTION
    Use when a rerun fails immediately inside the Setup step, or free space is low.
    Only run once the PC has booted normally back into the existing Windows.

    NOTE: this is for a FAILED / interrupted upgrade. After a SUCCESSFUL upgrade,
    remove Windows.old and $WINDOWS.~BT together with Remove-UpgradeLeftovers.ps1
    instead, so the rollback option is retired cleanly.
.EXAMPLE
    .\Fix-D-SetupLeftovers.ps1
.EXAMPLE
    .\Fix-D-SetupLeftovers.ps1 -LogDestination D:\UpgradeLogs
#>
[CmdletBinding()]
param(
    [string]$LogDestination = "C:\Temp\UpgradeLogs_$(Get-Date -Format yyyyMMdd_HHmm)"
)

$bt = 'C:\$WINDOWS.~BT'    # single quotes are required: $WINDOWS is not a variable

if (Get-Process TSManager, SetupHost, setupprep -ErrorAction SilentlyContinue) {
    Write-Warning 'Upgrade is still running. Stopping here.'
    return
}

if (Test-Path 'C:\Windows.old') {
    Write-Warning 'C:\Windows.old exists - this PC may have upgraded successfully. Use Remove-UpgradeLeftovers.ps1 instead.'
    return
}

if (-not (Test-Path $bt)) {
    "$bt does not exist. Nothing to clean."
    "Free space on C: (GB): $([math]::Round((Get-PSDrive C).Free / 1GB, 1))"
    return
}

# 1. Save Setup logs for the failure reason
New-Item -ItemType Directory -Path $LogDestination -Force | Out-Null
Copy-Item "$bt\Sources\Panther\setup*.log" $LogDestination -ErrorAction SilentlyContinue
Copy-Item 'C:\Windows\CCM\Logs\smsts.log'  $LogDestination -ErrorAction SilentlyContinue
"Logs saved to $LogDestination"

# 2. Take ownership and remove the folder
takeown /F $bt /R /A /D Y | Out-Null
icacls  $bt /grant '*S-1-5-32-544:F' /T /C /Q | Out-Null    # built-in Administrators
Remove-Item $bt -Recurse -Force -ErrorAction SilentlyContinue

if (Test-Path $bt) { Write-Warning "Some files remain in $bt. Reboot and run again." }
else               { "Removed $bt" }

"Free space on C: (GB): $([math]::Round((Get-PSDrive C).Free / 1GB, 1))"
'Review setuperr.log for compatibility, driver or disk space blocks before rerunning.'
