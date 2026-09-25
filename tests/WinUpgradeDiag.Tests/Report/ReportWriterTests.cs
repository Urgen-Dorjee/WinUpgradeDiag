using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Web.Script.Serialization;
using WinUpgradeDiag.Core.Collect;
using WinUpgradeDiag.Core.Discovery;
using WinUpgradeDiag.Core.IO;
using WinUpgradeDiag.Core.Orchestration;
using WinUpgradeDiag.Core.Redaction;
using WinUpgradeDiag.Core.Report;
using WinUpgradeDiag.Tests.Support;
using Xunit;

namespace WinUpgradeDiag.Tests.Report
{
    public class ReportWriterTests
    {
        private static DiagnosticContext ContextWith(TempDirectory tmp, out string logPath, out string dumpPath)
        {
            logPath = tmp.File(Path.Combine("Users", "jdoe", "setupact.log"), "2026-09-18 12:10:17, Error MOUPG <failure> & more\n");
            dumpPath = tmp.File(Path.Combine("Minidump", "091826-01.dmp"), "MDMP");

            var sources = new[]
            {
                new LogSource("setup", LogSourceCategory.SetupRollback, "setupact.log (rollback)", "", logPath, highValue: true),
                new LogSource("dumps", LogSourceCategory.CrashDump, "Minidumps", "",
                    Path.GetDirectoryName(dumpPath), LogSourceKind.DirectoryGlob, "*.dmp")
            };

            var state = new SystemState
            {
                MachineName = "WS-TEST-0042",
                CollectedAtUtc = new DateTime(2026, 9, 18, 14, 30, 0, DateTimeKind.Utc),
                Processes = new ProcessSnapshot(new List<ProcessInfo> { new ProcessInfo("TSManager", false, null, null) }),
                Events = new List<EventRecordInfo>
                {
                    new EventRecordInfo { LogName = "System", EventId = 1001, Message = "Bugcheck on WS-TEST-0042 by jdoe <script>" }
                }
            };

            return new DiagnosticContext
            {
                ToolVersion = "0.1.0-test",
                StartedAtUtc = new DateTime(2026, 9, 18, 14, 29, 0, DateTimeKind.Utc),
                FinishedAtUtc = new DateTime(2026, 9, 18, 14, 30, 0, DateTimeKind.Utc),
                PrivilegedReadEnabled = true,
                Manifest = new LogManifestBuilder().Build(sources),
                SystemState = state
            };
        }

        private static readonly Redactor TestRedactor = new Redactor("WS-TEST-0042", new[] { "jdoe" });

        [Fact]
        public void Json_is_parseable_redacted_and_has_no_verdict_yet()
        {
            using (var tmp = new TempDirectory())
            {
                string log, dump;
                var json = JsonReportWriter.Serialize(ContextWith(tmp, out log, out dump), TestRedactor);

                var parsed = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);

                Assert.Equal(ReportModelBuilder.SchemaVersion, parsed["schemaVersion"]);
                Assert.Null(parsed["verdict"]);
                Assert.Equal(true, parsed["redacted"]);
                Assert.DoesNotContain("jdoe", json, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("WS-TEST-0042", json, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("2026-09-18T14:29:00", json);
            }
        }

        [Fact]
        public void Unredacted_json_keeps_identifiers()
        {
            using (var tmp = new TempDirectory())
            {
                string log, dump;
                var json = JsonReportWriter.Serialize(ContextWith(tmp, out log, out dump), null);

                Assert.Contains("WS-TEST-0042", json);
                Assert.Contains("\"redacted\":false", json);
            }
        }

        [Fact]
        public void Html_is_self_contained_encoded_and_redacted()
        {
            using (var tmp = new TempDirectory())
            {
                string log, dump;
                var html = HtmlReportWriter.Render(ContextWith(tmp, out log, out dump), TestRedactor);

                Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("http://", html);
                Assert.DoesNotContain("https://", html);
                Assert.DoesNotContain("<link", html);
                Assert.Contains("&lt;script&gt;", html);
                Assert.DoesNotContain("jdoe", html, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("No verdict in this version", html);
            }
        }

        [Fact]
        public void Evidence_zip_contains_logs_and_findings_but_never_dumps()
        {
            using (var tmp = new TempDirectory())
            {
                string log, dump;
                var context = ContextWith(tmp, out log, out dump);
                var zipPath = Path.Combine(tmp.Path, "evidence.zip");

                var skipped = EvidenceBundleWriter.Write(context, zipPath);

                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    var names = zip.Entries.Select(e => e.FullName).ToList();
                    Assert.Contains("logs/SetupRollback/setupact.log", names);
                    Assert.Contains("findings.json", names);
                    Assert.Contains("README.txt", names);
                    Assert.DoesNotContain(names, n => n.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase));
                }
                Assert.Contains(skipped, s => s.Contains("091826-01.dmp"));
            }
        }

        [Fact]
        public void Exporter_writes_everything_into_one_timestamped_folder()
        {
            using (var tmp = new TempDirectory())
            {
                string log, dump;
                var context = ContextWith(tmp, out log, out dump);
                var outRoot = Path.Combine(tmp.Path, "out");

                var result = ReportExporter.Export(context, outRoot, ExportArtifacts.All, redact: true);

                Assert.StartsWith(Path.Combine(outRoot, "UpgradeDiag_WS-TEST-0042_"), result.OutputDirectory);
                Assert.True(File.Exists(result.HtmlPath));
                Assert.True(File.Exists(result.JsonPath));
                Assert.True(File.Exists(result.EvidenceZipPath));
                Assert.All(new[] { result.HtmlPath, result.JsonPath, result.EvidenceZipPath },
                    p => Assert.Equal(result.OutputDirectory, Path.GetDirectoryName(p)));
            }
        }

        [Theory]
        [InlineData(@"C:\$WINDOWS.~BT\Sources\Panther\miglog.xml", false)]
        [InlineData(@"C:\Windows\Minidump\091826-01.dmp", false)]
        [InlineData(@"C:\$WINDOWS.~BT\Sources\Rollback\System.evtx", false)]
        [InlineData(@"C:\$WINDOWS.~BT\Sources\Rollback\setupact.log", true)]
        public void Viewer_policy_never_renders_phi_or_binary_artifacts(string path, bool allowed)
        {
            string reason;
            Assert.Equal(allowed, LogViewerPolicy.CanRender(path, out reason));
            Assert.Equal(allowed, reason == null);
        }

        [Fact]
        public void Html_encoder_escapes_all_markup_characters()
        {
            Assert.Equal("&lt;a href=&quot;x&quot;&gt;&amp;&#39;", HtmlReportWriter.E("<a href=\"x\">&'"));
        }
    }
}
