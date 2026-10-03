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

    It answers the questions worth asking, in the order they matter:

      1. Which task sequence step failed, and with what code.
      2. What Windows Setup itself recorded as an error.
      3. What Microsoft's SetupDiag concluded - Windows runs it itself when an upgrade fails.
      4. Whether the machine crashed, with which stop code, and which device install the crash
         cut off - the answer to a DRIVER_PNP_WATCHDOG.
      5. Which device drivers failed to install.

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

# ---------------------------------------------------------------- 3. Windows' own diagnosis

Add-Heading "3. What Windows' own diagnosis (SetupDiag) concluded"

# Since Windows 10 2004, Setup runs Microsoft's SetupDiag when an upgrade fails and saves the
# result. It is Microsoft's rule set for upgrade failures and is often the most direct answer.
$sdFiles = @("$env:windir\Logs\SetupDiag\SetupDiagResults.xml",
             "$sysDrive\Windows.old\Windows\Logs\SetupDiag\SetupDiagResults.xml") |
    Where-Object { Test-Path -LiteralPath $_ }
$sdShown = $false

foreach ($sdPath in $sdFiles) {
    try { [xml] $sdDoc = Get-Content -LiteralPath $sdPath -Raw -ErrorAction Stop }
    catch { Add-Line "$sdPath - present but could not be read: $($_.Exception.Message)"; continue }

    # Matched by local name: the file carries a namespace that has changed between versions.
    $pick = { param($n) $x = $sdDoc.SelectSingleNode("//*[local-name()='$n']"); if ($x) { $x.InnerText.Trim() } }

    Add-Line "$sdPath"
    Add-Line "  (written $((Get-Item -LiteralPath $sdPath).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')))"
    $sdProfile = & $pick 'ProfileName'
    Add-Line ("  Matched  : " + $(if ($sdProfile) { $sdProfile } else { '(no known failure pattern)' }))
    $sdErr = & $pick 'ErrorCode'; $sdExt = & $pick 'ExtendedErrorCode'
    if ($sdErr -or $sdExt) { Add-Line "  Error    : $sdErr  extended $sdExt" }
    foreach ($m in $sdDoc.SelectNodes("//*[local-name()='FailureData']/*[local-name()='Message']")) {
        Add-Line "  >> $($m.InnerText.Trim())"
    }
    # Any device or driver it names, leaving out the machine inventory under SystemInfo.
    foreach ($leaf in $sdDoc.SelectNodes("//*[not(*)]")) {
        if ($leaf.LocalName -match '(?i)driver|hardwareid|infname|device' -and $leaf.InnerText.Trim() -and
            -not $leaf.SelectSingleNode("ancestor::*[local-name()='SystemInfo']")) {
            Add-Line "  DRIVER   : $($leaf.LocalName) = $($leaf.InnerText.Trim())"
        }
    }
    $sdDetails = & $pick 'FailureDetails'
    if ($sdDetails) { Add-Line "  Details  : $sdDetails" }
    foreach ($r in $sdDoc.SelectNodes("//*[contains(translate(local-name(),'REMDIATON','remdiaton'),'remediation')][not(*)]")) {
        if ($r.InnerText.Trim()) { Add-Line "  Fix      : $($r.InnerText.Trim())" }
    }
    Add-Line ''
    $sdShown = $true
}

if (-not $sdShown) {
    # The registry copy survives when the file has been tidied away.
    $sdKey = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\Setup\SetupDiag\Results' -ErrorAction SilentlyContinue
    if ($sdKey) {
        Add-Line 'From the registry (HKLM\SYSTEM\Setup\SetupDiag\Results):'
        foreach ($prop in $sdKey.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' }) {
            Add-Line "  $($prop.Name) : $(($prop.Value | Out-String).Trim())"
        }
        $sdShown = $true
    }
}

if (-not $sdShown) {
    Add-Line 'No SetupDiag result on this PC. Windows writes one when an upgrade fails, so either none has'
    Add-Line 'failed here or a later attempt cleared it. Run-SetupDiag.ps1 runs it on demand if setupdiag.exe'
    Add-Line 'is available.'
}

# ---------------------------------------------------------------- 4. the crash

Add-Heading '4. Did the machine crash, and while installing what?'

# The name on the crash screen (DRIVER_PNP_WATCHDOG) is never written to a Setup log, so
# searching the logs for it finds nothing. The evidence is elsewhere:
#   - the stop code, in the dump header and in the BugCheck event
#   - the event, in the copies of the event logs Setup saves into Rollback - the crash happened
#     in the NEW Windows, whose System log the rollback threw away
#   - the device, as the install in setupapi.dev.log that started and never finished before
#     Windows booted again
# The Rollback files are owned by TrustedInstaller, so they are copied out in backup mode first.

