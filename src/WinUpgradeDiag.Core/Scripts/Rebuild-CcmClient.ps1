#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Removes a broken ConfigMgr client completely and reinstalls it with an explicit management point.

.DESCRIPTION
    For a client whose WMI provider will not load — the state where the Configuration Manager
    control panel applet will not open, Software Center is empty, CcmExec starts and then stops,
    and root\ccm returns 0x80041013 provider load failure.

    Repair-CcmClient.ps1 is the lighter option and should be tried first: it runs ccmrepair.exe,
    which is enough for a client that is merely unhealthy. It does not fix this state. ccmrepair
    hands off to ccmsetup, and ccmsetup queries the same broken root\ccm provider and fails with
    the same code — on the machine this was written for it returned 1612, install source
    unavailable. When that has been tried, the client has to go completely and come back.

    This performs the removal that would otherwise be about forty commands typed by hand:

      services            CcmExec, ccmsetup, smstsmgr, CmRcService
      WMI namespaces      root\ccm, root\ccmvdi, root\smsdm, root\cimv2\sms
      folders             C:\Windows\CCM, C:\Windows\ccmcache, SMSCFG.ini
      registry            HKLM\SOFTWARE\Microsoft\{CCM, CCMSetup, SMS}
      certificates        Cert:\LocalMachine\SMS
      scheduled task      the ccmsetup retry task, which otherwise re-runs the failed install

    C:\Windows\ccmsetup is deliberately kept: it holds the installer this script then runs.

.PARAMETER SiteCode
    Three-character site code, e.g. ABC. Becomes SMSSITECODE=.

.PARAMETER ManagementPoint
    Fully qualified management point name, e.g. mp01.contoso.com. Passed as BOTH /mp: and SMSMP=.

    Passing only /mp: is the most common reason a client installs with return code 0 and then never
    registers: that switch says where to download the installer from, not which management point
    the client is assigned to. Where the site is not published to Active Directory and there is no
    DNS SRV record, the client has no other way to find one, and it retries the site-code refresh
    every few minutes forever.

.PARAMETER Stage
    Remove  - clean up only, then stop so the machine can be rebooted (default).
    Install - install only. Run this after the reboot.
    Both    - do both in one pass, without a reboot between them.

    Remove is the default deliberately. Skipping the reboot is what failed on the machine this
    was written for: the install afterwards returned 0 but the client never registered. A reboot
    releases file and WMI handles the reinstall otherwise trips over.

.PARAMETER TryMsiUninstall
    Run ccmsetup.exe /uninstall before the manual cleanup. Off by default: once ccmsetup has
    cleaned up the folder a previous install ran from, the MSI uninstall returns 1612 and the
    attempt ends with 0x8007064c. The manual cleanup does not need it.

.PARAMETER WhatIf
    Show every action without performing any of it.

.PARAMETER Target
    The same two values as one argument, "SITE/mp.fqdn", e.g. ABC/mp01.contoso.com. For callers
    that can only pass a single parameter.

.EXAMPLE
    .\Rebuild-CcmClient.ps1 -SiteCode ABC -ManagementPoint mp01.contoso.com -WhatIf
    # Shows the removal without doing it. Always start here.

.EXAMPLE
    .\Rebuild-CcmClient.ps1 -SiteCode ABC -ManagementPoint mp01.contoso.com -Stage Remove
    # reboot, then:
    .\Rebuild-CcmClient.ps1 -SiteCode ABC -ManagementPoint mp01.contoso.com -Stage Install

