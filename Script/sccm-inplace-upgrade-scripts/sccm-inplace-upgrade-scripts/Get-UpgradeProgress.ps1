<#
.SYNOPSIS
    Shows whether Windows Setup is alive on a remote PC, and at what percent.
.DESCRIPTION
    Answers the "stuck at 99%?" question. Requires WinRM for the process and
    registry checks; the setupact.log timestamp is read over the admin share.

    Interpretation:
      setupact.log advancing + SetupHost using CPU  -> working, leave it alone
      log untouched 30+ min, SetupHost still there  -> read the last log lines
      SetupHost gone, UI still shows a percentage   -> Setup finished; check smsts.log
.EXAMPLE
    .\Get-UpgradeProgress.ps1 -ComputerName PC001
.EXAMPLE
    .\Get-UpgradeProgress.ps1 -ComputerName PC001 -ShowLogTail 30
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ComputerName,

    [int]$ShowLogTail = 0
)

Invoke-Command -ComputerName $ComputerName -ScriptBlock {
    "Computer              : $env:COMPUTERNAME"
    $procs = Get-Process TSManager, SetupHost, setupprep -ErrorAction SilentlyContinue
    if ($procs) { $procs | Select-Object Name, Id, CPU, StartTime | Format-Table -AutoSize }
    else        { 'No TSManager / SetupHost process running.' }

    $p = Get-ItemProperty 'HKLM:\SYSTEM\Setup\MoSetup\Volatile' -ErrorAction SilentlyContinue
    "Windows Setup progress: $($p.SetupProgress)%"
    "Free space on C: (GB) : $([math]::Round((Get-PSDrive C).Free / 1GB, 1))"

    $panther = 'C:\$WINDOWS.~BT\Sources\Panther\setupact.log'
    if (Test-Path $panther) {
        $f = Get-Item $panther
        "setupact.log size (MB): $([math]::Round($f.Length / 1MB, 1))"
        "setupact.log last write: $($f.LastWriteTime) ($([math]::Round(((Get-Date) - $f.LastWriteTime).TotalMinutes,1)) min ago)"
    } else {
        'No C:\$WINDOWS.~BT\Sources\Panther\setupact.log on this PC.'
    }
} | Out-Host

if ($ShowLogTail -gt 0) {
    $remoteLog = "\\$ComputerName\c$\`$WINDOWS.~BT\Sources\Panther\setupact.log"
    if (Test-Path $remoteLog) {
        ""
        "--- last $ShowLogTail lines of setupact.log ---"
        Get-Content -Path $remoteLog -Tail $ShowLogTail
    }
}
