# WinUpgradeDiag

A technician-run diagnostic for failed ConfigMgr Windows 10 → Windows 11 in-place upgrades.

Run it on the affected machine. It reads every relevant log already on disk — including the
rollback logs that normally need `takeown` — correlates them onto one timeline, and shows the
root cause with the evidence beside it.

**Status: Phase 1 implemented** — discovery, live-state collection, log manifest, viewer with
whole-file streaming search, and export. No verdicts yet; see `docs/DESIGN.md` §7 for the phase
plan.

## Why

A fleet migration produces the same handful of failures over and over: an orphaned task
sequence that leaves Software Center stuck on "Installing..." forever, a Setup phase that looks
frozen at 99% but isn't, a rollback to Windows 10 with a generic `0xC1900101`, a
`DRIVER_PNP_WATCHDOG` bugcheck inside a driver-install step, and occasionally a failing SSD
underneath all of it. Each diagnosis is mechanical and each one currently takes a technician
half an hour of opening logs by hand. This automates it.

## Principles

- **Offline.** Zero network calls, ever. No telemetry, no update check.
- **Read-only.** Diagnosis never changes system state. Remediation is separate and explicit.
- **No prerequisites.** Single `.exe`, .NET Framework 4.8 in-box, runs on a broken machine.
- **Evidence or it didn't happen.** Every verdict quotes the log line that produced it.

## Documentation

| Document | Contents |
| --- | --- |
| `AGENTS.md` | Standing project context and constraints |
| `docs/DESIGN.md` | Full specification: architecture, data sources, UI, testing, phases |
| `docs/RULES.md` | Diagnostic rule catalogue with error codes and actions |
| `docs/SECURITY.md` | Security posture, PHI handling, distribution, governance |

## Layout

```
WinUpgradeDiag.sln
src/WinUpgradeDiag.Core/   discovery, collectors, redaction, report export (all logic; only project with tests)
src/WinUpgradeDiag.App/    WPF UI, thin, no business logic
src/WinUpgradeDiag.Cli/    console head for ConfigMgr Run Script
tests/WinUpgradeDiag.Tests/  xUnit tests + fixture logs in tests/fixtures/
docs/
```

## Deploying to a problem machine

The build produces **one file**: `WinUpgradeDiag.exe` (about 400 KB). The Core assembly and all 12
recovery scripts are embedded inside it, so nothing else needs to be copied alongside. One file
also means one hash to code-sign and one hash to allow-list.

**Requirements on the target machine**

| | |
| --- | --- |
| .NET Framework | **4.8** — in-box on Windows 10 1903 and later, and on Windows 11. Nothing to install on a fleet running 20H2/21H2/22H2. |
| Older Windows 10 | 1809 and earlier ship 4.7.2; those need the 4.8 runtime installed first. |
| Privileges | Runs unelevated, but the protected Setup logs under `$WINDOWS.~BT` need administrator. The status bar says which you have. |
| Network | None. The tool makes no outbound connection of any kind. |

**Getting it there.** `docs/SECURITY.md` is explicit that technicians should not download executables
from the internet onto managed endpoints — that is the exact pattern endpoint security hunts for, and
it is likely a policy violation regardless of the tool's quality. Distribute it the same way you
distribute anything else: a ConfigMgr package or Run Script, an internal file share, or a signed copy
on managed removable media. Code-sign it with the organisation's internal certificate and allow-list
that hash with the endpoint security team before any pilot.

## Building

Requires Windows with the .NET Framework 4.8 targeting pack (Visual Studio 2019+ or the .NET
SDK's MSBuild). All four projects are SDK-style and target `net48`; only the test project pulls
in NuGet packages (xUnit), and only for the build machine — nothing ships in the `.exe`.

```
dotnet build WinUpgradeDiag.sln
dotnet test tests/WinUpgradeDiag.Tests/WinUpgradeDiag.Tests.csproj
```

The App requires elevation (its manifest asks for it) because protected-log reads and some WMI
queries need an administrator token. The CLI deliberately runs `asInvoker`: it starts at whatever
privilege the caller has, reports which logs it could not read, and returns exit code 4 so an
unelevated run is detectable rather than silent. Run either as Administrator for full coverage.

## Searching a huge log

`setupact.log` is routinely 100–700 MB. Notepad cannot open a file that size — it reads the whole
thing into memory — and the viewer's tail window only covers the last couple of megabytes, well
under 1% of a large file. Both heads therefore stream the file instead:

```
WinUpgradeDiag.Cli --find 0xC1900101
WinUpgradeDiag.Cli --find "DRIVER_PNP_WATCHDOG" --in "C:\$WINDOWS.~BT\Sources\Rollback\setupact.log"
```

Each hit is printed with its line number and surrounding context, and the summary states whether
the whole file was covered or the scan stopped early. Measured on a 750 MB / 7-million-line
`setupact.log`: about 7 seconds end to end, with the managed heap flat at a few MB regardless of
file size. In the WPF app the same thing is the **Search whole file** button on the Logs tab.

## Build order

1. Discover, collect, manifest, log viewer, export — no verdicts *(this phase)*
2. Parsers, correlation, rules engine, verdict
3. CLI head and ConfigMgr integration, JSON aggregation
4. Remediation actions

## Recovery tools

The recovery scripts are **embedded in the executable**, not loaded from a folder beside it. A
directory of loose `.ps1` files that an elevated process executes is a writable execution path:
whoever can drop a file there decides what runs as administrator. Embedded, the scripts are covered
by whatever signature the `.exe` carries, so code-signing one binary protects all of them, and
tampering means tampering with the signed assembly.

Each run records the SHA-256 of the exact bytes that executed in an audit log beside the diagnostic
output, along with the operator, elevation state and the preconditions observed at launch time.

Before running anything, **Preview** reports what the tool would touch on this machine — whether the
orphaned execution request actually exists, whether the folder it would delete is present and how
large, whether the content id matches anything cached. Most of these scripts have no `-WhatIf`, so a
simulated dry run would be fiction; inspecting live state is checkable.

## Disclaimer

Provided as-is. Not affiliated with or endorsed by Microsoft. Not a substitute for vendor
support. Test in a lab before use on production endpoints.
