#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Recovers a PC interrupted during "Windows upgrade progress: xx%".
.DESCRIPTION
    In that phase install.wim is already downloaded and verified, so the cache is
    left alone. Only the orphaned task sequence is cleared. The existing Windows
    installation is untouched in this phase, so the PC boots normally.

    If the rerun then fails inside Setup, run Fix-D-SetupLeftovers.ps1.
.EXAMPLE
    .\Fix-B-SetupInterrupted.ps1
#>
[CmdletBinding()]
param()

if (Get-Process TSManager, SetupHost, setupprep -ErrorAction SilentlyContinue) {
    Write-Warning 'Upgrade is still running. Stopping here - do not clean up a live task sequence.'
    return
}

# 1. Clear the orphaned task sequence (cache is NOT touched)
'Stopping CcmExec...'
Stop-Service CcmExec -Force

$req = Get-CimInstance -Namespace root\ccm\SoftMgmtAgent -ClassName CCM_TSExecutionRequest -ErrorAction SilentlyContinue
if ($req) { $req | Remove-CimInstance; "Removed $($req.Count) task sequence execution request(s)." }
else      { 'No task sequence execution request found.' }

Remove-Item 'C:\_SMSTaskSequence' -Recurse -Force -ErrorAction SilentlyContinue

'Starting CcmExec...'
Start-Service CcmExec
Start-Sleep -Seconds 60

# 2. Machine Policy Retrieval & Evaluation Cycle
Invoke-CimMethod -Namespace root\ccm -ClassName SMS_Client -MethodName TriggerSchedule `
    -Arguments @{ sScheduleID = '{00000000-0000-0000-0000-000000000021}' } | Out-Null

# 3. Report Setup leftovers
if (Test-Path 'C:\$WINDOWS.~BT') {
    'C:\$WINDOWS.~BT exists. Setup normally reuses or cleans it. If the rerun fails, use Fix-D-SetupLeftovers.ps1.'
}

"Free space on C: (GB): $([math]::Round((Get-PSDrive C).Free / 1GB, 1))"
'Done. Close and reopen Software Center, then click Install.'
