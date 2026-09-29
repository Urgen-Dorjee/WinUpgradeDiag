using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using WinUpgradeDiag.Core.Collect;
using WinUpgradeDiag.Core.Discovery;
using WinUpgradeDiag.Core.Orchestration;
using WinUpgradeDiag.Core.Rules;
using WinUpgradeDiag.Tests.Support;
using Xunit;

namespace WinUpgradeDiag.Tests.Rules
{
    /// <summary>
    /// A machine that rolled back from Windows 10 22H2 produced a report with ten findings, none of
    /// which quoted a single line from the 373 MB Panther setupact.log, the 104 MB Rollback
    /// setupact.log or the 42 KB setuperr.log — all three of which had been read successfully. The
    /// verdict was "the Rollback folder exists, so Setup reverted", which the technician could see
    /// from the machine being back on Windows 10, and the recommended action was to go and read the
    /// logs by hand.
    /// <para>
    /// These fixtures use the vocabulary real Setup logs use, deliberately without any of the twelve
    /// literal codes the signature catalogue looks for, because that is the case that failed.
    /// </para>
    /// </summary>
    public class SetupFailureAnalyzerTests
    {
        private const string RollbackErr =
            "2026-09-25 22:19:41, Error      SP     Operation failed: Add device driver oem47.inf. Error: 0x800F0247[gle=0x000000b7]\r\n" +
            "2026-09-25 22:19:41, Error      SP     CSetupPlatformImpl::Rollback: Failed to install driver package. hr = 0x800F0247\r\n" +
            "2026-09-25 22:20:02, Error      MIG    Failed to gather user state for profile. hr = 0x80070005\r\n";

        private const string RollbackAct =
            "2026-09-25 22:18:03, Info       SP     Executing offline operations\r\n" +
            "2026-09-25 22:18:44, Info       SP     CAgentApplication::PostApply: result = 0x00000000\r\n" +
            "2026-09-25 22:19:41, Error      SP     Operation failed: Add device driver oem47.inf. Error: 0x800F0247\r\n" +
            "2026-09-25 22:19:42, Info       SP     Abandoning apply due to error for object oem47.inf\r\n" +
            "2026-09-25 22:21:36, Info       SP     Rollback started\r\n";

        private static DiagnosticContext ContextFor(params LogManifestEntry[] entries)
        {
            return new DiagnosticContext
            {
                ToolVersion = "0.1.0-test",
                StartedAtUtc = new DateTime(2026, 9, 28, 16, 11, 0, DateTimeKind.Utc),
                PrivilegedReadEnabled = true,
                Manifest = entries,
                SystemState = new SystemState { MachineName = "CM-TEST-0001" }
            };
        }

        private static LogManifestEntry Entry(string path, LogSourceCategory category, string display)
        {
            var source = new LogSource("s-" + display, category, display, "", path, highValue: true);
            return new LogManifestBuilder().Build(new[] { source }).Single();
        }

        private static IReadOnlyList<Finding> Run(DiagnosticContext context)
        {
            return new SetupFailureAnalyzer().Analyze(context, null, CancellationToken.None);
        }

