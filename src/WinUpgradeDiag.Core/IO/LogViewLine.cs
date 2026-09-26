namespace WinUpgradeDiag.Core.IO
{
    /// <summary>What a line in a rendered log view represents.</summary>
    public enum LogViewLineKind
    {
        /// <summary>A line that matched the query.</summary>
        Match,

        /// <summary>A line shown only to give a match its surrounding context.</summary>
        Context,

        /// <summary>A marker standing in for lines that were skipped between two result groups.</summary>
        Gap
    }

    /// <summary>
    /// One line of a log prepared for display, with its position in the file when that is known.
    /// <para>
    /// A 700 MB setupact.log is around seven million lines, so "the fourth line of the results"
    /// means nothing on its own — a finding has to be able to cite where in the file it came from.
    /// <see cref="LineNumber"/> is deliberately nullable: a tail window starts mid-file and
    /// genuinely does not know how many lines preceded it, and inventing a number there would be
    /// worse than showing none.
    /// </para>
    /// </summary>
    public sealed class LogViewLine
    {
        private LogViewLine(int? lineNumber, string text, LogViewLineKind kind)
        {
            LineNumber = lineNumber;
            Text = text;
            Kind = kind;
        }

        /// <summary>1-based line number in the file, or null when the position is not known.</summary>
        public int? LineNumber { get; }

        public string Text { get; }

        public LogViewLineKind Kind { get; }

        public bool IsMatch => Kind == LogViewLineKind.Match;
        public bool IsGap => Kind == LogViewLineKind.Gap;

        public static LogViewLine Match(int? lineNumber, string text)
        {
            return new LogViewLine(lineNumber, text, LogViewLineKind.Match);
        }

        public static LogViewLine Context(int? lineNumber, string text)
        {
            return new LogViewLine(lineNumber, text, LogViewLineKind.Context);
        }

        public static LogViewLine Gap(string text)
        {
            return new LogViewLine(null, text, LogViewLineKind.Gap);
        }
    }
}
