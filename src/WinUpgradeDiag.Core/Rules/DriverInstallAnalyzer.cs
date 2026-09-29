using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using WinUpgradeDiag.Core.Discovery;
using WinUpgradeDiag.Core.IO;
using WinUpgradeDiag.Core.Orchestration;

namespace WinUpgradeDiag.Core.Rules
{
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
            var sectionLines = new List<string>();

            foreach (var raw in lines)
            {
                var line = raw ?? "";

                var start = SectionStart.Match(line);
                if (start.Success)
                {
                    currentSection = start.Groups["what"].Value.Trim();
                    sectionLines.Clear();
                    continue;
                }

                if (currentSection != null && sectionLines.Count < 400)
                {
                    sectionLines.Add(line);
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
                    Code = exit.Groups["code"].Success ? exit.Groups["code"].Value : null,
                    // setupapi marks errors with "!!!" and warnings with "!".
                    Detail = sectionLines.LastOrDefault(l => l.TrimStart().StartsWith("!!!", StringComparison.Ordinal))
                             ?? sectionLines.LastOrDefault(l => l.TrimStart().StartsWith("!", StringComparison.Ordinal))
                });

                currentSection = null;
                sectionLines.Clear();
            }

            if (failures.Count == 0)
            {
                return null;
            }

            // Newest last in the file; the most recent failures are the ones that matter.
            var reported = failures
                .AsEnumerable()
                .Reverse()
                .GroupBy(f => f.Section, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Take(MaxReportedDevices)
                .ToList();

            var evidence = new List<Evidence>();
            foreach (var failure in reported)
            {
                evidence.Add(new Evidence(
                    log.ResolvedPath, null,
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

            return new Finding(
                "DR-100",
                reported.Count == 1
                    ? "A device driver failed to install"
                    : reported.Count + " device drivers failed to install",
                Severity.Critical,
                Confidence.High,
                meaning,
                action,
                evidence);
        }

        private static string Trim(string line)
        {
            var trimmed = (line ?? "").Trim();
            return trimmed.Length <= 400 ? trimmed : trimmed.Substring(0, 400) + "…";
        }

        private sealed class DeviceFailure
        {
            public string Section;
            public string Code;
            public string Detail;
        }
    }
}
