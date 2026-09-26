using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace WinUpgradeDiag.Core.IO
{
    /// <summary>
    /// Searches an entire log file for a substring while holding only a small buffer in memory.
    /// <para>
    /// This is the counterpart to <see cref="TailReader"/>: the tail window answers "what happened
    /// last", but a 700 MB setupact.log usually keeps the interesting failure thousands of lines
    /// earlier, and a 2 MB window covers well under 1% of the file. Notepad cannot open such a
    /// file at all — it reads the whole thing into memory — so streaming search is the reason a
    /// technician uses this tool instead of a text editor.
    /// </para>
    /// Reads forward in fixed-size chunks with a stateful decoder, so a multi-byte character
    /// spanning a chunk boundary is still decoded correctly.
    /// </summary>
    public sealed class LogSearcher
    {
        public const int DefaultMaxMatches = 5000;
        public const int DefaultContextLines = 2;
        public const int ChunkBytes = 1024 * 1024; // 1 MB per read

        /// <summary>
        /// Searches <paramref name="path"/>, transparently using SeBackupPrivilege if a standard
        /// open is denied (TrustedInstaller-owned Setup logs).
        /// </summary>
        public LogSearchResult Search(
            string path,
            string query,
            int maxMatches = DefaultMaxMatches,
            int contextLines = DefaultContextLines,
            IProgress<double> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            using (var stream = TailReader.OpenForRead(path))
            {
                return Search(stream, query, maxMatches, contextLines, progress, cancellationToken);
            }
        }

        /// <summary>
        /// Scans once for several signatures at the same time, returning the matches for each.
        /// <para>
        /// The rules engine looks for a dozen error codes. Running a separate pass per code over a
        /// 712 MB setupact.log would cost roughly seven seconds each; one pass finds them all for
        /// the price of a single read.
        /// </para>
        /// </summary>
        public MultiSearchResult SearchMany(
            string path,
            IReadOnlyList<string> queries,
            int maxMatchesPerQuery = 50,
            int contextLines = DefaultContextLines,
            IProgress<double> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            using (var stream = TailReader.OpenForRead(path))
            {
                return SearchMany(stream, queries, maxMatchesPerQuery, contextLines, progress, cancellationToken);
            }
        }

        public MultiSearchResult SearchMany(
            Stream stream,
            IReadOnlyList<string> queries,
            int maxMatchesPerQuery = 50,
            int contextLines = DefaultContextLines,
            IProgress<double> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }
            if (queries == null || queries.Count == 0)
            {
                throw new ArgumentException("At least one query is required.", nameof(queries));
            }
            if (queries.Any(string.IsNullOrEmpty))
            {
                throw new ArgumentException("Queries must all be non-empty.", nameof(queries));
            }

            var log = LogEncoding.Detect(stream);
            var fileSize = stream.CanSeek ? stream.Length : 0L;
            if (stream.CanSeek)
            {
                stream.Seek(log.PreambleLength, SeekOrigin.Begin);
            }

            var scan = new MultiScanState(queries, maxMatchesPerQuery, contextLines);
            var decoder = log.Encoding.GetDecoder();
            var bytes = new byte[ChunkBytes];
            var chars = new char[log.Encoding.GetMaxCharCount(ChunkBytes)];
            long bytesScanned = 0;
            var lastReported = -1.0;
            var cancelled = false;

            while (!scan.Stop)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                var read = stream.Read(bytes, 0, bytes.Length);
                if (read <= 0)
                {
                    break;
                }
                bytesScanned += read;

                var charCount = decoder.GetChars(bytes, 0, read, chars, 0);
                scan.Feed(chars, charCount);

                if (progress != null && fileSize > 0)
                {
                    var fraction = Math.Min(1.0, (double)bytesScanned / fileSize);
                    if (fraction - lastReported >= 0.01 || fraction >= 1.0)
                    {
                        lastReported = fraction;
                        progress.Report(fraction);
                    }
                }
            }

            scan.Flush();

            var byQuery = new Dictionary<string, LogSearchResult>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < queries.Count; i++)
            {
                byQuery[queries[i]] = new LogSearchResult(
                    scan.MatchesFor(i), scan.LimitReachedFor(i), cancelled,
                    bytesScanned, fileSize, scan.LinesScanned, log.Encoding);
            }

            return new MultiSearchResult(byQuery, cancelled, bytesScanned, fileSize, scan.LinesScanned);
        }

        public LogSearchResult Search(
            Stream stream,
            string query,
            int maxMatches = DefaultMaxMatches,
            int contextLines = DefaultContextLines,
            IProgress<double> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }
            if (string.IsNullOrEmpty(query))
            {
                throw new ArgumentException("A search needs a non-empty query.", nameof(query));
            }
            if (maxMatches <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxMatches), "The match cap must be at least one.");
            }
            if (contextLines < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(contextLines), "Context cannot be negative.");
            }

            var log = LogEncoding.Detect(stream);
            var fileSize = stream.CanSeek ? stream.Length : 0L;
            if (stream.CanSeek)
            {
                stream.Seek(log.PreambleLength, SeekOrigin.Begin);
            }

            var scan = new ScanState(query, maxMatches, contextLines);
            var decoder = log.Encoding.GetDecoder();
            var bytes = new byte[ChunkBytes];
            var chars = new char[log.Encoding.GetMaxCharCount(ChunkBytes)];
            long bytesScanned = 0;
            var lastReported = -1.0;

            while (!scan.Stop)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    scan.Cancelled = true;
                    break;
                }

                var read = stream.Read(bytes, 0, bytes.Length);
                if (read <= 0)
                {
                    break;
                }
                bytesScanned += read;

                var charCount = decoder.GetChars(bytes, 0, read, chars, 0);
                scan.Feed(chars, charCount);

                if (progress != null && fileSize > 0)
                {
                    var fraction = Math.Min(1.0, (double)bytesScanned / fileSize);
                    // Report at most once per percent: a 700 MB file is ~700 chunks, and the UI
                    // does not need marshalling to more often than that.
                    if (fraction - lastReported >= 0.01 || fraction >= 1.0)
                    {
                        lastReported = fraction;
                        progress.Report(fraction);
                    }
                }
            }

            scan.Flush();

            return new LogSearchResult(
                scan.Matches,
                scan.MatchLimitReached,
                scan.Cancelled,
                bytesScanned,
                fileSize,
                scan.LinesScanned,
                log.Encoding);
        }

        /// <summary>
        /// Line-splitting scan that tests every line against several queries at once.
        /// </summary>
        private sealed class MultiScanState
        {
            private readonly IReadOnlyList<string> _queries;
            private readonly int _maxPerQuery;
            private readonly int _contextLines;
            private readonly Queue<string> _before = new Queue<string>();
            private readonly List<PendingMatch>[] _awaitingAfter;
            private readonly List<LogSearchMatch>[] _matches;
            private readonly bool[] _limitReached;
            private readonly StringBuilder _line = new StringBuilder(256);

            public MultiScanState(IReadOnlyList<string> queries, int maxPerQuery, int contextLines)
            {
                _queries = queries;
                _maxPerQuery = maxPerQuery;
                _contextLines = contextLines;
                _awaitingAfter = new List<PendingMatch>[queries.Count];
                _matches = new List<LogSearchMatch>[queries.Count];
                _limitReached = new bool[queries.Count];
                for (int i = 0; i < queries.Count; i++)
                {
                    _awaitingAfter[i] = new List<PendingMatch>();
                    _matches[i] = new List<LogSearchMatch>();
                }
            }

            public int LinesScanned { get; private set; }

            public IReadOnlyList<LogSearchMatch> MatchesFor(int index) => _matches[index];
            public bool LimitReachedFor(int index) => _limitReached[index];

            /// <summary>Only stops once every query has hit its cap and finished its context.</summary>
            public bool Stop
            {
                get
                {
                    for (int i = 0; i < _queries.Count; i++)
                    {
                        if (!_limitReached[i] || _awaitingAfter[i].Count > 0)
                        {
                            return false;
                        }
                    }
                    return true;
                }
            }

            public void Feed(char[] buffer, int count)
            {
                for (int i = 0; i < count; i++)
                {
                    var c = buffer[i];
                    if (c == '\n')
                    {
                        CompleteLine();
                    }
                    else
                    {
                        _line.Append(c);
                    }
                }
            }

            public void Flush()
            {
                if (_line.Length > 0)
                {
                    CompleteLine();
                }
            }

            private void CompleteLine()
            {
                var text = TailReader.TrimCarriageReturn(_line.ToString());
                _line.Length = 0;
                LinesScanned++;

                for (int q = 0; q < _queries.Count; q++)
                {
                    var awaiting = _awaitingAfter[q];
                    for (int i = awaiting.Count - 1; i >= 0; i--)
                    {
                        awaiting[i].After.Add(text);
                        if (awaiting[i].After.Count >= _contextLines)
                        {
                            awaiting.RemoveAt(i);
                        }
                    }

                    if (_limitReached[q] ||
                        text.IndexOf(_queries[q], StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    var match = new PendingMatch(LinesScanned, text, new List<string>(_before));
                    if (_contextLines > 0)
                    {
                        awaiting.Add(match);
                    }
                    _matches[q].Add(match.ToMatch());
                    if (_matches[q].Count >= _maxPerQuery)
                    {
                        _limitReached[q] = true;
                    }
                }

                if (_contextLines > 0)
                {
                    _before.Enqueue(text);
                    while (_before.Count > _contextLines)
                    {
                        _before.Dequeue();
                    }
                }
            }
        }

        /// <summary>
        /// A match whose trailing context is still being filled in. <see cref="After"/> is the same
        /// list instance handed to the published match, so appends here appear in the result.
        /// </summary>
        private sealed class PendingMatch
        {
            private readonly int _lineNumber;
            private readonly string _text;
            private readonly List<string> _before;

            public PendingMatch(int lineNumber, string text, List<string> before)
            {
                _lineNumber = lineNumber;
                _text = text;
                _before = before;
                After = new List<string>();
            }

            public List<string> After { get; }

            public LogSearchMatch ToMatch()
            {
                return new LogSearchMatch(_lineNumber, _text, _before, After);
            }
        }

        /// <summary>
        /// Accumulates decoded characters into lines and tests each against the query, keeping a
        /// rolling window of preceding lines so a match can be reported with its context.
        /// </summary>
        private sealed class ScanState
        {
            private readonly string _query;
            private readonly int _maxMatches;
            private readonly int _contextLines;
            private readonly Queue<string> _before = new Queue<string>();
            private readonly List<PendingMatch> _awaitingAfter = new List<PendingMatch>();
            private readonly List<LogSearchMatch> _matches = new List<LogSearchMatch>();
            private readonly StringBuilder _line = new StringBuilder(256);

            public ScanState(string query, int maxMatches, int contextLines)
            {
                _query = query;
                _maxMatches = maxMatches;
                _contextLines = contextLines;
            }

            public IReadOnlyList<LogSearchMatch> Matches => _matches;
            public int LinesScanned { get; private set; }
            public bool MatchLimitReached { get; private set; }
            public bool Cancelled { get; set; }

            /// <summary>
            /// True once nothing further can change the result: the cap is hit and every recorded
            /// match already has all the trailing context it is going to get.
            /// </summary>
            public bool Stop => MatchLimitReached && _awaitingAfter.Count == 0;

            public void Feed(char[] buffer, int count)
            {
                for (int i = 0; i < count; i++)
                {
                    var c = buffer[i];
                    if (c == '\n')
                    {
                        CompleteLine();
                    }
                    else
                    {
                        _line.Append(c);
                    }
                }
            }

            /// <summary>Emits a final line that had no trailing newline.</summary>
            public void Flush()
            {
                if (_line.Length > 0)
                {
                    CompleteLine();
                }
            }

            private void CompleteLine()
            {
                var text = TailReader.TrimCarriageReturn(_line.ToString());
                _line.Length = 0;
                LinesScanned++;

                // Trailing context for matches already found takes priority: a capped search must
                // still finish the context of the matches it did report.
                for (int i = _awaitingAfter.Count - 1; i >= 0; i--)
                {
                    var pending = _awaitingAfter[i];
                    pending.After.Add(text);
                    if (pending.After.Count >= _contextLines)
                    {
                        _awaitingAfter.RemoveAt(i);
                    }
                }

                if (!MatchLimitReached && text.IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var match = new PendingMatch(LinesScanned, text, new List<string>(_before));
                    if (_contextLines > 0)
                    {
                        _awaitingAfter.Add(match);
                    }
                    _matches.Add(match.ToMatch());

                    if (_matches.Count >= _maxMatches)
                    {
                        MatchLimitReached = true;
                    }
                }

                if (_contextLines > 0)
                {
                    _before.Enqueue(text);
                    while (_before.Count > _contextLines)
                    {
                        _before.Dequeue();
                    }
                }
            }
        }
    }
}
