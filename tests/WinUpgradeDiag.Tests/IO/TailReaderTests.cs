using System.IO;
using System.Linq;
using System.Text;
using WinUpgradeDiag.Core.IO;
using Xunit;

namespace WinUpgradeDiag.Tests.IO
{
    public class TailReaderTests
    {
        private static MemoryStream Utf8(string text, bool bom = false)
        {
            var bytes = new UTF8Encoding(bom).GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
            return new MemoryStream(bytes);
        }

        [Fact]
        public void Small_file_is_read_whole_without_dropping_the_first_line()
        {
            var result = new TailReader().ReadTail(Utf8("one\r\ntwo\r\nthree\r\n"));

            Assert.Equal(new[] { "one", "two", "three" }, result.Lines.ToArray());
            Assert.False(result.WindowTruncatedFromStart);
            Assert.False(result.LineCountTruncated);
        }

        [Fact]
        public void Window_smaller_than_file_drops_partial_first_line_and_keeps_the_end()
        {
            var text = string.Join("\n", Enumerable.Range(1, 1000).Select(i => "line " + i.ToString("D4"))) + "\n";

            var result = new TailReader().ReadTail(Utf8(text), windowBytes: 100, maxLines: 5000);

            Assert.True(result.WindowTruncatedFromStart);
            Assert.Equal("line 1000", result.Lines.Last());
            Assert.All(result.Lines, l => Assert.StartsWith("line ", l)); // no partial fragment survived
        }

        [Fact]
        public void Max_lines_keeps_only_the_newest_lines()
        {
            var text = string.Join("\n", Enumerable.Range(1, 50).Select(i => "L" + i));

            var result = new TailReader().ReadTail(Utf8(text), maxLines: 3);

            Assert.Equal(new[] { "L48", "L49", "L50" }, result.Lines.ToArray());
            Assert.True(result.LineCountTruncated);
        }

        [Fact]
        public void Utf8_bom_is_not_part_of_the_first_line()
        {
            var result = new TailReader().ReadTail(Utf8("first\nsecond", bom: true));

            Assert.Equal("first", result.Lines[0]);
        }

        [Fact]
        public void Utf16_log_is_decoded_even_when_window_starts_at_an_odd_offset()
        {
            var text = string.Join("\r\n", Enumerable.Range(1, 200).Select(i => "2026-09-18 12:10:17, Info MOUPG entry " + i));
            var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray();

            var result = new TailReader().ReadTail(new MemoryStream(bytes), windowBytes: 301);

            Assert.Equal(Encoding.Unicode, result.DetectedEncoding);
            Assert.Equal("2026-09-18 12:10:17, Info MOUPG entry 200", result.Lines.Last());
            Assert.All(result.Lines, l => Assert.StartsWith("2026-09-18", l));
        }

        [Fact]
        public void Empty_stream_yields_no_lines()
        {
            var result = new TailReader().ReadTail(new MemoryStream());

            Assert.Empty(result.Lines);
        }
    }
}
