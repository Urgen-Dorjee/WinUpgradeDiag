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
    /// The error a technician actually sees is "failed with the error code 0x80070002 in the task
    /// sequence step 'Run Hardware Inventory'". The engine had signatures for bare hex codes, so it
    /// could say the code appeared somewhere in a log but never which step returned it — and the
    /// step is what decides what the code means. 0x80070002 in an inventory step that runs after
    /// Windows is already installed is a very different situation from the same code in the step
    /// that downloads the OS image.
    /// </summary>
    public class TaskSequenceFailureAnalyzerTests
    {
        /// <summary>The shape ConfigMgr actually writes, wrapper and all.</summary>
        private const string HardwareInventoryFailure =
            "<![LOG[Expand a string: WinPEandFullOS]LOG]!><time=\"09:11:04.001+420\" date=\"09-28-2026\" component=\"TSManager\">\r\n" +
            "<![LOG[Start executing an instruction. Instruction name: Run Hardware Inventory]LOG]!><time=\"09:11:05.114+420\" date=\"09-28-2026\" component=\"TSManager\">\r\n" +
            "<![LOG[Failed to run the action: Run Hardware Inventory.\r\n" +
            "The system cannot find the file specified. (Error: 80070002; Source: Windows)]LOG]!><time=\"09:11:07.882+420\" date=\"09-28-2026\" component=\"TSManager\">\r\n" +
            "<![LOG[The execution of the group (Post-Processing) has failed and the execution has been aborted. An action failed.]LOG]!><time=\"09:11:07.901+420\" date=\"09-28-2026\" component=\"TSManager\">\r\n" +
            "<![LOG[Task Sequence Engine failed! Code: enExecutionFail]LOG]!><time=\"09:11:07.955+420\" date=\"09-28-2026\" component=\"TSManager\">\r\n";

        private static DiagnosticContext ContextFor(params LogManifestEntry[] entries)
        {
            return new DiagnosticContext
            {
                ToolVersion = "0.1.0-test",
                StartedAtUtc = new DateTime(2026, 9, 28, 16, 11, 0, DateTimeKind.Utc),
                PrivilegedReadEnabled = true,
                Manifest = entries,
                SystemState = new SystemState { MachineName = "CM-TEST-0001", IsElevated = true }
            };
        }

        private static LogManifestEntry Entry(string path, string display)
        {
            var source = new LogSource(
                "ts-" + display, LogSourceCategory.TaskSequence, display, "", path, highValue: true);
            return new LogManifestBuilder().Build(new[] { source }).Single();
        }

        private static IReadOnlyList<Finding> Run(DiagnosticContext context)
        {
            return new TaskSequenceFailureAnalyzer().Analyze(context, null, CancellationToken.None);
        }

        [Fact]
        public void The_failing_step_is_named_with_its_code()
        {
            using (var tmp = new TempDirectory())
            {
                var path = tmp.File("smsts.log", HardwareInventoryFailure);
                var finding = Run(ContextFor(Entry(path, "smsts.log"))).Single();

                Assert.Equal("TS-100", finding.Id);
                Assert.Equal(Severity.Critical, finding.Severity);
                Assert.Equal(Confidence.High, finding.Confidence);

                Assert.Contains("Run Hardware Inventory", finding.Title, StringComparison.Ordinal);
                Assert.Contains("0x80070002", finding.Title, StringComparison.Ordinal);

                // The code is translated, not just repeated.
                Assert.Contains("cannot find the file specified", finding.Meaning, StringComparison.Ordinal);

                // The group gives the phase, which is how you find the step in the console.
                Assert.Contains("Post-Processing", finding.Meaning, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// A step that runs after the OS is in place fails differently from one that runs before it:
        /// Windows is installed and the machine is usable even though ConfigMgr reports failure.
        /// Saying so is the difference between "your upgrade failed" and "your upgrade worked and
        /// the reporting step didn't".
        /// </summary>
        [Fact]
        public void A_post_install_step_says_the_operating_system_is_already_in_place()
        {
            using (var tmp = new TempDirectory())
            {
                var path = tmp.File("smsts.log", HardwareInventoryFailure);
                var finding = Run(ContextFor(Entry(path, "smsts.log"))).Single();

                Assert.Contains("already been installed", finding.Action, StringComparison.Ordinal);
                Assert.Contains("still report the whole task sequence as failed", finding.Action, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void The_last_failure_wins_when_a_sequence_failed_more_than_once()
        {
            using (var tmp = new TempDirectory())
            {
                var log =
                    "<![LOG[Failed to run the action: Download Package Content.\r\n" +
                    "Unspecified error (Error: 80004005; Source: Windows)]LOG]!>\r\n" +
                    HardwareInventoryFailure;
                var path = tmp.File("smsts.log", log);

                var finding = Run(ContextFor(Entry(path, "smsts.log"))).Single();

                Assert.Contains("Run Hardware Inventory", finding.Title, StringComparison.Ordinal);
                Assert.DoesNotContain("Download Package Content", finding.Title, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void A_newer_log_is_preferred_over_an_earlier_attempt()
        {
            using (var tmp = new TempDirectory())
            {
                var older = tmp.File("smsts-20240909-164010.log",
                    "<![LOG[Failed to run the action: Upgrade Operating System.\r\n" +
                    "Unspecified error (Error: 80004005; Source: Windows)]LOG]!>\r\n");
                File.SetLastWriteTimeUtc(older, new DateTime(2024, 9, 9, 23, 40, 10, DateTimeKind.Utc));

                var newer = tmp.File("smsts.log", HardwareInventoryFailure);
                File.SetLastWriteTimeUtc(newer, new DateTime(2026, 9, 28, 16, 11, 7, DateTimeKind.Utc));

                var finding = Run(ContextFor(Entry(older, "smsts.log (old)"), Entry(newer, "smsts.log")))
                    .Single();

                Assert.Contains("Run Hardware Inventory", finding.Title, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void A_log_with_no_failed_step_produces_nothing()
        {
            using (var tmp = new TempDirectory())
            {
                var path = tmp.File("smsts.log",
                    "<![LOG[Successfully completed the action (Run Hardware Inventory) with the exit win32 code 0]LOG]!>\r\n");

                Assert.Empty(Run(ContextFor(Entry(path, "smsts.log"))));
            }
        }

        [Fact]
        public void An_unreadable_log_is_skipped_rather_than_crashing_the_run()
        {
            var missing = Entry(
                Path.Combine(Path.GetTempPath(), "gone-" + Guid.NewGuid().ToString("N"), "smsts.log"),
                "smsts.log");

            Assert.Empty(Run(ContextFor(missing)));
        }

        [Fact]
        public void The_engine_leads_with_the_named_step_rather_than_a_bare_code()
        {
            using (var tmp = new TempDirectory())
            {
                var path = tmp.File("smsts.log", HardwareInventoryFailure);
                var verdict = new RuleEngine().Evaluate(ContextFor(Entry(path, "smsts.log")));

                Assert.Equal(VerdictKind.CauseIdentified, verdict.Kind);
                Assert.Contains("Run Hardware Inventory", verdict.Headline, StringComparison.Ordinal);
            }
        }
    }
}
