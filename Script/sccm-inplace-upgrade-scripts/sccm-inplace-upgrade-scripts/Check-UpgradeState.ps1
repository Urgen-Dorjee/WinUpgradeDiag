#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Reports whether a ConfigMgr in-place upgrade task sequence is running or orphaned.
.DESCRIPTION
    Run this FIRST. If TSManager / SetupHost are not running but a task sequence
    execution request still exists in WMI, the task sequence is orphaned - that is
    what makes Software Center sit on "Installing..." forever.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Check-UpgradeState.ps1
.EXAMPLE
    Invoke-Command -ComputerName PC001 -FilePath .\Check-UpgradeState.ps1
#>
[CmdletBinding()]
param()

$ts    = Get-Process TSManager -ErrorAction SilentlyContinue
$setup = Get-Process SetupHost, setupprep -ErrorAction SilentlyContinue
$req   = Get-CimInstance -Namespace root\ccm\SoftMgmtAgent -ClassName CCM_TSExecutionRequest -ErrorAction SilentlyContinue
$free  = [math]::Round((Get-PSDrive C).Free / 1GB, 1)
$setupProgress = (Get-ItemProperty 'HKLM:\SYSTEM\Setup\MoSetup\Volatile' -ErrorAction SilentlyContinue).SetupProgress

"Computer                 : $env:COMPUTERNAME"
"OS build                 : $((Get-CimInstance Win32_OperatingSystem).Version)"
"TSManager running        : $([bool]$ts)"
"Windows Setup running    : $([bool]$setup)"
"Setup progress (%)       : $setupProgress"
"TS execution request     : $([bool]$req)"
"C:\_SMSTaskSequence      : $(Test-Path 'C:\_SMSTaskSequence')"
'C:\$WINDOWS.~BT exists   : ' + (Test-Path 'C:\$WINDOWS.~BT')
"C:\Windows.old exists    : $(Test-Path 'C:\Windows.old')"
"Free space on C: (GB)    : $free"

# Is Windows Setup still doing work? (log write time advancing = alive)
$panther = 'C:\$WINDOWS.~BT\Sources\Panther\setupact.log'
if (Test-Path $panther) {
    $lw = (Get-Item $panther).LastWriteTime
    "setupact.log last write : $lw ($([math]::Round(((Get-Date) - $lw).TotalMinutes,1)) min ago)"
}

""
if ($ts -or $setup) {
    'RESULT: Upgrade is still running. Do NOT clean up. Monitor with Watch-Smsts.ps1.'
} elseif ($req) {
    'RESULT: Orphaned task sequence. Use Fix-A / Fix-B / Fix-C as appropriate.'
} else {
    'RESULT: No task sequence in progress.'
}

if ($req) { "`nExecution request detail:"; $req | Format-List }
