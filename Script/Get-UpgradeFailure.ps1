#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Names what failed in a Windows 10 to 11 in-place upgrade, without needing an executable.

.DESCRIPTION
    A script-only version of the WinUpgradeDiag analysis, for machines where application
    control (WDAC or AppLocker) blocks unsigned executables. Those policies enforce on
    .exe files by publisher, path or hash; script rules are frequently audit-only or
    path-allowed, which is why the recovery scripts in this folder run on machines that
    refuse WinUpgradeDiag.Cli.exe.

    It answers the three questions worth asking, in the order they matter:

      1. Which task sequence step failed, and with what code.
      2. What Windows Setup itself recorded as an error.
      3. Which device driver failed to install - the answer to a PnP watchdog bugcheck.

    Read-only. It opens logs and reads the registry. It changes nothing.

.PARAMETER OutputFolder
    Where to write the report. Defaults to the desktop. A timestamped file is created,
    so repeated runs do not overwrite each other.

.PARAMETER Quiet
    Write the report but do not print it to the console.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Get-UpgradeFailure.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Get-UpgradeFailure.ps1 -OutputFolder \\server\share\diag -Quiet

.NOTES
    Must run elevated. The Setup logs under C:\$WINDOWS.~BT are owned by TrustedInstaller
    and an unelevated run reports them as unreadable - which is the single most common
    reason a diagnostic comes back saying nothing useful.
#>
[CmdletBinding()]
param(
    [string] $OutputFolder = [Environment]::GetFolderPath('Desktop'),
    [switch] $Quiet
)

$ErrorActionPreference = 'Continue'
$report = New-Object System.Collections.ArrayList

function Add-Line {
    param([string] $Text = '')
    [void]$report.Add($Text)
    if (-not $Quiet) { Write-Host $Text }
}

function Add-Heading {
    param([string] $Text)
    Add-Line
    Add-Line ('=' * 78)
    Add-Line "  $Text"
    Add-Line ('=' * 78)
}

# ---------------------------------------------------------------- where the logs are

$sysDrive = $env:SystemDrive
$paths = [ordered]@{
    'smsts (CCM)'            = "$env:windir\CCM\Logs"
    'smsts (staging)'        = "$sysDrive\_SMSTaskSequence\Logs"
    'setuperr (rollback)'    = "$sysDrive\`$WINDOWS.~BT\Sources\Rollback\setuperr.log"
    'setupact (rollback)'    = "$sysDrive\`$WINDOWS.~BT\Sources\Rollback\setupact.log"
    'setupapi (rollback)'    = "$sysDrive\`$WINDOWS.~BT\Sources\Rollback\setupapi.dev.log"
    'setuperr (current)'     = "$sysDrive\`$WINDOWS.~BT\Sources\Panther\setuperr.log"
    'setupact (current)'     = "$sysDrive\`$WINDOWS.~BT\Sources\Panther\setupact.log"
    'setupapi (current)'     = "$sysDrive\`$WINDOWS.~BT\Sources\Panther\setupapi.dev.log"
    'setupapi (device)'      = "$env:windir\INF\setupapi.dev.log"
    'setuperr (completed)'   = "$env:windir\Panther\setuperr.log"
}

Add-Heading "WinUpgradeDiag (script) - $env:COMPUTERNAME - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$elevated = (New-Object Security.Principal.WindowsPrincipal $identity).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

$os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
$cs = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
$bios = Get-CimInstance Win32_BIOS -ErrorAction SilentlyContinue

Add-Line "Machine    : $($cs.Manufacturer) $($cs.Model)"
Add-Line "OS         : $($os.Caption) $($os.Version)"
Add-Line "BIOS       : $($bios.SMBIOSBIOSVersion)"
Add-Line "Elevated   : $elevated"
if (-not $elevated) {
    Add-Line "  WARNING: not elevated. The Setup logs under `$WINDOWS.~BT cannot be read,"
    Add-Line "           so anything recorded only there is missing from this report."
}

# ---------------------------------------------------------------- 1. the failing step

Add-Heading '1. Task sequence step that failed'

$smstsFiles = @()
foreach ($dir in @($paths['smsts (CCM)'], $paths['smsts (staging)'], "$env:windir\CCM\Logs\SMSTSLog")) {
    if (Test-Path -LiteralPath $dir) {
        $smstsFiles += Get-ChildItem -LiteralPath $dir -Filter 'smsts*.log' -ErrorAction SilentlyContinue
    }
}
$smstsFiles = $smstsFiles | Sort-Object LastWriteTime -Descending

