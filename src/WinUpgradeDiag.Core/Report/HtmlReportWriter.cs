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

            sb.AppendLine("<div class=\"card info\"><strong>No verdict in this version.</strong> " +
                          "This report lists what was found on the machine. Automated diagnosis arrives in a later release.</div>");

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
            Row(sb, "OS", s0.Os == null ? null :
                string.Join(" ", new[] { s0.Os.ProductName, s0.Os.DisplayVersion, "build " + s0.Os.CurrentBuildNumber + "." + s0.Os.Ubr }.Where(x => x != null)));
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
            ".path{font-family:Consolas,monospace;word-break:break-all}.msg{white-space:pre-wrap}";

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
