using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using WinUpgradeDiag.Core.Collect;
using WinUpgradeDiag.Core.Discovery;
using WinUpgradeDiag.Core.Orchestration;
using WinUpgradeDiag.Core.Redaction;

namespace WinUpgradeDiag.Core.Report
{
    /// <summary>
    /// Self-contained HTML report: inline CSS, no scripts, no external assets (DESIGN.md §4.6).
    /// Every value is HTML-encoded and, unless explicitly disabled, redacted.
    /// <para>
    /// The document is ordered the way it gets read. Somebody picking it up off a ticket wants the
    /// conclusion, then the reasoning, then the proof — in that order, without scrolling past a
    /// machine inventory to reach any of it. Supporting material that is bulky but occasionally
    /// decisive (every event, every log path that was checked and found absent) is present but
    /// folded away, because a report where 94% of the page is an undifferentiated event dump reads
    /// as a data file rather than a diagnosis.
    /// </para>
    /// </summary>
    public static class HtmlReportWriter
    {
        private const int MaxEventsInReport = 200;

        /// <summary>Events at these levels are shown outright; everything else is folded away.</summary>
        private static readonly HashSet<string> SignificantLevels =
            new HashSet<string>(new[] { "Error", "Critical" }, StringComparer.OrdinalIgnoreCase);

        public static void Write(DiagnosticContext context, Redactor redactor, string path)
        {
            File.WriteAllText(path, Render(context, redactor), new UTF8Encoding(false));
        }

        public static string Render(DiagnosticContext context, Redactor redactor)
        {
            Func<string, string> r = s => redactor == null ? s : redactor.Redact(s);
            var s0 = context.SystemState ?? new SystemState();
            var sb = new StringBuilder();

            var machine = redactor == null ? (s0.MachineName ?? Environment.MachineName) : "[machine name withheld]";
            var collected = Fmt(context.StartedAtUtc) + " UTC";

            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
            sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            sb.Append("<title>Upgrade diagnostic — ").Append(E(machine)).Append(" — ")
              .Append(E(Fmt(context.StartedAtUtc).Split(' ')[0])).AppendLine("</title>");
            sb.AppendLine("<style>" + Css + "</style></head><body>");

            WriteDocumentHeader(sb, context, s0, machine, collected, redactor);
            WriteVerdict(sb, context, r);

            if (context.Cancelled)
            {
                sb.AppendLine("<div class=\"card warn\">The run was cancelled, so the sections below are incomplete.</div>");
            }
            if (!context.PrivilegedReadEnabled)
            {
                sb.Append("<div class=\"card warn\">Protected logs could not be read with backup privilege: ")
                  .Append(E(r(context.PrivilegedReadError))).AppendLine("</div>");
            }

            WriteFindings(sb, context, r);
            WriteExamined(sb, context, r);
            WriteSystem(sb, s0, r);
            WriteEvents(sb, s0, r);

            if (s0.CollectionErrors.Count > 0)
            {
                sb.AppendLine("<h2 id=\"gaps\">Collection gaps</h2>");
                sb.AppendLine("<p class=\"lede\">Checks that could not be completed. Anything recorded only in these " +
                              "places is not reflected in the verdict above.</p><ul>");
                foreach (var err in s0.CollectionErrors)
                {
                    sb.Append("<li>").Append(E(r(err))).AppendLine("</li>");
                }
                sb.AppendLine("</ul>");
            }

            sb.Append("<p class=\"foot\">Generated offline on the machine by WinUpgradeDiag ")
              .Append(E(ShortVersion(context.ToolVersion)))
              .AppendLine(". This file references no external resources and can be attached to a ticket as it is.</p>");
            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- header and contents

        private static void WriteDocumentHeader(
            StringBuilder sb, DiagnosticContext context, SystemState s0,
            string machine, string collected, Redactor redactor)
        {
            sb.AppendLine("<header>");
            sb.AppendLine("<div class=\"doctype\">Windows upgrade diagnostic</div>");
            sb.Append("<h1>").Append(E(machine)).AppendLine("</h1>");

            sb.AppendLine("<dl class=\"ident\">");
            IdentRow(sb, "Collected", collected);
            IdentRow(sb, "Operating system", s0.Os?.FullDescription);
            IdentRow(sb, "Tool version", ShortVersion(context.ToolVersion));
            sb.Append("<dt>Contents</dt><dd>")
              .Append(redactor != null
                  ? "Redacted — user names, profile paths and the machine name have been removed."
                  : "<strong class=\"warn\">Not redacted</strong> — contains user names and paths. Internal use only.")
              .AppendLine("</dd>");
            sb.AppendLine("</dl>");
            sb.AppendLine("</header>");
        }

        private static void IdentRow(StringBuilder sb, string label, string value)
        {
            sb.Append("<dt>").Append(E(label)).Append("</dt><dd>")
              .Append(string.IsNullOrWhiteSpace(value) ? "<span class=\"dim\">unknown</span>" : E(value))
              .AppendLine("</dd>");
        }

        /// <summary>
        /// Strips the build metadata off an informational version. The full commit hash is 40
        /// characters of noise in a header a human reads; the first seven identify the build just
        /// as uniquely for anyone who needs to go looking.
        /// </summary>
        public static string ShortVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                return "";
            }

