# SCCM Windows 11 24H2 In-Place Upgrade – Recovery Scripts

PowerShell scripts for recovering PCs where a ConfigMgr (SCCM/MECM) in-place upgrade task
sequence was interrupted and Software Center is stuck on **"Installing..."** forever.

Root cause: an unplanned restart leaves an orphaned task sequence execution request in WMI
(`root\ccm\SoftMgmtAgent:CCM_TSExecutionRequest`). The client still believes a task sequence is
running, so reruns queue behind one that no longer exists. The PC does not need replacing.

## Start here

**Right-click `Start-Here.ps1` → Run with PowerShell (as administrator).** It checks the PC,
asks what was on screen before it broke, and runs the matching script. You do not need to open
any script to use these.

Prefer paper? [QUICK-REFERENCE.md](QUICK-REFERENCE.md) is the one-page symptom → script sheet.

## Usage

All scripts require an elevated PowerShell session.

```powershell
powershell -ExecutionPolicy Bypass -File .\Start-Here.ps1
```

Remote:

```powershell
Invoke-Command -ComputerName PC001 -FilePath .\Fix-B-SetupInterrupted.ps1
```

**Safety rule:** never run a fix while `TSManager.exe` or `SetupHost.exe` is running — that means
the upgrade is still alive. Every fix script checks this and exits.

## Which script

| Situation | Script |
| --- | --- |
| Don't know which one to run | `Start-Here.ps1` (menu) |
| Diagnose only | `Check-UpgradeState.ps1` |
| Find the upgrade package's ContentId | `List-CcmCache.ps1` |
| Watch progress without unlocking the PC | `Watch-Smsts.ps1` |
| "Stuck at 99%?" — is Setup alive | `Get-UpgradeProgress.ps1` |
| Interrupted during "Downloading install.wim" | `Fix-A-DownloadInterrupted.ps1` |
| Interrupted during "Windows upgrade progress: xx%" | `Fix-B-SetupInterrupted.ps1` |
| ccmcache was cleared afterwards | `Fix-C-CacheCleared.ps1` |
| Rerun fails in Setup, or disk space low | `Fix-D-SetupLeftovers.ps1` |
| Rerun still refused after a fix | `Reset-TSHistory.ps1` |
| Client itself is broken | `Repair-CcmClient.ps1` |
| Reclaim space after a successful upgrade | `Remove-UpgradeLeftovers.ps1` |

Interrupted *after* the upgrade reboot ("Working on updates xx%") usually needs nothing:
Windows rolls back and the deployment reports Failed, so the user can click Install again.

## Phases, so the symptom maps to the cause

1. **Download** — the task sequence pulls install.wim into `C:\Windows\ccmcache`. Interruptions
   here do not resume, because content is downloaded inside the running task sequence.
2. **Downlevel Setup** — `setup.exe /Auto Upgrade /NoReboot` builds the new OS in
   `C:\$WINDOWS.~BT`. This is the "Windows upgrade progress: xx%" bar. It saturates near 99%
   while profiles are gathered and the rollback point is prepared.
3. **Restart** — `/NoReboot` means Setup hands control back to the task sequence, whose
   **Restart Computer** step starts the full-screen "Working on updates" phase from 0%.

## Logs

| Log | Path |
| --- | --- |
| smsts.log | `C:\Windows\CCM\Logs\SMSTSLog\` (running), `C:\Windows\CCM\Logs\` (finished) |
| execmgr.log | `C:\Windows\CCM\Logs\` |
| CAS.log | `C:\Windows\CCM\Logs\` |
| ContentTransferManager.log | `C:\Windows\CCM\Logs\` |
| DataTransferService.log | `C:\Windows\CCM\Logs\` |
| setupact.log / setuperr.log | `C:\$WINDOWS.~BT\Sources\Panther\` |
| ccmsetup.log | `C:\Windows\ccmsetup\Logs\` |

## Prevention

- Set the deployment to **"Download all content locally before starting task sequence"**, or
  pre-cache the content. BITS downloads resume after a restart; a download inside the task
  sequence does not.
- Deploy as required during a **maintenance window / off-hours**.
- Tell staff not to restart a PC showing the Installation Progress window. Check progress
  remotely with `Watch-Smsts.ps1` instead.

## Disclaimer

Test in a lab or on a pilot machine before running fleet-wide. These scripts stop services,
delete WMI instances and registry keys, and remove folders.
