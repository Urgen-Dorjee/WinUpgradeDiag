using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using WinUpgradeDiag.Core.Collect;
using WinUpgradeDiag.Core.Orchestration;
using WinUpgradeDiag.Core.Redaction;

namespace WinUpgradeDiag.Core.Report
{
    /// <summary>
    /// Self-contained HTML report: inline CSS, no scripts, no external assets (DESIGN.md §4.6).
    /// Every value is HTML-encoded and, unless explicitly disabled, redacted.
    /// </summary>
    public static class HtmlReportWriter
    {
        private const int MaxEventsInReport = 200;

        public static void Write(DiagnosticContext context, Redactor redactor, string path)
        {
            File.WriteAllText(path, Render(context, redactor), new UTF8Encoding(false));
        }

        public static string Render(DiagnosticContext context, Redactor redactor)
        {
            Func<string, string> r = s => redactor == null ? s : redactor.Redact(s);
            var s0 = context.SystemState ?? new SystemState();
            var sb = new StringBuilder();

            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
            sb.AppendLine("<title>WinUpgradeDiag report</title>");
            sb.AppendLine("<style>" + Css + "</style></head><body>");

            sb.AppendLine("<h1>WinUpgradeDiag report</h1>");
            sb.Append("<p class=\"meta\">Machine: ").Append(E(r(s0.MachineName)))
              .Append(" &middot; Collected: ").Append(E(Fmt(context.StartedAtUtc)))
              .Append(" UTC &middot; Tool ").Append(E(context.ToolVersion))
              .Append(redactor != null ? " &middot; <strong>Redacted</strong>" : " &middot; <strong class=\"warn\">NOT redacted</strong>")
              .AppendLine("</p>");

            WriteVerdict(sb, context, r);

            if (context.Cancelled)
            {
                sb.AppendLine("<div class=\"card warn\">The run was cancelled; sections below may be incomplete.</div>");
            }
            if (!context.PrivilegedReadEnabled)
            {
                sb.Append("<div class=\"card warn\">Protected logs could not be read with backup privilege: ")
                  .Append(E(r(context.PrivilegedReadError))).AppendLine("</div>");
            }

            // --- System ---
            sb.AppendLine("<h2>System</h2><table>");
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

            sb.AppendLine("<h3>Processes</h3><table><tr><th>Process</th><th>Running</th><th>PID</th><th>Started (UTC)</th></tr>");
            foreach (var p in s0.Processes?.Processes ?? new List<ProcessInfo>())
            {
                sb.Append("<tr><td>").Append(E(p.Name)).Append("</td><td>").Append(YesNo(p.IsRunning))
                  .Append("</td><td>").Append(E(p.ProcessId?.ToString(CultureInfo.InvariantCulture)))
                  .Append("</td><td>").Append(E(Fmt(p.StartTimeUtc))).AppendLine("</td></tr>");
            }
            sb.AppendLine("</table>");

            sb.AppendLine("<h3>Upgrade folders</h3><table><tr><th>Path</th><th>Exists</th><th>Last write (UTC)</th></tr>");
            foreach (var f in s0.UpgradeFolders ?? new List<UpgradeFolderInfo>())
            {
                sb.Append("<tr><td>").Append(E(r(f.Path))).Append("</td><td>").Append(YesNo(f.Exists))
                  .Append("</td><td>").Append(E(Fmt(f.LastWriteTimeUtc))).AppendLine("</td></tr>");
            }
            sb.AppendLine("</table>");

            sb.AppendLine("<h3>Storage health</h3><table><tr><th>Disk</th><th>Health</th><th>Wear</th><th>Read errors (uncorrected)</th><th>Write errors (uncorrected)</th><th>Power-on hours</th></tr>");
            foreach (var d in (s0.StorageHealth ?? new List<StorageHealthInfo>()).Where(d => d.Error == null))
            {
                sb.Append("<tr><td>").Append(E(d.FriendlyName)).Append("</td><td>").Append(E(d.HealthStatus))
                  .Append("</td><td>").Append(E(d.Wear?.ToString(CultureInfo.InvariantCulture)))
                  .Append("</td><td>").Append(E(d.ReadErrorsUncorrected?.ToString(CultureInfo.InvariantCulture)))
                  .Append("</td><td>").Append(E(d.WriteErrorsUncorrected?.ToString(CultureInfo.InvariantCulture)))
                  .Append("</td><td>").Append(E(d.PowerOnHours?.ToString(CultureInfo.InvariantCulture))).AppendLine("</td></tr>");
            }
            sb.AppendLine("</table>");

            sb.AppendLine("<h3>Filter drivers</h3><table><tr><th>Service</th><th>Display name</th><th>Altitude group</th><th>Start</th><th>Image</th></tr>");
            foreach (var f in s0.FilterDrivers ?? new List<FilterDriverInfo>())
            {
                sb.Append("<tr><td>").Append(E(f.ServiceName)).Append("</td><td>").Append(E(f.DisplayName))
                  .Append("</td><td>").Append(E(f.AltitudeGroup))
                  .Append("</td><td>").Append(E(f.StartModeName))
                  .Append("</td><td>").Append(E(r(f.ImagePath))).AppendLine("</td></tr>");
            }
            sb.AppendLine("</table>");

            // --- Manifest ---
            sb.AppendLine("<h2>Log manifest</h2><table><tr><th>Source</th><th>Path</th><th>Exists</th><th>Size</th><th>Last write (UTC)</th><th>Readable</th></tr>");
            foreach (var m in context.Manifest)
            {
                var cls = m.Source.HighValue && m.Exists ? " class=\"hv\"" : (!m.Exists ? " class=\"dim\"" : "");
                sb.Append("<tr").Append(cls).Append("><td>").Append(E(m.Source.DisplayName))
                  .Append("</td><td class=\"path\">").Append(E(r(m.ResolvedPath)))
                  .Append("</td><td>").Append(YesNo(m.Exists))
                  .Append("</td><td>").Append(m.SizeKnown ? E(Size(m.SizeBytes)) : "")
                  .Append("</td><td>").Append(E(Fmt(m.LastWriteTimeUtc)))
                  .Append("</td><td>").Append(
                      m.Readable ? "Yes"
                      : !m.Exists && !m.RequiresPrivilegedRead ? ""
                      : "<span class=\"warn\">No</span> " + E(r(m.AccessError)))
                  .AppendLine("</td></tr>");
            }
            sb.AppendLine("</table>");

            // --- Events ---
            var events = (s0.Events ?? new List<EventRecordInfo>()).OrderByDescending(e => e.TimeCreatedUtc).Take(MaxEventsInReport).ToList();
            sb.Append("<h2>Event log (newest ").Append(events.Count).AppendLine(")</h2>");
            sb.AppendLine("<table><tr><th>Time (UTC)</th><th>Log</th><th>Provider</th><th>ID</th><th>Level</th><th>Message</th></tr>");
            foreach (var e in events)
            {
                sb.Append("<tr><td>").Append(E(Fmt(e.TimeCreatedUtc))).Append("</td><td>").Append(E(e.LogName))
                  .Append("</td><td>").Append(E(e.ProviderName)).Append("</td><td>").Append(e.EventId)
                  .Append("</td><td>").Append(E(e.Level)).Append("</td><td class=\"msg\">").Append(E(r(Truncate(e.Message, 600))))
                  .AppendLine("</td></tr>");
            }
            sb.AppendLine("</table>");

            if (s0.CollectionErrors.Count > 0)
            {
                sb.AppendLine("<h2>Collection gaps</h2><ul>");
                foreach (var err in s0.CollectionErrors)
                {
                    sb.Append("<li>").Append(E(r(err))).AppendLine("</li>");
                }
                sb.AppendLine("</ul>");
            }

            sb.AppendLine("<p class=\"meta\">Generated offline on the machine. This file references no external resources.</p>");
            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        private const string Css =
            "body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#1b1b1b;background:#fff}" +
            "h1{font-size:22px;margin:0 0 4px}h2{font-size:18px;margin-top:28px;border-bottom:1px solid #ddd;padding-bottom:4px}" +
            "h3{font-size:15px;margin-top:18px}.meta{color:#555;font-size:12px}" +
            "table{border-collapse:collapse;width:100%;font-size:12px;margin-top:6px}" +
            "th,td{border:1px solid #ddd;padding:4px 6px;text-align:left;vertical-align:top}th{background:#f3f3f3}" +
            ".card{padding:10px 12px;border-radius:4px;margin:12px 0;font-size:13px}" +
            ".info{background:#eef4fb;border:1px solid #b9d3ee}.card.warn{background:#fff4e5;border:1px solid #f0c27b}" +
            "span.warn,strong.warn{color:#a15c00}.hv td{background:#fffbe6}.dim td{color:#999}" +
            ".path{font-family:Consolas,monospace;word-break:break-all}.msg{white-space:pre-wrap}" +
            // Verdict and findings
            ".verdict{border-radius:6px;border:1px solid;padding:18px 20px;margin:16px 0}" +
            ".verdict .kind{font-size:11px;font-weight:700;letter-spacing:.06em;text-transform:uppercase;opacity:.85}" +
            ".verdict .headline{font-size:20px;margin:8px 0 0;border:0;padding:0}" +
            ".verdict .detail{font-size:14px;line-height:1.5;margin:10px 0 0;color:#333}" +
            ".verdict .action{background:#fff;border:1px solid #ddd;border-radius:4px;padding:14px;margin-top:16px}" +
            ".label{font-size:11px;font-weight:700;letter-spacing:.06em;color:#666}" +
            "pre.cmd{background:#14181f;color:#e6edf3;padding:10px 12px;border-radius:4px;overflow-x:auto;font-size:12px;white-space:pre-wrap;word-break:break-all}" +
            ".finding{border:1px solid #e3e7ec;border-left-width:4px;border-radius:5px;padding:14px 16px;margin:10px 0;background:#fff}" +
            ".fhead{display:flex;align-items:baseline;gap:10px;flex-wrap:wrap}" +
            ".fhead .ftitle{font-size:15px;font-weight:600;flex:1}" +
            ".fhead .conf{font-size:11px;color:#666;white-space:nowrap}" +
            ".badge{font-size:11px;font-weight:700;padding:2px 7px;border-radius:3px;border:1px solid}" +
            ".sev-critical{border-left-color:#8e1f1f}.sev-critical .badge{color:#8e1f1f;background:#fdecec;border-color:#f0bdbd}" +
            ".sev-warning{border-left-color:#8a5200}.sev-warning .badge{color:#8a5200;background:#fff6e6;border-color:#f3d39a}" +
            ".sev-info{border-left-color:#1b4f7a}.sev-info .badge{color:#1b4f7a;background:#edf4fb;border-color:#b9d3ee}" +
            ".verdict.sev-critical{background:#fdecec;border-color:#f0bdbd;color:#8e1f1f}" +
            ".verdict.sev-warning{background:#fff6e6;border-color:#f3d39a;color:#8a5200}" +
            ".verdict.sev-info{background:#edf4fb;border-color:#b9d3ee;color:#1b4f7a}" +
            ".evidence{background:#f7f8fa;border:1px solid #e3e7ec;border-radius:4px;padding:10px 12px;margin-top:12px}" +
            ".esrc{font-size:11px;color:#666;margin-top:8px}" +
            "pre.eline{font-family:Consolas,monospace;font-size:12px;background:#fff;border:1px solid #e3e7ec;border-radius:3px;padding:7px 9px;margin:3px 0 0;overflow-x:auto;white-space:pre-wrap;word-break:break-all}" +
            ".fix{background:#f7f8fa;border:1px solid #e3e7ec;border-radius:4px;padding:10px 13px;margin-top:12px}" +
            ".fix ul{margin:4px 0 10px;padding-left:20px;font-size:12px;color:#3b444f}" +
            ".fix li{margin:2px 0}.fix code{font-family:Consolas,monospace;font-size:12px;color:#1f4b78}" +
            ".fix .label{margin-top:8px}.blocked{color:#8a5200;font-size:12px}";

        /// <summary>
        /// The verdict, the recommended action and the ranked findings — written first, because a
        /// report attached to a ticket has to answer the question on its opening screen rather than
        /// make the reader scroll through a manifest.
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

            var cls = SeverityClass(verdict.DisplaySeverity);

            sb.Append("<div class=\"verdict ").Append(cls).AppendLine("\">");
            sb.Append("<div class=\"kind\">").Append(E(Humanise(verdict.Kind))).AppendLine("</div>");
            sb.Append("<h2 class=\"headline\">").Append(E(r(verdict.Headline))).AppendLine("</h2>");
            sb.Append("<p class=\"detail\">").Append(E(r(verdict.Detail))).AppendLine("</p>");

            var top = verdict.TopFinding;
            if (top != null && !string.IsNullOrWhiteSpace(top.Action))
            {
                sb.AppendLine("<div class=\"action\"><div class=\"label\">RECOMMENDED ACTION</div>");
                sb.Append("<p>").Append(E(r(top.Action))).AppendLine("</p>");
                if (top.HasCommand)
                {
                    sb.Append("<pre class=\"cmd\">").Append(E(r(top.Command))).AppendLine("</pre>");
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

            sb.Append("<h2>Findings</h2>");
            if (!verdict.HasFindings)
            {
                sb.AppendLine("<p class=\"meta\">Nothing in the collected state or the logs that were read matched a known failure pattern.</p>");
                return;
            }

            sb.AppendLine("<p class=\"meta\">Ranked by how likely each is to be the cause. Every finding quotes the evidence that produced it.</p>");

            foreach (var finding in verdict.Findings)
            {
                sb.Append("<div class=\"finding ").Append(SeverityClass(finding.Severity)).AppendLine("\">");
                sb.Append("<div class=\"fhead\"><span class=\"badge\">").Append(E(finding.SeverityText))
                  .Append("</span><span class=\"ftitle\">").Append(E(r(finding.Title)))
                  .Append("</span><span class=\"conf\">").Append(E(finding.ConfidenceText))
                  .Append(" &middot; ").Append(E(finding.Id)).AppendLine("</span></div>");

                sb.Append("<p>").Append(E(r(finding.Meaning))).AppendLine("</p>");
                sb.Append("<p><strong>What to do:</strong> ").Append(E(r(finding.Action))).AppendLine("</p>");

                if (finding.HasCommand)
                {
                    sb.Append("<pre class=\"cmd\">").Append(E(r(finding.Command))).AppendLine("</pre>");
                }

                if (finding.HasRemediation)
                {
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

                    sb.AppendLine("<p class=\"meta\">This tool does not run fixes. Confirm the conditions above " +
                                  "still hold, then run the script from an elevated PowerShell.</p></div>");
                }

                if (finding.HasEvidence)
                {
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

                sb.AppendLine("</div>");
            }
        }

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
