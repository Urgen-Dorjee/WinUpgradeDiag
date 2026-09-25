using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using WinUpgradeDiag.Core.Native;

namespace WinUpgradeDiag.Core.IO
{
    /// <summary>
    /// Reads the last portion of a log file without loading the whole thing into memory.
    /// AGENTS.md constraint #5: setupact.log is routinely 100-200 MB; this seeks to
    /// <c>length - windowBytes</c> and reads forward from there, widening only on request.
    /// </summary>
    public sealed class TailReader
    {
        public const int DefaultWindowBytes = 2 * 1024 * 1024; // 2 MB
        public const int DefaultMaxLines = 5000;

        /// <summary>
        /// Opens <paramref name="path"/> for reading, transparently retrying with
        /// SeBackupPrivilege if a standard open is denied (TrustedInstaller-owned logs).
        /// </summary>
        public static Stream OpenForRead(string path)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (UnauthorizedAccessException)
            {
                return ProtectedFileReader.OpenForPrivilegedRead(path);
            }
        }

        public TailReadResult ReadTail(string path, int windowBytes = DefaultWindowBytes, int maxLines = DefaultMaxLines)
        {
            using (var stream = OpenForRead(path))
            {
                return ReadTail(stream, windowBytes, maxLines);
            }
        }

        public TailReadResult ReadTail(Stream stream, int windowBytes = DefaultWindowBytes, int maxLines = DefaultMaxLines)
        {
            if (!stream.CanSeek)
            {
                throw new ArgumentException("TailReader requires a seekable stream.", nameof(stream));
            }

            var fileSize = stream.Length;
            var encoding = DetectEncoding(stream);
            var bomLength = GetPreambleLength(encoding, stream);

            long windowStart = Math.Max(bomLength, fileSize - windowBytes);
            bool truncatedFromStart = windowStart > bomLength;

            // Unicode encodings use fixed-width code units; seeking to an odd byte offset would
            // split a character down the middle, so align the window start to a code-unit
            // boundary relative to where content begins.
            var unitSize = GetCodeUnitSize(encoding);
            if (unitSize > 1)
            {
                var offsetFromContentStart = windowStart - bomLength;
                var remainder = offsetFromContentStart % unitSize;
                windowStart -= remainder;
            }

            stream.Seek(windowStart, SeekOrigin.Begin);

            var windowLength = (int)Math.Min(int.MaxValue, fileSize - windowStart);
            var buffer = new byte[windowLength];
            int totalRead = 0;
            while (totalRead < windowLength)
            {
                int read = stream.Read(buffer, totalRead, windowLength - totalRead);
                if (read <= 0)
                {
                    break;
                }
                totalRead += read;
            }

            var text = encoding.GetString(buffer, 0, totalRead);
            var rawLines = text.Split('\n');

            // If we started mid-file, the first "line" is very likely a partial line; drop it
            // unless the whole file fit in the window (nothing was truncated).
            var startIndex = truncatedFromStart && rawLines.Length > 1 ? 1 : 0;

            var lines = new List<string>();
            for (int i = startIndex; i < rawLines.Length; i++)
            {
                var line = rawLines[i];
                if (line.Length > 0 && line[line.Length - 1] == '\r')
                {
                    line = line.Substring(0, line.Length - 1);
                }
                lines.Add(line);
            }

            // Drop a trailing empty line caused by a final newline in the file.
            if (lines.Count > 0 && lines[lines.Count - 1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            bool lineCountTruncated = false;
            if (lines.Count > maxLines)
            {
                lines = lines.GetRange(lines.Count - maxLines, maxLines);
                lineCountTruncated = true;
            }

            return new TailReadResult(lines, truncatedFromStart, lineCountTruncated, fileSize, windowStart, encoding);
        }

        private static Encoding DetectEncoding(Stream stream)
        {
            var originalPosition = stream.Position;
            stream.Seek(0, SeekOrigin.Begin);

            var bom = new byte[4];
            int read = stream.Read(bom, 0, 4);
            stream.Seek(originalPosition, SeekOrigin.Begin);

            if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
            {
                return new UTF8Encoding(false);
            }
            if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
            {
                return Encoding.Unicode; // UTF-16 LE
            }
            if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode;
            }

            // ConfigMgr and Panther logs are almost always plain ASCII/UTF-8 without a BOM.
            return new UTF8Encoding(false);
        }

        private static int GetPreambleLength(Encoding encoding, Stream stream)
        {
            var originalPosition = stream.Position;
            stream.Seek(0, SeekOrigin.Begin);
            var head = new byte[4];
            var read = stream.Read(head, 0, 4);
            stream.Seek(originalPosition, SeekOrigin.Begin);

            if (read >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            {
                return 3;
            }
            if (read >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF)))
            {
                return 2;
            }

            return 0;
        }

        private static int GetCodeUnitSize(Encoding encoding)
        {
            if (Equals(encoding, Encoding.Unicode) || Equals(encoding, Encoding.BigEndianUnicode))
            {
                return 2;
            }

            return 1;
        }
    }
}
