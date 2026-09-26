using System;
using System.Collections.Generic;
using System.IO;
using WinUpgradeDiag.Core.Native;

namespace WinUpgradeDiag.Core.IO
{
    /// <summary>
    /// Reads the last portion of a log file without loading the whole thing into memory.
    /// AGENTS.md constraint #5: setupact.log is routinely 100-700 MB; this seeks to
    /// <c>length - windowBytes</c> and reads forward from there, widening only on request.
    /// To search the parts the window does not cover, use <see cref="LogSearcher"/>.
    /// </summary>
    public sealed class TailReader
    {
        public const int DefaultWindowBytes = 2 * 1024 * 1024; // 2 MB
        public const int DefaultMaxLines = 5000;

        /// <summary>
        /// Upper bound on a single window. The window is buffered in memory in one array, so an
        /// unbounded value would defeat the whole point of streaming a 700 MB log.
        /// </summary>
        public const int MaxWindowBytes = 64 * 1024 * 1024; // 64 MB

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
            if (windowBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(windowBytes), "The tail window must be at least one byte.");
            }
            if (maxLines <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxLines), "The line cap must be at least one.");
            }

            var effectiveWindow = Math.Min(windowBytes, MaxWindowBytes);
            var fileSize = stream.Length;
            var log = LogEncoding.Detect(stream);

            long windowStart = Math.Max(log.PreambleLength, fileSize - effectiveWindow);
            bool truncatedFromStart = windowStart > log.PreambleLength;
            windowStart = log.AlignToCodeUnit(windowStart);

            stream.Seek(windowStart, SeekOrigin.Begin);

            // Read to the end of the file, not just `effectiveWindow` bytes: aligning the start
            // down to a code-unit boundary can push the window a byte or two past the requested
            // size, and clamping here would slice the final UTF-16 character in half.
            var windowLength = (int)(fileSize - windowStart);
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

            var text = log.Encoding.GetString(buffer, 0, totalRead);
            var rawLines = text.Split('\n');

            // If we started mid-file, the first "line" is very likely a partial line; drop it
            // unless the whole file fit in the window (nothing was truncated).
            var startIndex = truncatedFromStart && rawLines.Length > 1 ? 1 : 0;

            var lines = new List<string>();
            for (int i = startIndex; i < rawLines.Length; i++)
            {
                lines.Add(TrimCarriageReturn(rawLines[i]));
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

            return new TailReadResult(lines, truncatedFromStart, lineCountTruncated, fileSize, windowStart, log.Encoding);
        }

        internal static string TrimCarriageReturn(string line)
        {
            return line.Length > 0 && line[line.Length - 1] == '\r'
                ? line.Substring(0, line.Length - 1)
                : line;
        }
    }
}