if (-not $smstsFiles) {
    Add-Line 'No smsts*.log found. Either ConfigMgr is not involved or the client logs are gone.'
} else {
    $named = $false
    foreach ($file in $smstsFiles) {
        # Select-String streams the file, so a 5 MB log costs nothing.
        $hits = Select-String -LiteralPath $file.FullName -Pattern 'Failed to run the action' -ErrorAction SilentlyContinue
        if (-not $hits) { continue }

        $last = $hits[-1]
        Add-Line "Log        : $($file.FullName)"
        Add-Line "Last write : $($file.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))"
        Add-Line ''
        Add-Line "  >> $($last.Line.Trim())"

        # ConfigMgr wraps the code onto the following line more often than not.
        $following = Get-Content -LiteralPath $file.FullName -TotalCount ($last.LineNumber + 1) -ErrorAction SilentlyContinue |
                     Select-Object -Last 1
        if ($following -and $following -ne $last.Line) {
            Add-Line "  >> $($following.Trim())"
        }

        $group = Select-String -LiteralPath $file.FullName -Pattern 'execution of the group' -ErrorAction SilentlyContinue
        if ($group) { Add-Line "  >> $($group[-1].Line.Trim())" }

        $named = $true
        break
    }
    if (-not $named) {
        Add-Line 'No "Failed to run the action" line in any smsts log. The sequence may not have failed at a step.'
    }
}

# ---------------------------------------------------------------- 2. what Setup recorded

Add-Heading '2. Errors Windows Setup recorded'

$errLogs = @($paths['setuperr (rollback)'], $paths['setuperr (current)'], $paths['setuperr (completed)'])
$foundErrors = $false

foreach ($path in $errLogs) {
    if (-not (Test-Path -LiteralPath $path)) { continue }

    $item = Get-Item -LiteralPath $path -ErrorAction SilentlyContinue
    if (-not $item) {
        Add-Line "$path - present but could not be opened (run elevated)."
        continue
    }

    # setuperr.log is small and is nothing but errors, so every line is a finding.
    $lines = Get-Content -LiteralPath $path -Tail 60 -ErrorAction SilentlyContinue
    if (-not $lines) { continue }

    Add-Line "$path"
    Add-Line "  ($([math]::Round($item.Length / 1KB, 1)) KB, last written $($item.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')))"
    Add-Line ''

    # Newest first, collapsing repeats of the same failure.
    $seen = @{}
    $shown = 0
    for ($i = $lines.Count - 1; $i -ge 0 -and $shown -lt 12; $i--) {
        $line = $lines[$i].Trim()
        if (-not $line) { continue }
        # Key on everything after the timestamp so retries collapse together.
        $key = $line -replace '^\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2},\s*', ''
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true
        Add-Line "  >> $line"
        $shown++
    }

    $foundErrors = $true
    break
}

if (-not $foundErrors) {
    Add-Line 'No readable setuperr.log. If one exists under $WINDOWS.~BT, re-run this elevated.'
}

# ---------------------------------------------------------------- 3. the failing driver

Add-Heading '3. Device drivers that failed to install'

$apiLogs = @($paths['setupapi (rollback)'], $paths['setupapi (current)'], $paths['setupapi (device)'])
$foundDriver = $false

foreach ($path in $apiLogs) {
    if (-not (Test-Path -LiteralPath $path)) { continue }

    $failures = Select-String -LiteralPath $path -Pattern '^<<<\s*\[Exit status: FAILURE' -ErrorAction SilentlyContinue
    if (-not $failures) { continue }

    Add-Line "$path"
    Add-Line ''

    # setupapi writes each install as a bracketed section; walk back from the failing
    # exit status to the ">>> [" header that opened it, which names the device.
    $all = Get-Content -LiteralPath $path -ErrorAction SilentlyContinue
    $reported = @{}
    $shown = 0

    for ($f = $failures.Count - 1; $f -ge 0 -and $shown -lt 6; $f--) {
        $at = $failures[$f].LineNumber - 1
        $header = $null
        $detail = $null

        for ($i = $at; $i -ge 0 -and $i -gt ($at - 400); $i--) {
            $line = $all[$i]
            if (-not $detail -and $line -match '^!+\s') { $detail = $line.Trim() }
            if ($line -match '^>>>\s*\[(.+)\]\s*$') { $header = $Matches[1].Trim(); break }
        }

        # A package being uninstalled is not a driver failing to start. The recent end of this
        # log is mostly SetupUninstallOEMInf sections failing because the package is already
        # gone, and those crowd out the device install that actually matters.
        if ($header -and ($header -match '(?i)uninstall|delete')) { continue }

        if (-not $header -or $reported.ContainsKey($header)) { continue }
        $reported[$header] = $true

        Add-Line "  >> [$header]"
        Add-Line "     $($failures[$f].Line.Trim())"
        if ($detail) { Add-Line "     $detail" }
        $shown++
    }

    $foundDriver = $true
    break
}

