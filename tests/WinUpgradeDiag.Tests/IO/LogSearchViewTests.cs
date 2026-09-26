using System.Collections.Generic;
using System.Linq;
using System.Text;
using WinUpgradeDiag.Core.IO;
using Xunit;

namespace WinUpgradeDiag.Tests.IO
{
    public class LogSearchViewTests
    {
        private static LogSearchResult ResultOf(bool capped = false, bool cancelled = false, params LogSearchMatch[] matches)
        {
            return new LogSearchResult(matches, capped, cancelled, 1024, 1024, 100, Encoding.UTF8);
        }

        private static LogSearchMatch Match(int line, string text, string[] before = null, string[] after = null)
        {
            return new LogSearchMatch(line, text, before ?? new string[0], after ?? new string[0]);
        }

        [Fact]
        public void Distant_matches_get_a_gap_marker_naming_how_many_lines_were_skipped()
        {
            var result = ResultOf(false, false,
                Match(10, "first"),
                Match(100, "second"));

            var lines = LogSearchView.FromSearch(result);

            Assert.Collection(lines,
                l => Assert.Equal(10, l.LineNumber),
                l =>
                {
                    Assert.True(l.IsGap);
                    Assert.Null(l.LineNumber);
                    Assert.Contains("89", l.Text); // lines 11..99 inclusive
                },
                l => Assert.Equal(100, l.LineNumber));
        }

        [Fact]
        public void Adjacent_matches_get_no_gap_marker()
        {
            var result = ResultOf(false, false, Match(10, "a"), Match(11, "b"));

            var lines = LogSearchView.FromSearch(result);

            Assert.DoesNotContain(lines, l => l.IsGap);
            Assert.Equal(new int?[] { 10, 11 }, lines.Select(l => l.LineNumber).ToArray());
        }

        [Fact]
        public void Overlapping_context_of_two_close_matches_is_not_duplicated()
        {
            // Match at 10 trails context 11,12; match at 12 leads with context 10,11.
            // Line 11 must appear once, and line 12 must be the match, not a repeated context line.
            var result = ResultOf(false, false,
                Match(10, "hit-one", after: new[] { "ctx-11", "hit-two" }),
                Match(12, "hit-two", before: new[] { "ctx-10", "ctx-11" }, after: new[] { "ctx-13" }));

            var lines = LogSearchView.FromSearch(result);

            var numbers = lines.Where(l => l.LineNumber.HasValue).Select(l => l.LineNumber.Value).ToArray();
            Assert.Equal(numbers, numbers.Distinct().ToArray());
            Assert.Equal(new[] { 10, 11, 12, 13 }, numbers);
        }

        [Fact]
        public void Context_lines_are_marked_as_context_and_matches_as_matches()
        {
            var result = ResultOf(false, false,
                Match(5, "the failure", before: new[] { "before" }, after: new[] { "after" }));

            var lines = LogSearchView.FromSearch(result);

            Assert.Equal(new[] { LogViewLineKind.Context, LogViewLineKind.Match, LogViewLineKind.Context },
                lines.Select(l => l.Kind).ToArray());
            Assert.Equal(new int?[] { 4, 5, 6 }, lines.Select(l => l.LineNumber).ToArray());
        }

        [Fact]
        public void No_matches_renders_nothing()
        {
            Assert.Empty(LogSearchView.FromSearch(ResultOf()));
        }

        // ---------- status text: must never imply a complete scan it did not do ----------

        [Fact]
        public void A_complete_scan_says_the_whole_file_was_searched()
        {
            var text = LogSearchView.DescribeSearch(ResultOf(false, false, Match(1, "x")), "x", 5000);

            Assert.Contains("The whole file was searched", text);
        }

        [Fact]
        public void A_capped_scan_warns_that_later_matches_may_exist()
        {
            var text = LogSearchView.DescribeSearch(ResultOf(true, false, Match(1, "x")), "x", 5000);

            Assert.Contains("Stopped at the first", text);
            Assert.DoesNotContain("The whole file was searched", text);
        }

        [Fact]
        public void A_cancelled_scan_says_the_rest_was_not_searched()
        {
            var text = LogSearchView.DescribeSearch(ResultOf(false, true, Match(1, "x")), "x", 5000);

            Assert.Contains("not searched", text);
            Assert.DoesNotContain("The whole file was searched", text);
        }

        // ---------- tail rendering ----------

        private static TailReadResult Tail(IReadOnlyList<string> lines, bool truncated, bool lineCapped = false)
        {
            return new TailReadResult(lines, truncated, lineCapped, 4096, truncated ? 2048 : 0, Encoding.UTF8);
        }

        [Fact]
        public void A_tail_covering_the_whole_file_is_numbered_from_one()
        {
            var lines = LogSearchView.FromTail(Tail(new[] { "a", "b", "c" }, truncated: false), null);

            Assert.Equal(new int?[] { 1, 2, 3 }, lines.Select(l => l.LineNumber).ToArray());
        }

        [Fact]
        public void A_truncated_tail_is_unnumbered_rather_than_numbered_from_a_guess()
        {
            var lines = LogSearchView.FromTail(Tail(new[] { "a", "b" }, truncated: true), null);

            Assert.All(lines, l => Assert.Null(l.LineNumber));
        }

        [Fact]
        public void A_line_capped_tail_is_also_unnumbered()
        {
            var lines = LogSearchView.FromTail(Tail(new[] { "a" }, truncated: false, lineCapped: true), null);

            Assert.All(lines, l => Assert.Null(l.LineNumber));
        }

        [Fact]
        public void Filtering_a_tail_keeps_only_matching_lines_and_marks_them()
        {
            var tail = Tail(new[] { "alpha", "BRAVO", "charlie", "bravo again" }, truncated: false);

            var lines = LogSearchView.FromTail(tail, "bravo");

            Assert.Equal(new[] { "BRAVO", "bravo again" }, lines.Select(l => l.Text).ToArray());
            Assert.All(lines, l => Assert.True(l.IsMatch));
            // Numbering still refers to the real position in the file, not the filtered position.
            Assert.Equal(new int?[] { 2, 4 }, lines.Select(l => l.LineNumber).ToArray());
        }

        [Fact]
        public void An_empty_filter_keeps_every_line_and_marks_none()
        {
            var lines = LogSearchView.FromTail(Tail(new[] { "a", "b" }, truncated: false), "");

            Assert.Equal(2, lines.Count);
            Assert.All(lines, l => Assert.False(l.IsMatch));
        }

        [Theory]
        [InlineData(0L, "0 B")]
        [InlineData(1023L, "1023 B")]
        [InlineData(1024L, "1.0 KB")]
        [InlineData(746759241L, "712.2 MB")]
        public void Sizes_read_the_way_a_technician_expects(long bytes, string expected)
        {
            Assert.Equal(expected, LogSearchView.FormatSize(bytes));
        }
    }
}