            var plus = version.IndexOf('+');
            if (plus < 0 || plus == version.Length - 1)
            {
                return version;
            }

            var build = version.Substring(plus + 1);
            return version.Substring(0, plus) + " (" + (build.Length > 7 ? build.Substring(0, 7) : build) + ")";
        }

        // ---------------------------------------------------------------- verdict

        /// <summary>
        /// The verdict, the advice that follows from it, and the ranked findings — written first,
        /// because a report attached to a ticket has to answer the question on its opening screen
        /// rather than make the reader scroll through a manifest.
        /// </summary>
        private static void WriteVerdict(StringBuilder sb, DiagnosticContext context, Func<string, string> r)
        {
            var verdict = context.Verdict;
            if (verdict == null)
            {
                sb.AppendLine("<div class=\"card warn\"><strong>No verdict was produced.</strong> " +
                              "The run did not reach the rules stage; the collected state is below.</div>");
                return;
            }

            sb.Append("<div class=\"verdict ").Append(SeverityClass(verdict.DisplaySeverity)).AppendLine("\">");
            sb.Append("<div class=\"kind\">").Append(E(Humanise(verdict.Kind))).AppendLine("</div>");
            sb.Append("<h2 class=\"headline\">").Append(E(r(verdict.Headline))).AppendLine("</h2>");
            sb.Append("<p class=\"detail\">").Append(E(r(verdict.Detail))).AppendLine("</p>");

            // The advice follows the conclusion, not whichever finding ranked first. A machine with
            // no failed upgrade used to be told to "restart before attempting the upgrade again".
            var action = verdict.Action;
            if (action != null && action.HasText)
            {
                sb.Append("<div class=\"action\"><div class=\"label\">").Append(E(action.Label)).AppendLine("</div>");
                sb.Append("<p>").Append(E(r(action.Text))).AppendLine("</p>");
                if (action.HasCommand)
                {
                    sb.Append("<pre class=\"cmd\">").Append(E(r(action.Command))).AppendLine("</pre>");
                    sb.AppendLine("<p class=\"meta\">Review before running. This tool never executes commands.</p>");
                }
                sb.AppendLine("</div>");
            }
            sb.AppendLine("</div>");

            if (verdict.HasGaps)
            {
                sb.AppendLine("<div class=\"card warn\"><strong>What could not be checked.</strong> " +
                              "A clean result from a partial collection is not the same as a clean machine.<ul>");
                foreach (var gap in verdict.Gaps)
                {
                    sb.Append("<li>").Append(E(r(gap))).AppendLine("</li>");
                }
                sb.AppendLine("</ul></div>");
            }

            WriteContents(sb, context, verdict);
        }

        private static void WriteContents(StringBuilder sb, DiagnosticContext context, Rules.Verdict verdict)
        {
            var events = (context.SystemState?.Events ?? new List<EventRecordInfo>()).Count;
            var read = context.Manifest.Count(m => m.Exists);

            sb.AppendLine("<nav class=\"toc\">");
            sb.Append("<a href=\"#findings\">").Append(E(verdict.FindingsHeading)).Append(" (")
              .Append(verdict.Findings.Count).AppendLine(")</a>");
            sb.Append("<a href=\"#examined\">What was examined (").Append(read).AppendLine(" logs read)</a>");
            sb.AppendLine("<a href=\"#system\">System state</a>");
            sb.Append("<a href=\"#events\">Event log (").Append(events).AppendLine(")</a>");
            sb.AppendLine("</nav>");
        }

        private static void WriteFindings(StringBuilder sb, DiagnosticContext context, Func<string, string> r)
        {
            var verdict = context.Verdict;
            if (verdict == null)
            {
                return;
            }

            sb.Append("<h2 id=\"findings\">").Append(E(verdict.FindingsHeading)).AppendLine("</h2>");

            if (!verdict.HasFindings)
            {
                sb.AppendLine("<p class=\"lede\">Nothing in the collected state or the logs that were read matched " +
                              "a known failure pattern.</p>");
                return;
            }

            sb.Append("<p class=\"lede\">").Append(E(verdict.FindingsPreamble)).AppendLine("</p>");

            foreach (var finding in verdict.Findings)
            {
                var isCause = verdict.Cause != null && ReferenceEquals(finding, verdict.Cause);
                sb.Append("<div class=\"finding ").Append(SeverityClass(finding.Severity)).AppendLine("\">");
                sb.Append("<div class=\"fhead\"><span class=\"badge\">").Append(E(finding.SeverityText))
                  .Append("</span><span class=\"ftitle\">").Append(E(r(finding.Title)))
                  .Append("</span>");
                if (isCause)
                {
                    sb.Append("<span class=\"cause\">THE VERDICT</span>");
                }
                sb.Append("<span class=\"conf\">").Append(E(finding.ConfidenceText))
                  .Append(" &middot; ").Append(E(finding.Id)).AppendLine("</span></div>");

                sb.Append("<p>").Append(E(r(finding.Meaning))).AppendLine("</p>");
                sb.Append("<p><strong>What to do:</strong> ").Append(E(r(finding.Action))).AppendLine("</p>");

                if (finding.HasCommand)
                {
                    sb.Append("<pre class=\"cmd\">").Append(E(r(finding.Command))).AppendLine("</pre>");
                }

                WriteRemediation(sb, finding, r);
                WriteEvidence(sb, finding, r);

                sb.AppendLine("</div>");
            }
        }

