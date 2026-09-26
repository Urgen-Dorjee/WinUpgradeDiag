using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace WinUpgradeDiag.Core.IO
{
    /// <summary>One matching line, with the lines either side of it for context.</summary>
    public sealed class LogSearchMatch
    {
        /// <summary>1-based line number within the file, counted from the first line.</summary>
        public int LineNumber { get; }

        public string Text { get; }

        /// <summary>Lines immediately before the match, oldest first. May be shorter than asked.</summary>
        public IReadOnlyList<string> Before { get; }

        /// <summary>Lines immediately after the match. May be shorter near end of file.</summary>
        public IReadOnlyList<string> After { get; }

        public LogSearchMatch(int lineNumber, string text, IReadOnlyList<string> before, IReadOnlyList<string> after)
        {
            LineNumber = lineNumber;
            Text = text;
            Before = before;
            After = after;
        }
    }

    /// <summary>
    /// Outcome of scanning one file for several signatures in a single pass.
    /// </summary>
    public sealed class MultiSearchResult
    {
        private readonly IReadOnlyDictionary<string, LogSearchResult> _byQuery;

        public MultiSearchResult(
            IReadOnlyDictionary<string, LogSearchResult> byQuery,
            bool cancelled,
            long bytesScanned,
            long fileSizeBytes,
            int linesScanned)
        {
            _byQuery = byQuery;
            Cancelled = cancelled;
            BytesScanned = bytesScanned;
            FileSizeBytes = fileSizeBytes;
            LinesScanned = linesScanned;
        }

        public bool Cancelled { get; }
        public long BytesScanned { get; }
        public long FileSizeBytes { get; }
        public int LinesScanned { get; }

        /// <summary>Every query that matched at least once.</summary>
        public IEnumerable<string> MatchedQueries =>
            _byQuery.Where(kv => kv.Value.Matches.Count > 0).Select(kv => kv.Key);

        /// <summary>Result for one query; never null for a query that was asked for.</summary>
        public LogSearchResult For(string query)
        {
            LogSearchResult result;
            return _byQuery.TryGetValue(query, out result) ? result : null;
        }

        public bool HasMatch(string query)
        {
            var result = For(query);
            return result != null && result.Matches.Count > 0;
        }
    }

    /// <summary>
    /// Outcome of a whole-file search. Reports what it did <em>not</em> cover as loudly as what it
    /// found: DESIGN.md §8 is explicit that absent evidence must be stated, never implied, and a
    /// capped or cancelled search is exactly that case.
    /// </summary>
    public sealed class LogSearchResult
    {
        public IReadOnlyList<LogSearchMatch> Matches { get; }

        /// <summary>True if the match cap was hit, so later matches in the file were not reported.</summary>
        public bool MatchLimitReached { get; }

        /// <summary>True if the caller cancelled before reaching the end of the file.</summary>
        public bool Cancelled { get; }

        public long BytesScanned { get; }
        public long FileSizeBytes { get; }
        public int LinesScanned { get; }
        public Encoding DetectedEncoding { get; }

        /// <summary>True when the whole file was examined — no cap, no cancellation.</summary>
        public bool ScannedWholeFile => !MatchLimitReached && !Cancelled;

        public LogSearchResult(
            IReadOnlyList<LogSearchMatch> matches,
            bool matchLimitReached,
            bool cancelled,
            long bytesScanned,
            long fileSizeBytes,
            int linesScanned,
            Encoding detectedEncoding)
        {
            Matches = matches;
            MatchLimitReached = matchLimitReached;
            Cancelled = cancelled;
            BytesScanned = bytesScanned;
            FileSizeBytes = fileSizeBytes;
            LinesScanned = linesScanned;
            DetectedEncoding = detectedEncoding;
        }
    }
}
