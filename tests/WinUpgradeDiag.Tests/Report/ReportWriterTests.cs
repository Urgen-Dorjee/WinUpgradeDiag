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
using WinUpgradeDiag.Core.Rules;
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
        public void Json_is_parseable_and_redacted_and_reports_a_null_verdict_when_rules_did_not_run()
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
                // This fixture has no verdict, so the report must say so rather than imply a clean machine.
                Assert.Contains("No verdict was produced", html);
            }
        }

        [Fact]
        public void The_report_leads_with_the_verdict_its_action_and_the_evidence()
        {
            using (var tmp = new TempDirectory())
            {
                string log, dump;
                var context = ContextWith(tmp, out log, out dump);
                context.Verdict = new Verdict(
                    VerdictKind.CauseIdentified,
                    "A task sequence is stuck",
                    "The engine is gone but its lock survives.",
                    new[]
                    {
                        new Finding("TS-001", "A task sequence is stuck", Severity.Critical, Confidence.High,
                            "The client still believes a task sequence is running.",
                            "Clear the stale execution request and re-run.",
                            new[] { new Evidence(@"C:\Users\jdoe\setupact.log", 2481003, "CCM_TSExecutionRequest exists for jdoe") },
                            command: "Restart-Service CcmExec")
                    },
                    new[] { "Not running as administrator." });

                var html = HtmlReportWriter.Render(context, TestRedactor);

                // The answer, the action, the command and the evidence must all be present...
                Assert.Contains("A task sequence is stuck", html);
                Assert.Contains("RECOMMENDED ACTION", html);
                Assert.Contains("Restart-Service CcmExec", html);
                Assert.Contains("EVIDENCE", html);
                Assert.Contains("2,481,003", html);
                Assert.Contains("Cause identified", html);
                Assert.Contains("Not running as administrator", html);

                // ...and the verdict must come before the manifest, not after it.
                Assert.True(html.IndexOf("A task sequence is stuck", StringComparison.Ordinal)
                          < html.IndexOf("What was examined", StringComparison.Ordinal),
                    "the verdict must appear above the manifest");

                // Redaction still applies to everything the verdict carries.
                Assert.DoesNotContain("jdoe", html, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// The bug this covers: on a machine already running Windows 11 the highest-ranked finding
        /// was a pending reboot, so the report printed "Restart the machine before attempting the
        /// upgrade again" directly underneath "No failed upgrade found". The advice has to follow
        /// the conclusion, not whichever finding happened to sort first.
        /// </summary>
        [Fact]
        public void A_clean_machine_is_not_told_to_retry_an_upgrade_that_never_failed()
        {
            var reboot = new Finding("PR-001", "This machine is waiting for a restart",
                Severity.Warning, Confidence.High,
                "A pending restart blocks servicing operations.",
                "Restart the machine before attempting the upgrade again.",
                new Evidence[0]);

            var verdict = new Verdict(
                VerdictKind.NoFailureFound,
                "No failed upgrade found - this machine is already running Windows 11 Pro.",
                "There is no rollback folder and no Setup failure in the logs that were read.",
                new[] { reboot },
                new string[0]);

            Assert.Equal("NOTHING TO DO", verdict.Action.Label);
            Assert.DoesNotContain("attempting the upgrade again", verdict.Action.Text, StringComparison.Ordinal);
            Assert.False(verdict.Action.HasCommand);

            // The findings are still listed - they are just not presented as causes.
            Assert.Equal("Observations", verdict.FindingsHeading);
            Assert.Contains("None of them caused a failure", verdict.FindingsPreamble, StringComparison.Ordinal);

            using (var tmp = new TempDirectory())
            {
                string log, dump;
                var context = ContextWith(tmp, out log, out dump);
                context.Verdict = verdict;
                var html = HtmlReportWriter.Render(context, TestRedactor);

                Assert.Contains("NOTHING TO DO", html, StringComparison.Ordinal);
                Assert.DoesNotContain("RECOMMENDED ACTION", html, StringComparison.Ordinal);
                // The finding keeps its own advice; only the verdict-level instruction changed.
                Assert.Contains("This machine is waiting for a restart", html, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// Ranking deliberately floats a live upgrade to the top of the list. Taking the action from
        /// findings[0] regardless meant a verdict built from the Setup-progress rule underneath it
        /// printed the wrong instruction.
        /// </summary>
        [Fact]
        public void The_action_comes_from_the_finding_the_verdict_was_built_from()
        {
            var live = new Finding("TS-002", "An upgrade is running", Severity.Info, Confidence.High,
                "Setup is active.", "Leave it alone.", new Evidence[0]);
            var blocked = new Finding("SU-002", "Setup is blocked at 99%", Severity.Critical, Confidence.High,
                "Progress has not moved for over an hour.", "Collect the Panther logs and escalate.",
                new Evidence[0]);

            var verdict = new Verdict(
                VerdictKind.CauseIdentified, blocked.Title, blocked.Meaning,
                new[] { live, blocked }, new string[0], cause: blocked);

            Assert.Same(live, verdict.TopFinding);
            Assert.Equal("RECOMMENDED ACTION", verdict.Action.Label);
            Assert.Equal("Collect the Panther logs and escalate.", verdict.Action.Text);
        }

        /// <summary>
        /// An in-progress upgrade is the one state where a finding's advice could do real damage if
        /// it were promoted to the verdict's instruction, so that text is never taken from a finding.
        /// </summary>
        [Fact]
        public void A_running_upgrade_is_always_told_to_wait()
        {
            var destructive = new Finding("TS-001", "Stale task sequence", Severity.Critical, Confidence.High,
                "A lock survived.", "Delete the task sequence state and reboot.", new Evidence[0]);

            var verdict = new Verdict(
                VerdictKind.InProgress, "An upgrade is in progress - leave it alone.",
                "Setup is still running.", new[] { destructive }, new string[0]);

            Assert.Equal("DO NOTHING YET", verdict.Action.Label);
            Assert.DoesNotContain("Delete", verdict.Action.Text, StringComparison.Ordinal);
            Assert.Contains("Leave the machine alone", verdict.Action.Text, StringComparison.Ordinal);
        }

        [Fact]
        public void The_event_table_collapses_repeats_and_folds_away_the_routine_ones()
        {
            using (var tmp = new TempDirectory())
            {
                string log, dump;
                var context = ContextWith(tmp, out log, out dump);
                context.SystemState.Events = new List<EventRecordInfo>
                {
                    new EventRecordInfo { LogName = "System", ProviderName = "disk", EventId = 7,
                        Level = "Error", TimeCreatedUtc = new DateTime(2026, 9, 18, 9, 0, 0, DateTimeKind.Utc),
                        Message = "The device has a bad block." },
                    new EventRecordInfo { LogName = "System", ProviderName = "disk", EventId = 7,
                        Level = "Error", TimeCreatedUtc = new DateTime(2026, 9, 18, 8, 0, 0, DateTimeKind.Utc),
                        Message = "The device has a bad block." },
                    new EventRecordInfo { LogName = "Application", ProviderName = "Telemetry", EventId = 1001,
                        Level = "Information", TimeCreatedUtc = new DateTime(2026, 9, 18, 7, 0, 0, DateTimeKind.Utc),
                        Message = "Fault bucket 2111525315961788241" }
                };

                var html = HtmlReportWriter.Render(context, TestRedactor);

                // Two identical errors render as one row carrying a count.
                Assert.Contains("&times;2", html, StringComparison.Ordinal);
                // The error is surfaced; the routine event is only inside the fold.
                var fold = html.IndexOf("<details>", StringComparison.Ordinal);
                Assert.True(fold > 0, "the full event list should be folded away");
                Assert.True(html.IndexOf("bad block", StringComparison.Ordinal) < fold,
                    "errors belong above the fold");
                Assert.True(html.IndexOf("Fault bucket", StringComparison.Ordinal) > fold,
                    "routine events belong inside the fold");
            }
        }

        [Theory]
        [InlineData("0.1.0+e33d6cf4501e440faeef1a5cf9d59e28bd2f8aeb", "0.1.0 (e33d6cf)")]
        [InlineData("0.1.0-test", "0.1.0-test")]
        [InlineData("", "")]
        public void The_header_shows_a_short_build_id_not_a_full_hash(string version, string expected)
        {
            Assert.Equal(expected, HtmlReportWriter.ShortVersion(version));
        }

        [Fact]
        public void Filter_drivers_are_named_readably_and_third_party_ones_are_identified()
        {
            var inbox = new FilterDriverInfo
            {
                ServiceName = "AppvStrm",
                DisplayName = @"@%systemroot%\system32\drivers\AppvStrm.sys,-101",
                ImagePath = @"\SystemRoot\system32\drivers\AppvStrm.sys"
            };
            var oem = new FilterDriverInfo
            {
                ServiceName = "iaStorAfs",
                DisplayName = "@oem42.inf,%iaStorAfs.ServiceName%;iaStorAfs",
                ImagePath = @"System32\drivers\iaStorAfs.sys"
            };
            var agent = new FilterDriverInfo
            {
                ServiceName = "VendorFlt",
                DisplayName = "Vendor Endpoint Filter",
                ImagePath = @"C:\Program Files\Vendor\VendorFlt.sys"
            };

            // An unexpanded MUI reference is not a name; fall back to the service name.
            Assert.Equal("AppvStrm", HtmlReportWriter.DriverName(inbox));
            Assert.Equal("Vendor Endpoint Filter", HtmlReportWriter.DriverName(agent));

            Assert.False(HtmlReportWriter.IsThirdParty(inbox));
            Assert.True(HtmlReportWriter.IsThirdParty(oem));
            Assert.True(HtmlReportWriter.IsThirdParty(agent));
        }

        [Fact]
        public void An_old_setup_log_is_flagged_as_old()
        {
            var run = new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

            Assert.Contains("today", HtmlReportWriter.Staleness(run.AddHours(-3), run), StringComparison.Ordinal);
            Assert.Contains("5 days ago", HtmlReportWriter.Staleness(run.AddDays(-5), run), StringComparison.Ordinal);

            var stale = HtmlReportWriter.Staleness(new DateTime(2024, 12, 15, 0, 0, 0, DateTimeKind.Utc), run);
            Assert.Contains("21 months ago", stale, StringComparison.Ordinal);
            Assert.Contains("check this is the attempt you are investigating", stale, StringComparison.Ordinal);
        }

        [Fact]
        public void The_report_prescribes_the_recovery_script_with_its_parameters_resolved()
        {
            using (var tmp = new TempDirectory())
            {
                string log, dump;
                var context = ContextWith(tmp, out log, out dump);
                context.SystemState.TaskSequenceExecutionRequest =
                    WinUpgradeDiag.Core.Collect.OrphanedTaskSequenceInfo.Found("ABC00123", "ABC20001");
                context.SystemState.UpgradeFolders = new List<WinUpgradeDiag.Core.Collect.UpgradeFolderInfo>
                {
                    new WinUpgradeDiag.Core.Collect.UpgradeFolderInfo { Path = @"C:\$WINDOWS.~BT", Exists = true }
                };
                context.SystemState.IsElevated = true;
                context.SystemState.PendingReboot = new WinUpgradeDiag.Core.Collect.PendingRebootState();
                context.SystemState.SystemDriveFreeBytes = 200L * 1024 * 1024 * 1024;
                context.Verdict = new RuleEngine().Evaluate(context);

                var html = HtmlReportWriter.Render(context, TestRedactor);

                Assert.Contains("PRESCRIBED FIX", html);
                Assert.Contains("Fix-B-SetupInterrupted.ps1", html);
                Assert.Contains("IT WILL", html);
                Assert.Contains("ONLY IF", html);
                // The ticket reader must see the guard rail, not just the command.
                Assert.Contains("TSManager", html);
                Assert.Contains("does not run fixes", html);
            }
        }

        [Fact]
        public void The_json_export_carries_the_verdict_and_findings_for_fleet_aggregation()
        {
            using (var tmp = new TempDirectory())
            {
                string log, dump;
                var context = ContextWith(tmp, out log, out dump);
                context.Verdict = new Verdict(
                    VerdictKind.NoFailureFound, "No failed upgrade found.", "Nothing matched.",
                    new Finding[0], new string[0]);

                var json = JsonReportWriter.Serialize(context, TestRedactor);
                var parsed = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
                var verdict = (Dictionary<string, object>)parsed["verdict"];

                Assert.Equal("NoFailureFound", verdict["kind"]);
                Assert.Equal("No failed upgrade found.", verdict["headline"]);
                Assert.Equal(0, verdict["criticalCount"]);
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

                var bundle = EvidenceBundleWriter.Write(context, zipPath);

                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    var names = zip.Entries.Select(e => e.FullName).ToList();
                    Assert.Contains("logs/SetupRollback/setupact.log", names);
                    Assert.Contains("findings.json", names);
                    Assert.Contains("README.txt", names);
                    Assert.DoesNotContain(names, n => n.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase));
                }
                Assert.Contains(bundle.Omitted, s => s.Contains("091826-01.dmp"));
                Assert.Empty(bundle.PartiallyCaptured);
            }
        }

        [Fact]
        public void An_oversized_log_has_its_tail_captured_rather_than_being_dropped()
        {
            using (var tmp = new TempDirectory())
            {
                // A real machine carries a 700 MB setupact.log. Dropping it from an escalation
                // bundle loses the primary evidence, so the end of it must survive instead.
                var big = Path.Combine(tmp.Path, "setupact.log");
                using (var writer = new StreamWriter(big))
                {
                    for (int i = 1; i <= 20000; i++)
                    {
                        writer.WriteLine("2026-09-18 12:10:17, Info  SP  progress line " + i);
                    }
                    writer.WriteLine("2026-09-18 12:10:17, Error SP  the failure at the very end");
                }

                var source = new LogSource("setup", LogSourceCategory.SetupCurrent, "setupact.log", "", big);
                var context = new DiagnosticContext
                {
                    ToolVersion = "0.1.0-test",
                    StartedAtUtc = new DateTime(2026, 9, 18, 14, 29, 0, DateTimeKind.Utc),
                    Manifest = new LogManifestBuilder().Build(new[] { source }),
                    SystemState = new SystemState { MachineName = "WS-TEST-0042" }
                };
                var zipPath = Path.Combine(tmp.Path, "evidence.zip");

                var bundle = EvidenceBundleWriter.Write(
                    context, zipPath, maxWholeFileBytes: 4096, tailCaptureBytes: 2048);

                Assert.Empty(bundle.Omitted);
                Assert.Single(bundle.PartiallyCaptured);
                Assert.Contains("only the last", bundle.PartiallyCaptured[0]);

                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    // The name itself must disclose the truncation.
                    var captured = zip.Entries.Single(e => e.FullName.StartsWith("logs/", StringComparison.Ordinal));
                    Assert.Contains(".last-", captured.FullName);

                    using (var reader = new StreamReader(captured.Open()))
                    {
                        var text = reader.ReadToEnd();

                        // The end of the log — where the failure is — must be present…
                        Assert.Contains("the failure at the very end", text);
                        // …the beginning must not, since it was cut…
                        Assert.DoesNotContain("progress line 1\r\n", text);
                        // …and the cut must land on a line boundary, not mid-line.
                        Assert.StartsWith("2026-09-18", text);
                    }

                    using (var reader = new StreamReader(zip.GetEntry("README.txt").Open()))
                    {
                        var readme = reader.ReadToEnd();
                        Assert.Contains("PARTIALLY captured", readme);
                        Assert.Contains("setupact.log", readme);
                    }
                }
            }
        }

        [Fact]
        public void A_bundle_with_nothing_missing_says_so_explicitly()
        {
            using (var tmp = new TempDirectory())
            {
                var log = tmp.File("setupact.log", "one line\n");
                var source = new LogSource("s", LogSourceCategory.SetupCurrent, "setupact.log", "", log);
                var context = new DiagnosticContext
                {
                    Manifest = new LogManifestBuilder().Build(new[] { source }),
                    SystemState = new SystemState { MachineName = "WS-TEST-0042" }
                };
                var zipPath = Path.Combine(tmp.Path, "evidence.zip");

                var bundle = EvidenceBundleWriter.Write(context, zipPath);

                Assert.Empty(bundle.Omitted);
                Assert.Empty(bundle.PartiallyCaptured);
                using (var zip = ZipFile.OpenRead(zipPath))
                using (var reader = new StreamReader(zip.GetEntry("README.txt").Open()))
                {
                    Assert.Contains("captured in full", reader.ReadToEnd());
                }
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
