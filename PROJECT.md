# project context

Read this first. It is the standing context for this repository.

## What this is

**WinUpgradeDiag** — a Windows desktop application that a technician runs on a PC where a
ConfigMgr (SCCM) Windows 10 → Windows 11 in-place upgrade failed, stalled, or rolled back.
It reads every relevant log already on the machine, correlates them onto one timeline, and
shows a plain-language root cause plus a recommended action in a UI on that same machine.

It replaces a set of PowerShell scripts that each handled one failure mode. See
`docs/DESIGN.md` for the full specification and `docs/RULES.md` for the diagnostic rules.

## Non-negotiable constraints

1. **Zero network calls.** No telemetry, no update check, no outbound anything, ever.
   This is what makes the security review pass in a hospital environment. Do not add an HTTP
   client, do not reference `System.Net.Http`, do not add a "check for updates" feature.
2. **Read-only by default.** Diagnosis never modifies system state. Remediation lives behind a
   separate, explicitly gated Actions tab with per-action confirmation.
3. **Must run on a broken machine with no internet.** Target .NET Framework 4.8 (in-box on
   Windows 10 1903+ and Windows 11). No NuGet dependencies. Ship one `.exe`.
4. **Degrade, never crash.** Access denied on a log is normal. Report the gap and keep going.
5. **Never load a whole log into memory.** `setupact.log` is routinely 100–200 MB. Stream and
   read tail-first.
6. **PHI safety.** `miglog.xml` enumerates migrated file paths, which on a clinical workstation
   can include patient document names. Never render its contents — count and summarise only.
   Redact usernames and profile paths in exported HTML/JSON by default.
7. **Vendor-neutral.** No customer name, no real hostnames, no real logs in the repo.

## Stack and layout

- .NET Framework 4.8, C#, WPF (MVVM), xUnit for tests.
- `src/WinUpgradeDiag.Core/` — collectors, parsers, rules, report. All logic lives here; it is
  the only project with tests pointed at it.
- `src/WinUpgradeDiag.App/` — WPF UI, thin, no business logic.
- `src/WinUpgradeDiag.Cli/` — console head over the same Core, for ConfigMgr Run Script.
- `tests/WinUpgradeDiag.Tests/` — xUnit, runs against fixture logs in `tests/fixtures/`.

## Conventions

- Collectors return data; they never interpret it. Parsers emit neutral events. Rules interpret
  events. Keep those three separate.
- Every finding carries its evidence: source file, line number, timestamp, raw text. A verdict
  with no quotable evidence is a bug.
- Rules are data (embedded JSON), not `if` statements scattered through code.
- Requires administrator (app manifest). Use `SeBackupPrivilege` +
  `FILE_FLAG_BACKUP_SEMANTICS` to read TrustedInstaller-owned logs rather than taking ownership.
- All output goes to one timestamped folder under `%ProgramData%` or a user-chosen path.
  Never write into the repo folder.

## Real failure cases this tool must handle

These came from the live migration that motivated the project. Treat them as acceptance tests.

1. **Orphaned task sequence.** Unplanned restart kills `TSManager.exe`; the
   `CCM_TSExecutionRequest` instance survives in WMI; Software Center shows "Installing..."
   forever and blocks reruns. Distinguish this from a genuinely running upgrade (TSManager
   alive) — the tool must never advise cleanup on a live task sequence.
2. **Stuck at 99%.** The downlevel Setup phase saturates near the end. Alive vs hung is decided
   by `setupact.log` write time advancing and SetupHost CPU, not by the percentage.
3. **Interrupted download.** Restart during "Downloading install.wim" discards partial content;
   the task sequence cannot resume mid-step.
4. **`DRIVER_PNP_WATCHDOG (0x1D5)` + TS step failure `0x80004005`.** A PnP filter driver
   (removable-media security software) fails to complete a PnP operation during its re-install
   step, bugchecks the machine, and fails the task sequence. Correlating the bugcheck timestamp
   with the running TS step is the flagship rule — see `docs/RULES.md`.
5. **Rollback to Windows 10** with generic `0xC1900101`. Evidence is in
   `C:\$WINDOWS.~BT\Sources\Rollback\` (setupact.log, setupmem.dmp, setupapi.dev.log, *.evtx).
6. **Failing SSD.** SMART warnings and storage reliability counters must be checked, so that a
   hardware failure is not misdiagnosed as a software problem.

## Build order

Phase 1 collector + viewer + export (no verdicts) → Phase 2 rules engine and verdict →
Phase 3 CLI and ConfigMgr integration → Phase 4 remediation actions. Do not skip ahead;
phase 1 is independently useful and easy to get security approval for.

## What to ask the user about

- Whether approval to publish publicly has been granted (see `docs/SECURITY.md`).
- Real redacted log fixtures — the rules cannot be validated without them.
- The exact ConfigMgr task sequence step names in their environment.
