using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using WinUpgradeDiag.Core.Collect;
using WinUpgradeDiag.Core.Discovery;
using WinUpgradeDiag.Core.IO;
using WinUpgradeDiag.Core.Orchestration;

namespace WinUpgradeDiag.Core.Rules
{
    /// <summary>
    /// Establishes that the machine crashed, with which stop code, and while installing what.
    /// <para>
    /// Built for a machine that showed DRIVER_PNP_WATCHDOG on its crash screen during an upgrade,
    /// and for which the tool found nothing. Four things were wrong at once. The name on the screen
    /// is never written to a Setup log, so searching the logs for it could not succeed. The crash
    /// happened inside the new Windows, so its event went into a System log that the rollback
    /// discarded — the surviving copies sit in Rollback as .evtx files, which were listed and never
    /// opened. The dump holding the stop code was noticed only as "a dump exists". And the driver
    /// log that names the device was looked for at a path Setup does not use, and when found was
    /// searched only for failures that a crash never gets the chance to write.
    /// </para>
    /// <para>
    /// The stop code comes from the dump header or the saved BugCheck event. The device comes from
    /// the install that was cut off: opened in setupapi.dev.log and never closed before the next
    /// boot. When both are present and line up in time, the finding names the crash and the device
    /// together, and that is the answer.
    /// </para>
    /// </summary>
    public sealed class BugCheckAnalyzer
    {
        /// <summary>How close the cut-off install must be to the crash to be tied to it.</summary>
        private static readonly TimeSpan Correlation = TimeSpan.FromHours(6);

        private const int SetupApiWindowBytes = 16 * 1024 * 1024;

        private readonly TailReader _reader = new TailReader();

