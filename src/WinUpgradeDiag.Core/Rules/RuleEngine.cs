using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using WinUpgradeDiag.Core.Collect;
using WinUpgradeDiag.Core.Discovery;
using WinUpgradeDiag.Core.IO;
using WinUpgradeDiag.Core.Orchestration;

namespace WinUpgradeDiag.Core.Rules
{
    /// <summary>
    /// Turns collected state and log contents into ranked findings and one verdict.
    /// <para>
    /// Precedence follows docs/RULES.md: hardware first, because everything downstream of a failing
    /// disk is a symptom; and TS-002 overrides everything, because advising cleanup on a live
    /// upgrade is the one mistake this tool must never make.
    /// </para>
    /// </summary>
    public sealed class RuleEngine
    {
        /// <summary>Below this, Setup is known to fail or to have failed. 20 GB is Microsoft's practical floor.</summary>
        public const long MinimumFreeBytes = 20L * 1024 * 1024 * 1024;

        /// <summary>NAND wear at or above this is worth flagging even if it is not today's cause.</summary>
        public const ulong WearWarningThreshold = 90;

        private readonly LogSearcher _searcher = new LogSearcher();

        public Verdict Evaluate(
            DiagnosticContext context,
            IProgress<string> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var state = context.SystemState ?? new SystemState();
            var findings = new List<Finding>();

            findings.AddRange(EvaluateLiveUpgrade(state));
            findings.AddRange(EvaluateHardware(state));
            findings.AddRange(EvaluateTaskSequence(state));
            findings.AddRange(EvaluateRollback(context));
            findings.AddRange(EvaluateDiskSpace(state));
            findings.AddRange(EvaluateMachineState(state));
            findings.AddRange(ScanLogs(context, progress, cancellationToken));

            var ranked = Rank(findings);
            var gaps = CollectGaps(context, state);

            return BuildVerdict(ranked, gaps, state, context);
        }

        // ----------------------------------------------------------------- live upgrade