$bugcheckNames = @{
    0x1D5 = 'DRIVER_PNP_WATCHDOG';  0x9F  = 'DRIVER_POWER_STATE_FAILURE'; 0x133 = 'DPC_WATCHDOG_VIOLATION'
    0x101 = 'CLOCK_WATCHDOG_TIMEOUT'; 0x7B = 'INACCESSIBLE_BOOT_DEVICE';  0x7E  = 'SYSTEM_THREAD_EXCEPTION_NOT_HANDLED'
    0x3B  = 'SYSTEM_SERVICE_EXCEPTION'; 0xD1 = 'DRIVER_IRQL_NOT_LESS_OR_EQUAL'; 0x0A = 'IRQL_NOT_LESS_OR_EQUAL'
    0x50  = 'PAGE_FAULT_IN_NONPAGED_AREA'; 0xEF = 'CRITICAL_PROCESS_DIED'; 0x116 = 'VIDEO_TDR_FAILURE'
    0x124 = 'WHEA_UNCORRECTABLE_ERROR'; 0x139 = 'KERNEL_SECURITY_CHECK_FAILURE'; 0x154 = 'UNEXPECTED_STORE_EXCEPTION'
}
function Format-StopCode([uint32] $code) {
    $hex = '0x{0:X8}' -f $code
    if ($bugcheckNames.ContainsKey([int]$code)) { return "$($bugcheckNames[[int]$code]) ($hex)" }
    return "stop code $hex"
}