        [Fact]
        public void The_errors_Setup_recorded_are_quoted_even_though_no_signature_matches()
        {
            using (var tmp = new TempDirectory())
            {
                var errPath = tmp.File(Path.Combine("Rollback", "setuperr.log"), RollbackErr);
                var findings = Run(ContextFor(
                    Entry(errPath, LogSourceCategory.SetupRollback, "setuperr.log (rollback)")));

                var error = findings.Single(f => f.Id == "SU-100");

                Assert.Equal(Severity.Critical, error.Severity);
                Assert.Equal(Confidence.High, error.Confidence);

                // The actual failing thing, in Setup's own words.
                var text = string.Join("\n", error.Evidence.Select(e => e.Text));
                Assert.Contains("oem47.inf", text, StringComparison.Ordinal);
                Assert.Contains("0x800F0247", text, StringComparison.Ordinal);

                // And the code to search for is named rather than left in the evidence blob.
                Assert.Contains("0x800F0247", error.Meaning + error.Action, StringComparison.Ordinal);

                // Newest first: a technician reads the last thing that happened, not the first.
                Assert.Contains("Failed to gather user state", error.Evidence[0].Text, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void The_failing_operation_is_taken_from_the_end_of_setupact()
        {
            using (var tmp = new TempDirectory())
            {
                var actPath = tmp.File(Path.Combine("Rollback", "setupact.log"), RollbackAct);
                var findings = Run(ContextFor(
                    Entry(actPath, LogSourceCategory.SetupRollback, "setupact.log (rollback)")));

                var operation = findings.Single(f => f.Id == "SU-101");
                var text = string.Join("\n", operation.Evidence.Select(e => e.Text));

                Assert.Contains("Abandoning apply due to error", text, StringComparison.Ordinal);
                Assert.Contains("oem47.inf", text, StringComparison.Ordinal);

                // A result of zero is Setup succeeding, and must not be reported as the failure.
                Assert.DoesNotContain("PostApply", text, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void A_rollback_log_outranks_a_completed_one_from_an_older_upgrade()
        {
            using (var tmp = new TempDirectory())
            {
                // The machine in the report had a 2024 completed upgrade sitting beside a 2026
                // rollback. Quoting the old one would describe the wrong event entirely.
                var old = tmp.File(Path.Combine("Panther", "setuperr.log"),
                    "2024-09-09 23:00:11, Error      SP     Harmless historical noise. hr = 0x80070001\r\n");
                var current = tmp.File(Path.Combine("Rollback", "setuperr.log"), RollbackErr);

                var findings = Run(ContextFor(
                    Entry(old, LogSourceCategory.SetupCompleted, "setuperr.log (completed)"),
                    Entry(current, LogSourceCategory.SetupRollback, "setuperr.log (rollback)")));

                var error = findings.Single(f => f.Id == "SU-100");
                Assert.Contains("Rollback", error.Evidence[0].Source, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void Repeats_of_one_failure_are_collapsed_to_one_line()
        {
            using (var tmp = new TempDirectory())
            {
                // Setup retries, and logs the same failure once per attempt a second apart.
                var repeated = string.Concat(Enumerable.Range(0, 8).Select(i =>
                    "2026-09-25 22:19:4" + i + ", Error      SP     Operation failed: Add device driver oem47.inf. Error: 0x800F0247\r\n"));
                var path = tmp.File(Path.Combine("Rollback", "setuperr.log"), repeated);

                var error = Run(ContextFor(
                    Entry(path, LogSourceCategory.SetupRollback, "setuperr.log (rollback)")))
                    .Single(f => f.Id == "SU-100");

                Assert.Single(error.Evidence);
            }
        }

        [Fact]
        public void Nothing_is_invented_when_there_are_no_setup_logs()
        {
            using (var tmp = new TempDirectory())
            {
                var unrelated = tmp.File("smsts.log", "<![LOG[all fine]LOG]!>\r\n");
                var findings = Run(ContextFor(
                    Entry(unrelated, LogSourceCategory.TaskSequence, "smsts.log")));

                Assert.Empty(findings);
            }
        }

        [Fact]
        public void An_unreadable_log_is_skipped_rather_than_crashing_the_run()
        {
            var missing = Entry(
                Path.Combine(Path.GetTempPath(), "no-such-folder-" + Guid.NewGuid().ToString("N"), "setuperr.log"),
                LogSourceCategory.SetupRollback, "setuperr.log (rollback)");

            Assert.Empty(Run(ContextFor(missing)));
        }

        /// <summary>
        /// Both lines below are verbatim from a real report. Neither says anything about content on
        /// disk or about a task sequence step, yet CT-002 fired on them and told the technician to
        /// clear the ConfigMgr content cache.
        /// </summary>
        [Theory]
        [InlineData("<![LOG[Failed to delete registry value HKLM\\Software\\Microsoft\\SMS\\Task Sequence\\Package. Error code 0x80070002]LOG]!>")]
        [InlineData("<![LOG[GetTsRegValue() is unsuccessful. 0x80070002.]LOG]!>")]
        public void A_registry_failure_is_not_reported_as_missing_content(string line)
        {
            using (var tmp = new TempDirectory())
            {
                var path = tmp.File("smsts.log", line + "\r\n");
                var context = ContextFor(Entry(path, LogSourceCategory.TaskSequence, "smsts.log"));
                context.SystemState.IsElevated = true;

                var verdict = new RuleEngine().Evaluate(context);

                Assert.DoesNotContain(verdict.Findings, f => f.Id == "CT-002");
            }
        }

        /// <summary>
        /// "Unable to load profiler" is logged by the ConfigMgr client at startup. It carries
        /// 0x80004005 and has nothing to do with a failed step, but TS-004 reported it as one.
        /// </summary>
        [Fact]
        public void A_client_startup_message_is_not_reported_as_a_failed_step()
        {
            using (var tmp = new TempDirectory())
            {
                var path = tmp.File("smsts.log",
                    "<![LOG[Unable to load profiler: 0x80004005]LOG]!><time=\"16:09:36.046+420\" date=\"09-09-2024\" component=\"InstallSoftware\">\r\n");
                var context = ContextFor(Entry(path, LogSourceCategory.TaskSequence, "smsts.log"));
                context.SystemState.IsElevated = true;

                var verdict = new RuleEngine().Evaluate(context);

                Assert.DoesNotContain(verdict.Findings, f => f.Id == "TS-004");
            }
        }

        [Fact]
        public void A_genuine_step_failure_still_reports()
        {
            using (var tmp = new TempDirectory())
            {
                var path = tmp.File("smsts.log",
                    "<![LOG[Failed to run the action: Install Application. Error code 0x80004005]LOG]!>\r\n");
                var context = ContextFor(Entry(path, LogSourceCategory.TaskSequence, "smsts.log"));
                context.SystemState.IsElevated = true;

                var verdict = new RuleEngine().Evaluate(context);

                Assert.Contains(verdict.Findings, f => f.Id == "TS-004");
            }
        }

        /// <summary>
        /// The report presented evidence dated 2024-09-09 alongside a 2026-09-25 rollback with
        /// nothing to indicate the two were two years apart.
        /// </summary>
        [Fact]
        public void Evidence_from_an_older_attempt_is_labelled_and_demoted()
        {
            using (var tmp = new TempDirectory())
            {
                var oldLog = tmp.File("smsts-old.log",
                    "<![LOG[Failed to run the action: Install Application. Error code 0x80004005]LOG]!>\r\n");
                File.SetLastWriteTimeUtc(oldLog, new DateTime(2024, 9, 9, 23, 40, 10, DateTimeKind.Utc));

                var rollback = tmp.File(Path.Combine("Rollback", "setupact.log"), RollbackAct);
                File.SetLastWriteTimeUtc(rollback, new DateTime(2026, 9, 25, 22, 21, 36, DateTimeKind.Utc));

                var context = ContextFor(
                    Entry(oldLog, LogSourceCategory.TaskSequence, "smsts.log (old)"),
                    Entry(rollback, LogSourceCategory.SetupRollback, "setupact.log (rollback)"));
                context.SystemState.IsElevated = true;

                var stale = new RuleEngine().Evaluate(context).Findings.Single(f => f.Id == "TS-004");

                Assert.Contains("from a log written", stale.Title, StringComparison.Ordinal);
                Assert.Contains("years before the failure", stale.Title, StringComparison.Ordinal);
                Assert.Equal(Severity.Info, stale.Severity);
                Assert.Equal(Confidence.Low, stale.Confidence);
            }
        }

        /// <summary>
        /// The end-to-end shape of the bug: the full engine, on a rollback whose logs contain none
        /// of the catalogue's literals, must still produce a verdict that quotes the failure.
        /// </summary>
        [Fact]
        public void The_engine_now_names_the_failure_instead_of_only_the_rollback()
        {
            using (var tmp = new TempDirectory())
            {
                var errPath = tmp.File(Path.Combine("Rollback", "setuperr.log"), RollbackErr);
                var actPath = tmp.File(Path.Combine("Rollback", "setupact.log"), RollbackAct);

                var context = ContextFor(
                    Entry(errPath, LogSourceCategory.SetupRollback, "setuperr.log (rollback)"),
                    Entry(actPath, LogSourceCategory.SetupRollback, "setupact.log (rollback)"));
                context.SystemState.IsElevated = true;

                var verdict = new RuleEngine().Evaluate(context);

                Assert.Equal(VerdictKind.CauseIdentified, verdict.Kind);

                var quoted = string.Join("\n", verdict.Findings.SelectMany(f => f.Evidence).Select(e => e.Text));
                Assert.Contains("oem47.inf", quoted, StringComparison.Ordinal);

                // The action must be something other than "go and read the logs".
                Assert.DoesNotContain("Read the rollback setupact.log", verdict.Action.Text, StringComparison.Ordinal);
            }
        }
    }
}