.NOTES
    DESTRUCTIVE. The client's identity is reset, so the machine may appear twice in the console
    until the duplicate record ages out. Do not run this on a machine in the middle of a task
    sequence — the script refuses if one is running.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    # Named form, for a human at a prompt.
    [Parameter(ParameterSetName = 'Named', Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9]{3}$')]
    [string] $SiteCode,

    [Parameter(ParameterSetName = 'Named', Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9.\-]+$')]
    [string] $ManagementPoint,

    # Combined form "SITE/mp.fqdn", for callers that can only pass one argument - the tool
    # runner in the application is one of them. Split and validated below exactly as above,
    # so a typo fails here rather than producing a client assigned to nothing.
    [Parameter(ParameterSetName = 'Combined', Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9]{3}/[A-Za-z0-9][A-Za-z0-9.\-]+$')]
    [string] $Target,

    [ValidateSet('Both', 'Remove', 'Install')]
    [string] $Stage = 'Remove',

    # Attempt ccmsetup.exe /uninstall before the manual cleanup. Off by default because on the
    # client this was written for it returns 1612 and achieves nothing.
    [switch] $TryMsiUninstall,

    [int] $InstallTimeoutMinutes = 30
)

$ErrorActionPreference = 'Continue'

if ($PSCmdlet.ParameterSetName -eq 'Combined') {
    $parts = $Target.Split('/')
    $SiteCode = $parts[0]
    $ManagementPoint = $parts[1]
}

function Write-Step { param([string] $Text) Write-Host "`n== $Text" -ForegroundColor Cyan }
function Write-Ok   { param([string] $Text) Write-Host "   $Text" -ForegroundColor Green }
function Write-Skip { param([string] $Text) Write-Host "   $Text" -ForegroundColor DarkGray }

# ---------------------------------------------------------------- safety

# Never tear the client down underneath a running task sequence: that is how a machine ends up
# half-upgraded with no way to report what happened.
$tsRunning = Get-Process TSManager, SetupHost, setupprep -ErrorAction SilentlyContinue
if ($tsRunning) {
    throw "A task sequence or Windows Setup is running ($($tsRunning.Name -join ', ')). Refusing to touch the client."
}

$ccmsetupExe = Join-Path $env:windir 'ccmsetup\ccmsetup.exe'
if ($Stage -ne 'Remove' -and -not (Test-Path -LiteralPath $ccmsetupExe)) {
    throw "$ccmsetupExe not found. Copy the client source to C:\Windows\ccmsetup before installing."
}

Write-Host "Rebuild-CcmClient  site=$SiteCode  mp=$ManagementPoint  stage=$Stage" -ForegroundColor White

# ---------------------------------------------------------------- remove

if ($Stage -in @('Both', 'Remove')) {

    Write-Step 'Stopping and deleting client services'
    foreach ($name in 'CcmExec', 'ccmsetup', 'smstsmgr', 'CmRcService') {
        $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
        if (-not $svc) { Write-Skip "$name - not present"; continue }

        if ($PSCmdlet.ShouldProcess($name, 'Stop and delete service')) {
            try { Stop-Service -Name $name -Force -ErrorAction Stop } catch { }
            # sc.exe delete is the only reliable removal on Windows PowerShell 5.1;
            # Remove-Service is PowerShell 6+.
            & sc.exe delete $name | Out-Null
            Write-Ok "$name - deleted"
        }
    }

    # ccmsetup.exe /uninstall is deliberately NOT run.
    #
    # It is the step that fails. A previous install leaves the MSI registered against a source
    # folder that ccmsetup itself deleted when that install finished, so the uninstall returns
    # 1612 - "installation source for this product is not available" - and the whole attempt ends
    # with 0x8007064c. The manual cleanup below removes the same things without asking MSI to do
    # anything, which is why it works where /uninstall and /forceinstall do not.
    #
    # -TryMsiUninstall exists for a client that is merely unhealthy rather than in this state.
    if ($TryMsiUninstall) {
        Write-Step 'Running the client uninstaller (requested)'
        if ($PSCmdlet.ShouldProcess('ccmsetup.exe /uninstall', 'Run')) {
            if (Test-Path -LiteralPath $ccmsetupExe) {
                $p = Start-Process -FilePath $ccmsetupExe -ArgumentList '/uninstall' -PassThru -Wait
                Write-Ok "ccmsetup /uninstall exit code $($p.ExitCode)"
                if ($p.ExitCode -eq 1612) {
                    Write-Warning '1612 - the MSI source folder is gone. Expected on a client ccmsetup has already cleaned up after; the manual cleanup below does not need it.'
                }
            } else {
                Write-Skip 'ccmsetup.exe not present - continuing with the manual cleanup'
            }
        }
    } else {
        Write-Skip 'Skipping ccmsetup /uninstall - it returns 1612 once the MSI source folder is gone. Pass -TryMsiUninstall to attempt it anyway.'
    }

    Write-Step 'Removing WMI namespaces'
    foreach ($ns in 'ccm', 'ccmvdi', 'smsdm') {
        if ($PSCmdlet.ShouldProcess("root\$ns", 'Remove WMI namespace')) {
            try {
                Get-CimInstance -Namespace 'root' -ClassName '__Namespace' -ErrorAction Stop |
                    Where-Object Name -eq $ns |
                    Remove-CimInstance -ErrorAction Stop
                Write-Ok "root\$ns - removed"
            } catch { Write-Skip "root\$ns - $($_.Exception.Message)" }
        }
    }
    if ($PSCmdlet.ShouldProcess('root\cimv2\sms', 'Remove WMI namespace')) {
        try {
            Get-CimInstance -Namespace 'root\cimv2' -ClassName '__Namespace' -ErrorAction Stop |
                Where-Object Name -eq 'sms' |
                Remove-CimInstance -ErrorAction Stop
            Write-Ok 'root\cimv2\sms - removed'
        } catch { Write-Skip "root\cimv2\sms - $($_.Exception.Message)" }
    }

    Write-Step 'Removing client folders and files'
    foreach ($path in "$env:windir\CCM", "$env:windir\ccmcache", "$env:windir\SMSCFG.ini") {
        if (-not (Test-Path -LiteralPath $path)) { Write-Skip "$path - not present"; continue }
        if ($PSCmdlet.ShouldProcess($path, 'Delete')) {
            try {
                Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop
                Write-Ok "$path - deleted"
            } catch { Write-Warning "$path - $($_.Exception.Message)" }
        }
    }
    Write-Skip "$env:windir\ccmsetup - kept deliberately (holds the installer)"

    Write-Step 'Removing registry keys'
    foreach ($key in 'HKLM:\SOFTWARE\Microsoft\CCM',
                     'HKLM:\SOFTWARE\Microsoft\CCMSetup',
                     'HKLM:\SOFTWARE\Microsoft\SMS') {
        if (-not (Test-Path -LiteralPath $key)) { Write-Skip "$key - not present"; continue }
        if ($PSCmdlet.ShouldProcess($key, 'Delete registry key')) {
            try {
                Remove-Item -LiteralPath $key -Recurse -Force -ErrorAction Stop
                Write-Ok "$key - deleted"
            } catch { Write-Warning "$key - $($_.Exception.Message)" }
        }
    }

    Write-Step 'Removing SMS certificates'
    if ($PSCmdlet.ShouldProcess('Cert:\LocalMachine\SMS', 'Delete certificates')) {
        try {
            $certs = Get-ChildItem 'Cert:\LocalMachine\SMS' -ErrorAction Stop
            foreach ($cert in $certs) {
                Remove-Item -LiteralPath $cert.PSPath -Force -ErrorAction SilentlyContinue
            }
            Write-Ok "$($certs.Count) certificate(s) removed"
        } catch { Write-Skip "Cert:\LocalMachine\SMS - $($_.Exception.Message)" }
    }

    Write-Step 'Removing the ccmsetup retry task'
    # Left behind by a failed install, holding the command line that failed - including
    # /forceinstall if that was tried - and it will run it again on its own schedule.
    # Targeted by name: unregistering everything under the Configuration Manager task path
    # would take out tasks this script has no business touching.
    if ($PSCmdlet.ShouldProcess('Configuration Manager Client Retry Task', 'Unregister')) {
        try {
            $task = Get-ScheduledTask -TaskName 'Configuration Manager Client Retry Task' -ErrorAction SilentlyContinue
            if ($task) {
                $task | Unregister-ScheduledTask -Confirm:$false -ErrorAction Stop
                Write-Ok 'retry task removed'
            } else {
                Write-Skip 'retry task - not present'
            }
        } catch { Write-Skip "retry task - $($_.Exception.Message)" }
    }

    if ($Stage -eq 'Remove') {
        Write-Host "`nRemoval complete. Reboot, then re-run with -Stage Install." -ForegroundColor Yellow
        return
    }
}

# ---------------------------------------------------------------- install

if ($Stage -in @('Both', 'Install')) {

    Write-Step 'Installing the client'

    # SMSMP= is the argument that is usually missing. /mp: only sets the download source.
    # No /forceinstall. It makes ccmsetup uninstall first, which is the 1612 path again.
    $arguments = "/mp:$ManagementPoint SMSSITECODE=$SiteCode SMSMP=$ManagementPoint"
    Write-Host "   $ccmsetupExe $arguments" -ForegroundColor DarkGray

    if ($PSCmdlet.ShouldProcess($ccmsetupExe, "Install with $arguments")) {
        Start-Process -FilePath $ccmsetupExe -ArgumentList $arguments | Out-Null

        # ccmsetup.exe returns immediately and continues in the background, so follow the service
        # rather than the process exit code.
        Write-Host "   waiting for CcmExec (up to $InstallTimeoutMinutes minutes)..." -ForegroundColor DarkGray
        $deadline = (Get-Date).AddMinutes($InstallTimeoutMinutes)
        $running = $false
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 15
            $svc = Get-Service -Name CcmExec -ErrorAction SilentlyContinue
            if ($svc -and $svc.Status -eq 'Running') { $running = $true; break }
        }

        if ($running) {
            Write-Ok 'CcmExec is running'
        } else {
            Write-Warning "CcmExec did not reach Running within $InstallTimeoutMinutes minutes. Check C:\Windows\ccmsetup\Logs\ccmsetup.log."
        }
    }

    Write-Step 'Verifying'
    $checks = [ordered]@{}

    $svc = Get-Service -Name CcmExec -ErrorAction SilentlyContinue
    $checks['CcmExec service'] = if ($svc) { $svc.Status } else { 'not installed' }

    try {
        $client = Get-CimInstance -Namespace 'root\ccm' -ClassName SMS_Client -ErrorAction Stop
        $checks['root\ccm provider'] = 'responds'
        $checks['Client version'] = $client.ClientVersion
    } catch {
        $checks['root\ccm provider'] = "FAILED - $($_.Exception.Message)"
    }

    $assigned = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\SMS\Mobile Client' -ErrorAction SilentlyContinue).AssignedSiteCode
    $checks['Assigned site code'] = if ($assigned) { $assigned } else { '(none yet)' }

    foreach ($k in $checks.Keys) { Write-Host ("   {0,-22}: {1}" -f $k, $checks[$k]) }

    Write-Host "`nRegistration can take several minutes. Confirm with:" -ForegroundColor Yellow
    Write-Host "  Select-String 'Client is registered' `"$env:windir\CCM\Logs\ClientIDManagerStartup.log`"" -ForegroundColor DarkGray
    Write-Host "  Select-String 'Assignment Site Code' `"$env:windir\CCM\Logs\LocationServices.log`" | Select-Object -Last 3" -ForegroundColor DarkGray
}