if (-not $foundDriver) {
    Add-Line 'No failed device installs found in any readable setupapi.dev.log.'
}

# ---------------------------------------------------------------- state and drivers

Add-Heading '4. Machine state'

$reboot = @()
if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') { $reboot += 'component servicing' }
if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired') { $reboot += 'Windows Update' }

Add-Line "Pending reboot  : $(if ($reboot) { $reboot -join ', ' } else { 'no' })"
Add-Line "Free on C:      : $([math]::Round((Get-PSDrive C).Free / 1GB, 1)) GB"
Add-Line "TSManager       : $(if (Get-Process TSManager -ErrorAction SilentlyContinue) { 'running' } else { 'not running' })"
Add-Line "SetupHost       : $(if (Get-Process SetupHost, setupprep -ErrorAction SilentlyContinue) { 'running' } else { 'not running' })"

foreach ($folder in @("$sysDrive\`$WINDOWS.~BT", "$sysDrive\Windows.old", "$sysDrive\_SMSTaskSequence")) {
    Add-Line "$($folder.PadRight(16)): $(if (Test-Path -LiteralPath $folder) { 'exists' } else { 'absent' })"
}

Add-Heading '5. Third-party driver packages'
Add-Line 'If the same model upgrades fine elsewhere, this list and the BIOS version above'
Add-Line 'are where the two machines differ. Compare before looking anywhere else.'
Add-Line ''

$oem = Get-CimInstance Win32_PnPSignedDriver -ErrorAction SilentlyContinue |
       Where-Object { $_.InfName -like 'oem*' } |
       Select-Object InfName, DriverProviderName, DeviceClass, DriverVersion, DeviceName -Unique |
       Sort-Object DriverProviderName, InfName

if ($oem) {
    foreach ($d in $oem) {
        Add-Line ("  {0,-12} {1,-22} {2,-18} {3}" -f $d.InfName, $d.DriverProviderName, $d.DriverVersion, $d.DeviceName)
    }
} else {
    Add-Line '  None reported.'
}

Add-Heading 'Filter drivers (third-party filters are a common upgrade blocker)'
$filters = Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Services' -ErrorAction SilentlyContinue |
    ForEach-Object {
        $group = (Get-ItemProperty $_.PSPath -Name Group -ErrorAction SilentlyContinue).Group
        if ($group -like 'FSFilter*') {
            $image = (Get-ItemProperty $_.PSPath -Name ImagePath -ErrorAction SilentlyContinue).ImagePath
            [PSCustomObject]@{ Name = $_.PSChildName; Group = $group; Image = $image }
        }
    }

foreach ($f in $filters | Sort-Object Name) {
    # An image outside System32\drivers marks a filter Windows did not ship.
    $thirdParty = $f.Image -and ($f.Image -notmatch '(?i)system32\\drivers\\')
    Add-Line ("  {0} {1,-20} {2}" -f $(if ($thirdParty) { '[3rd]' } else { '     ' }), $f.Name, $f.Group)
}

# ---------------------------------------------------------------- write it out

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$file = Join-Path $OutputFolder "UpgradeFailure_${env:COMPUTERNAME}_$stamp.txt"

try {
    if (-not (Test-Path -LiteralPath $OutputFolder)) {
        New-Item -ItemType Directory -Path $OutputFolder -Force | Out-Null
    }
    $report | Set-Content -LiteralPath $file -Encoding UTF8
    Write-Host ''
    Write-Host "Report written to: $file" -ForegroundColor Green
} catch {
    Write-Warning "Could not write the report to $OutputFolder : $($_.Exception.Message)"
}
