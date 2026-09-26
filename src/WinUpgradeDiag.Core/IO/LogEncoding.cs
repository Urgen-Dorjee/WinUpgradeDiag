using System;
using System.IO;
using System.Text;

namespace WinUpgradeDiag.Core.IO
{
    /// <summary>
    /// Sniffs the byte-order mark of a log file to decide how to decode it. Shared by
    /// <see cref="TailReader"/> and <see cref="LogSearcher"/> so both agree on where content
    /// starts and how wide a code unit is — reading a 700 MB setupact.log from the wrong offset
    /// is the difference between usable evidence and mojibake.
    /// </summary>
    public sealed class LogEncoding
    {
        private LogEncoding(Encoding encoding, int preambleLength, int codeUnitSize)
        {
            Encoding = encoding;
            PreambleLength = preambleLength;
            CodeUnitSize = codeUnitSize;
        }

        public Encoding Encoding { get; }

        /// <summary>Bytes of byte-order mark at the head of the file; content starts after these.</summary>
        public int PreambleLength { get; }

        /// <summary>
        /// Width of one code unit in bytes. Seeking into the middle of a UTF-16 code unit splits
        /// a character down the middle, so window offsets must be aligned to this.
        /// </summary>
        public int CodeUnitSize { get; }

        /// <summary>
        /// Detects the encoding from the file's byte-order mark in a single seek, restoring the
        /// stream position afterwards. ConfigMgr and Panther logs are almost always plain
        /// ASCII/UTF-8 without a BOM, which is the fallback.
        /// </summary>
        public static LogEncoding Detect(Stream stream)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            var originalPosition = stream.Position;
            var bom = new byte[4];
            int read;
            try
            {
                stream.Seek(0, SeekOrigin.Begin);
                read = stream.Read(bom, 0, 4);
            }
            finally
            {
                stream.Seek(originalPosition, SeekOrigin.Begin);
            }

            if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
            {
                return new LogEncoding(new UTF8Encoding(false), 3, 1);
            }
            if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
            {
                return new LogEncoding(Encoding.Unicode, 2, 2); // UTF-16 LE
            }
            if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
            {
                return new LogEncoding(Encoding.BigEndianUnicode, 2, 2);
            }

            return new LogEncoding(new UTF8Encoding(false), 0, 1);
        }

        /// <summary>
        /// Rounds <paramref name="offset"/> down to a code-unit boundary measured from the first
        /// byte of content, so a seek never lands mid-character.
        /// </summary>
        public long AlignToCodeUnit(long offset)
        {
            if (CodeUnitSize <= 1)
            {
                return offset;
            }

            var fromContentStart = offset - PreambleLength;
            return offset - (fromContentStart % CodeUnitSize);
        }
    }
}
