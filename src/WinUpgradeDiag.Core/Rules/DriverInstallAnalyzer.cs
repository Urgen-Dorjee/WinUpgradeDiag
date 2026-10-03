using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using WinUpgradeDiag.Core.Discovery;
using WinUpgradeDiag.Core.IO;
using WinUpgradeDiag.Core.Orchestration;

namespace WinUpgradeDiag.Core.Rules
{
    /// <summary>A device install that was running when Windows went down, and when it next started.</summary>
    public sealed class InterruptedInstall
    {
        public InterruptedInstall(string section, DateTime? startedUtc, DateTime? nextBootUtc)
        {
            Section = section;
            StartedUtc = startedUtc;
            NextBootUtc = nextBootUtc;
        }

        /// <summary>The section header, e.g. "Device Install (Hardware initiated) - PCI\\VEN_10EC...".</summary>
        public string Section { get; }

        public DateTime? StartedUtc { get; }

        /// <summary>The boot that followed — close to the moment of the crash.</summary>
        public DateTime? NextBootUtc { get; }
    }

    /// <summary>
    /// Names the device and driver that failed to install, from setupapi.dev.log.
    /// <para>
    /// This is the answer to a DRIVER_PNP_WATCHDOG. Setup tears down and re-enumerates every device
    /// during an upgrade, and one driver stalls past the watchdog timeout; setupapi.dev.log is the
    /// record of exactly which. The tool used to tell technicians to "check setupapi.dev.log" while
    /// looking for it in one place only — the copy inside the Rollback folder, which is frequently
    /// absent — so on the machine that actually bugchecked it had no driver log at all and named
    /// nothing.
    /// </para>
    /// </summary>
    public sealed class DriverInstallAnalyzer
    {
        /// <summary>setupapi.dev.log runs to a few MB; the recent end is what matters.</summary>
        private const int WindowBytes = 12 * 1024 * 1024;

        private const int MaxReportedDevices = 5;

