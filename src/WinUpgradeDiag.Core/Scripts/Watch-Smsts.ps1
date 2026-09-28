<#
.SYNOPSIS
    Tails smsts.log on a remote PC so you can watch upgrade progress without
    unlocking or signing in to the machine.
.DESCRIPTION
    Uses the admin share (\\PC\c$). No WinRM required. Press Ctrl+C to stop.
    CMTrace gives a colour view of the same file.
.EXAMPLE
    .\Watch-Smsts.ps1 -ComputerName PC001
.EXAMPLE
    .\Watch-Smsts.ps1 -ComputerName PC001 -Tail 100
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ComputerName,

    [int]$Tail = 25
)

$paths = @(
    "\\$ComputerName\c$\Windows\CCM\Logs\SMSTSLog\smsts.log",      # while the TS is running
    "\\$ComputerName\c$\_SMSTaskSequence\Logs\Smstslog\smsts.log",
    "\\$ComputerName\c$\Windows\CCM\Logs\smsts.log"                # after the TS ends
)

$log = $paths | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $log) {
    Write-Warning "No smsts.log found on $ComputerName. Checked:"
    $paths | ForEach-Object { "  $_" }
    return
}

"Reading: $log"
"Press Ctrl+C to stop."
""
Get-Content -Path $log -Tail $Tail -Wait
