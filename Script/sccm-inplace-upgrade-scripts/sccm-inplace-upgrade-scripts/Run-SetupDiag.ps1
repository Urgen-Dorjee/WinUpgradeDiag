#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Shows Windows' own diagnosis of a failed upgrade, and runs Microsoft's SetupDiag if it is here.

.DESCRIPTION
    SetupDiag is Microsoft's analyser for failed Windows upgrades. Since Windows 10 2004, Windows
    Setup ships it and runs it automatically when an upgrade fails, saving the result to
    C:\Windows\Logs\SetupDiag\SetupDiagResults.xml. This script:

      1. shows that saved result, if there is one;
      2. looks for setupdiag.exe on this PC - in Setup's working folder, under Windows.old after a
         later successful upgrade, or inside the OS upgrade package in the client cache;
      3. refuses to run any copy that is not validly signed by Microsoft;
      4. runs it against this PC's upgrade logs, with Microsoft telemetry turned off, and shows what
         it found.

    Nothing is downloaded. If no copy is on the machine, the script says where to get one.

.PARAMETER OutputFolder
    Where SetupDiag writes its result. Defaults to the temp folder.

.NOTES
    Read-only as far as the PC is concerned: SetupDiag reads logs and writes its own result file,
    and may record that result in the registry under HKLM\SYSTEM\Setup. It changes nothing else.
#>
[CmdletBinding()]
param(
    [string] $OutputFolder = $env:TEMP
)

$ErrorActionPreference = 'Continue'

function Show-Result([string] $path, [string] $heading) {
    try { [xml] $doc = Get-Content -LiteralPath $path -Raw -ErrorAction Stop }
    catch { Write-Host "   Could not read $path : $($_.Exception.Message)" -ForegroundColor Yellow; return }

    # Matched by local name: the file carries a namespace that has changed between versions.
    function Pick([string] $name) { $n = $doc.SelectSingleNode("//*[local-name()='$name']"); if ($n) { $n.InnerText.Trim() } }

    Write-Host "`n== $heading" -ForegroundColor Cyan
    Write-Host "   File      : $path  ($((Get-Item -LiteralPath $path).LastWriteTime))"
    $rule = Pick 'ProfileName'
    Write-Host ("   Matched   : " + $(if ($rule) { $rule } else { '(no known failure pattern matched)' })) -ForegroundColor White
    $err = Pick 'ErrorCode'; $ext = Pick 'ExtendedErrorCode'
    if ($err -or $ext) { Write-Host "   Error     : $err  extended $ext" }

    foreach ($m in $doc.SelectNodes("//*[local-name()='FailureData']/*[local-name()='Message']")) {
        Write-Host "   >> $($m.InnerText.Trim())"
    }
    # Anything about a driver or device, outside the machine inventory.
    foreach ($leaf in $doc.SelectNodes("//*[not(*)]")) {
        $name = $leaf.LocalName
        if ($name -match '(?i)driver|hardwareid|infname|device' -and $leaf.InnerText.Trim() -and
            -not $leaf.SelectSingleNode("ancestor::*[local-name()='SystemInfo']")) {
            Write-Host "   DRIVER    : $name = $($leaf.InnerText.Trim())" -ForegroundColor Yellow
        }
    }
    $details = Pick 'FailureDetails'
    if ($details) { Write-Host "   Details   : $details" }
    foreach ($r in $doc.SelectNodes("//*[contains(translate(local-name(),'REMDIATON','remdiaton'),'remediation')][not(*)]")) {
        if ($r.InnerText.Trim()) { Write-Host "   Fix       : $($r.InnerText.Trim())" -ForegroundColor Green }
    }
}

# ---------------------------------------------------------------- 1. what Windows already found

$saved = @(
    "$env:windir\Logs\SetupDiag\SetupDiagResults.xml",
    "$env:SystemDrive\Windows.old\Windows\Logs\SetupDiag\SetupDiagResults.xml"
) | Where-Object { Test-Path -LiteralPath $_ }

if ($saved) {
    foreach ($s in $saved) { Show-Result $s 'What Windows found when the upgrade failed' }
} else {
    Write-Host "`n== No saved SetupDiag result on this PC." -ForegroundColor Cyan
    Write-Host '   Windows writes one when an upgrade fails, so either none has failed here, or the'
    Write-Host '   result was cleared by a later attempt.'
}

# ---------------------------------------------------------------- 2. find a copy to run

Write-Host "`n== Looking for setupdiag.exe on this PC" -ForegroundColor Cyan
$candidates = @()
foreach ($root in @("$env:SystemDrive\`$WINDOWS.~BT\Sources",
                    "$env:SystemDrive\Windows.old",
                    "$env:windir\ccmcache",
                    $PSScriptRoot)) {
    if ($root -and (Test-Path -LiteralPath $root)) {
        $candidates += @(Get-ChildItem -LiteralPath $root -Filter 'setupdiag.exe' -Recurse -Depth 4 -ErrorAction SilentlyContinue)
    }
}

# ---------------------------------------------------------------- 3. only Microsoft's own

$exe = $null
foreach ($c in $candidates) {
    try {
        $sig = Get-AuthenticodeSignature -LiteralPath $c.FullName -ErrorAction Stop
    } catch {
        Write-Host "   $($c.FullName) - could not check its signature, so it will not be run." -ForegroundColor Yellow
        continue
    }
    if ($sig.Status -eq 'Valid' -and $sig.SignerCertificate.Subject -match 'O=Microsoft Corporation') {
        Write-Host "   $($c.FullName) - signed by Microsoft." -ForegroundColor Green
        $exe = $c.FullName
        break
    }
    # Run elevated, a binary found in a folder decides what happens to this PC. Only Microsoft's.
    Write-Host "   $($c.FullName) - NOT validly signed by Microsoft ($($sig.Status)). Refusing to run it." -ForegroundColor Red
}

if (-not $exe) {
    Write-Host '   No Microsoft-signed setupdiag.exe found here.'
    Write-Host '   Setup keeps one in C:\$WINDOWS.~BT\Sources while an upgrade is in place. Otherwise download'
    Write-Host '   SetupDiag from Microsoft on another PC (search "SetupDiag Microsoft Learn"), copy setupdiag.exe'
    Write-Host '   next to this script, and run it again.'
    return
}

# ---------------------------------------------------------------- 4. run it

if (-not (Test-Path -LiteralPath $OutputFolder)) { New-Item -ItemType Directory -Path $OutputFolder -Force | Out-Null }
$out = Join-Path $OutputFolder ("SetupDiagResults_" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".xml")

Write-Host "`n== Running SetupDiag (this can take a few minutes)" -ForegroundColor Cyan
# /NoTel: no telemetry to Microsoft. Nothing in this toolkit sends anything off the machine.
& $exe "/Output:$out" '/Format:xml' '/ZipLogs:False' '/NoTel' | ForEach-Object { Write-Host "   $_" }
Write-Host "   exit code $LASTEXITCODE"

if (Test-Path -LiteralPath $out) {
    Show-Result $out 'What SetupDiag found just now'
} else {
    Write-Host '   SetupDiag produced no result file. Its own messages above say why.' -ForegroundColor Yellow
}
