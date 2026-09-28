# Quick reference – which script, when

**Easiest route: right-click `Start-Here.ps1` → Run with PowerShell (as administrator).**
It checks the PC, asks what you saw on screen, and runs the right script for you.
Nothing below needs to be opened in Notepad.

---

## Rule 0 – before anything

Run `Check-UpgradeState.ps1`.

- **"Upgrade is still running"** → stop. Do not run any fix. Watch it with `Watch-Smsts.ps1`.
- **"Orphaned task sequence"** → pick the fix below.

Never run a fix while `TSManager.exe` or `SetupHost.exe` is alive.

---

## Symptom → script

| What the user reports | What you saw on screen before it broke | Run |
| --- | --- | --- |
| Software Center stuck on "Installing...", never downloads again | `Downloading install.wim (xx% complete)` | `Fix-A-DownloadInterrupted.ps1` (needs a ContentId from `List-CcmCache.ps1`) |
| Software Center stuck on "Installing...", download had finished | `Windows upgrade progress: xx%` | `Fix-B-SetupInterrupted.ps1` |
| Same, but someone already deleted ccmcache | Either | `Fix-C-CacheCleared.ps1` |
| Rerun starts then fails fast, or C: nearly full | Either | `Fix-D-SetupLeftovers.ps1`, then retry |
| Fix ran fine, Software Center still won't launch it | Either | `Reset-TSHistory.ps1` (task sequence package ID) |
| Nothing above works | Either | `Repair-CcmClient.ps1`, then `Fix-B`, then retry |
| Upgrade worked, C: is short on space | n/a | `Remove-UpgradeLeftovers.ps1` |
| PC rebooted itself and came back on the old Windows | Full-screen "Working on updates xx%" | **Nothing.** Windows rolled back and the deployment reports Failed. Click Install again. |

---

## "Is it stuck or just slow?" (the 99% question)

Run `Get-UpgradeProgress.ps1 -ComputerName PCNAME`.

| Reading | Meaning | Action |
| --- | --- | --- |
| `setupact.log` written in the last few minutes, SetupHost using CPU | Working | Leave it. 99% for 10–30 min is normal. |
| Log untouched 30+ min, SetupHost still present | Possibly blocked | `Get-UpgradeProgress.ps1 -ComputerName PCNAME -ShowLogTail 30` |
| SetupHost gone, dialog still shows a percentage | Setup finished; the UI is stale | Check `smsts.log`. `0xC1900210` = success. It should be heading for the restart. |

---

## Watching a PC you can't unlock

```powershell
.\Watch-Smsts.ps1 -ComputerName PCNAME
.\Get-UpgradeProgress.ps1 -ComputerName PCNAME
```

Never unlock or reboot a PC just to check progress. That is what creates the stuck state.

---

## Running them

```powershell
# Locally, elevated
powershell -ExecutionPolicy Bypass -File .\Start-Here.ps1

# One fix, remotely
Invoke-Command -ComputerName PC001 -FilePath .\Fix-B-SetupInterrupted.ps1
```

## After any fix

Close and reopen Software Center, then click **Install**.
