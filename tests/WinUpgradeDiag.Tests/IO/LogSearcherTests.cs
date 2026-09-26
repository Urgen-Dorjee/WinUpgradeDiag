using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using WinUpgradeDiag.Core.IO;
using Xunit;

namespace WinUpgradeDiag.Tests.IO
{
    public class LogSearcherTests
    {
        private static MemoryStream Utf8(string text, bool bom = false)
        {
            var bytes = new UTF8Encoding(bom).GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
            return new MemoryStream(bytes);
        }

        private static string Lines(int count, Func<int, string> line)
        {
            return string.Join("\r\n", Enumerable.Range(1, count).Select(line)) + "\r\n";
        }

        [Fact]
        public void Finds_a_match_far_from_the_end_that_the_tail_window_would_miss()
        {
            // The whole point: the failure is at line 10 of 100,000, so no tail window sees it.
            var text = Lines(100000, i => i == 10
                ? "2026-09-18 12:10:17, Error  SP  Operation failed: 0xC1900101"
                : "2026-09-18 12:10:17, Info  SP  routine progress line " + i);

            var result = new LogSearcher().Search(Utf8(text), "0xC1900101");

            Assert.Single(result.Matches);
            Assert.Equal(10, result.Matches[0].LineNumber);
            Assert.Equal(100000, result.LinesScanned);
            Assert.True(result.ScannedWholeFile);
        }

        [Fact]
        public void Match_carries_the_lines_either_side_of_it()
        {
            var text = "alpha\nbravo\nTARGET here\ncharlie\ndelta\n";

            var match = new LogSearcher().Search(Utf8(text), "target", contextLines: 2).Matches.Single();

            Assert.Equal(3, match.LineNumber);
            Assert.Equal(new[] { "alpha", "bravo" }, match.Before.ToArray());
            Assert.Equal(new[] { "charlie", "delta" }, match.After.ToArray());
        }

        [Fact]
        public void Context_is_short_rather_than_padded_at_the_edges_of_the_file()
        {
            var result = new LogSearcher().Search(Utf8("TARGET\n"), "TARGET", contextLines: 3);

            var match = result.Matches.Single();
            Assert.Empty(match.Before);
            Assert.Empty(match.After);
        }

        [Fact]
        public void Search_is_case_insensitive()
        {
            var result = new LogSearcher().Search(Utf8("Setup exit code 0xC1900208\n"), "0xc1900208");

            Assert.Single(result.Matches);
        }

        [Fact]
        public void Every_occurrence_is_reported_with_its_own_line_number()
        {
            var text = "hit\nmiss\nhit\nmiss\nhit\n";

            var result = new LogSearcher().Search(Utf8(text), "hit", contextLines: 0);

            Assert.Equal(new[] { 1, 3, 5 }, result.Matches.Select(m => m.LineNumber).ToArray());
        }

        [Fact]
        public void Match_cap_stops_the_scan_and_says_so()
        {
            var text = Lines(1000, i => "failure " + i);

            var result = new LogSearcher().Search(Utf8(text), "failure", maxMatches: 5, contextLines: 0);

            Assert.Equal(5, result.Matches.Count);
            Assert.True(result.MatchLimitReached);
            Assert.False(result.ScannedWholeFile); // absent evidence must be stated, not implied
        }

        [Fact]
        public void A_capped_search_still_completes_the_context_of_the_matches_it_reported()
        {
            var text = "hit\nafter-one\nafter-two\nhit\n";

            var result = new LogSearcher().Search(Utf8(text), "hit", maxMatches: 1, contextLines: 2);

            var match = result.Matches.Single();
            Assert.True(result.MatchLimitReached);
            Assert.Equal(new[] { "after-one", "after-two" }, match.After.ToArray());
        }

        [Fact]
        public void A_line_straddling_a_chunk_boundary_is_still_matched()
        {
            // Build a file whose target line lands across the 1 MB read boundary.
            var filler = new string('x', 64);
            var sb = new StringBuilder();
            while (sb.Length < LogSearcher.ChunkBytes - 20)
            {
                sb.Append(filler).Append('\n');
            }
            sb.Append("NEEDLE-across-the-boundary\n");
            sb.Append(filler).Append('\n');

            var result = new LogSearcher().Search(Utf8(sb.ToString()), "NEEDLE-across-the-boundary");

            Assert.Single(result.Matches);
            Assert.Equal("NEEDLE-across-the-boundary", result.Matches[0].Text);
        }