$copyRoot = Join-Path $env:TEMP ("UpgradeFailure_" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $copyRoot -Force | Out-Null

$unreadable = @()
foreach ($folder in @("$sysDrive\`$WINDOWS.~BT\Sources\Rollback", "$sysDrive\`$WINDOWS.~BT\Sources\Panther")) {
    if (-not (Test-Path -LiteralPath $folder)) { continue }
    $target = Join-Path $copyRoot (Split-Path $folder -Leaf)
    $robocopyArgs = @($folder, $target, 'setupmem.dmp', '*.evtx', 'setupapi*.log', '/S', '/R:0', '/W:0', '/NJH', '/NJS', '/NFL', '/NDL', '/NP')

    # /ZB reads TrustedInstaller-owned files an administrator cannot otherwise open, by falling back
    # to backup mode. robocopy refuses to start at all without the Backup right, though, and its
    # exit code was being thrown away - so a run without that right copied nothing and this section
    # then reported "no stop code found", a confident wrong answer. Check the exit code (8 and above
    # is failure), fall back to an ordinary copy of whatever is readable, and record what was not.
    & robocopy.exe @robocopyArgs '/ZB' | Out-Null
    if ($LASTEXITCODE -ge 8) {
        & robocopy.exe @robocopyArgs | Out-Null
        if ($LASTEXITCODE -ge 8) { $unreadable += $folder }
        elseif (-not $elevated) { $unreadable += "$folder (protected files only - run elevated)" }
    }
}

$crashes = @()

# Dump headers: "PAGE" "DU64", stop code at 0x38.
$dumps = @(Get-ChildItem -Path $copyRoot -Filter 'setupmem.dmp' -Recurse -ErrorAction SilentlyContinue)
$dumps += @(Get-ChildItem -Path "$env:windir\Minidump" -Filter '*.dmp' -ErrorAction SilentlyContinue)
if (Test-Path "$env:windir\MEMORY.DMP") { $dumps += Get-Item "$env:windir\MEMORY.DMP" }

foreach ($d in $dumps) {
    try {
        $fs = [IO.File]::OpenRead($d.FullName)
        try {
            $h = New-Object byte[] 96
            $n = $fs.Read($h, 0, 96)
        } finally { $fs.Dispose() }
        $sig = [Text.Encoding]::ASCII.GetString($h, 0, 8)
        if ($n -ge 96 -and $sig -eq 'PAGEDU64') {
            $code = [BitConverter]::ToUInt32($h, 0x38)
            $crashes += [PSCustomObject]@{ When = $d.LastWriteTime; Code = $code; Source = $d.Name; Text = 'Crash dump header' }
        } elseif ($sig -like 'MDMP*') {
            Add-Line "  $($d.Name) is a process dump, not a crash of the machine."
        }
    } catch {
        Add-Line "  Could not read $($d.Name): $($_.Exception.Message)"
    }
}

# Event logs: Setup's saved copies first, then the live one.
$evtx = @(Get-ChildItem -Path $copyRoot -Filter '*.evtx' -Recurse -ErrorAction SilentlyContinue)
$xpath = '*[System[(EventID=41 or EventID=1001 or EventID=6008)]]'
$events = @()
foreach ($f in $evtx) { $events += @(Get-WinEvent -Path $f.FullName -FilterXPath $xpath -ErrorAction SilentlyContinue) }
$events += @(Get-WinEvent -LogName System -FilterXPath $xpath -MaxEvents 50 -ErrorAction SilentlyContinue)

foreach ($e in $events) {
    $code = $null
    if ($e.Id -eq 1001 -and $e.Message -match 'bug\s*check\s+was:?\s*0x([0-9a-fA-F]+)') {
        $code = [Convert]::ToUInt32($Matches[1], 16)
    } elseif ($e.Id -eq 41 -and $e.ToXml() -match "Name=.BugcheckCode.>(\d+)<") {
        $code = [uint32]$Matches[1]
    }
    if ($code) {
        $crashes += [PSCustomObject]@{ When = $e.TimeCreated; Code = $code; Source = "event $($e.Id)"; Text = $e.LogName }
    }
}

# The device install that was cut off: started, never finished, then Windows booted again.
$apiCopies = @(Get-ChildItem -Path $copyRoot -Filter 'setupapi.dev*.log' -Recurse -ErrorAction SilentlyContinue)
$cutOff = @()
foreach ($log in $apiCopies) {
    $open = $null; $openedAt = $null
    foreach ($line in [IO.File]::ReadLines($log.FullName)) {
        if ($line -match '^\[Boot Session:\s*(\d{4}/\d{2}/\d{2}\s+\d{2}:\d{2}:\d{2})') {
            # Take the time now: every later -match overwrites $Matches, and the checks below
            # are -match operations, which is how the boot time used to come out blank.
            $bootedAt = $Matches[1]
            if ($open -and $open -match '(?i)Device Install|Driver Install|DiInstallDriver|Device Start' -and
                $open -notmatch '(?i)uninstall|delete') {
                $cutOff += [PSCustomObject]@{ Section = $open; Started = $openedAt; NextBoot = $bootedAt; Log = $log.FullName }
            }
            $open = $null; $openedAt = $null; continue
        }
        if ($line -match '^>>>\s*\[(.+)\]\s*$') { $open = $Matches[1].Trim(); $openedAt = $null; continue }
        if ($open -and $line -match '^>>>\s*Section start\s+(\S+\s+\S+)') { $openedAt = $Matches[1]; continue }
        if ($open -and $line -match '^<<<\s*(Section end|\[Exit status)') { $open = $null; $openedAt = $null }
    }
}

if ($crashes) {
    $latest = $crashes | Sort-Object When -Descending | Select-Object -First 1
    Add-Line "  CRASHED: $(Format-StopCode $latest.Code)"
    Add-Line "           from $($latest.Source), $($latest.When)"
    $crashes | Sort-Object When -Descending | Select-Object -Skip 1 -First 3 | ForEach-Object {
        Add-Line "           also: $(Format-StopCode $_.Code) from $($_.Source), $($_.When)"
    }
} elseif ($unreadable) {
    # Not "no crash": "could not look". The difference is the whole point.
    Add-Line '  Could not read everything Setup saved, so a crash recorded only there would not show here:'
    $unreadable | ForEach-Object { Add-Line "    $_" }
    Add-Line '  Run this elevated, as an administrator, and try again.'
} else {
    Add-Line '  No stop code found in a dump or in any event log, saved or live.'
}

if ($cutOff) {
    $last = $cutOff | Select-Object -Last 1
    Add-Line ''
    Add-Line '  THE INSTALL THE CRASH CUT OFF - this is the device to suspect:'
    Add-Line "  >> [$($last.Section)]"
    Add-Line "     started $($last.Started), never finished; Windows booted again at $($last.NextBoot)"
    if ($last.Section -match '((?:PCI|USB|HDAUDIO|ACPI|SWC|ROOT|HID|BTH)\\[^\s\]]+)') {
        Add-Line ''
        Add-Line "  Hardware id: $($Matches[1])"
        Add-Line '  Find it in Device Manager (Details tab, Hardware Ids) to see the vendor, then get a'
        Add-Line '  Windows 11 driver from them. Disabling just that device and retrying confirms it first.'
    }
} elseif ($crashes) {
    Add-Line ''
    Add-Line '  No device install was found cut off, so the logs do not name the driver. It is inside the'
    Add-Line '  dump: open it in WinDbg and run !analyze -v; the IMAGE_NAME line is the driver.'
}

Remove-Item -LiteralPath $copyRoot -Recurse -Force -ErrorAction SilentlyContinue

# ---------------------------------------------------------------- 5. the failing driver

Add-Heading '5. Device drivers that failed to install'

# The failed attempt's logs live in a setupapi subfolder of Rollback, not beside setupact.log.
$apiLogs = @(Get-ChildItem -Path "$sysDrive\`$WINDOWS.~BT\Sources" -Filter 'setupapi.dev*.log' -Recurse -ErrorAction SilentlyContinue |
             ForEach-Object { $_.FullName })
$apiLogs += $paths['setupapi (device)']
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

Add-Heading '6. Machine state'

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

Add-Heading '7. Third-party driver packages'
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
