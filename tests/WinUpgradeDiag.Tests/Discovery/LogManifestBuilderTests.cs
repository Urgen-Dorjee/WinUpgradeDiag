using System;
using System.IO;
using System.Linq;
using WinUpgradeDiag.Core.Discovery;
using WinUpgradeDiag.Tests.Support;
using Xunit;

namespace WinUpgradeDiag.Tests.Discovery
{
    public class LogManifestBuilderTests
    {
        [Fact]
        public void Existing_file_is_reported_with_size_time_and_readable()
        {
            using (var tmp = new TempDirectory())
            {
                var written = new DateTime(2026, 9, 18, 12, 10, 17, DateTimeKind.Utc);
                var path = tmp.File("setupact.log", "hello world", written);
                var source = new LogSource("s", LogSourceCategory.SetupCurrent, "setupact", "", path);

                var entry = new LogManifestBuilder().Build(new[] { source }).Single();

                Assert.True(entry.Exists);
                Assert.True(entry.Readable);
                Assert.False(entry.RequiresPrivilegedRead);
                Assert.Equal(11, entry.SizeBytes);
                Assert.Equal(written, entry.LastWriteTimeUtc);
                Assert.Null(entry.AccessError);
            }
        }

        [Fact]
        public void Missing_file_is_reported_not_omitted()
        {
            using (var tmp = new TempDirectory())
            {
                var source = new LogSource("s", LogSourceCategory.SetupRollback, "rollback", "", Path.Combine(tmp.Path, "nope.log"));

                var entry = new LogManifestBuilder().Build(new[] { source }).Single();

                Assert.False(entry.Exists);
                Assert.False(entry.Readable);
                Assert.Same(source, entry.Source);
            }
        }

        [Fact]
        public void Directory_glob_collects_every_match_newest_first()
        {
            using (var tmp = new TempDirectory())
            {
                tmp.File(Path.Combine("SMSTSLog", "smsts-20260917-101010.log"), "a", new DateTime(2026, 9, 17, 10, 10, 10, DateTimeKind.Utc));
                tmp.File(Path.Combine("SMSTSLog", "smsts.log"), "bb", new DateTime(2026, 9, 18, 14, 22, 7, DateTimeKind.Utc));
                tmp.File(Path.Combine("SMSTSLog", "unrelated.txt"), "x");

                var source = new LogSource("ts", LogSourceCategory.TaskSequence, "smsts", "",
                    Path.Combine(tmp.Path, "SMSTSLog"), LogSourceKind.DirectoryGlob, "smsts*.log");

                var entries = new LogManifestBuilder().Build(new[] { source });

                Assert.Equal(
                    new[] { "smsts.log", "smsts-20260917-101010.log" },
                    entries.Select(e => Path.GetFileName(e.ResolvedPath)).ToArray());
                Assert.All(entries, e => Assert.True(e.Exists));
            }
        }

        [Fact]
        public void Directory_glob_with_no_matches_reports_one_missing_entry()
        {
            using (var tmp = new TempDirectory())
            {
                Directory.CreateDirectory(Path.Combine(tmp.Path, "Minidump"));
                var source = new LogSource("d", LogSourceCategory.CrashDump, "dumps", "",
                    Path.Combine(tmp.Path, "Minidump"), LogSourceKind.DirectoryGlob, "*.dmp");

                var entry = new LogManifestBuilder().Build(new[] { source }).Single();

                Assert.False(entry.Exists);
            }
        }

        [Fact]
        public void Missing_directory_reports_one_missing_entry()
        {
            using (var tmp = new TempDirectory())
            {
                var source = new LogSource("d", LogSourceCategory.SetupRollback, "evtx", "",
                    Path.Combine(tmp.Path, "Rollback"), LogSourceKind.DirectoryGlob, "*.evtx");

                var entry = new LogManifestBuilder().Build(new[] { source }).Single();

                Assert.False(entry.Exists);
                Assert.Equal(source.Path, entry.ResolvedPath);
            }
        }

        [Fact]
        public void Default_catalog_covers_every_design_category()
        {
            var categories = LogSourceCatalog.GetDefaultSources().Select(s => s.Category).Distinct().ToList();

            foreach (LogSourceCategory c in Enum.GetValues(typeof(LogSourceCategory)))
            {
                Assert.Contains(c, categories);
            }
        }

        [Fact]
        public void Default_catalog_flags_rollback_set_as_high_value()
        {
            var rollback = LogSourceCatalog.GetDefaultSources().Where(s => s.Category == LogSourceCategory.SetupRollback).ToList();

            Assert.NotEmpty(rollback);
            Assert.All(rollback, s => Assert.True(s.HighValue));
        }
    }
}
