using System.Collections.Generic;
using System.Text;

namespace WinUpgradeDiag.Core.IO
{
    /// <summary>Result of reading the tail window of a log file.</summary>
    public sealed class TailReadResult
    {
        /// <summary>Lines within the window, oldest first, newest (end of file) last.</summary>
        public IReadOnlyList<string> Lines { get; }

        /// <summary>True if the window did not cover the whole file (the common case for large logs).</summary>
        public bool WindowTruncatedFromStart { get; }

        /// <summary>True if more lines existed inside the window than <c>maxLines</c> allowed.</summary>
        public bool LineCountTruncated { get; }

        public long FileSizeBytes { get; }
        public long WindowStartOffset { get; }
        public Encoding DetectedEncoding { get; }

        public TailReadResult(
            IReadOnlyList<string> lines,
            bool windowTruncatedFromStart,
            bool lineCountTruncated,
            long fileSizeBytes,
            long windowStartOffset,
            Encoding detectedEncoding)
        {
            Lines = lines;
            WindowTruncatedFromStart = windowTruncatedFromStart;
            LineCountTruncated = lineCountTruncated;
            FileSizeBytes = fileSizeBytes;
            WindowStartOffset = windowStartOffset;
            DetectedEncoding = detectedEncoding;
        }
    }
}
