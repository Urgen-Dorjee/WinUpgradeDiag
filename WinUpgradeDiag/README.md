# WinUpgradeDiag

A technician-run diagnostic for failed ConfigMgr Windows 10 → Windows 11 in-place upgrades.

Run it on the affected machine. It reads every relevant log already on disk — including the
rollback logs that normally need `takeown` — correlates them onto one timeline, and shows the
root cause with the evidence beside it.

**Status: design complete, implementation not started.** See `docs/DESIGN.md`.

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
| `CLAUDE.md` | Standing project context and constraints |
| `docs/DESIGN.md` | Full specification: architecture, data sources, UI, testing, phases |
| `docs/RULES.md` | Diagnostic rule catalogue with error codes and actions |
| `docs/SECURITY.md` | Security posture, PHI handling, distribution, governance |

## Layout

```
src/WinUpgradeDiag.Core/   collectors, parsers, rules, report
src/WinUpgradeDiag.App/    WPF UI
src/WinUpgradeDiag.Cli/    console head for fleet use
tests/                     xUnit + redacted log fixtures
docs/
```

## Build order

1. Collector, manifest, log viewer, export — no verdicts
2. Parsers, correlation, rules engine, verdict
3. CLI head and ConfigMgr integration, JSON aggregation
4. Remediation actions

## Disclaimer

Provided as-is. Not affiliated with or endorsed by Microsoft. Not a substitute for vendor
support. Test in a lab before use on production endpoints.
