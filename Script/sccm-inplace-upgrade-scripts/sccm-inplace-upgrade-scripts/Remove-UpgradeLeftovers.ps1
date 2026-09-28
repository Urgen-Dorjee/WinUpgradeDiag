#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Reclaims disk space after a SUCCESSFUL in-place upgrade.
.DESCRIPTION
    Windows.old and C:\$WINDOWS.~BT are one rollback set: removing either alone
    breaks "Go back" while leaving the other wasting space. This script uses the
    supported DISM route (/Remove-OSUninstall), which removes both cleanly and
    retires the rollback entry in Settings.

    Run only after the user has confirmed the upgraded PC is working.
    C:\$WINDOWS.~WS and C:\$GetCurrent have no rollback role and are removed too.

    Windows deletes Windows.old automatically 10 days after the upgrade, so in
    most cases you do not need to run this at all.
.EXAMPLE
    .\Remove-UpgradeLeftovers.ps1 -WhatIf
.EXAMPLE
    .\Remove-UpgradeLeftovers.ps1 -Confirm:$false
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param()

"Free space before (GB): $([math]::Round((Get-PSDrive C).Free / 1GB, 1))"

# How many days of rollback are left?
dism.exe /Online /Get-OSUninstallWindow

if (Test-Path 'C:\Windows.old') {
    if ($PSCmdlet.ShouldProcess('C:\Windows.old and C:\$WINDOWS.~BT', 'Remove rollback data (DISM /Remove-OSUninstall)')) {
        dism.exe /Online /Remove-OSUninstall
        'Rollback data removed. "Go back" is no longer available on this PC.'
    }
} else {
    'C:\Windows.old not present - nothing to remove via DISM.'
}

# Staging folders with no rollback role
foreach ($p in 'C:\$WINDOWS.~WS', 'C:\$GetCurrent') {
    if (Test-Path $p) {
        if ($PSCmdlet.ShouldProcess($p, 'Remove staging folder')) {
            takeown /F $p /R /A /D Y | Out-Null
            icacls  $p /grant '*S-1-5-32-544:F' /T /C /Q | Out-Null
            Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue
            if (Test-Path $p) { Write-Warning "Some files remain in $p." } else { "Removed $p" }
        }
    }
}

"Free space after (GB): $([math]::Round((Get-PSDrive C).Free / 1GB, 1))"
'Note: do not delete C:\Windows\ccmcache by hand. Use Control Panel > Configuration Manager > Cache.'
