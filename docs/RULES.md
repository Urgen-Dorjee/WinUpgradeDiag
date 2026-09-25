# RULES — diagnostic catalogue

Starter rules for the engine. Each becomes a JSON entry with an id, a predicate over parsed
events or collected state, a severity, a meaning, and a recommended action. Every rule must be
able to quote the evidence that fired it.

Confidence: **High** = the evidence names the cause. **Medium** = strong inference.
**Low** = suggestive, needs a human.

---

## A. Task sequence state

| Id | Signal | Meaning | Action | Conf |
| --- | --- | --- | --- | --- |
| TS-001 | No `TSManager` process **and** `CCM_TSExecutionRequest` exists | Orphaned task sequence. Software Center shows "Installing..." permanently and blocks reruns. | Clear the execution request and `C:\_SMSTaskSequence`, restart CcmExec, pull machine policy, rerun. | High |
| TS-002 | `TSManager` **or** `SetupHost` running | Upgrade is live. | **Suppress all cleanup advice.** Advise monitoring only. | High |
| TS-003 | `execmgr.log`: a task sequence is already running | Rerun blocked by the stale lock | Same as TS-001 | High |
| TS-004 | `smsts.log` last step + non-zero return code | Names the failing step | Look up the step's own installer log | High |
| TS-005 | Execution history present, deployment not re-runnable | History blocks restart | Clear the package's execution history key | Medium |

## B. Content and download

| Id | Signal | Meaning | Action | Conf |
| --- | --- | --- | --- | --- |
| CT-001 | `CAS.log` hash mismatch | Cached content corrupt | Delete that cache element via the client, rerun | High |
| CT-002 | `0x80070002` file not found on content | Cache record points at a missing folder (manual ccmcache deletion) | Remove stale `CacheInfoEx` records, rerun | High |
| CT-003 | `ContentTransferManager.log` job suspended/stalled, no progress | Download interrupted | Clear TS, delete partial content, rerun | Medium |
| CT-004 | Last `smsts.log` line inside a download step + unexpected restart event | Interrupted mid-download; cannot resume | Same as CT-003 | High |
| CT-005 | Cache size < install.wim + apps in sequence | Cache too small; late-sequence failures | Raise client cache size | Medium |

## C. Windows Setup — downlevel and stall

| Id | Signal | Meaning | Action | Conf |
| --- | --- | --- | --- | --- |
| SU-001 | `SetupHost` alive, `setupact.log` written < 5 min ago | Working, not stuck — including at 99% | None. Wait. | High |
| SU-002 | `SetupHost` alive, `setupact.log` untouched > 30 min | Possibly blocked | Read the last 30 log lines for the blocking operation | Medium |
| SU-003 | No `SetupHost`, UI still showed a percentage | Setup exited; the dialog is stale | Read the Setup exit code from `smsts.log` | High |
| SU-004 | Setup exit `0xC1900210` | **No compatibility issues — success.** Not an error. | Keep reading; the failure is elsewhere | High |
| SU-005 | Setup exit `0xC1900208` | Compatibility block | Parse `CompatData*.xml` and `setuperr.log` for the blocking app or driver | High |
| SU-006 | Free space below threshold | Setup will fail or has failed | Clear Setup leftovers; ensure ample free space before retry | High |

## D. Rollback

