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
    /// <summary>What the evidence bundle did with each log it was offered.</summary>
    public sealed class EvidenceBundleResult
    {
        /// <summary>Logs left out entirely, each with the reason.</summary>
        public IReadOnlyList<string> Omitted { get; }

        /// <summary>
        /// Logs too large to include whole, of which only the tail was captured. Distinct from
        /// <see cref="Omitted"/> because a truncated setupact.log is still the primary evidence,
        /// whereas an absent one silently invites the wrong conclusion.
        /// </summary>
        public IReadOnlyList<string> PartiallyCaptured { get; }

        public EvidenceBundleResult(IReadOnlyList<string> omitted, IReadOnlyList<string> partiallyCaptured)
        {
            Omitted = omitted;
            PartiallyCaptured = partiallyCaptured;
        }
    }

    /// <summary>
    /// Evidence zip for internal escalation: the collected logs plus an unredacted findings.json.
    /// Per docs/SECURITY.md, crash dumps are referenced by path only and never copied, and the
    /// zip is written locally and never sent anywhere.
    /// </summary>
    public static class EvidenceBundleWriter
    {
        /// <summary>Logs up to this size are copied in full.</summary>
        public const long MaxWholeFileBytes = 512L * 1024 * 1024;

        /// <summary>
        /// How much of the end of an oversized log to capture. Beyond
        /// <see cref="MaxWholeFileBytes"/> the choice is between the last slice and nothing at
        /// all; a real machine can carry a 700 MB setupact.log, and dropping the single most
        /// important log from an escalation bundle is far worse than truncating it.
        /// </summary>
        public const long TailCaptureBytes = 64L * 1024 * 1024;

        /// <summary>
        /// How far to scan forward from the cut point looking for a line break, so a captured tail
        /// does not begin halfway through a log line.
        /// </summary>
        private const int LineAlignmentScanBytes = 64 * 1024;

        /// <param name="maxWholeFileBytes">Logs at or below this size are copied in full.</param>
        /// <param name="tailCaptureBytes">How much of the end of a larger log to capture instead.</param>
        public static EvidenceBundleResult Write(
            DiagnosticContext context,
            string zipPath,
            long maxWholeFileBytes = MaxWholeFileBytes,
            long tailCaptureBytes = TailCaptureBytes)
        {
            var omitted = new List<string>();
            var partial = new List<string>();

            using (var zipStream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in context.Manifest.Where(e => e.Exists))
                {
                    if (entry.Source.Category == LogSourceCategory.CrashDump ||
                        entry.ResolvedPath.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
                    {
                        omitted.Add(entry.ResolvedPath + " (crash dump: referenced, not copied)");
                        continue;
                    }

                    var oversized = entry.SizeBytes > maxWholeFileBytes;
                    var fileName = Path.GetFileName(entry.ResolvedPath);
                    if (oversized)
                    {
                        // Mark the truncation in the name as well as the README: whoever opens this
                        // zip during an escalation must not mistake a tail for a whole log.
                        fileName = Path.GetFileNameWithoutExtension(fileName) +
                                   ".last-" + LogSearchView.FormatSize(tailCaptureBytes).Replace(" ", "") +
                                   Path.GetExtension(fileName);
                    }

                    var name = UniqueName(usedNames, "logs/" + entry.Source.Category + "/" + fileName);

                    try
                    {
                        long startOffset;
                        long copied;
                        using (var source = TailReader.OpenForRead(entry.ResolvedPath))
                        {
                            if (oversized)
                            {
                                startOffset = SeekToTailLineStart(source, tailCaptureBytes);
                            }
                            else
                            {
                                startOffset = 0;
                            }

                            using (var target = archive.CreateEntry(name, CompressionLevel.Optimal).Open())
                            {
                                copied = Copy(source, target);
                            }
                        }

                        if (oversized)
                        {
                            partial.Add(string.Format(
                                "{0} — only the last {1} of {2} captured, from byte offset {3:N0}, as \"{4}\"",
                                entry.ResolvedPath,
                                LogSearchView.FormatSize(copied),
                                LogSearchView.FormatSize(entry.SizeBytes),
                                startOffset,
                                name));
                        }
                    }
                    catch (Exception ex)
                    {
                        omitted.Add(entry.ResolvedPath + " (" + ex.Message + ")");
                    }
                }

                WriteText(archive, "findings.json", JsonReportWriter.Serialize(context, null));
                WriteText(archive, "README.txt", BuildReadme(omitted, partial));
            }

            return new EvidenceBundleResult(omitted, partial);
        }

        private static string BuildReadme(IReadOnlyList<string> omitted, IReadOnlyList<string> partial)
        {
            var notes = new StringBuilder();
            notes.AppendLine("Evidence bundle — NOT redacted. For internal escalation only.");
            notes.AppendLine();

            if (partial.Count > 0)
            {
                notes.AppendLine("PARTIALLY captured (too large to include whole — the END of the file is kept,");
                notes.AppendLine("which is where an upgrade failure is recorded; earlier content is NOT here):");
                foreach (var p in partial)
                {
                    notes.AppendLine("  " + p);
                }
                notes.AppendLine();
            }

            if (omitted.Count > 0)
            {
                notes.AppendLine("NOT included:");
                foreach (var o in omitted)
                {
                    notes.AppendLine("  " + o);
                }
                notes.AppendLine();
            }

            if (partial.Count == 0 && omitted.Count == 0)
            {
                notes.AppendLine("Every discovered log was captured in full.");
            }

            return notes.ToString();
        }

        /// <summary>
        /// Positions <paramref name="stream"/> roughly <paramref name="tailBytes"/> from the end,
        /// then advances past the first line break so the captured tail starts on a whole line.
        /// Returns the offset actually started from.
        /// </summary>
        private static long SeekToTailLineStart(Stream stream, long tailBytes)
        {
            var start = Math.Max(0, stream.Length - tailBytes);
            stream.Seek(start, SeekOrigin.Begin);

            if (start == 0)
            {
                return 0;
            }

            // Walk forward to just past the next newline, bounded so a file with absurdly long
            // lines cannot turn this into a scan of the whole tail.
            var limit = Math.Min(LineAlignmentScanBytes, stream.Length - start);
            for (long i = 0; i < limit; i++)
            {
                var b = stream.ReadByte();
                if (b < 0)
                {
                    break;
                }
                if (b == '\n')
                {
                    return start + i + 1;
                }
            }

            stream.Seek(start, SeekOrigin.Begin);
            return start;
        }

        private static long Copy(Stream source, Stream target)
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                target.Write(buffer, 0, read);
                total += read;
            }
            return total;
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
