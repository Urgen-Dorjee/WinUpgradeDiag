# WinUpgradeDiag

A technician-run diagnostic for failed ConfigMgr Windows 10 → Windows 11 in-place upgrades.

Run it on the affected machine. It reads every relevant log already on disk — including the
rollback logs that normally need `takeown` — correlates them onto one timeline, and shows the
root cause with the evidence beside it.

**Status: Phase 1 implemented** — discovery, live-state collection, log manifest, viewer, and
export. No verdicts yet; see `docs/DESIGN.md` §7 for the phase plan.

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

## Building

Requires Windows with the .NET Framework 4.8 targeting pack (Visual Studio 2019+ or the .NET
SDK's MSBuild). All four projects are SDK-style and target `net48`; only the test project pulls
in NuGet packages (xUnit), and only for the build machine — nothing ships in the `.exe`.

```
dotnet build WinUpgradeDiag.sln
dotnet test tests/WinUpgradeDiag.Tests/WinUpgradeDiag.Tests.csproj
```

The App and CLI projects require elevation to run for real (protected-log reads and WMI), so
run the built `.exe` as Administrator.

## Build order

1. Discover, collect, manifest, log viewer, export — no verdicts *(this phase)*
2. Parsers, correlation, rules engine, verdict
3. CLI head and ConfigMgr integration, JSON aggregation
4. Remediation actions

## Disclaimer

Provided as-is. Not affiliated with or endorsed by Microsoft. Not a substitute for vendor
support. Test in a lab before use on production endpoints.
