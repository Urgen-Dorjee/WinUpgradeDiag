#Requires -RunAsAdministrator
<#
.SYNOPSIS
    START HERE. Menu-driven launcher - pick the symptom, it runs the right script.
.DESCRIPTION
    Right-click this file > "Run with PowerShell" (as administrator), or:
        powershell -ExecutionPolicy Bypass -File .\Start-Here.ps1

    You do not need to open any other script. This one checks the PC's state
    first, then asks what you saw on screen and runs the matching fix.
#>
[CmdletBinding()]
param()

$here = $PSScriptRoot
if (-not $here) { $here = Split-Path -Parent $MyInvocation.MyCommand.Definition }

function Invoke-Script {
    param([string]$Name, [hashtable]$Params = @{})
    $path = Join-Path $here $Name
    if (-not (Test-Path $path)) { Write-Warning "$Name not found in $here"; return }
    Write-Host "`n--- Running $Name ---`n" -ForegroundColor Cyan
    & $path @Params
    Write-Host "`n--- $Name finished ---" -ForegroundColor Cyan
}

function Pause-Here { Read-Host "`nPress Enter to return to the menu" | Out-Null }

# ---------------------------------------------------------------- state check
Write-Host "`n=================================================" -ForegroundColor Yellow
Write-Host " Windows 11 24H2 In-Place Upgrade - recovery menu" -ForegroundColor Yellow
Write-Host "=================================================`n" -ForegroundColor Yellow
Invoke-Script 'Check-UpgradeState.ps1'

if (Get-Process TSManager, SetupHost, setupprep -ErrorAction SilentlyContinue) {
    Write-Host "`nThe upgrade is STILL RUNNING on this PC." -ForegroundColor Green
    Write-Host "Do not run any fix. Leave it alone and watch the log instead:" -ForegroundColor Green
    Write-Host "    Get-Content 'C:\Windows\CCM\Logs\SMSTSLog\smsts.log' -Tail 25 -Wait`n"
    return
}

# ---------------------------------------------------------------------- menu
do {
    Write-Host "`n--------------------------------------------------" -ForegroundColor Yellow
    Write-Host " What did you see before it got stuck?" -ForegroundColor Yellow
    Write-Host "--------------------------------------------------"
    Write-Host " 1  Restarted during 'Downloading install.wim (xx%)'"
    Write-Host " 2  Restarted during 'Windows upgrade progress: xx%'"
    Write-Host " 3  Same as above, but ccmcache was cleared/deleted afterwards"
    Write-Host " 4  Rerun fails inside Setup, or C: is low on space"
    Write-Host " 5  Ran a fix already, Software Center still won't start it"
    Write-Host " 6  Nothing works - repair the ConfigMgr client"
    Write-Host " 7  Upgrade SUCCEEDED - reclaim space (Windows.old, `$WINDOWS.~BT)"
    Write-Host ""
    Write-Host " L  List cached content (find the ContentId)"
    Write-Host " S  Re-run the state check"
    Write-Host " Q  Quit"
    $choice = (Read-Host "`nChoice").Trim().ToUpper()

    switch ($choice) {
        '1' {
            Write-Host "`nFix A deletes the partial download and clears the stuck task sequence." -ForegroundColor Gray
            Write-Host "You need the ContentId of the upgrade package (the multi-GB item)." -ForegroundColor Gray
            Invoke-Script 'List-CcmCache.ps1' @{ MinimumSizeGB = 1 }
            $id = (Read-Host "`nContentId to delete (blank to cancel)").Trim()
            if ($id) { Invoke-Script 'Fix-A-DownloadInterrupted.ps1' @{ ContentId = $id } }
            else     { 'Cancelled.' }
            Pause-Here
        }
        '2' {
            Write-Host "`nFix B clears the stuck task sequence and KEEPS the downloaded install.wim." -ForegroundColor Gray
            Invoke-Script 'Fix-B-SetupInterrupted.ps1'
            Pause-Here
        }
        '3' {
            Write-Host "`nFix C clears the stuck task sequence and removes stale cache records." -ForegroundColor Gray
            Write-Host "install.wim will download again from 0%." -ForegroundColor Gray
            Invoke-Script 'Fix-C-CacheCleared.ps1'
            Pause-Here
        }
        '4' {
            Write-Host "`nFix D saves the Setup logs, then removes the half-built C:\`$WINDOWS.~BT." -ForegroundColor Gray
            Invoke-Script 'Fix-D-SetupLeftovers.ps1'
            Pause-Here
        }
        '5' {
            Write-Host "`nThis clears the deployment's run history so it can start again." -ForegroundColor Gray
            Write-Host "The task sequence package ID is in execmgr.log or the ConfigMgr console." -ForegroundColor Gray
            $ts = (Read-Host "`nTask sequence package ID (blank to cancel)").Trim()
            if ($ts) { Invoke-Script 'Reset-TSHistory.ps1' @{ TsPackageId = $ts } }
            else     { 'Cancelled.' }
            Pause-Here
        }
        '6' {
            Write-Host "`nRepairs the client, then run option 2 and retry the deployment." -ForegroundColor Gray
            Invoke-Script 'Repair-CcmClient.ps1'
            Pause-Here
        }
        '7' {
            Write-Host "`nThis retires the rollback option ('Go back' in Settings) permanently." -ForegroundColor Gray
            Write-Host "Only do it once the user confirms the upgraded PC is working." -ForegroundColor Gray
            if ((Read-Host "Type YES to continue") -eq 'YES') {
                Invoke-Script 'Remove-UpgradeLeftovers.ps1' @{ Confirm = $false }
            } else { 'Cancelled.' }
            Pause-Here
        }
        'L' { Invoke-Script 'List-CcmCache.ps1'; Pause-Here }
        'S' { Invoke-Script 'Check-UpgradeState.ps1'; Pause-Here }
        'Q' { 'Bye.' }
        default { Write-Warning 'Pick 1-7, L, S or Q.' }
    }
} while ($choice -ne 'Q')
