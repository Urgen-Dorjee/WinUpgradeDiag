# SECURITY — posture, review notes, and governance

This document exists so a security reviewer can approve the tool quickly, and so the author
does not create a problem for themselves by publishing it.

## Design guarantees

| Guarantee | How it is enforced |
| --- | --- |
| **No network access** | No HTTP client, no socket, no `System.Net` reference. Verifiable by inspecting the source and by running the binary behind a host firewall with all egress denied. |
| **No telemetry, no update check** | Not implemented and not to be added. |
| **Read-only diagnosis** | Collection and parsing never write outside the output folder. Remediation (phase 4) is a separate tab, per-action confirmed and logged. |
| **No installation** | Single portable `.exe`. No service, no scheduled task, no driver, no registry footprint beyond reads. |
| **Least privilege** | Requires administrator (needed to read protected logs and query WMI). Uses `SeBackupPrivilege` to read rather than modifying ACLs or taking ownership. |
| **Bounded output** | All artefacts in one timestamped folder chosen by the operator. |
| **Open source** | The whole tool is inspectable. No obfuscation, no packed resources. |

## Handling of sensitive data

Windows upgrade logs are not neutral text on a clinical workstation:

- `miglog.xml` enumerates migrated file paths, which can include patient document names.
  **The tool never renders its contents.** It reports counts and categories only.
- `setupact.log`, `AppEnforce.log` and profile paths contain usernames and machine names.
- Crash dumps can contain memory contents.

Controls:

1. **Redaction on by default** for HTML and JSON exports: usernames, profile paths, machine
   name. A `--no-redact` switch exists for internal escalation and is off by default.
2. **The evidence zip is never redacted and never leaves the device automatically.** It is
   produced for the operator to attach to an internal ticket, deliberately.
3. **Crash dumps are referenced by path and bugcheck summary, never copied into exports.**
4. **No sample of real customer data is committed to the repository.** Test fixtures are
   synthetic or thoroughly redacted, and redaction is reviewed before commit.

## Deployment — the real risk

The code is the easy part. An unsigned executable downloaded from the internet onto a managed
clinical endpoint is the part that causes an incident:

- **Code-sign the binary** with the organisation's internal certificate. Unsigned binaries are
  blocked or flagged by SmartScreen, Defender, AppLocker and WDAC.
- **Distribute through ConfigMgr** as a package or via the Run Script feature. Do not have
  technicians download executables from GitHub onto workstations — that is the exact pattern
  security teams hunt for, and it is likely a policy violation regardless of the tool's quality.
- **Allow-list the signed hash** with the endpoint security team before any pilot.
- **Announce the pilot** to the security operations team so the tool's behaviour (reading logs,
  querying WMI, elevated) does not generate an alert with no context behind it.

## Governance and IP

The author is a contractor. Before anything is published:

1. **Confirm work-product ownership** under the contracting agreement. A tool built for and
   during a client migration project is plausibly the client's property.
2. **Get written approval** from the engagement manager and the client's security or compliance
   function before making the repository public.
3. **Start private.** Use a private repository or the client's internal source control until
   approval exists. Nothing is lost by waiting; a public repository cannot be unpublished.
4. **Keep the repository vendor-neutral.** No customer name in the project name, code,
   documentation, screenshots or commit history. No real hostnames, usernames, or logs.
   Scrub screenshots before they go in `docs/`.
5. **Licence** — MIT or Apache 2.0 once approved, with a disclaimer that the tool is provided
   as-is and is not a substitute for vendor support.

## Review checklist

- [ ] Source contains no networking code
- [ ] No third-party binary dependencies
- [ ] Binary is signed
- [ ] Redaction verified on a real report before sharing outside the team
- [ ] No customer identifiers anywhere in the repository or its history
- [ ] Written approval on file for publication
- [ ] Distribution path agreed with endpoint security