        private static void WriteRemediation(StringBuilder sb, Rules.Finding finding, Func<string, string> r)
        {
            if (!finding.HasRemediation)
            {
                return;
            }

            var fix = finding.Remediation;
            sb.Append("<div class=\"fix\"><div class=\"label\">PRESCRIBED FIX &middot; ")
              .Append(E(fix.RiskText)).AppendLine("</div>");
            sb.Append("<p><strong>").Append(E(fix.Title)).Append("</strong><br><code>")
              .Append(E(fix.ScriptName)).AppendLine("</code></p>");
            sb.Append("<p>").Append(E(r(fix.Summary))).AppendLine("</p>");

            sb.AppendLine("<div class=\"label\">IT WILL</div><ul>");
            foreach (var step in fix.Steps)
            {
                sb.Append("<li>").Append(E(r(step))).AppendLine("</li>");
            }
            sb.AppendLine("</ul>");

            if (fix.Preconditions.Count > 0)
            {
                sb.AppendLine("<div class=\"label\">ONLY IF</div><ul>");
                foreach (var condition in fix.Preconditions)
                {
                    sb.Append("<li>").Append(E(r(condition))).AppendLine("</li>");
                }
                sb.AppendLine("</ul>");
            }

            if (fix.IsReady)
            {
                sb.Append("<pre class=\"cmd\">").Append(E(r(fix.CommandLine))).AppendLine("</pre>");
            }
            else if (!string.IsNullOrWhiteSpace(fix.BlockedReason))
            {
                sb.Append("<p class=\"blocked\">").Append(E(r(fix.BlockedReason))).AppendLine("</p>");
            }

            sb.AppendLine("<p class=\"meta\">This tool does not run fixes from the report. Confirm the conditions " +
                          "above still hold, then run the script from an elevated PowerShell.</p></div>");
        }

        private static void WriteEvidence(StringBuilder sb, Rules.Finding finding, Func<string, string> r)
        {
            if (!finding.HasEvidence)
            {
                return;
            }

            sb.AppendLine("<div class=\"evidence\"><div class=\"label\">EVIDENCE</div>");
            foreach (var evidence in finding.Evidence)
            {
                sb.Append("<div class=\"esrc\">").Append(E(r(evidence.Source)));
                if (evidence.LineNumber.HasValue)
                {
                    sb.Append("  line ").Append(evidence.LineNumber.Value.ToString("N0", CultureInfo.InvariantCulture));
                }
                sb.AppendLine("</div>");
                sb.Append("<pre class=\"eline\">").Append(E(r(evidence.Text))).AppendLine("</pre>");
            }
            sb.AppendLine("</div>");
        }

        // ---------------------------------------------------------------- what was examined

        /// <summary>
        /// Which logs were actually read, and how current they are.
        /// <para>
        /// The old manifest table listed every candidate path in discovery order, so two dozen rows
        /// reading "No" buried the handful that were read. Which files exist is the interesting
        /// half; the date on them is what says whether they describe the failure being investigated
        /// or an upgrade from eighteen months ago.
        /// </para>
        /// </summary>
        private static void WriteExamined(StringBuilder sb, DiagnosticContext context, Func<string, string> r)
        {
            var present = context.Manifest.Where(m => m.Exists).ToList();
            var absent = context.Manifest.Where(m => !m.Exists).ToList();

            sb.AppendLine("<h2 id=\"examined\">What was examined</h2>");

            var newestSetup = context.Manifest
                .Where(m => m.Exists && m.LastWriteTimeUtc.HasValue &&
                            (m.Source.Category == LogSourceCategory.SetupCompleted ||
                             m.Source.Category == LogSourceCategory.SetupCurrent ||
                             m.Source.Category == LogSourceCategory.SetupRollback))
                .OrderByDescending(m => m.LastWriteTimeUtc.Value)
                .FirstOrDefault();

            sb.Append("<p class=\"lede\">").Append(present.Count).Append(" of ").Append(context.Manifest.Count)
              .Append(" known log locations exist on this machine. ");
            if (newestSetup != null)
            {
                sb.Append("The most recent Setup log was last written <strong>")
                  .Append(E(Fmt(newestSetup.LastWriteTimeUtc))).Append(" UTC</strong>")
                  .Append(Staleness(newestSetup.LastWriteTimeUtc.Value, context.StartedAtUtc))
                  .Append(newestSetup.Readable
                      // Only claim to describe an attempt whose logs were actually opened. A file
                      // that exists but returned access-denied tells us its date and nothing else.
                      ? ", so that is the upgrade attempt this report describes."
                      : ", but it could not be read, so its contents are not reflected in the verdict above.");
            }
            else
            {
                sb.Append("No Setup logs were found, so nothing here describes an upgrade attempt.");
            }
            sb.AppendLine("</p>");

            sb.AppendLine("<table><tr><th>Source</th><th>Path</th><th>Size</th><th>Last write (UTC)</th><th>Read</th></tr>");
            foreach (var m in present)
            {
                var cls = m.Source.HighValue ? " class=\"hv\"" : "";
                sb.Append("<tr").Append(cls).Append("><td>").Append(E(m.Source.DisplayName))
                  .Append("</td><td class=\"path\">").Append(E(r(m.ResolvedPath)))
                  .Append("</td><td>").Append(m.SizeKnown ? E(Size(m.SizeBytes)) : "")
                  .Append("</td><td>").Append(E(Fmt(m.LastWriteTimeUtc)))
                  .Append("</td><td>").Append(
                      m.Readable ? "Yes" : "<span class=\"warn\">No</span> " + E(r(m.AccessError)))
                  .AppendLine("</td></tr>");
            }
            sb.AppendLine("</table>");

            if (absent.Count > 0)
            {
                sb.Append("<details><summary>").Append(absent.Count)
                  .AppendLine(" location(s) checked and not present on this machine</summary>");
                sb.AppendLine("<table><tr><th>Source</th><th>Path</th></tr>");
                foreach (var m in absent)
                {
                    sb.Append("<tr class=\"dim\"><td>").Append(E(m.Source.DisplayName))
                      .Append("</td><td class=\"path\">").Append(E(r(m.ResolvedPath)))
                      .AppendLine("</td></tr>");
                }
                sb.AppendLine("</table></details>");
            }
        }

