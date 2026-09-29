# Getting the tool onto a managed machine

The problem this document exists for: downloading the executable from GitHub onto a managed
endpoint and double-clicking it. That route is blocked in most organisations, and it should be.

## Why SmartScreen blocks it

A file downloaded by a browser gets a **Mark of the Web** — an alternate data stream
(`Zone.Identifier`) recording that it came from the internet. When you launch an executable
carrying that mark, SmartScreen checks its reputation with Microsoft. An executable built an hour
ago and signed by nobody has no reputation, so it is flagged.

Normally that dialog has a **Run anyway** behind "More info". Where the organisation sets

```
Computer Configuration → Administrative Templates → Windows Components
  → Windows Defender SmartScreen → Explorer → Configure Windows Defender SmartScreen
     = Enabled, "Warn and prevent bypass"
```

there is no Run anyway. That is the policy working as intended, and it is not something to argue
with or work around on an endpoint you do not own.

**Two things do not help**, so do not waste time on them: renaming the file, and right-clicking
→ Run as administrator. Both still launch a file carrying the mark.

## Route A — ConfigMgr (recommended)

This is the right answer for a fleet, and it removes the problem rather than dodging it. Content
delivered by the ConfigMgr client never passes through a browser, so it never gets a Mark of the
Web, so SmartScreen has nothing to check.

It also solves two other problems at the same time:

- The client runs it as **SYSTEM**, which holds `SeBackupPrivilege`, so the protected Setup logs
  under `$WINDOWS.~BT` are readable. This is the single biggest cause of an inconclusive report.
- `WinUpgradeDiag.Cli.exe` is manifested `asInvoker`, so there is no UAC prompt to answer on a
  machine nobody is sitting at.

Use the **console head**, not the window:

1. Put `WinUpgradeDiag.Cli.exe` in a package source folder on your content share.
2. Create a Package with a Program, or use **Run Script**.
3. Command line:

   ```
   WinUpgradeDiag.Cli.exe --output \\server\share\UpgradeDiag --quiet
   ```

   Each run writes a `UpgradeDiag_<PC>_<timestamp>` folder, so many machines can write to the same
   share without colliding.

4. Branch on the exit code:

   | Code | Meaning |
   | --- | --- |
   | 0 | Ran and exported |
   | 1 | Export failed — check the output path is writable |
   | 2 | Bad arguments |
   | 3 | Cancelled |
   | 4 | Ran, but not elevated — protected logs were unreadable, so the result is incomplete |

   Code 4 from a ConfigMgr deployment means something is wrong with how it was invoked: as SYSTEM
   it should never happen.

To look at a single machine interactively, deploy the same way and open the HTML report from the
share afterwards. There is no need to put the window on the endpoint at all.

## Route B — an internal file share

Files opened from a UNC path in the **Local Intranet** zone do not get a Mark of the Web. If your
share is already in that zone — most internal file servers are, via the Site to Zone Assignment
policy — copying from it and running is enough.

Check whether a copy carries the mark:

```powershell
Get-Item .\WinUpgradeDiag.exe -Stream Zone.Identifier -ErrorAction SilentlyContinue
```

No output means no mark, and SmartScreen will not fire.

## Route C — clear the mark on one machine

For a single machine where you have already copied the file across:

```powershell
Unblock-File .\WinUpgradeDiag.exe
```

or Properties → tick **Unblock** → OK. This removes the `Zone.Identifier` stream, and with it the
reason SmartScreen was looking.

Do this only on a machine you are authorised to touch, and only with a file whose hash you have
checked against `SHA256SUMS.txt` from the release. Clearing the mark is exactly what an attacker
would want you to do to a file they sent you; the check is what makes it safe.

## If it is still blocked

SmartScreen is not the only gate. If the file is refused with the mark already cleared, something
else is enforcing:

- **AppLocker or WDAC.** These allow-list by publisher, path or hash and do not care about the mark
  at all. An unsigned executable needs a hash rule, and the hash is in `SHA256SUMS.txt`. Your
  endpoint team adds it; you cannot work around it locally, and should not try.
- **Smart App Control.** Windows 11 only, and only ever on for clean installs — a machine upgraded
  from Windows 10 always has it off. See the note in the README.
- **The antivirus agent.** An unsigned executable that reads Setup logs and enumerates drivers
  looks like something worth quarantining. Give the team the hash in advance.

## The actual fix

Code-sign the binary with the organisation's certificate. Everything above is working around the
fact that this executable is signed by nobody, which is a reasonable thing for Windows to object
to. Once signed, Route A stays the right deployment method anyway — it is simply no longer the only
one that works.

`release.yml` is where a signing step belongs, so every tagged build comes out signed rather than
somebody signing a file by hand and losing track of which one shipped.
