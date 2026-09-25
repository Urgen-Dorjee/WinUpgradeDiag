# DESIGN — WinUpgradeDiag

Specification for a technician-run diagnostic application for failed ConfigMgr
Windows 10 → Windows 11 in-place upgrades.

---

## 1. Problem

A fleet migration from Windows 10 Enterprise to Windows 11 Enterprise 24H2, deployed by
ConfigMgr task sequence, produces several recurring failure modes. Diagnosing each one today
means opening several logs by hand, in several locations, some of which are unreadable without
taking ownership, and correlating timestamps mentally. Techs reach "replace the machine" far
earlier than the evidence justifies.

The same diagnosis is mechanical: find the phase, find what ended it, name the cause. That is
what this tool automates.

## 2. Goals and non-goals

**Goals**

- Run on the affected machine, offline, with no prior setup, and produce a verdict in under a
  minute.
- Read every relevant log including the ones normally inaccessible without `takeown`.
- Show a plain-language root cause with the evidence beside it.
- Export a self-contained HTML report and an evidence bundle for the ticket.
- Emit machine-readable JSON so results can be aggregated across the fleet.

**Non-goals**

- Not a replacement for SetupDiag. Call it when present; fall back to internal rules otherwise.
- Not an agent. Nothing installs, nothing runs in the background, nothing phones home.
- Not a remediation tool first. Remediation is phase 4 and is always explicitly invoked.

## 3. Constraints

See `AGENTS.md` for the enforced list. In summary: .NET Framework 4.8, WPF, no NuGet, single
`.exe`, zero network, read-only by default, streaming log reads, PHI-safe exports, admin
required.

## 4. Architecture

```
Discover → Collect → Parse → Correlate → Verdict → Report
```

Each stage writes into one `DiagnosticContext`. Stages are independently testable.

### 4.1 Discover

Locations move by phase and by client configuration, so resolve at run time. Never hardcode.