        /// <summary>
        /// TS-002. Checked first and, if it fires, everything else becomes context: the tool must
        /// never recommend clearing a task sequence that is still running.
        /// </summary>
        private static IEnumerable<Finding> EvaluateLiveUpgrade(SystemState state)
        {
            var processes = state.Processes;
            if (processes == null)
            {
                yield break;
            }

            var live = new[] { "TSManager", "SetupHost", "setupprep" }
                .Where(processes.IsRunning)
                .ToList();

            if (live.Count == 0)
            {
                yield break;
            }

            var evidence = live
                .Select(name =>
                {
                    var info = processes.Processes.First(p =>
                        p.IsRunning && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                    return new Evidence(
                        "Running processes",
                        null,
                        name + " is running (pid " + (info.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "unknown") + ")");
                })
                .ToList();

            yield return new Finding(
                "TS-002",
                "An upgrade is running on this machine right now",
                Severity.Info,
                Confidence.High,
                "Setup or the task sequence engine is alive, so this machine is mid-upgrade rather than failed. " +
                "A Setup phase that appears frozen near the end is usually still working.",
                "Do not clear the task sequence, delete content, or restart the client. Wait, and re-run this " +
                "diagnostic later if it has not finished.",
                evidence);
        }

        // ----------------------------------------------------------------- hardware first

        private static IEnumerable<Finding> EvaluateHardware(SystemState state)
        {
            foreach (var disk in state.StorageHealth ?? new List<StorageHealthInfo>())
            {
                if (disk.Error != null)
                {
                    continue;
                }

                var name = disk.FriendlyName ?? disk.DeviceId ?? "disk";

                if (!string.IsNullOrEmpty(disk.HealthStatus) &&
                    !string.Equals(disk.HealthStatus, "Healthy", StringComparison.OrdinalIgnoreCase))
                {
                    yield return new Finding(
                        "HW-001",
                        "The disk reports itself as " + disk.HealthStatus.ToLowerInvariant(),
                        Severity.Critical,
                        Confidence.High,
                        "Windows reports this drive as not healthy. Everything downstream of a failing disk — " +
                        "including a failed upgrade — is a symptom rather than the cause.",
                        "Stop software triage. Back the machine up, run the vendor's drive diagnostics, record the " +
                        "failure ID, and plan a replacement.",
                        new[]
                        {
                            new Evidence("Storage health (WMI)", null,
                                name + ": HealthStatus = " + disk.HealthStatus +
                                (disk.OperationalStatus != null ? ", OperationalStatus = " + disk.OperationalStatus : ""))
                        });
                }

                var readErrors = disk.ReadErrorsUncorrected ?? 0;
                var writeErrors = disk.WriteErrorsUncorrected ?? 0;
                if (readErrors > 0 || writeErrors > 0)
                {
                    yield return new Finding(
                        "HW-002",
                        "The disk is reporting uncorrected read/write errors",
                        Severity.Critical,
                        Confidence.High,
                        "Uncorrected errors mean the drive lost data it could not recover. This causes exactly the " +
                        "kind of file-copy failure an in-place upgrade trips over.",
                        "Treat this as failing hardware. Back up, run vendor diagnostics, and replace the drive.",
                        new[]
                        {
                            new Evidence("Storage reliability counters (WMI)", null,
                                name + ": ReadErrorsUncorrected = " + readErrors + ", WriteErrorsUncorrected = " + writeErrors)
                        });
                }

                if (disk.Wear.HasValue && disk.Wear.Value >= WearWarningThreshold)
                {
                    yield return new Finding(
                        "HW-003",
                        "The SSD is near the end of its rated write endurance",
                        Severity.Warning,
                        Confidence.Medium,
                        "Wear of " + disk.Wear.Value + "% means the NAND is close to its rated life. Not necessarily " +
                        "today's cause, but it makes failures more likely and is worth planning around.",
                        "Plan a replacement. If the upgrade keeps failing on file operations, bring that plan forward.",
                        new[] { new Evidence("Storage reliability counters (WMI)", null, name + ": Wear = " + disk.Wear.Value + "%") });
                }
            }
        }

        // ----------------------------------------------------------------- task sequence

        private static IEnumerable<Finding> EvaluateTaskSequence(SystemState state)
        {
            var request = state.TaskSequenceExecutionRequest;
            var processes = state.Processes;
            if (request == null || !request.ExecutionRequestExists || processes == null)
            {
                yield break;
            }

            // TS-001 requires BOTH halves: a surviving WMI lock AND no live engine. AGENTS.md calls
            // out that the tool must never advise cleanup while TSManager is alive.
            if (processes.IsRunning("TSManager"))
            {
                yield break;
            }

            var detail = "CCM_TSExecutionRequest exists in WMI";
            if (!string.IsNullOrEmpty(request.PackageId))
            {
                detail += " for package " + request.PackageId;
            }

            yield return new Finding(
                "TS-001",
                "A task sequence is stuck: Software Center will show \"Installing...\" forever",
                Severity.Critical,
                Confidence.High,
                "The task sequence engine is not running, but its execution request survives in WMI. " +
                "The client still believes a task sequence is in progress, so Software Center shows it as " +
                "installing and refuses to run it again. An unplanned restart during the upgrade causes this.",
                "Clear the stale execution request and the task sequence working folder, restart the ConfigMgr " +
                "client, refresh machine policy, then re-run the deployment.",
                new[]
                {
                    new Evidence("WMI: root\\ccm\\SoftMgmtAgent", null, detail),
                    new Evidence("Running processes", null, "TSManager is not running")
                },
                command: "Get-WmiObject -Namespace root\\ccm\\SoftMgmtAgent -Class CCM_TSExecutionRequest | Remove-WmiObject; " +
                         "Remove-Item C:\\_SMSTaskSequence -Recurse -Force; Restart-Service CcmExec");
        }

        // ----------------------------------------------------------------- rollback

        private static IEnumerable<Finding> EvaluateRollback(DiagnosticContext context)
        {
            var rollback = context.Manifest
                .Where(m => m.Source.Category == LogSourceCategory.SetupRollback && m.Exists)
                .ToList();

            if (rollback.Count == 0)
            {
                yield break;
            }

            var evidence = rollback
                .Select(m => new Evidence(m.ResolvedPath, null,
                    "Present" + (m.SizeKnown ? ", " + LogSearchView.FormatSize(m.SizeBytes) : "") +
                    (m.LastWriteTimeUtc.HasValue
                        ? ", last written " + m.LastWriteTimeUtc.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC"
                        : "")))
                .ToList();

            var dumpPresent = rollback.Any(m =>
                m.ResolvedPath.EndsWith("setupmem.dmp", StringComparison.OrdinalIgnoreCase));

            yield return new Finding(
                "RB-001",
                "Windows Setup rolled this machine back to the previous version",
                Severity.Critical,
                Confidence.High,
                "The Rollback folder exists, which Setup only creates when it gives up and reverts. " +
                (dumpPresent
                    ? "A crash dump sits alongside it, so the machine bugchecked during the upgrade — that is the thread to pull."
                    : "These logs record what Setup was doing immediately before it reverted."),
                dumpPresent
                    ? "Analyse the crash dump to name the offending driver, then check whether its vendor supports the target build."
                    : "Read the rollback setupact.log and setupapi.dev.log for the last operation before the revert.",
                evidence);
        }

        // ----------------------------------------------------------------- disk space

        private static IEnumerable<Finding> EvaluateDiskSpace(SystemState state)
        {
            if (!state.SystemDriveFreeBytes.HasValue)
            {
                yield break;
            }

            var free = state.SystemDriveFreeBytes.Value;
            if (free >= MinimumFreeBytes)
            {
                yield break;
            }

            yield return new Finding(
                "SU-006",
                "Not enough free space on the system drive for an in-place upgrade",
                Severity.Critical,
                Confidence.High,
                "Only " + LogSearchView.FormatSize(free) + " is free. An in-place upgrade needs roughly " +
                LogSearchView.FormatSize(MinimumFreeBytes) + " to stage the new image and keep a rollback copy, " +
                "so it will fail — or already has — regardless of anything else.",
                "Free up space before retrying: clear previous Setup leftovers, empty the ConfigMgr cache, and " +
                "remove Windows.old if a previous upgrade left one behind.",
                new[]
                {
                    new Evidence("System drive", null,
                        LogSearchView.FormatSize(free) + " free of " +
                        LogSearchView.FormatSize(state.SystemDriveTotalBytes ?? 0))
                },
                command: "cleanmgr /sageset:1");
        }

        // ----------------------------------------------------------------- machine state

        private static IEnumerable<Finding> EvaluateMachineState(SystemState state)
        {
            if (state.PendingReboot != null && state.PendingReboot.Any)
            {
                var reasons = new List<string>();
                if (state.PendingReboot.ComponentBasedServicing) reasons.Add("component servicing");
                if (state.PendingReboot.WindowsUpdate) reasons.Add("Windows Update");
                if (state.PendingReboot.PendingFileRenameOperations) reasons.Add("pending file renames");

                yield return new Finding(
                    "PR-001",
                    "This machine is waiting for a restart",
                    Severity.Warning,
                    Confidence.High,
                    "A pending restart blocks servicing operations. Setup will usually refuse to start, or fail early, " +
                    "until the machine has rebooted.",
                    "Restart the machine before attempting the upgrade again.",
                    new[] { new Evidence("Registry", null, "Reboot pending due to: " + string.Join(", ", reasons)) });
            }

            if (state.MemoryIntegrityEnabled == true)
            {
                yield return new Finding(
                    "BC-006",
                    "Memory Integrity is on, which enforces stricter driver signing",
                    Severity.Info,
                    Confidence.Low,
                    "With Memory Integrity (HVCI) enabled, the target build refuses drivers it considers unsafe. " +
                    "A third-party driver that loaded fine before an upgrade can be rejected afterwards.",
                    "If the upgrade failed on a driver, check that driver against the target build's signing requirements.",
                    new[] { new Evidence("Registry: DeviceGuard", null, "HypervisorEnforcedCodeIntegrity is enabled") });
            }
        }

        // ----------------------------------------------------------------- log scanning

        /// <summary>
        /// Streams each readable log once, looking for every signature at the same time. A separate
        /// pass per code over a 700 MB setupact.log would take minutes; one pass takes seconds.
        /// </summary>
        private IEnumerable<Finding> ScanLogs(
            DiagnosticContext context, IProgress<string> progress, CancellationToken cancellationToken)
        {
            var findings = new List<Finding>();
            var patterns = ErrorSignatureCatalog.All.Select(s => s.Pattern).Distinct().ToList();

            var targets = context.Manifest
                .Where(m => m.Exists && m.Readable)
                .Where(m => IsScannableText(m.ResolvedPath))
                .GroupBy(m => m.ResolvedPath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            foreach (var target in targets)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                progress?.Report("Scanning " + System.IO.Path.GetFileName(target.ResolvedPath) + " for known errors");

                MultiSearchResult result;
                try
                {
                    result = _searcher.SearchMany(
                        target.ResolvedPath, patterns, maxMatchesPerQuery: 20, contextLines: 1,
                        progress: null, cancellationToken: cancellationToken);
                }
                catch (Exception)
                {
                    // Degrade, never crash: an unreadable log is recorded as a gap elsewhere.
                    continue;
                }

                foreach (var signature in ErrorSignatureCatalog.All)
                {
                    var hits = result.For(signature.Pattern);
                    if (hits == null || hits.Matches.Count == 0)
                    {
                        continue;
                    }

                    var evidence = hits.Matches
                        .Take(3)
                        .Select(m => new Evidence(target.ResolvedPath, m.LineNumber, m.Text.Trim()))
                        .ToList();

                    var title = signature.Title;
                    if (hits.Matches.Count > 1)
                    {
                        title += " (" + hits.Matches.Count.ToString("N0", CultureInfo.CurrentCulture) +
                                 (hits.MatchLimitReached ? "+" : "") + " occurrences)";
                    }

                    findings.Add(new Finding(
                        signature.RuleId, title, signature.Severity, signature.Confidence,
                        signature.Meaning, signature.Action, evidence, signature.Command));
                }
            }

            return findings;
        }

        private static bool IsScannableText(string path)
        {
            string reason;
            if (!LogViewerPolicy.CanRender(path, out reason))
            {
                return false;
            }

            var extension = System.IO.Path.GetExtension(path);
            return string.Equals(extension, ".log", StringComparison.OrdinalIgnoreCase);
        }

        // ----------------------------------------------------------------- ranking and verdict

        /// <summary>
        /// Orders findings by the precedence in docs/RULES.md. A live upgrade sorts to the very top
        /// so its "change nothing" advice is the first thing read; otherwise hardware leads, then
        /// severity, then confidence.
        /// </summary>
        private static IReadOnlyList<Finding> Rank(IEnumerable<Finding> findings)
        {
            return findings
                .GroupBy(f => f.Id + "|" + f.Title)
                .Select(g => g.First())
                .OrderByDescending(f => f.Id == "TS-002" ? 1 : 0)
                .ThenByDescending(f => f.Id.StartsWith("HW-", StringComparison.Ordinal) ? 1 : 0)
                .ThenByDescending(f => (int)f.Severity)
                .ThenByDescending(f => (int)f.Confidence)
                .ThenBy(f => f.Id, StringComparer.Ordinal)
                .ToList();
        }

        private static IReadOnlyList<string> CollectGaps(DiagnosticContext context, SystemState state)
        {
            var gaps = new List<string>();

            if (!state.IsElevated)
            {
                gaps.Add("Not running as administrator, so the protected Setup logs under $WINDOWS.~BT could not be read. " +
                         "Re-run elevated for a complete picture.");
            }

            var unreadable = context.Manifest.Count(m => m.RequiresPrivilegedRead && !m.Readable);
            if (unreadable > 0)
            {
                gaps.Add(unreadable + " log(s) exist but could not be read, so any failure recorded only in those is not reflected here.");
            }

            if (context.Cancelled)
            {
                gaps.Add("The run was cancelled, so collection is incomplete.");
            }

            gaps.AddRange(state.CollectionErrors);
            return gaps;
        }

        private static Verdict BuildVerdict(
            IReadOnlyList<Finding> findings, IReadOnlyList<string> gaps, SystemState state, DiagnosticContext context)
        {
            var live = findings.FirstOrDefault(f => f.Id == "TS-002");
            if (live != null)
            {
                return new Verdict(
                    VerdictKind.InProgress,
                    "An upgrade is in progress on this machine — leave it alone.",
                    "Setup or the task sequence engine is still running. A Setup phase can sit near the end for a long " +
                    "time and still be working. Clearing the task sequence now would break a healthy upgrade.",
                    findings, gaps);
            }

            var critical = findings.Where(f => f.Severity == Severity.Critical).ToList();
            var topCritical = critical.FirstOrDefault(f => f.Confidence >= Confidence.Medium);

            if (topCritical != null)
            {
                return new Verdict(
                    VerdictKind.CauseIdentified,
                    topCritical.Title,
                    topCritical.Meaning,
                    findings, gaps);
            }

            if (critical.Count > 0)
            {
                return new Verdict(
                    VerdictKind.Inconclusive,
                    "Problems were found, but the evidence does not name a single cause.",
                    "The findings below are real but each is only suggestive. Work down them in order, and widen the " +
                    "search in the Logs tab around the times they mention.",
                    findings, gaps);
            }

            // Nothing critical. Decide between "this machine is fine" and "we could not see enough".
            var upgradeEvidenceExists = context.Manifest.Any(m =>
                m.Exists &&
                (m.Source.Category == LogSourceCategory.SetupRollback ||
                 m.Source.Category == LogSourceCategory.SetupCurrent ||
                 m.Source.Category == LogSourceCategory.TaskSequence));

            if (!state.IsElevated && !upgradeEvidenceExists)
            {
                return new Verdict(
                    VerdictKind.InsufficientEvidence,
                    "Not enough could be read to reach a conclusion.",
                    "The protected Setup logs need administrator rights, and no task sequence or in-progress Setup logs " +
                    "were found without them. Re-run this tool as an administrator.",
                    findings, gaps);
            }

            var os = state.Os;
            var alreadyOn11 = os != null && os.IsWindows11;

            return new Verdict(
                VerdictKind.NoFailureFound,
                alreadyOn11
                    ? "No failed upgrade found — this machine is already running " + (os.DisplayName ?? "Windows 11") + "."
                    : "No evidence of a failed upgrade was found on this machine.",
                alreadyOn11
                    ? "There is no rollback folder, no stuck task sequence, and no Setup failure in the logs that were " +
                      "read. The Setup logs present are from an upgrade that completed."
                    : "No rollback folder, no stuck task sequence, and no Setup failure in the logs that were read. " +
                      "If you expected a failure here, check the gaps listed below first.",
                findings, gaps);
        }
    }
}
