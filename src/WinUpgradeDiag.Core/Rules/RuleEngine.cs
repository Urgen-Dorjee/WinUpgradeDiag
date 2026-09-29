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

        /// <summary>
        /// Processes whose presence means an upgrade is genuinely in flight. Used both to raise
        /// TS-002 and to suppress TS-001, so the two can never disagree about what "live" means.
        /// </summary>
        public static readonly IReadOnlyList<string> LiveUpgradeProcessNames =
            new[] { "TSManager", "SetupHost", "setupprep" };

        private readonly LogSearcher _searcher = new LogSearcher();
        private readonly SetupFailureAnalyzer _setupFailures = new SetupFailureAnalyzer();
        private readonly TaskSequenceFailureAnalyzer _taskSequenceFailures = new TaskSequenceFailureAnalyzer();
        private readonly DriverInstallAnalyzer _driverInstalls = new DriverInstallAnalyzer();

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
            findings.AddRange(EvaluateSetupProgress(context, state));
            findings.AddRange(EvaluateHardware(state));
            findings.AddRange(EvaluateTaskSequence(state));
            findings.AddRange(EvaluateRollback(context));
            findings.AddRange(EvaluateDiskSpace(state));
            findings.AddRange(EvaluateCache(state));
            findings.AddRange(EvaluateMachineState(state));
            findings.AddRange(ScanLogs(context, progress, cancellationToken));

            // Setup's own account of what went wrong. The signature catalogue matches fixed
            // strings and finds nothing when a real rollback does not happen to contain one of
            // them, which is how a report came back having read 477 MB of Setup logs and quoted a
            // line from none of them.
            findings.AddRange(_setupFailures.Analyze(context, progress, cancellationToken));

            // Which step failed, and with what. This is the fact on the error dialog the user sees,
            // and the engine had no concept of it.
            findings.AddRange(_taskSequenceFailures.Analyze(context, progress, cancellationToken));

            // Which device and driver. This is the answer to a PnP watchdog bugcheck, and the tool
            // used to recommend reading setupapi.dev.log without knowing where it lives.
            findings.AddRange(_driverInstalls.Analyze(context, progress, cancellationToken));

            var ranked = Rank(findings);

            // Attach the scripted fix, with its parameters resolved from what was collected.
            foreach (var finding in ranked)
            {
                finding.Remediation = Remediation.RemediationCatalog.For(finding.Id, context);
            }

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

            var live = LiveUpgradeProcessNames.Where(processes.IsRunning).ToList();

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

        // ----------------------------------------------------------------- stuck at 99%?

        /// <summary>Setup written to within this long is demonstrably still working (SU-001).</summary>
        public static readonly TimeSpan SetupActiveWindow = TimeSpan.FromMinutes(5);

        /// <summary>Setup silent for longer than this is a candidate for genuinely blocked (SU-002).</summary>
        public static readonly TimeSpan SetupStalledWindow = TimeSpan.FromMinutes(30);

        /// <summary>
        /// SU-001 / SU-002 / SU-003 — the "stuck at 99%" question.
        /// <para>
        /// The percentage on screen is not evidence. It comes from the task sequence, not from
        /// Setup, and it saturates near the end while a great deal of work remains. Two things
        /// decide whether an upgrade is alive: is Setup still writing to its log, and is it using
        /// CPU. This rule answers with those, and refuses to answer without them.
        /// </para>
        /// </summary>
        private static IEnumerable<Finding> EvaluateSetupProgress(DiagnosticContext context, SystemState state)
        {
            var processes = state.Processes;
            if (processes == null)
            {
                yield break;
            }

            var worker = new[] { "SetupHost", "setupprep" }
                .Select(n => processes.Processes.FirstOrDefault(p =>
                    p.IsRunning && string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(p => p != null);

            // Newest write across the in-progress Setup logs: Setup's own heartbeat.
            var setupLog = context.Manifest
                .Where(m => m.Exists && m.LastWriteTimeUtc.HasValue)
                .Where(m => m.Source.Category == LogSourceCategory.SetupCurrent ||
                            m.Source.Category == LogSourceCategory.SetupRollback)
                .OrderByDescending(m => m.LastWriteTimeUtc.Value)
                .FirstOrDefault();

            var progressShown = state.SetupProgressPercent;

            if (worker == null)
            {
                // SU-003: nothing is running, yet the machine still reports a Setup percentage.
                if (progressShown.HasValue)
                {
                    yield return new Finding(
                        "SU-003",
                        "Setup is no longer running, but the machine still reports " + progressShown.Value + "% progress",
                        Severity.Critical,
                        Confidence.High,
                        "The progress value in the registry is left over from an attempt that has already ended. " +
                        "Any progress dialog still on screen is stale and will never advance — the upgrade stopped, " +
                        "it is not still working.",
                        "Do not keep waiting. Find how the attempt ended: read the Setup exit code in smsts.log, " +
                        "and check the Timeline tab for a restart or crash around the time the Setup log stopped.",
                        new[]
                        {
                            new Evidence("Registry: SYSTEM\\Setup\\MoSetup\\Volatile", null,
                                "SetupProgress = " + progressShown.Value + "%"),
                            new Evidence("Running processes", null, "Neither SetupHost nor setupprep is running")
                        });
                }
                yield break;
            }

            // A worker is alive. Decide working vs blocked from log freshness and CPU, not the bar.
            var evidence = new List<Evidence>
            {
                new Evidence("Running processes", null,
                    worker.Name + " is running (pid " +
                    (worker.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "unknown") + ")" +
                    (string.IsNullOrEmpty(worker.RunningFor) ? "" : ", started " + worker.RunningFor + " ago"))
            };

            if (worker.CpuPercent.HasValue)
            {
                evidence.Add(new Evidence("CPU sample", null,
                    worker.Name + " used " + worker.CpuText + " of one core during the sample window"));
            }

            if (setupLog == null)
            {
                yield return new Finding(
                    "SU-002",
                    "Setup is running, but its log could not be read to confirm it is making progress",
                    Severity.Warning,
                    Confidence.Low,
                    "Setup is alive, so the upgrade has not obviously died. Without its log there is no way to " +
                    "tell whether it is working or blocked, and the percentage on screen does not distinguish them.",
                    "Re-run this tool elevated so the protected Setup logs under $WINDOWS.~BT can be read.",
                    evidence);
                yield break;
            }

            var age = DateTime.UtcNow - setupLog.LastWriteTimeUtc.Value;
            evidence.Add(new Evidence(setupLog.ResolvedPath, null,
                "Last written " + ProcessInfo.Describe(age) + " ago"));

            if (age <= SetupActiveWindow || worker.IsBusy)
            {
                var because = age <= SetupActiveWindow
                    ? "Setup wrote to its log " + ProcessInfo.Describe(age) + " ago"
                    : worker.Name + " is using " + worker.CpuText + " CPU";

                yield return new Finding(
                    "SU-001",
                    "The upgrade is still working — including if it shows 99%",
                    Severity.Info,
                    Confidence.High,
                    because + ", so this machine is progressing rather than hung. The percentage is reported by the " +
                    "task sequence, not by Setup, and it sits at 99% through the longest part of the job — applying " +
                    "the image and migrating user data. Several hours at 99% is normal on a slow disk.",
                    "Leave it alone. Do not restart the machine, clear the task sequence, or kill Setup. " +
                    "Re-run this diagnostic later if it has still not finished.",
                    evidence);
                yield break;
            }

            if (age >= SetupStalledWindow)
            {
                var idle = worker.CpuPercent.HasValue && !worker.IsBusy;

                yield return new Finding(
                    "SU-002",
                    "Setup is running but appears blocked — no log activity for " + ProcessInfo.Describe(age),
                    Severity.Critical,
                    idle ? Confidence.Medium : Confidence.Low,
                    "Setup has not written to its log for " + ProcessInfo.Describe(age) +
                    (idle ? ", and is using almost no CPU" : "") +
                    ". That combination points to a blocked operation rather than slow progress — commonly a driver " +
                    "or filter driver that is not returning, or a device that is not responding.",
                    "Read the last lines of the Setup log in the Logs tab: the final entry names the operation it is " +
                    "waiting on. Check the Timeline tab for a device or service failure at that time. Do not restart " +
                    "yet — a restart here usually produces a rollback and destroys the evidence.",
                    evidence);
                yield break;
            }

            yield return new Finding(
                "SU-001",
                "The upgrade is running; no log activity for " + ProcessInfo.Describe(age),
                Severity.Info,
                Confidence.Medium,
                "Setup is alive but has been quiet for a few minutes. That is within normal range — long single " +
                "operations such as applying the image produce no log output while they run.",
                "Wait, and re-run this diagnostic in ten minutes. If the log is still untouched then, it is blocked " +
                "rather than slow.",
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

            // TS-001 requires BOTH halves: a surviving WMI lock AND no live upgrade.
            //
            // Every upgrade worker counts, not just TSManager. There are windows during an in-place
            // upgrade where Setup is running and TSManager is not, and in those the execution
            // request in WMI is legitimate rather than orphaned. Recommending cleanup there would
            // destroy a healthy upgrade — the one mistake AGENTS.md says this tool must never make.
            if (LiveUpgradeProcessNames.Any(processes.IsRunning))
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

        // ----------------------------------------------------------------- client cache

        /// <summary>
        /// CT-002. A cache record whose folder is gone is the signature of somebody deleting
        /// ccmcache in Explorer. The client still believes the content is present, so it will not
        /// download it again and the deployment fails on content it cannot find.
        /// </summary>
        private static IEnumerable<Finding> EvaluateCache(SystemState state)
        {
            var cache = state.CcmCache;
            if (cache == null || !cache.Available)
            {
                yield break;
            }

            var stale = cache.StaleElements;
            if (stale.Count == 0)
            {
                yield break;
            }

            yield return new Finding(
                "CT-002",
                stale.Count == 1
                    ? "A cached content record points at a folder that no longer exists"
                    : stale.Count + " cached content records point at folders that no longer exist",
                Severity.Warning,
                Confidence.High,
                "The ConfigMgr client still believes this content is downloaded, so it will not fetch it again. " +
                "A deployment that needs it fails looking for files that are not there. This is what a manual " +
                "delete of ccmcache leaves behind.",
                "Remove the stale cache records through the client so WMI and disk agree again, then re-run the " +
                "deployment.",
                stale.Take(3)
                    .Select(e => new Evidence(
                        "WMI: CacheInfoEx",
                        null,
                        "Content " + e.ContentId + " (" + e.SizeText + ") recorded at " + e.Location + ", which is missing"))
                    .ToList());
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

            var targets = context.Manifest
                .Where(m => m.Exists && m.Readable)
                .Where(m => IsScannableText(m.ResolvedPath))
                .GroupBy(m => m.ResolvedPath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            // The failure under investigation is dated by the newest Setup log; anything much
            // older than that belongs to a previous attempt.
            var newestSetupWrite = context.Manifest
                .Where(m => m.Exists && m.LastWriteTimeUtc.HasValue &&
                            (m.Source.Category == LogSourceCategory.SetupRollback ||
                             m.Source.Category == LogSourceCategory.SetupCurrent))
                .Select(m => (DateTime?)m.LastWriteTimeUtc.Value)
                .OrderByDescending(d => d)
                .FirstOrDefault();

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
                    var relevant = ErrorSignatureCatalog.All
                        .Where(sig => sig.Covers(target.Source.Category))
                        .Select(sig => sig.Pattern)
                        .Distinct()
                        .ToList();

                    if (relevant.Count == 0)
                    {
                        continue;
                    }

                    result = _searcher.SearchMany(
                        target.ResolvedPath, relevant, maxMatchesPerQuery: 20, contextLines: 1,
                        progress: null, cancellationToken: cancellationToken);
                }
                catch (Exception)
                {
                    // Degrade, never crash: an unreadable log is recorded as a gap elsewhere.
                    continue;
                }

                foreach (var signature in ErrorSignatureCatalog.All)
                {
                    // Only apply a signature to a log where its meaning actually holds.
                    if (!signature.Covers(target.Source.Category))
                    {
                        continue;
                    }

                    var hits = result.For(signature.Pattern);
                    if (hits == null || hits.Matches.Count == 0)
                    {
                        continue;
                    }

                    // The code has to appear on a line that supports what the signature claims.
                    var supported = hits.Matches
                        .Where(m => signature.Corroborated(m.Text))
                        .ToList();

                    if (supported.Count == 0)
                    {
                        continue;
                    }

                    var evidence = supported
                        .Take(3)
                        .Select(m => new Evidence(target.ResolvedPath, m.LineNumber, m.Text.Trim()))
                        .ToList();

                    var title = signature.Title;
                    if (supported.Count > 1)
                    {
                        title += " (" + supported.Count.ToString("N0", CultureInfo.CurrentCulture) +
                                 (hits.MatchLimitReached ? "+" : "") + " occurrences)";
                    }

                    // A log last written long before the failure describes a different attempt.
                    // The report quoted 2024 task sequence logs beside a 2026 rollback with nothing
                    // to say they were two years apart.
                    var severity = signature.Severity;
                    var confidence = signature.Confidence;
                    var meaning = signature.Meaning;
                    var age = StaleBy(target, newestSetupWrite);
                    if (age != null)
                    {
                        title += " — from a log written " + age;
                        meaning += " This log was last written " + age + ", so it describes an earlier " +
                                   "attempt rather than the failure being investigated.";
                        severity = Severity.Info;
                        confidence = Confidence.Low;
                    }

                    findings.Add(new Finding(
                        signature.RuleId, title, severity, confidence,
                        meaning, signature.Action, evidence, signature.Command));
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
                // Setup quoting its own failure outranks anything inferred from state. "The
                // Rollback folder exists, so a rollback happened" is a restatement of what the
                // technician can already see; the error lines say what actually failed.
                .ThenByDescending(f => QuotesTheFailure(f.Id) ? 1 : 0)
                .ThenByDescending(f => (int)f.Severity)
                .ThenByDescending(f => (int)f.Confidence)
                .ThenBy(f => f.Id, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// How much older this log is than the failure, in words, or null when it is current.
        /// </summary>
        private static string StaleBy(Discovery.LogManifestEntry target, DateTime? failureTime)
        {
            if (!failureTime.HasValue || !target.LastWriteTimeUtc.HasValue)
            {
                return null;
            }

            var days = (failureTime.Value - target.LastWriteTimeUtc.Value).TotalDays;
            if (days < 30)
            {
                return null;
            }

            var months = (int)Math.Round(days / 30.44);
            return months < 12
                ? months.ToString(CultureInfo.CurrentCulture) + " months before the failure"
                : (months / 12).ToString(CultureInfo.CurrentCulture) + "+ years before the failure";
        }

        /// <summary>
        /// Findings that quote the machine's own account of the failure, rather than inferring one
        /// from state. "The Rollback folder exists, so a rollback happened" restates what the
        /// technician can already see; "step X failed with 0x80070002" is the answer.
        /// </summary>
        private static bool QuotesTheFailure(string id)
        {
            return id.StartsWith("SU-10", StringComparison.Ordinal) ||
                   id.StartsWith("TS-10", StringComparison.Ordinal) ||
                   id.StartsWith("DR-10", StringComparison.Ordinal);
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
                // If the Setup-progress rules ran, let their more specific conclusion lead: the
                // difference between "working at 99%" and "blocked at 99%" is the whole question.
                var setupState = findings.FirstOrDefault(f => f.Id == "SU-001" || f.Id == "SU-002");

                if (setupState != null && setupState.Id == "SU-002" && setupState.Severity == Severity.Critical)
                {
                    return new Verdict(
                        VerdictKind.CauseIdentified,
                        setupState.Title,
                        setupState.Meaning,
                        findings, gaps, setupState);
                }

                return new Verdict(
                    VerdictKind.InProgress,
                    setupState != null
                        ? setupState.Title + " — leave it alone."
                        : "An upgrade is in progress on this machine — leave it alone.",
                    setupState != null
                        ? setupState.Meaning
                        : "Setup or the task sequence engine is still running. A Setup phase can sit near the end for " +
                          "a long time and still be working. Clearing the task sequence now would break a healthy upgrade.",
                    findings, gaps, setupState ?? live);
            }

            var critical = findings.Where(f => f.Severity == Severity.Critical).ToList();
            var topCritical = critical.FirstOrDefault(f => f.Confidence >= Confidence.Medium);

            if (topCritical != null)
            {
                return new Verdict(
                    VerdictKind.CauseIdentified,
                    topCritical.Title,
                    topCritical.Meaning,
                    findings, gaps, topCritical);
            }

            if (critical.Count > 0)
            {
                return new Verdict(
                    VerdictKind.Inconclusive,
                    "Problems were found, but the evidence does not name a single cause.",
                    "The findings below are real but each is only suggestive. Work down them in order, and widen the " +
                    "search in the Logs tab around the times they mention.",
                    findings, gaps, critical[0]);
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