        /// <summary>
        /// Says in words how old a log is. "2024-12-15" means nothing until the reader works out
        /// that it is twenty months before the run, at which point it means a great deal.
        /// </summary>
        public static string Staleness(DateTime lastWriteUtc, DateTime runUtc)
        {
            var days = (runUtc - lastWriteUtc).TotalDays;
            if (days < 0)
            {
                return "";
            }
            if (days < 1)
            {
                return " (today)";
            }
            if (days < 2)
            {
                return " (yesterday)";
            }
            if (days < 45)
            {
                return " (" + ((int)days).ToString(CultureInfo.InvariantCulture) + " days ago)";
            }

            var months = (int)Math.Round(days / 30.44);
            return " (about " + months.ToString(CultureInfo.InvariantCulture) + " months ago — " +
                   "check this is the attempt you are investigating)";
        }

        // ---------------------------------------------------------------- system

        private static void WriteSystem(StringBuilder sb, SystemState s0, Func<string, string> r)
        {
            sb.AppendLine("<h2 id=\"system\">System state</h2><table>");
            Row(sb, "Machine", s0.Machine?.Describe());
            Row(sb, "BIOS", s0.Machine == null ? null : Bios(s0.Machine));
            Row(sb, "Firmware", s0.Machine?.FirmwareType);
            Row(sb, "OS", s0.Os?.FullDescription);
            Row(sb, "Edition", s0.Os?.EditionId);
            Row(sb, "Elevated", YesNo(s0.IsElevated));
            Row(sb, "Setup progress (registry)", s0.SetupProgressPercent.HasValue
                ? s0.SetupProgressPercent.Value.ToString(CultureInfo.InvariantCulture) + "%" : null);
            Row(sb, "Pending reboot", s0.PendingReboot == null ? null : YesNo(s0.PendingReboot.Any));
            Row(sb, "Secure Boot", YesNo(s0.SecureBootEnabled));
            Row(sb, "Memory Integrity (HVCI)", YesNo(s0.MemoryIntegrityEnabled));
            Row(sb, "System drive free", s0.SystemDriveFreeBytes.HasValue
                ? Size(s0.SystemDriveFreeBytes.Value) + " of " + Size(s0.SystemDriveTotalBytes ?? 0) : null);
            Row(sb, "CCM_TSExecutionRequest present", s0.TaskSequenceExecutionRequest == null ? null :
                YesNo(s0.TaskSequenceExecutionRequest.ExecutionRequestExists) +
                (s0.TaskSequenceExecutionRequest.PackageId != null ? " (package " + s0.TaskSequenceExecutionRequest.PackageId + ")" : ""));
            sb.AppendLine("</table>");

            sb.AppendLine("<h3>Upgrade processes and folders</h3>");
            sb.AppendLine("<table><tr><th>Process</th><th>Running</th><th>PID</th><th>Started (UTC)</th></tr>");
            foreach (var p in s0.Processes?.Processes ?? new List<ProcessInfo>())
            {
                sb.Append("<tr><td>").Append(E(p.Name)).Append("</td><td>").Append(YesNo(p.IsRunning))
                  .Append("</td><td>").Append(E(p.ProcessId?.ToString(CultureInfo.InvariantCulture)))
                  .Append("</td><td>").Append(E(Fmt(p.StartTimeUtc))).AppendLine("</td></tr>");
            }
            sb.AppendLine("</table>");

            sb.AppendLine("<table><tr><th>Folder</th><th>Exists</th><th>Last write (UTC)</th></tr>");
            foreach (var f in s0.UpgradeFolders ?? new List<UpgradeFolderInfo>())
            {
                sb.Append("<tr><td class=\"path\">").Append(E(r(f.Path))).Append("</td><td>").Append(YesNo(f.Exists))
                  .Append("</td><td>").Append(E(Fmt(f.LastWriteTimeUtc))).AppendLine("</td></tr>");
            }
            sb.AppendLine("</table>");

            WriteStorage(sb, s0);
            WriteDriverPackages(sb, s0);
            WriteFilterDrivers(sb, s0, r);
        }