        [Fact]
        public void A_multibyte_character_split_across_a_chunk_boundary_is_decoded_correctly()
        {
            // U+2192 is three UTF-8 bytes; emit enough that one inevitably straddles the 1 MB
            // read boundary. Escapes rather than literals so the test cannot depend on how the
            // compiler decodes this source file.
            const string arrow = "→";
            var unit = "step " + arrow + " phase\n";
            var repeats = ((LogSearcher.ChunkBytes + 4096) / Encoding.UTF8.GetByteCount(unit)) + 1;
            var sb = new StringBuilder(repeats * unit.Length + 32);
            for (int i = 0; i < repeats; i++)
            {
                sb.Append(unit);
            }
            var target = "final " + arrow + " marker";
            sb.Append(target).Append('\n');
            Assert.True(Encoding.UTF8.GetByteCount(sb.ToString()) > LogSearcher.ChunkBytes,
                "the fixture must be larger than one chunk for this test to mean anything");

            var result = new LogSearcher().Search(Utf8(sb.ToString()), target);

            Assert.Single(result.Matches);
            Assert.Equal(target, result.Matches[0].Text);
            // Ordinal: the default comparison is culture-sensitive, and U+FFFD collates as an
            // ignorable character, so a culture-sensitive IndexOf finds it at position 0 of
            // literally any string.
            Assert.DoesNotContain("�", result.Matches[0].Text, StringComparison.Ordinal);
        }

        [Fact]
        public void Utf16_log_is_searched_correctly()
        {
            var text = Lines(500, i => i == 250 ? "bugcheck 0x1D5 DRIVER_PNP_WATCHDOG" : "line " + i);
            var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray();

            var result = new LogSearcher().Search(new MemoryStream(bytes), "DRIVER_PNP_WATCHDOG");

            Assert.Equal(Encoding.Unicode, result.DetectedEncoding);
            Assert.Equal(250, result.Matches.Single().LineNumber);
        }

        [Fact]
        public void Utf8_bom_is_not_counted_as_part_of_the_first_line()
        {
            var result = new LogSearcher().Search(Utf8("TARGET first\nsecond\n", bom: true), "TARGET");

            Assert.Equal(1, result.Matches.Single().LineNumber);
            Assert.Equal("TARGET first", result.Matches.Single().Text);
        }

        [Fact]
        public void Final_line_without_a_trailing_newline_is_still_searched()
        {
            var result = new LogSearcher().Search(Utf8("first\nlast line TARGET"), "TARGET");

            Assert.Equal(2, result.Matches.Single().LineNumber);
        }

        [Fact]
        public void Cancellation_stops_early_and_reports_an_incomplete_scan()
        {
            var text = Lines(200000, i => "line " + i);
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();

                var result = new LogSearcher().Search(Utf8(text), "line", cancellationToken: cts.Token);

                Assert.True(result.Cancelled);
                Assert.False(result.ScannedWholeFile);
            }
        }

        [Fact]
        public void No_match_reports_a_complete_scan_of_the_whole_file()
        {
            var result = new LogSearcher().Search(Utf8(Lines(1000, i => "line " + i)), "nothing-here");

            Assert.Empty(result.Matches);
            Assert.True(result.ScannedWholeFile);
            Assert.Equal(1000, result.LinesScanned);
        }

        [Fact]
        public void Progress_runs_from_start_to_completion()
        {
            var reported = new System.Collections.Generic.List<double>();
            var text = Lines(200000, i => "line " + i);

            new LogSearcher().Search(Utf8(text), "line 199999",
                progress: new Progress<double>(reported.Add));

            // Progress is marshalled asynchronously; give it a moment to drain.
            SpinWait.SpinUntil(() => reported.Count > 0 && reported[reported.Count - 1] >= 1.0, 2000);
            Assert.NotEmpty(reported);
            Assert.All(reported, p => Assert.InRange(p, 0.0, 1.0));
        }

        [Fact]
        public void Empty_query_is_rejected_rather_than_matching_everything()
        {
            Assert.Throws<ArgumentException>(() => new LogSearcher().Search(Utf8("a\n"), ""));
        }

        [Fact]
        public void Empty_file_yields_no_matches()
        {
            var result = new LogSearcher().Search(new MemoryStream(), "anything");

            Assert.Empty(result.Matches);
            Assert.Equal(0, result.LinesScanned);
        }
    }
}