        /// <summary>Section header: "&gt;&gt;&gt;  [Device Install (Hardware initiated) - PCI\VEN_10EC...]".</summary>
        private static readonly Regex SectionStart = new Regex(
            @"^>>>\s*\[(?<what>.+?)\]\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>Section result: "&lt;&lt;&lt;  [Exit status: FAILURE(0x800f0203)]".</summary>
        private static readonly Regex ExitStatus = new Regex(
            @"^<<<\s*\[Exit status:\s*(?<status>[A-Z_]+)(?:\((?<code>0x[0-9A-Fa-f]+)\))?\]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>A hardware id, which is what identifies the device to a driver package.</summary>
        private static readonly Regex HardwareId = new Regex(
            @"(?<id>(?:PCI|USB|HDAUDIO|ACPI|SWC|ROOT|HID|BTH)\\[^\s\]]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Section timestamp: "&gt;&gt;&gt;  Section start 2026/08/13 09:35:19.250".</summary>
        private static readonly Regex SectionTime = new Regex(
            @"^>>>\s*Section start\s+(?<when>\d{4}/\d{2}/\d{2}\s+\d{2}:\d{2}:\d{2})",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>An oem inf, which is what identifies the third-party driver package.</summary>
        private static readonly Regex OemInf = new Regex(
            @"(?<inf>oem\d+\.inf)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly TailReader _reader = new TailReader();

        public IReadOnlyList<Finding> Analyze(
            DiagnosticContext context, IProgress<string> progress, CancellationToken cancellationToken)
        {
            var findings = new List<Finding>();
            if (context?.Manifest == null)
            {
                return findings;
            }

            var logs = context.Manifest
                .Where(m => m.Exists && m.Readable &&
                            Path.GetFileName(m.ResolvedPath)
                                .StartsWith("setupapi.dev", StringComparison.OrdinalIgnoreCase))
                .GroupBy(m => m.ResolvedPath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                // A rollback copy describes the failed attempt directly; the live INF log is the
                // fallback and covers the case where Setup left no rollback copy behind.
                .OrderBy(m => m.Source.Category == LogSourceCategory.SetupRollback ? 0 : 1)
                .ToList();

            foreach (var log in logs)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                progress?.Report("Reading " + Path.GetFileName(log.ResolvedPath) + " for failed device installs");

                var finding = FromLog(log);
                if (finding != null)
                {
                    findings.Add(finding);
                    break;
                }
            }

            return findings;
        }

        private Finding FromLog(LogManifestEntry log)
        {
            IReadOnlyList<string> lines;
            try
            {
                lines = _reader.ReadTail(log.ResolvedPath, WindowBytes).Lines;
            }
            catch (Exception)
            {
                return null;
            }

            var failures = new List<DeviceFailure>();
            string currentSection = null;
            DateTime? currentStarted = null;
            var sectionLines = new List<string>();

            foreach (var raw in lines)
            {
                var line = raw ?? "";

                var start = SectionStart.Match(line);
                if (start.Success)
                {
                    currentSection = start.Groups["what"].Value.Trim();
                    currentStarted = null;
                    sectionLines.Clear();
                    continue;
                }

                if (currentSection != null)
                {
                    var when = SectionTime.Match(line);
                    if (when.Success)
                    {
                        DateTime parsed;
                        if (DateTime.TryParseExact(
                                when.Groups["when"].Value, "yyyy/MM/dd HH:mm:ss",
                                CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                        {
                            currentStarted = parsed;
                        }
                    }

                    if (sectionLines.Count < 400)
                    {
                        sectionLines.Add(line);
                    }
                }

                var exit = ExitStatus.Match(line);
                if (!exit.Success || currentSection == null)
                {
                    continue;
                }

                var status = exit.Groups["status"].Value;
                if (status.IndexOf("FAILURE", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    currentSection = null;
                    sectionLines.Clear();
                    continue;
                }

                failures.Add(new DeviceFailure
                {
                    Section = currentSection,
                    Started = currentStarted,
                    Code = exit.Groups["code"].Success ? exit.Groups["code"].Value : null,
                    // setupapi marks errors with "!!!" and warnings with "!".
                    Detail = sectionLines.LastOrDefault(l => l.TrimStart().StartsWith("!!!", StringComparison.Ordinal))
                             ?? sectionLines.LastOrDefault(l => l.TrimStart().StartsWith("!", StringComparison.Ordinal))
                });

                currentSection = null;
                currentStarted = null;
                sectionLines.Clear();
            }

            if (failures.Count == 0)
            {
                return null;
            }

            // Only device installs, with no fallback.
            //
            // A driver being uninstalled is not a driver failing to start. On a healthy machine
            // this log is overwhelmingly SetupUninstallOEMInf sections failing with "cannot find
            // the path specified" — Disk Cleanup running cleanmgr /autocleanstoragesense and
            // removing driver packages that were already gone. This machine had 458 of those and
            // 8 device installs. Falling back to "report something rather than nothing" turned
            // that routine housekeeping into "Cause identified: 5 device drivers failed to
            // install", with a recommendation to run pnputil /delete-driver, on a machine where
            // nothing was wrong. Silence is the correct output when there is nothing to say.
            var reported = failures
                .Where(f => IsDeviceInstall(f.Section))
                .AsEnumerable()
                .Reverse()
                .GroupBy(f => f.Section, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Take(MaxReportedDevices)
                .ToList();

            if (reported.Count == 0)
            {
                return null;
            }

            var evidence = new List<Evidence>();
            foreach (var failure in reported)
            {
                evidence.Add(new Evidence(
                    log.ResolvedPath, null,
                    (failure.Started.HasValue
                        ? failure.Started.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  "
                        : "") +
                    "[" + failure.Section + "]" +
                    (failure.Code != null ? "  Exit status: FAILURE(" + failure.Code + ")" : "  Exit status: FAILURE")));

                if (failure.Detail != null)
                {
                    evidence.Add(new Evidence(log.ResolvedPath, null, Trim(failure.Detail)));
                }
            }

            var ids = reported
                .Select(f => HardwareId.Match(f.Section))
                .Where(m => m.Success)
                .Select(m => m.Groups["id"].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();

            var infs = reported
                .Select(f => OemInf.Match(f.Section + " " + (f.Detail ?? "")))
                .Where(m => m.Success)
                .Select(m => m.Groups["inf"].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();

            var meaning =
                "Windows records every device install and its result here, and these are the ones that came back " +
                "FAILURE, newest first. During an upgrade Setup re-enumerates every device and asks each driver to " +
                "start on the new build; a driver that stalls doing that is what a PnP watchdog bugcheck is." +
                (ids.Count > 0 ? " Hardware: " + string.Join(", ", ids) + "." : "") +
                (infs.Count > 0 ? " Driver package: " + string.Join(", ", infs) + "." : "");

            var action = ids.Count > 0
                ? "Look up " + ids[0] + " in Device Manager (View → Devices by connection, or match the hardware id " +
                  "on the Details tab) to identify the vendor, then get a driver for it that the vendor supports on " +
                  "the target build. Disabling that one device and re-running the upgrade confirms it is the cause " +
                  "before you go looking for a driver."
                : "Identify the device named above, then check the vendor supports its driver on the target build.";

            if (infs.Count > 0)
            {
                action += " " + infs[0] + " is a third-party package: pnputil /delete-driver " + infs[0] +
                          " /uninstall removes it so Setup uses the in-box driver instead.";
            }

            // A device install that failed six weeks ago is not why an upgrade failed today.
            var newest = reported
                .Where(f => f.Started.HasValue)
                .Select(f => f.Started.Value)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();

            var stale = newest != DateTime.MinValue && (DateTime.Now - newest).TotalDays > 30;
            if (stale)
            {
                var days = (int)(DateTime.Now - newest).TotalDays;
                meaning += " The most recent of these was " + days.ToString(CultureInfo.CurrentCulture) +
                           " days ago, so unless the upgrade attempt is at least that old, this is history " +
                           "rather than the cause.";
            }

            return new Finding(
                "DR-100",
                (reported.Count == 1
                    ? "A device driver failed to install"
                    : reported.Count + " device drivers failed to install") +
                (stale ? " (over a month ago)" : ""),
                stale ? Severity.Warning : Severity.Critical,
                stale ? Confidence.Low : Confidence.High,
                meaning,
                action,
                evidence);
        }

        /// <summary>
        /// Whether a section describes a device or driver being installed, as opposed to removed.
        /// An uninstall that fails because the package is already gone says nothing about why an
        /// upgrade rolled back.
        /// </summary>
        public static bool IsDeviceInstall(string section)
        {
            if (string.IsNullOrWhiteSpace(section))
            {
                return false;
            }

            if (section.IndexOf("uninstall", StringComparison.OrdinalIgnoreCase) >= 0 ||
                section.IndexOf("Delete", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            return section.IndexOf("Device Install", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   section.IndexOf("Driver Install", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   section.IndexOf("Device Start", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   section.IndexOf("DiInstallDriver", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string Trim(string line)
        {
            var trimmed = (line ?? "").Trim();
            return trimmed.Length <= 400 ? trimmed : trimmed.Substring(0, 400) + "…";
        }

        /// <summary>"[Boot Session: 2026/09/25 22:21:05.500]" — written at every start of Windows.</summary>
        private static readonly Regex BootSession = new Regex(
            @"^\[Boot Session:\s*(?<when>\d{4}/\d{2}/\d{2}\s+\d{2}:\d{2}:\d{2})",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex SectionEnd = new Regex(
            @"^<<<\s*(?:Section end|\[Exit status)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Device installs that were running when Windows went down: opened, never closed, and
        /// followed by the start of the next boot.
        /// <para>
        /// This is how a PnP watchdog names its driver in this log. A crash does not get as far as
        /// writing FAILURE — the machine is gone before the section can be closed — so looking only
        /// for failed exit statuses, as this analyser did, finds nothing on exactly the machines
        /// that bugchecked. What the crash leaves behind is a section that started and then stops
        /// mid-flight, with the next line being Windows booting again.
        /// </para>
        /// <para>
        /// A section still open at the end of the file is ignored: that is an install in progress
        /// now, not one that was cut off.
        /// </para>
        /// </summary>
        public static IReadOnlyList<InterruptedInstall> FindInterrupted(IEnumerable<string> lines)
        {
            var found = new List<InterruptedInstall>();
            string open = null;
            DateTime? openedAt = null;

            foreach (var raw in lines ?? new string[0])
            {
                var line = raw ?? "";

                var boot = BootSession.Match(line);
                if (boot.Success)
                {
                    if (open != null && IsDeviceInstall(open))
                    {
                        found.Add(new InterruptedInstall(open, openedAt, ParseTime(boot.Groups["when"].Value)));
                    }
                    open = null;
                    openedAt = null;
                    continue;
                }

                var start = SectionStart.Match(line);
                if (start.Success)
                {
                    open = start.Groups["what"].Value.Trim();
                    openedAt = null;
                    continue;
                }

                if (open != null)
                {
                    var when = SectionTime.Match(line);
                    if (when.Success)
                    {
                        openedAt = ParseTime(when.Groups["when"].Value);
                        continue;
                    }

                    if (SectionEnd.IsMatch(line))
                    {
                        open = null;
                        openedAt = null;
                    }
                }
            }

            return found;
        }

        /// <summary>The hardware id in a section header, for looking the device up.</summary>
        public static string HardwareIdOf(string section)
        {
            var m = HardwareId.Match(section ?? "");
            return m.Success ? m.Groups["id"].Value : null;
        }

        /// <summary>The oem*.inf in a section header, for removing the package.</summary>
        public static string OemInfOf(string section)
        {
            var m = OemInf.Match(section ?? "");
            return m.Success ? m.Groups["inf"].Value : null;
        }

        private static DateTime? ParseTime(string text)
        {
            DateTime parsed;
            return DateTime.TryParseExact(text, "yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture,
                       DateTimeStyles.AssumeLocal, out parsed)
                ? parsed.ToUniversalTime()
                : (DateTime?)null;
        }

        private sealed class DeviceFailure
        {
            public string Section;
            public string Code;
            public string Detail;

            /// <summary>When the section ran, so age can be judged.</summary>
            public DateTime? Started;
        }
    }
}
