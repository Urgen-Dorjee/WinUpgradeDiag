using System;
using System.Collections.Generic;
using System.Globalization;

namespace WinUpgradeDiag.Core.IO
{
    /// <summary>
    /// Turns raw read results into the flat sequence of lines a viewer displays. Pure data
    /// transformation, kept in Core rather than in the UI so the awkward parts — matches whose
    /// context windows overlap, and the gap markers between distant results — are testable
    /// (AGENTS.md: all logic lives in Core, the UI stays thin).
    /// </summary>
    public static class LogSearchView
    {
        /// <summary>
        /// Flattens search matches into a numbered, de-duplicated line sequence. Where two matches
        /// are close enough that their context overlaps, the shared lines appear once; where they
        /// are far apart, a gap marker records how many lines were skipped.
        /// </summary>
        public static IReadOnlyList<LogViewLine> FromSearch(LogSearchResult result)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            var lines = new List<LogViewLine>();
            var lastRendered = 0;

            foreach (var match in result.Matches)
            {
                var firstContextLine = match.LineNumber - match.Before.Count;

                if (lastRendered > 0 && firstContextLine > lastRendered + 1)
                {
                    var skipped = firstContextLine - lastRendered - 1;
                    lines.Add(LogViewLine.Gap(
                        "⋯ " + skipped.ToString("N0", CultureInfo.CurrentCulture) + " lines not shown ⋯"));
                }

                for (int i = 0; i < match.Before.Count; i++)
                {
                    var number = firstContextLine + i;
                    if (number > lastRendered)
                    {
                        lines.Add(LogViewLine.Context(number, match.Before[i]));
                    }
                }

                if (match.LineNumber > lastRendered)
                {
                    lines.Add(LogViewLine.Match(match.LineNumber, match.Text));
                }

                for (int i = 0; i < match.After.Count; i++)
                {
                    var number = match.LineNumber + 1 + i;
                    if (number > lastRendered)
                    {
                        lines.Add(LogViewLine.Context(number, match.After[i]));
                    }
                }

                lastRendered = Math.Max(lastRendered, match.LineNumber + match.After.Count);
            }

            return lines;
        }

        /// <summary>
        /// Flattens a tail window, optionally keeping only lines containing <paramref name="filter"/>.
        /// <para>
        /// Absolute line numbers are emitted only when the window covered the whole file. A
        /// truncated tail does not know how many lines preceded it, so its lines are returned
        /// unnumbered rather than numbered from a guess.
        /// </para>
        /// </summary>
        public static IReadOnlyList<LogViewLine> FromTail(TailReadResult tail, string filter)
        {
            if (tail == null)
            {
                throw new ArgumentNullException(nameof(tail));
            }

            var numbered = !tail.WindowTruncatedFromStart && !tail.LineCountTruncated;
            var hasFilter = !string.IsNullOrEmpty(filter);
            var lines = new List<LogViewLine>(tail.Lines.Count);

            for (int i = 0; i < tail.Lines.Count; i++)
            {
                var text = tail.Lines[i];
                var isMatch = hasFilter && text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
                if (hasFilter && !isMatch)
                {
                    continue;
                }

                int? number = numbered ? i + 1 : (int?)null;
                lines.Add(isMatch ? LogViewLine.Match(number, text) : LogViewLine.Context(number, text));
            }

            return lines;
        }

        /// <summary>
        /// One sentence describing what a search did and, critically, what it did not cover.
        /// DESIGN.md §5 and §8: a capped or cancelled scan that reads like a complete one is the
        /// "confident wrong answer" this tool is supposed to avoid.
        /// </summary>
        public static string DescribeSearch(LogSearchResult result, string query, int matchLimit)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            var description =
                string.Format(
                    CultureInfo.CurrentCulture,
                    "{0:N0} match(es) for \"{1}\" across {2:N0} lines of {3}",
                    result.Matches.Count, query, result.LinesScanned, FormatSize(result.FileSizeBytes));

            if (result.Cancelled)
            {
                return description + string.Format(
                    CultureInfo.CurrentCulture,
                    ". Cancelled after {0} — the rest of the file was not searched.",
                    FormatSize(result.BytesScanned));
            }

            if (result.MatchLimitReached)
            {
                return description + string.Format(
                    CultureInfo.CurrentCulture,
                    ". Stopped at the first {0:N0} matches; there may be more later in the file.",
                    matchLimit);
            }

            return description + ". The whole file was searched.";
        }

        /// <summary>
        /// Byte size in the largest sensible unit. Duplicated from the report writer deliberately:
        /// IO must not depend on Report.
        /// </summary>
        public static string FormatSize(long bytes)
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
    }
}