        private static string Bios(MachineIdentityInfo machine)
        {
            if (string.IsNullOrWhiteSpace(machine.BiosVersion))
            {
                return null;
            }

            return machine.BiosVersion +
                   (machine.BiosReleaseDate.HasValue
                       ? "  (" + machine.BiosReleaseDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ")"
                       : "");
        }

        /// <summary>
        /// Third-party driver packages with the version actually installed.
        /// <para>
        /// This table exists for one sentence a technician says about every failure: "the same model
        /// upgrades fine". It is usually true and usually beside the point. Two machines off the
        /// same order diverge in BIOS level and driver versions within months, and those are the
        /// axes an upgrade failure falls on. Printing the versions turns the comparison against a
        /// machine that worked into a diff rather than an argument.
        /// </para>
        /// </summary>
        private static void WriteDriverPackages(StringBuilder sb, SystemState s0)
        {
            var packages = s0.DriverPackages ?? new List<DriverPackageInfo>();
            if (packages.Count == 0)
            {
                return;
            }

            sb.AppendLine("<h3>Third-party driver packages</h3>");
            sb.Append("<p class=\"lede\">").Append(packages.Count)
              .AppendLine(" package(s) Windows did not ship, with the version installed on this machine. " +
                          "If the same model upgrades successfully elsewhere, this table and the BIOS level above " +
                          "are where the two machines differ — compare them against a report from one that worked " +
                          "before looking anywhere else. The published name is what " +
                          "<code>pnputil /delete-driver</code> takes.</p>");

            sb.AppendLine("<table><tr><th>Published</th><th>Vendor</th><th>Class</th><th>Version</th>" +
                          "<th>Driver date</th><th>Device</th></tr>");
            foreach (var p in packages)
            {
                sb.Append("<tr><td class=\"path\">").Append(E(p.PublishedName))
                  .Append("</td><td>").Append(E(p.Provider))
                  .Append("</td><td>").Append(E(p.DeviceClass))
                  .Append("</td><td>").Append(E(p.Version))
                  .Append("</td><td>").Append(E(p.DriverDate.HasValue
                      ? p.DriverDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null))
                  .Append("</td><td>").Append(E(p.DeviceName)).AppendLine("</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        private static void WriteStorage(StringBuilder sb, SystemState s0)
        {
            var disks = (s0.StorageHealth ?? new List<StorageHealthInfo>()).Where(d => d.Error == null).ToList();
            if (disks.Count == 0)
            {
                return;
            }

            sb.AppendLine("<h3>Storage health</h3>");
            sb.AppendLine("<p class=\"lede\">Reliability counters come from the drive itself. A blank cell means the " +
                          "drive does not report that counter, which is common on consumer NVMe and is not a fault.</p>");
            sb.AppendLine("<table><tr><th>Disk</th><th>Health</th><th>Wear</th><th>Read errors (uncorrected)</th>" +
                          "<th>Write errors (uncorrected)</th><th>Power-on hours</th></tr>");
            foreach (var d in disks)
            {
                sb.Append("<tr><td>").Append(E(d.FriendlyName)).Append("</td><td>").Append(E(d.HealthStatus))
                  .Append("</td><td>").Append(Num(d.Wear))
                  .Append("</td><td>").Append(Num(d.ReadErrorsUncorrected))
                  .Append("</td><td>").Append(Num(d.WriteErrorsUncorrected))
                  .Append("</td><td>").Append(Num(d.PowerOnHours)).AppendLine("</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        /// <summary>
        /// Filter drivers, third-party ones first.
        /// <para>
        /// The list is long and almost entirely in-box, and the in-box rows displayed a raw MUI
        /// reference — "@%systemroot%\system32\drivers\AppvStrm.sys,-101" — which reads like a bug
        /// rather than a name. The rows that matter for an upgrade are the third-party ones:
        /// filter drivers from security and backup agents are a standard cause of a rollback, and
        /// those are exactly the rows that were hardest to pick out.
        /// </para>
        /// </summary>
        private static void WriteFilterDrivers(StringBuilder sb, SystemState s0, Func<string, string> r)
        {
            var drivers = s0.FilterDrivers ?? new List<FilterDriverInfo>();
            if (drivers.Count == 0)
            {
                return;
            }

            var ordered = drivers
                .Select(d => new { Driver = d, ThirdParty = IsThirdParty(d) })
                .OrderByDescending(x => x.ThirdParty)
                .ThenBy(x => x.Driver.ServiceName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var thirdPartyCount = ordered.Count(x => x.ThirdParty);

            sb.AppendLine("<h3>File system filter drivers</h3>");
            sb.Append("<p class=\"lede\">").Append(thirdPartyCount)
              .Append(thirdPartyCount == 1 ? " third-party filter driver" : " third-party filter drivers")
              .Append(" of ").Append(ordered.Count)
              .AppendLine(" total, listed first. Third-party filters from security, backup and encryption agents are a " +
                          "common cause of an upgrade rolling back; the in-box ones below them are listed for " +
                          "completeness. Origin is inferred from the driver's image path, so treat it as a hint.</p>");

            sb.AppendLine("<table><tr><th>Origin</th><th>Service</th><th>Name</th><th>Altitude group</th>" +
                          "<th>Start</th><th>Image</th></tr>");
            foreach (var x in ordered)
            {
                var d = x.Driver;
                sb.Append(x.ThirdParty ? "<tr class=\"hv\"><td><strong>Third-party</strong></td><td>" : "<tr class=\"dim\"><td>In-box</td><td>")
                  .Append(E(d.ServiceName))
                  // Repeating the service name in the name column is just noise; say plainly that
                  // Windows never expanded a name for this one.
                  .Append("</td><td>").Append(
                      string.Equals(DriverName(d), d.ServiceName, StringComparison.Ordinal)
                          ? "<span class=\"dim\">no name registered</span>"
                          : E(DriverName(d)))
                  .Append("</td><td>").Append(E(d.AltitudeGroup))
                  .Append("</td><td>").Append(E(d.StartModeName))
                  .Append("</td><td class=\"path\">").Append(E(r(d.ImagePath))).AppendLine("</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        /// <summary>
        /// A readable name for a filter driver. Service display names are frequently stored as an
        /// unexpanded MUI reference; showing the service name instead is more use than showing the
        /// resource id of a string nobody loaded.
        /// </summary>
        public static string DriverName(FilterDriverInfo driver)
        {
            var name = driver.DisplayName;
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith("@", StringComparison.Ordinal))
            {
                return driver.ServiceName ?? "";
            }
            return name;
        }

        /// <summary>
        /// Whether a filter driver ships with Windows, inferred from where its image lives. In-box
        /// filters load from System32\drivers under the Windows directory; anything installed by a
        /// third-party agent gets a different path, or an OEM inf as its display name.
        /// </summary>
        public static bool IsThirdParty(FilterDriverInfo driver)
        {
            var display = driver.DisplayName ?? "";
            if (display.StartsWith("@oem", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var image = (driver.ImagePath ?? "").Replace('/', '\\');
            if (image.Length == 0)
            {
                // No image path to judge by. An unexpanded system MUI reference still says in-box.
                return !display.StartsWith("@%systemroot%", StringComparison.OrdinalIgnoreCase) &&
                       !display.StartsWith("@%SystemRoot%", StringComparison.Ordinal) &&
                       display.StartsWith("@", StringComparison.Ordinal);
            }

            image = image.TrimStart('\\');
            if (image.StartsWith("SystemRoot\\", StringComparison.OrdinalIgnoreCase))
            {
                image = image.Substring("SystemRoot\\".Length);
            }
            else if (image.StartsWith("??\\", StringComparison.Ordinal))
            {
                image = image.Substring(3);
            }

            return !image.StartsWith("system32\\drivers\\", StringComparison.OrdinalIgnoreCase) &&
                   !image.StartsWith("System32\\drivers\\", StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------- events

        /// <summary>
        /// Errors and criticals up front, the rest folded away.
        /// <para>
        /// The event table used to be the report: 105 rows, mostly Information, mostly unrelated
        /// telemetry, running to twenty times the length of everything else combined. Repeats are
        /// collapsed to one row with a count, because the same crash logged eleven times is one
        /// fact, not eleven.
        /// </para>
        /// </summary>
        private static void WriteEvents(StringBuilder sb, SystemState s0, Func<string, string> r)
        {
            var events = (s0.Events ?? new List<EventRecordInfo>())
                .OrderByDescending(e => e.TimeCreatedUtc)
                .Take(MaxEventsInReport)
                .ToList();

            sb.AppendLine("<h2 id=\"events\">Event log</h2>");

            if (events.Count == 0)
            {
                sb.AppendLine("<p class=\"lede\">No events were collected.</p>");
                return;
            }

            var significant = events.Where(e => SignificantLevels.Contains(e.Level ?? "")).ToList();

            sb.Append("<p class=\"lede\">").Append(events.Count)
              .Append(" events collected from the System and Application logs, newest first. ");
            sb.Append(significant.Count == 0
                ? "None of them are errors."
                : significant.Count + (significant.Count == 1 ? " is an error or critical event" : " are errors or critical events") +
                  ", shown below. The rest are folded away.");
            sb.AppendLine("</p>");

            if (significant.Count > 0)
            {
                EventTable(sb, significant, r);
            }

            sb.Append("<details><summary>All ").Append(events.Count)
              .AppendLine(" collected events</summary>");
            EventTable(sb, events, r);
            sb.AppendLine("</details>");
        }

        private static void EventTable(StringBuilder sb, IReadOnlyList<EventRecordInfo> events, Func<string, string> r)
        {
            sb.AppendLine("<table><tr><th>Time (UTC)</th><th>Log</th><th>Provider</th><th>ID</th>" +
                          "<th>Level</th><th>Message</th></tr>");

            // Collapse repeats: one crash logged eleven times is one fact.
            var groups = events
                .GroupBy(e => new
                {
                    e.LogName,
                    e.ProviderName,
                    e.EventId,
                    e.Level,
                    Head = Truncate(e.Message, 180)
                })
                .Select(g => new { Latest = g.OrderByDescending(e => e.TimeCreatedUtc).First(), Count = g.Count() })
                .OrderByDescending(g => g.Latest.TimeCreatedUtc)
                .ToList();

            foreach (var g in groups)
            {
                var e = g.Latest;
                var cls = SignificantLevels.Contains(e.Level ?? "") ? " class=\"ev-err\"" : "";
                sb.Append("<tr").Append(cls).Append("><td>").Append(E(Fmt(e.TimeCreatedUtc)))
                  .Append("</td><td>").Append(E(e.LogName))
                  .Append("</td><td>").Append(E(e.ProviderName))
                  .Append("</td><td>").Append(e.EventId)
                  .Append("</td><td>").Append(E(e.Level))
                  .Append("</td><td class=\"msg\">");
                if (g.Count > 1)
                {
                    sb.Append("<span class=\"rep\">&times;").Append(g.Count).Append("</span> ");
                }
                sb.Append(E(r(Truncate(e.Message, 600)))).AppendLine("</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        // ---------------------------------------------------------------- style

        private const string Css =
            "body{font-family:Segoe UI,Arial,sans-serif;margin:0;padding:28px 32px 48px;color:#1b1b1b;" +
            "background:#fff;max-width:1100px;line-height:1.45}" +
            "header{border-bottom:2px solid #1b4f7a;padding-bottom:14px;margin-bottom:6px}" +
            ".doctype{font-size:11px;font-weight:700;letter-spacing:.09em;text-transform:uppercase;color:#1b4f7a}" +
            "h1{font-size:26px;margin:4px 0 12px;font-weight:600}" +
            "dl.ident{display:grid;grid-template-columns:max-content 1fr;gap:3px 16px;margin:0;font-size:12.5px}" +
            "dl.ident dt{color:#667;font-weight:600}dl.ident dd{margin:0}" +
            "h2{font-size:18px;margin-top:34px;border-bottom:1px solid #ddd;padding-bottom:5px;font-weight:600}" +
            "h3{font-size:14px;margin-top:22px;font-weight:600;color:#33404d}" +
            ".meta{color:#555;font-size:12px}" +
            ".lede{font-size:12.5px;color:#4a5560;margin:8px 0 4px;max-width:78ch}" +
            ".foot{margin-top:36px;padding-top:12px;border-top:1px solid #e3e7ec;color:#777;font-size:11.5px}" +
            "table{border-collapse:collapse;width:100%;font-size:12px;margin-top:8px}" +
            "th,td{border:1px solid #ddd;padding:5px 7px;text-align:left;vertical-align:top}th{background:#f3f5f7}" +
            ".card{padding:11px 13px;border-radius:4px;margin:12px 0;font-size:13px}" +
            ".info{background:#eef4fb;border:1px solid #b9d3ee}.card.warn{background:#fff4e5;border:1px solid #f0c27b}" +
            "span.warn,strong.warn{color:#a15c00}.hv td{background:#fffbe6}.dim td{color:#8a8a8a}" +
            "span.dim{color:#8a8a8a}" +
            ".path{font-family:Consolas,monospace;word-break:break-all}.msg{white-space:pre-wrap}" +
            // Contents
            "nav.toc{display:flex;flex-wrap:wrap;gap:6px 8px;margin:18px 0 4px;font-size:12px}" +
            "nav.toc a{color:#1b4f7a;text-decoration:none;border:1px solid #cfdcea;background:#f5f9fd;" +
            "border-radius:20px;padding:4px 12px}" +
            // Verdict and findings
            ".verdict{border-radius:6px;border:1px solid;padding:18px 20px;margin:18px 0}" +
            ".verdict .kind{font-size:11px;font-weight:700;letter-spacing:.06em;text-transform:uppercase;opacity:.85}" +
            ".verdict .headline{font-size:20px;margin:8px 0 0;border:0;padding:0}" +
            ".verdict .detail{font-size:14px;line-height:1.5;margin:10px 0 0;color:#333}" +
            ".verdict .action{background:#fff;border:1px solid #ddd;border-radius:4px;padding:14px;margin-top:16px}" +
            ".verdict .action p{margin:6px 0 0;color:#1b1b1b;font-size:13.5px}" +
            ".label{font-size:11px;font-weight:700;letter-spacing:.06em;color:#666}" +
            "pre.cmd{background:#14181f;color:#e6edf3;padding:10px 12px;border-radius:4px;overflow-x:auto;" +
            "font-size:12px;white-space:pre-wrap;word-break:break-all}" +
            ".finding{border:1px solid #e3e7ec;border-left-width:4px;border-radius:5px;padding:14px 16px;" +
            "margin:10px 0;background:#fff}" +
            ".fhead{display:flex;align-items:baseline;gap:10px;flex-wrap:wrap}" +
            ".fhead .ftitle{font-size:15px;font-weight:600;flex:1}" +
            ".fhead .conf{font-size:11px;color:#666;white-space:nowrap}" +
            ".cause{font-size:10px;font-weight:700;letter-spacing:.07em;background:#1b4f7a;color:#fff;" +
            "padding:2px 7px;border-radius:3px;white-space:nowrap}" +
            ".badge{font-size:11px;font-weight:700;padding:2px 7px;border-radius:3px;border:1px solid}" +
            ".sev-critical{border-left-color:#8e1f1f}.sev-critical .badge{color:#8e1f1f;background:#fdecec;border-color:#f0bdbd}" +
            ".sev-warning{border-left-color:#8a5200}.sev-warning .badge{color:#8a5200;background:#fff6e6;border-color:#f3d39a}" +
            ".sev-info{border-left-color:#1b4f7a}.sev-info .badge{color:#1b4f7a;background:#edf4fb;border-color:#b9d3ee}" +
            ".verdict.sev-critical{background:#fdecec;border-color:#f0bdbd;color:#8e1f1f}" +
            ".verdict.sev-warning{background:#fff6e6;border-color:#f3d39a;color:#8a5200}" +
            ".verdict.sev-info{background:#edf4fb;border-color:#b9d3ee;color:#1b4f7a}" +
            ".evidence{background:#f7f8fa;border:1px solid #e3e7ec;border-radius:4px;padding:10px 12px;margin-top:12px}" +
            ".esrc{font-size:11px;color:#666;margin-top:8px}" +
            "pre.eline{font-family:Consolas,monospace;font-size:12px;background:#fff;border:1px solid #e3e7ec;" +
            "border-radius:3px;padding:7px 9px;margin:3px 0 0;overflow-x:auto;white-space:pre-wrap;word-break:break-all}" +
            ".fix{background:#f7f8fa;border:1px solid #e3e7ec;border-radius:4px;padding:10px 13px;margin-top:12px}" +
            ".fix ul{margin:4px 0 10px;padding-left:20px;font-size:12px;color:#3b444f}" +
            ".fix li{margin:2px 0}.fix code{font-family:Consolas,monospace;font-size:12px;color:#1f4b78}" +
            ".fix .label{margin-top:8px}.blocked{color:#8a5200;font-size:12px}" +
            // Folded sections and events
            "details{margin-top:12px}" +
            "summary{cursor:pointer;font-size:12.5px;color:#1b4f7a;padding:6px 0;font-weight:600}" +
            "tr.ev-err td{background:#fdf4f4}" +
            ".rep{display:inline-block;background:#e6ebf1;color:#3b444f;border-radius:3px;padding:0 5px;" +
            "font-size:11px;font-weight:700;margin-right:5px}" +
            // Print: this gets saved to PDF and attached to tickets, so open every fold and drop
            // the interactive styling that means nothing on paper.
            "@media print{body{padding:0;max-width:none;font-size:11px}" +
            "details{display:block}details>summary{display:none}" +
            "nav.toc{display:none}" +
            "h2{margin-top:20px}.finding,.verdict,table{page-break-inside:avoid}" +
            "a{color:inherit;text-decoration:none}}";

        // ---------------------------------------------------------------- helpers

        private static string SeverityClass(Rules.Severity severity)
        {
            switch (severity)
            {
                case Rules.Severity.Critical: return "sev-critical";
                case Rules.Severity.Warning: return "sev-warning";
                default: return "sev-info";
            }
        }

        private static string Humanise(Rules.VerdictKind kind)
        {
            switch (kind)
            {
                case Rules.VerdictKind.InProgress: return "Upgrade in progress";
                case Rules.VerdictKind.CauseIdentified: return "Cause identified";
                case Rules.VerdictKind.Inconclusive: return "Inconclusive";
                case Rules.VerdictKind.NoFailureFound: return "No failure found";
                case Rules.VerdictKind.InsufficientEvidence: return "Insufficient evidence";
                default: return kind.ToString();
            }
        }

        private static void Row(StringBuilder sb, string label, string value)
        {
            sb.Append("<tr><th style=\"width:28%\">").Append(E(label)).Append("</th><td>")
              .Append(value == null ? "<span class=\"dim\">unknown</span>" : E(value)).AppendLine("</td></tr>");
        }

        private static string Num(ulong? value) =>
            value.HasValue ? E(value.Value.ToString("N0", CultureInfo.InvariantCulture)) : "";

        private static string Num(int? value) =>
            value.HasValue ? E(value.Value.ToString("N0", CultureInfo.InvariantCulture)) : "";

        private static string YesNo(bool value) => value ? "Yes" : "No";
        private static string YesNo(bool? value) => value.HasValue ? YesNo(value.Value) : null;

        private static string Fmt(DateTime? utc) =>
            utc.HasValue && utc.Value != default(DateTime) ? utc.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "";

        private static string Truncate(string s, int max) =>
            s == null || s.Length <= max ? s : s.Substring(0, max) + "…";

        public static string Size(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return value.ToString(unit == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
        }

        /// <summary>Minimal HTML encoder, kept local so the project has no System.Net reference at all.</summary>
        public static string E(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "";
            }

            var sb = new StringBuilder(s.Length + 16);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '&': sb.Append("&amp;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&#39;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