        public IReadOnlyList<Finding> Analyze(
            DiagnosticContext context, IProgress<string> progress, CancellationToken cancellationToken)
        {
            var findings = new List<Finding>();
            if (context?.Manifest == null || cancellationToken.IsCancellationRequested)
            {
                return findings;
            }

            progress?.Report("Looking for a crash and the stop code that went with it");

            var evidence = CrashEvidence(context, cancellationToken);
            var crashes = evidence.Where(e => e.Code.HasValue).ToList();
            var interrupted = InterruptedInstalls(context, cancellationToken);

            if (crashes.Count > 0)
            {
                findings.Add(FromCrash(crashes, evidence, interrupted));
                return findings;
            }

            // No stop code anywhere, but a device install inside the failed attempt was cut off by a
            // restart. Worth reporting, at lower confidence: a crash is the usual reason, but not the
            // only one. Only from Setup's own copies, never the live log, where an install cut off
            // by a power cut months ago is just history.
            var fromSetup = interrupted
                .Where(i => i.Log.IndexOf("$WINDOWS.~BT", StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(i => i.Install.NextBootUtc ?? DateTime.MinValue)
                .FirstOrDefault();

            if (fromSetup != null)
            {
                findings.Add(FromInterruptionAlone(fromSetup, evidence));
            }

            return findings;
        }

        // ------------------------------------------------------------------ the crash

        private Finding FromCrash(
            IReadOnlyList<BugCheckEvidence> crashes,
            IReadOnlyList<BugCheckEvidence> all,
            IReadOnlyList<LocatedInstall> interrupted)
        {
            var newest = crashes.OrderByDescending(c => c.TimeUtc ?? DateTime.MinValue).First();
            var code = newest.Code.Value;

            // Tie the cut-off install to this crash only if they line up in time. An install cut
            // off on some other day explains some other restart.
            var device = interrupted
                .Where(i => newest.TimeUtc == null || i.Install.NextBootUtc == null ||
                            (newest.TimeUtc.Value - i.Install.NextBootUtc.Value).Duration() <= Correlation)
                .OrderBy(i => i.Log.IndexOf("$WINDOWS.~BT", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1)
                .ThenByDescending(i => i.Install.NextBootUtc ?? DateTime.MinValue)
                .FirstOrDefault();

            var evidence = new List<Evidence>();
            foreach (var c in all
                         .Where(e => e.Code == code || e.Code == null)
                         .OrderByDescending(e => e.TimeUtc ?? DateTime.MinValue)
                         .Take(4))
            {
                evidence.Add(new Evidence(c.Source, null, Stamp(c.TimeUtc) + c.Text));
            }

            string hardwareId = null;
            string inf = null;
            if (device != null)
            {
                hardwareId = DriverInstallAnalyzer.HardwareIdOf(device.Install.Section);
                inf = DriverInstallAnalyzer.OemInfOf(device.Install.Section);
                evidence.Add(new Evidence(device.Log, null,
                    "Started " + Stamp(device.Install.StartedUtc).Trim() + " and never finished: [" +
                    device.Install.Section + "]"));
                evidence.Add(new Evidence(device.Log, null,
                    "The next line is Windows starting again" +
                    (device.Install.NextBootUtc.HasValue ? " at " + Stamp(device.Install.NextBootUtc).Trim() : "") + "."));
            }

            var title = "The machine crashed with " + BugCheckNames.Describe(code) +
                        (device != null ? " while installing " + (hardwareId ?? "a device driver") : "");

            var meaning = BugCheckNames.Meaning(code) +
                          " The stop code is read from " + Where(newest) + ", not from the screen." +
                          (device != null
                              ? " The device install running at that moment never finished: it was opened in " +
                                Path.GetFileName(device.Log) + " and the next thing written is Windows booting " +
                                "again. That device's driver is what stalled."
                              : " No device install was found cut off at that moment, so the driver is not " +
                                "named by the logs.");

            string action;
            if (hardwareId != null)
            {
                action = "In Device Manager, find the device with hardware id " + hardwareId + " (Details tab, " +
                         "Hardware Ids) to identify the vendor, and get a driver they support on Windows 11. " +
                         "Disabling that one device and re-running the upgrade confirms it is the cause before " +
                         "you go looking for a driver.";
            }
            else if (device != null)
            {
                action = "The section above names what was being installed. Update or remove that driver, " +
                         "then retry.";
            }
            else
            {
                action = "The driver is named inside the dump, which needs a debugger to read: open " +
                         Path.GetFileName(newest.Source) + " in WinDbg and run !analyze -v — the IMAGE_NAME " +
                         "line is the driver. Without one, disabling devices in groups and retrying narrows " +
                         "it down.";
            }

            if (inf != null)
            {
                action += " " + inf + " is a third-party package: pnputil /delete-driver " + inf +
                          " /uninstall removes it so Setup uses the in-box driver instead.";
            }

            return new Finding(
                "BC-100",
                title,
                Severity.Critical,
                device != null ? Confidence.High : Confidence.Medium,
                meaning,
                action,
                evidence);
        }

        private static Finding FromInterruptionAlone(LocatedInstall located, IReadOnlyList<BugCheckEvidence> unclean)
        {
            var hardwareId = DriverInstallAnalyzer.HardwareIdOf(located.Install.Section);
            var evidence = new List<Evidence>
            {
                new Evidence(located.Log, null,
                    "Started " + Stamp(located.Install.StartedUtc).Trim() + " and never finished: [" +
                    located.Install.Section + "]"),
                new Evidence(located.Log, null,
                    "The next line is Windows starting again" +
                    (located.Install.NextBootUtc.HasValue ? " at " + Stamp(located.Install.NextBootUtc).Trim() : "") + ".")
            };

            evidence.AddRange(unclean.Take(2).Select(u => new Evidence(u.Source, null, Stamp(u.TimeUtc) + u.Text)));

            return new Finding(
                "DR-101",
                "A device install was cut off by a restart during the upgrade" +
                (hardwareId != null ? ": " + hardwareId : ""),
                Severity.Warning,
                Confidence.Medium,
                "Setup was installing this device's driver when Windows went down, and the install never " +
                "finished. No stop code was found to confirm a crash, so it may have been a power loss or a " +
                "forced restart — but when an upgrade rolls back, this is the first device to suspect.",
                hardwareId != null
                    ? "Identify " + hardwareId + " in Device Manager (Details, Hardware Ids), then disable that " +
                      "one device and retry the upgrade to confirm."
                    : "Identify the device in the section above, then disable it and retry to confirm.",
                evidence);
        }

        // ------------------------------------------------------------------ gathering

        private static IReadOnlyList<BugCheckEvidence> CrashEvidence(
            DiagnosticContext context, CancellationToken cancellationToken)
        {
            var found = new List<BugCheckEvidence>();
            var errors = context.SystemState?.CollectionErrors;

            // Dump headers: setupmem.dmp from the failed attempt, then any minidumps and MEMORY.DMP.
            foreach (var dump in context.Manifest
                         .Where(m => m.Exists && m.ResolvedPath.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
                         .GroupBy(m => m.ResolvedPath, StringComparer.OrdinalIgnoreCase)
                         .Select(g => g.First()))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var info = CrashDumpReader.ReadFile(dump.ResolvedPath);
                if (info.IsBugCheck)
                {
                    found.Add(new BugCheckEvidence(
                        dump.ResolvedPath, info.WrittenUtc, info.BugCheckCode,
                        "Crash dump header: stop code " + BugCheckNames.Describe(info.BugCheckCode) +
                        ", parameters " + string.Join(", ", info.Parameters.Select(p => "0x" + p.ToString("X", CultureInfo.InvariantCulture))) + "."));
                }
            }

            // Saved event logs from the attempt that was rolled back.
            foreach (var evtx in context.Manifest
                         .Where(m => m.Exists &&
                                     m.Source.Category == LogSourceCategory.SetupRollback &&
                                     m.ResolvedPath.EndsWith(".evtx", StringComparison.OrdinalIgnoreCase))
                         .GroupBy(m => m.ResolvedPath, StringComparer.OrdinalIgnoreCase)
                         .Select(g => g.First()))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                found.AddRange(RollbackEventReader.ReadFile(evtx.ResolvedPath, errors));
            }

            // The live System log, for a crash in the build that is running now.
            foreach (var e in context.SystemState?.Events ?? new List<EventRecordInfo>())
            {
                var live = RollbackEventReader.FromLiveEvent(e);
                if (live != null)
                {
                    found.Add(live);
                }
            }

            return found;
        }

        private IReadOnlyList<LocatedInstall> InterruptedInstalls(
            DiagnosticContext context, CancellationToken cancellationToken)
        {
            var located = new List<LocatedInstall>();

            foreach (var log in context.Manifest
                         .Where(m => m.Exists && m.Readable &&
                                     Path.GetFileName(m.ResolvedPath)
                                         .StartsWith("setupapi.dev", StringComparison.OrdinalIgnoreCase))
                         .GroupBy(m => m.ResolvedPath, StringComparer.OrdinalIgnoreCase)
                         .Select(g => g.First()))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                IReadOnlyList<string> lines;
                try
                {
                    lines = _reader.ReadTail(log.ResolvedPath, SetupApiWindowBytes).Lines;
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var install in DriverInstallAnalyzer.FindInterrupted(lines))
                {
                    located.Add(new LocatedInstall(log.ResolvedPath, install));
                }
            }

            return located;
        }

        private static string Where(BugCheckEvidence e)
        {
            return e.Source.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase)
                ? "the crash dump " + Path.GetFileName(e.Source)
                : "the " + e.Source.ToLowerInvariant().Replace("saved event log", "event log Setup saved");
        }

        private static string Stamp(DateTime? utc)
        {
            return utc.HasValue
                ? utc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  "
                : "";
        }

        private sealed class LocatedInstall
        {
            public LocatedInstall(string log, InterruptedInstall install)
            {
                Log = log;
                Install = install;
            }

            public string Log { get; }
            public InterruptedInstall Install { get; }
        }
    }
}