| Id | Signal | Meaning | Action | Conf |
| --- | --- | --- | --- | --- |
| RB-001 | `C:\$WINDOWS.~BT\Sources\Rollback\` exists | Setup reverted to Windows 10 | Parse the rollback set before anything else | High |
| RB-002 | `0xC1900101-0x<ext>` | Generic rollback; the extend code gives the phase (SAFE_OS, MIGRATE_DATA, SECOND_BOOT, sysprep 0x30018) | Map the extend code to the phase, then find the driver | Medium |
| RB-003 | `setupmem.dmp` present in Rollback | The OS bugchecked during the upgrade | Parse the bugcheck code; `!analyze -v` names the `.sys` | High |
| RB-004 | `setupapi.dev.log` shows a failed device install near the end | Device/driver install failure | Name the `.inf` and the device | High |
| RB-005 | Rollback `*.evtx` shows unexpected reboot | Crash or power loss during upgrade | Correlate with System 41 / 1001 | Medium |

## E. Crashes and drivers — the flagship case

| Id | Signal | Meaning | Action | Conf |
| --- | --- | --- | --- | --- |
| BC-001 | Bugcheck `0x1D5 DRIVER_PNP_WATCHDOG` | A driver failed to complete a PnP operation within the watchdog timeout. Typical of a PnP filter driver during device-stack re-enumeration. | Identify the driver from the dump; see BC-004 | High |
| BC-002 | Bugcheck `0x101 CLOCK_WATCHDOG_TIMEOUT` / `0x133 DPC_WATCHDOG_VIOLATION` | Watchdog bugcheck; commonly a driver or firmware issue | Same path as BC-001; also check BIOS/firmware level | Medium |
| BC-003 | Bugcheck `0x9F DRIVER_POWER_STATE_FAILURE` | Driver hung on a power transition, often during an upgrade reboot | Identify the driver | Medium |
| **BC-004** | **Bugcheck timestamp falls inside a running TS step's window, and that step returned `0x80004005`** | **The step's payload caused the crash.** Observed case: a removable-media security product (PnP filter driver) re-installed by the task sequence, bugchecking with 0x1D5 and failing the step with the generic 0x80004005. | Name the step and the driver. Recommend: verify vendor support for the target build; add a restart before the step; or move the install out of the task sequence into a separate post-upgrade deployment; pilot with the step disabled to confirm causality. | High |
| BC-005 | Third-party minifilter present from a risky class (removable media, encryption, AV, VPN) **and** a rollback or watchdog bugcheck occurred | Filter driver is a candidate cause | List the filters as contributing findings, ranked by class | Low |
| BC-006 | Memory Integrity (HVCI) enabled **and** an unsigned or old third-party driver present | The target build enforces stricter driver code integrity; a driver that loaded on Windows 10 may be refused | Flag the driver and the HVCI state together | Medium |

## F. Servicing and client

| Id | Signal | Meaning | Action | Conf |
| --- | --- | --- | --- | --- |
| CB-001 | `0x800F0922` or CBS errors | Servicing stack or component store problem | `DISM /RestoreHealth`, then `sfc /scannow`; different track from a driver rollback | Medium |
| CL-001 | `ccmsetup.log` shows repair/upgrade during the sequence window | Client restarted mid-sequence, killing TSManager | Note as the trigger; do not repair again mid-diagnosis | High |
| CL-002 | CcmExec service stop in System log inside the sequence window | Same as CL-001 | Same | High |

## G. Hardware

| Id | Signal | Meaning | Action | Conf |
| --- | --- | --- | --- | --- |
| HW-001 | SMART predict-failure true, or vendor POST disk warning | Drive reporting imminent failure | **Stop software triage.** Back up, run vendor diagnostics, record the failure ID, plan replacement. | High |
| HW-002 | `ReadErrorsUncorrected` or `WriteErrorsUncorrected` > 0 | Real media errors | As HW-001 | High |
| HW-003 | `Wear` ≥ 90 | NAND near rated endurance | Plan replacement; not necessarily today's cause | Medium |
| HW-004 | Any of the above **and** an upgrade failure | Hardware may be the underlying cause | Present hardware as the top finding; software findings become contributing | High |

## H. Event-log triggers (who ended it)

| Id | Signal | Meaning |
| --- | --- | --- |
| EV-001 | System 1074 inside the sequence window | Names the user or process that initiated the shutdown. Usually settles "who rebooted it". |
| EV-002 | System 41 | Power loss or hang — no clean shutdown |
| EV-003 | System 6008 | Previous shutdown was unexpected |
| EV-004 | Application 1000/1001 naming `TSManager.exe` | The engine itself crashed |

---

## Precedence

When several rules fire, rank as: HW-00x → BC-004 → RB-00x → TS-001 → CT-00x → SU-00x.
Hardware first, because everything downstream of a failing disk is a symptom. TS-002 overrides
everything: if the upgrade is live, the tool advises nothing but patience.
