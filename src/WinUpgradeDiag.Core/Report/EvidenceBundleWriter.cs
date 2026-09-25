using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using WinUpgradeDiag.Core.Discovery;
using WinUpgradeDiag.Core.IO;
using WinUpgradeDiag.Core.Orchestration;

namespace WinUpgradeDiag.Core.Report
{
    /// <summary>
    /// Evidence zip for internal escalation: the collected logs plus an unredacted findings.json.
    /// Per docs/SECURITY.md, crash dumps are referenced by path only and never copied, and the
    /// zip is written locally and never sent anywhere.
    /// </summary>
    public static class EvidenceBundleWriter
    {
        public const long MaxFileBytes = 512L * 1024 * 1024;

        public static IReadOnlyList<string> Write(DiagnosticContext context, string zipPath)
        {
            var skipped = new List<string>();

            using (var zipStream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in context.Manifest.Where(e => e.Exists))
                {
                    if (entry.Source.Category == LogSourceCategory.CrashDump ||
                        entry.ResolvedPath.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
                    {
                        skipped.Add(entry.ResolvedPath + " (crash dump: referenced, not copied)");
                        continue;
                    }

                    if (entry.SizeBytes > MaxFileBytes)
                    {
                        skipped.Add(entry.ResolvedPath + " (larger than " + HtmlReportWriter.Size(MaxFileBytes) + ")");
                        continue;
                    }

                    var name = UniqueName(usedNames, "logs/" + entry.Source.Category + "/" + Path.GetFileName(entry.ResolvedPath));
                    try
                    {
                        using (var source = TailReader.OpenForRead(entry.ResolvedPath))
                        using (var target = archive.CreateEntry(name, CompressionLevel.Optimal).Open())
                        {
                            source.CopyTo(target);
                        }
                    }
                    catch (Exception ex)
                    {
                        skipped.Add(entry.ResolvedPath + " (" + ex.Message + ")");
                    }
                }

                WriteText(archive, "findings.json", JsonReportWriter.Serialize(context, null));

                var notes = new StringBuilder();
                notes.AppendLine("Evidence bundle — NOT redacted. For internal escalation only.");
                notes.AppendLine("Files not included:");
                foreach (var s in skipped)
                {
                    notes.AppendLine("  " + s);
                }
                WriteText(archive, "README.txt", notes.ToString());
            }

            return skipped;
        }

        private static void WriteText(ZipArchive archive, string name, string content)
        {
            using (var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false)))
            {
                writer.Write(content);
            }
        }

        private static string UniqueName(HashSet<string> used, string name)
        {
            var candidate = name;
            var i = 2;
            while (!used.Add(candidate))
            {
                candidate = Path.GetDirectoryName(name)?.Replace('\\', '/') + "/" +
                            Path.GetFileNameWithoutExtension(name) + "_" + i++ + Path.GetExtension(name);
            }
            return candidate;
        }
    }
}