| Source | Discovery |
| --- | --- |
| ConfigMgr log dir | `HKLM\SOFTWARE\Microsoft\CCM\Logging\@GLOBAL` → `LogDirectory` |
| `smsts.log` | `<ccm>\Logs\SMSTSLog\`, `C:\_SMSTaskSequence\Logs\Smstslog\`, `<ccm>\Logs\` — collect **all** that exist; they are different runs. Sort by write time. |
| Setup, current attempt | `C:\$WINDOWS.~BT\Sources\Panther\` (setupact.log, setuperr.log) |
| Setup, rollback | `C:\$WINDOWS.~BT\Sources\Rollback\` (setupact.log, setupmem.dmp, setupapi.dev.log, *.evtx) — highest value, most often missed |
| Setup, completed | `C:\Windows\Panther\` |
| Previous OS | `C:\Windows.old\Windows\Panther\` |
| Downlevel WU comms | `C:\Windows\Logs\Mosetup\BlueBox.log` |
| Servicing | `C:\Windows\Logs\CBS\CBS.log`, `C:\Windows\Logs\DISM\dism.log` |
| Client install | `C:\Windows\ccmsetup\Logs\ccmsetup.log` |
| Other CCM logs | execmgr, CAS, ContentTransferManager, DataTransferService, AppEnforce, PolicyAgent |
| Crash dumps | `C:\Windows\MEMORY.DMP`, `C:\Windows\Minidump\*.dmp` |
| Event logs | System (41, 1001, 1074, 6008, Service Control Manager), Application (1000, 1001) |

Output: a **manifest** (path, exists, size, last write, readable). The manifest alone answers
"which phase did it die in" before a line is parsed. Show it in the UI even when parsing fails.

**Reading protected files.** `$WINDOWS.~BT` and `Rollback` are TrustedInstaller-owned. Enable
`SeBackupPrivilege` and open with `FILE_FLAG_BACKUP_SEMANTICS` rather than modifying ACLs. This
is the main technical reason this is a C# application rather than another script.

### 4.2 Collect (live state)

- Processes: `TSManager`, `SetupHost`, `setupprep`, `CcmExec`, `TrustedInstaller`
- WMI: `CCM_TSExecutionRequest` (the orphan lock), `CacheInfoEx` (content + stale records),
  `CCM_TaskSequence` policy in `root\ccm\Policy\Machine\ActualConfig`
- Registry: execution history per package; `HKLM\SYSTEM\Setup\MoSetup\Volatile\SetupProgress`;
  pending-reboot flags
- Filesystem: `$WINDOWS.~BT`, `$WINDOWS.~WS`, `Windows.old`, `_SMSTaskSequence`, free space
- OS build, edition, TPM, Secure Boot, Memory Integrity (HVCI) state
- Storage health: `MSFT_PhysicalDisk`, `MSFT_StorageReliabilityCounter` (wear, uncorrected
  errors, power-on hours), SMART predict-failure
- Third-party filter drivers: minifilter list, `Win32_SystemDriver` — flag removable-media,
  encryption, AV, VPN classes

### 4.3 Parse

Two formats, one normalised event shape `(Timestamp, Source, Component, Severity, Message)`.

- **ConfigMgr format**: `<![LOG[msg]LOG]!><time="12:10:17.123+420" date="09-18-2026"
  component="TSManager" type="3">`
- **Panther format**: `2026-09-18 12:10:17, Error  MOUPG  message`, with components
  SP, MIG, CONX, PANTHR, IBSLIB, DISM, CSI, CBS. SP, MIG and CONX carry the useful failures.

**Read tail-first.** Seek to `length - N` with a `StreamReader`; default window 2 MB / 5,000
lines per file, configurable, widened only when a rule needs earlier context.

**Extract structured facts**, not just matched strings: task sequence step names and boundaries
(`Start executing an instruction`), step exit codes, Setup exit code and extend code, content
IDs, download job state, bugcheck code and parameters from minidump headers.

### 4.4 Correlate

Merge all events onto one timeline, then bracket the failure:

1. Last `smsts.log` entry and its step name → **phase**
2. Nearest System event 1074 (names who initiated a shutdown) or 41 (power loss or hang) → **trigger**
3. Bugcheck 1001 event and minidump → **crash and implicated driver**
4. Service Control Manager stop for SMS Agent Host → client restart mid-sequence
5. Setup's last progress value vs `setupact.log` last write → stalled or killed
6. Storage reliability counters → hardware as underlying cause

Target output, one sentence: *"Task sequence was at step 'Re-Install SecRMM' when bugcheck
0x1D5 occurred at 14:22:07; step returned 0x80004005; driver X implicated."*

### 4.5 Verdict

Rules are data. Each: `Id, Phase, Source, Predicate, Severity, Meaning, Evidence, Action,
Confidence`. Embedded as JSON resource, overridable with `--rules`. Catalogue in
`docs/RULES.md`.

Severity: `Critical` (this is the cause) / `Warning` (contributing) / `Info` (context).
Rank by confidence and proximity to the failure point; present the top as the verdict, the rest
as contributing findings. Always print evidence beside the claim so the tech can disagree.

**SetupDiag integration.** If `SetupDiag.exe` is found on disk or supplied via `--setupdiag`,
run it and fold its result in as a high-confidence finding. Do not bundle the binary.

### 4.6 Report

Three artefacts per run, into `<output>\UpgradeDiag_<PC>_<timestamp>\`:

1. **UI** — the primary deliverable, see §5.
2. **Self-contained HTML** — inline CSS, no external assets: verdict, timeline, findings with
   evidence, system state, log manifest. Redacted by default.
3. **Evidence zip** — collected logs plus `findings.json`. Local only, unredacted, for internal
   escalation. The JSON is what enables fleet-wide aggregation.

## 5. User interface

Single window, three states: Idle → Running (progress, cancellable) → Results.

- **Verdict card** — colour-coded, one sentence, confidence indicator.
- **Recommended action** — concrete next step with a *Copy command* button. Never auto-runs.
- **Tabs**
  - *Timeline* — merged events, filterable by source and severity, failure point marked.
  - *Findings* — sortable table; each row expands to raw evidence lines.
  - *System* — live state snapshot, including storage health and filter drivers.
  - *Logs* — manifest with readable/unreadable status, plus a viewer with search.
  - *Export* — HTML, zip, JSON; redaction toggle.
  - *Actions* — phase 4 only; the remediation equivalents of the existing PowerShell fixes,
    each behind confirmation, each logged.

Design rules: a technician under pressure must get the answer without scrolling. No jargon in
the verdict line. Never show a spinner without a current-file label. If nothing conclusive is
found, say so plainly and show the manifest — a confident wrong answer is worse than "unknown".

## 6. Testing

**Unit** — xUnit against fixture logs in `tests/fixtures/`. Collect real failed-machine logs,
redact, and commit. Synthetic logs will not catch real format drift. This is the highest-value
task and can start before any code exists.

**Golden-file** — snapshot the generated report for a fixture set; a regex change that breaks
twenty rules then fails the build.

**Lab matrix** (Hyper-V, Windows 10 Enterprise 22H2 with checkpoints):

| Scenario | How to produce |
| --- | --- |
| Orphaned TS, download phase | Hard reset during "Downloading install.wim" |
| Orphaned TS, Setup phase | Hard reset at ~75% |
| Live upgrade (negative test) | Run normally; tool must refuse to advise cleanup |
| No ConfigMgr client | Plain VM |
| Non-admin | Run without elevation |
| Missing `$WINDOWS.~BT` | Clean machine |
| Protected files | Verify `SeBackupPrivilege` path reads Rollback logs |
| Large log performance | 150 MB `setupact.log` fixture |
| Rollback | Real logs from a failed machine as fixture |

**Accuracy tracking** — for each pilot machine record: tool verdict, SetupDiag verdict, actual
root cause after resolution, match yes/no. Ten machines tells you whether the rules engine is
trustworthy. Do not deploy fleet-wide before that number is good.

## 7. Phases

| Phase | Deliverable | Value |
| --- | --- | --- |
| 1 | Discover + Collect + manifest + log viewer + export. No verdicts. | Immediately useful, trivial security review |
| 2 | Parsers, correlation, rules engine, verdict card | The actual product |
| 3 | CLI head, ConfigMgr Run Script integration, JSON aggregation | Finds fleet-wide patterns |
| 4 | Remediation actions | Only once diagnosis is trusted |

## 8. Risks

- **False confidence.** A rules engine will name a symptom as a cause. Mitigated by always
  showing evidence and a confidence level, and by the accuracy tracking in §6.
- **Unreadable evidence.** If the Rollback logs cannot be read, say so explicitly rather than
  concluding "no Setup failure found".
- **Scope creep into a monitoring agent.** It is a hand-run diagnostic. Keep it that way.
